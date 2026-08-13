using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GPTino.AgentHost.Runtime;

/// <summary>
/// Server-owned solve watchdog for model-authored C# script sources. A running Grasshopper solve
/// holds Rhino's single UI thread and cannot be aborted from outside, so the abort trigger must
/// live inside the script: <see cref="Inject"/> plants a deadline stopwatch at the top of the
/// script and a sampled elapsed-time check at the head of every loop body, local-function body,
/// top-level method body, and statement-lambda body (Parallel.For included), each throwing a
/// TimeoutException when the budget is exceeded — the solve then ends as an ordinary component
/// runtime error instead of freezing Rhino.
///
/// <para>Ownership invariant: the model owns its text. Every injected artifact carries the
/// <c>gptino:guard</c> marker and <see cref="Strip"/> removes exactly the marked artifacts, so a
/// model-facing read returns the byte-identical text the model last wrote. Injection always strips
/// first, so the transform is idempotent however a source round-trips. The guard IS deliberately
/// visible to humans in the Grasshopper editor (marked "auto-injected"), so a self-aborted script
/// explains itself. A source that carries markers but no longer parses (a human mangled the guard)
/// is returned untouched rather than risk mutilating user text — the fingerprint conflict already
/// flags the foreign edit, and the next full-text write heals it.</para>
///
/// <para>Deterministic by design (same source + budget ⇒ same output): dispatch retries and
/// idempotent resubmits must produce identical stored text and fingerprints.</para>
///
/// <para>Known v1 gaps, accepted: expression-bodied lambdas/functions are not instrumented;
/// members inside type declarations are skipped entirely (the prologue locals are not in scope
/// there — script-mode sources are top-level statements per the cookbook); a deep-recursion
/// StackOverflow remains uncatchable by any in-process guard.</para>
/// </summary>
internal static class CSharpWatchdogInjector
{
    // Any injector-emitted statement mentions this prefix; user sources never legitimately use it
    // (injection is skipped entirely when a stripped source still contains it).
    internal const string GuardIdentifierPrefix = "__gptino_";
    internal const string MarkerToken = "gptino:guard";
    private const string InlineMarker = "/*gptino:guard*/";
    private const string BracedMarker = "/*gptino:guard-braced*/";

    /// <summary>
    /// Returns <paramref name="source"/> with the watchdog installed, or the (stripped) source
    /// unchanged when injection is impossible (parse failure, guard-identifier collision, no
    /// executable top level) or unnecessary (nothing loop-shaped to instrument).
    /// </summary>
    internal static string Inject(string source, int budgetMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return source;
        }
        var stripped = Strip(source);
        if (stripped.Contains(GuardIdentifierPrefix, StringComparison.Ordinal))
        {
            // The text still carries guard identifiers after stripping — either the model authored
            // colliding names or a mangled guard survived. Injecting would collide; leave it alone
            // (the pre-write backstop and the cost gates still apply).
            return stripped;
        }
        if (TryParse(stripped) is not { } root)
        {
            // Not parseable as top-level-statement or script C#: the compile error will surface at
            // execute. Never block a write on injection.
            return stripped;
        }

        var rewriter = new GuardRewriter(CheckStatement(budgetMilliseconds));
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root);
        if (rewriter.InjectedChecks == 0)
        {
            return stripped;
        }
        var firstGlobal = rewritten.Members.OfType<GlobalStatementSyntax>().FirstOrDefault();
        if (firstGlobal is null)
        {
            // Declarations only — nothing executes at the top level, so the prologue locals would
            // be orphaned (checks only land where they are in scope, so this is a hard guarantee,
            // not an expected path).
            return stripped;
        }

        var prologue = SyntaxFactory.ParseCompilationUnit(
            "// <gptino:guard v1> auto-injected solve watchdog - do not edit; GPTino re-injects it on\n" +
            "// every source write and strips it from model reads. Aborts the script cleanly when it\n" +
            "// exceeds the solve budget instead of freezing Rhino's UI thread. </gptino:guard>\n" +
            $"var {GuardIdentifierPrefix}sw = System.Diagnostics.Stopwatch.StartNew(); {InlineMarker}\n" +
            $"long {GuardIdentifierPrefix}i = 0L; {InlineMarker}\n").Members;
        // The first original statement's leading trivia (shebang-style header comments included)
        // must stay ABOVE the guard block, so it migrates onto the prologue head; Strip migrates
        // the pre-marker prefix back. Both directions are exercised by the round-trip tests.
        var index = rewritten.Members.IndexOf(firstGlobal);
        var lead = firstGlobal.GetLeadingTrivia();
        var members = rewritten.Members
            .Replace(firstGlobal, firstGlobal.WithLeadingTrivia())
            .InsertRange(index, new[]
            {
                prologue[0].WithLeadingTrivia(lead.AddRange(prologue[0].GetLeadingTrivia())),
                prologue[1],
            });
        return rewritten.WithMembers(members).ToFullString();
    }

    /// <summary>
    /// Removes every injected guard artifact, returning the model-owned text byte-identically.
    /// Sources with no guard marker pass through untouched — Python sources included, which makes
    /// this safe to call on any runtime's source text.
    /// </summary>
    internal static string Strip(string source)
    {
        if (string.IsNullOrEmpty(source) ||
            !source.Contains(MarkerToken, StringComparison.Ordinal))
        {
            return source;
        }
        if (TryParse(source) is not { } root)
        {
            return source;
        }
        return ((CompilationUnitSyntax)new StripRewriter().Visit(root)).ToFullString();
    }

    private static CompilationUnitSyntax? TryParse(string source) =>
        ParseWith(source, SourceCodeKind.Regular) ?? ParseWith(source, SourceCodeKind.Script);

    private static CompilationUnitSyntax? ParseWith(string source, SourceCodeKind kind)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest, kind: kind));
        return tree.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error)
            ? null
            : (CompilationUnitSyntax)tree.GetRoot();
    }

    // The sampled deadline check: the counter mask bounds Stopwatch reads to every 16th iteration,
    // so a tight numeric loop pays increment-and-branch only, while a slow-bodied loop still gets
    // its first real check by iteration 16.
    private static StatementSyntax CheckStatement(int budgetMilliseconds) =>
        SyntaxFactory.ParseStatement(
            $"if (((++{GuardIdentifierPrefix}i) & 15L) == 0L && " +
            $"{GuardIdentifierPrefix}sw.ElapsedMilliseconds > {budgetMilliseconds}L) " +
            "throw new System.TimeoutException(\"GPTino solve budget " +
            $"({budgetMilliseconds} ms) exceeded - reduce the workload or split this stage.\"); " +
            InlineMarker + "\n");

    private sealed class GuardRewriter(StatementSyntax check) : CSharpSyntaxRewriter
    {
        public int InjectedChecks { get; private set; }

        // The prologue locals are top-level; nothing inside a type declaration can see them, so
        // type bodies are skipped wholesale (do not descend).
        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => node;
        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => node;
        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => node;
        public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => node;

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
            return visited.WithStatement(Guarded(visited.Statement));
        }

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
            return visited.WithStatement(Guarded(visited.Statement));
        }

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        {
            var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
            return visited.WithStatement(Guarded(visited.Statement));
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            return visited.WithStatement(Guarded(visited.Statement));
        }

        public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
        {
            var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
            return visited.WithStatement(Guarded(visited.Statement));
        }

        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
            return visited.Body is { } body ? visited.WithBody(Prepend(body)) : visited;
        }

        // Reachable only for script-kind top-level methods: type declarations never descend here.
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            return visited.Body is { } body ? visited.WithBody(Prepend(body)) : visited;
        }

        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        {
            var visited = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;
            return visited.Block is { } block ? visited.WithBlock(Prepend(block)) : visited;
        }

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        {
            var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;
            return visited.Block is { } block ? visited.WithBlock(Prepend(block)) : visited;
        }

        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
        {
            var visited = (AnonymousMethodExpressionSyntax)base.VisitAnonymousMethodExpression(node)!;
            return visited.Block is { } block ? visited.WithBlock(Prepend(block)) : visited;
        }

        private StatementSyntax Guarded(StatementSyntax body) =>
            body is BlockSyntax block ? Prepend(block) : WrapBraced(body);

        private BlockSyntax Prepend(BlockSyntax block)
        {
            InjectedChecks++;
            // The check carries ALL of its own trivia (indent copied from the first statement's
            // final whitespace, or a default), so removing the node on strip restores the original
            // bytes exactly — sibling tokens are never touched.
            var indent = block.Statements.Count > 0
                ? block.Statements[0].GetLeadingTrivia()
                    .LastOrDefault(trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia))
                    .ToString()
                : string.Empty;
            var guarded = check.WithLeadingTrivia(
                SyntaxFactory.Whitespace(string.IsNullOrEmpty(indent) ? "    " : indent));
            return block.WithStatements(block.Statements.Insert(0, guarded));
        }

        private BlockSyntax WrapBraced(StatementSyntax original)
        {
            InjectedChecks++;
            // Single-statement loop body: wrap it in a marker-tagged block so Strip can unwrap
            // back to the untouched original statement byte-exactly. Every injector token ends its
            // own line (check and close brace both carry an EOL), so a REPARSE of the stored text
            // re-attaches the original statement's leading/trailing trivia to the original
            // statement — never to injector tokens whose removal would take user bytes with it.
            return SyntaxFactory.Block(
                SyntaxFactory.Token(SyntaxKind.OpenBraceToken)
                    .WithTrailingTrivia(SyntaxFactory.Space, SyntaxFactory.Comment(BracedMarker)),
                SyntaxFactory.List(new[]
                {
                    check
                        .WithLeadingTrivia(SyntaxFactory.Space)
                        .WithTrailingTrivia(
                            SyntaxFactory.Space,
                            SyntaxFactory.Comment(InlineMarker),
                            SyntaxFactory.EndOfLine("\n")),
                    original,
                }),
                SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
                    .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n")));
        }
    }

    private sealed class StripRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
        {
            var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
            // Drop the prologue globals, migrating any user trivia that sat ABOVE the guard header
            // (Inject moved it onto the prologue head) back onto the next surviving member.
            var kept = new List<MemberDeclarationSyntax>(visited.Members.Count);
            var carry = default(SyntaxTriviaList);
            foreach (var member in visited.Members)
            {
                if (member is GlobalStatementSyntax global && IsGuardStatement(global.Statement))
                {
                    carry = carry.AddRange(PreMarkerPrefix(member.GetLeadingTrivia()));
                    continue;
                }
                kept.Add(carry.Count > 0
                    ? member.WithLeadingTrivia(carry.AddRange(member.GetLeadingTrivia()))
                    : member);
                carry = default;
            }
            return visited.WithMembers(SyntaxFactory.List(kept));
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var visited = (BlockSyntax)base.VisitBlock(node)!;
            var kept = visited.Statements.Where(statement => !IsGuardStatement(statement)).ToArray();
            // A wrap-block the injector created: unwrap back to the single original statement.
            if (kept.Length == 1 &&
                visited.OpenBraceToken.TrailingTrivia.ToFullString()
                    .Contains(BracedMarker, StringComparison.Ordinal))
            {
                return kept[0];
            }
            return kept.Length == visited.Statements.Count
                ? visited
                : visited.WithStatements(SyntaxFactory.List(kept));
        }

        // Guard-owned statements are exactly the shapes the injector emits AND carry the marker
        // comment AND mention the guard identifiers — all three, so a user statement that merely
        // resembles one (even one using __gptino_ names) is never misclassified.
        private static bool IsGuardStatement(StatementSyntax statement) =>
            statement is IfStatementSyntax or LocalDeclarationStatementSyntax &&
            statement.ToString().Contains(GuardIdentifierPrefix, StringComparison.Ordinal) &&
            statement.ToFullString().Contains(MarkerToken, StringComparison.Ordinal);

        private static SyntaxTriviaList PreMarkerPrefix(SyntaxTriviaList trivia)
        {
            var prefix = default(SyntaxTriviaList);
            foreach (var item in trivia)
            {
                if (item.ToFullString().Contains(MarkerToken, StringComparison.Ordinal))
                {
                    return prefix;
                }
                prefix = prefix.Add(item);
            }
            // No marker in this statement's leading trivia (a mid-source guard check): none of it
            // is user prefix worth carrying — it is the injector's own indentation.
            return default;
        }
    }
}
