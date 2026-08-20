using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vino.AgentHost.Runtime;

/// <summary>
/// Deterministic, LLM-free merge of staged C# script components into one block-structured source
/// (W3 of the heavy-script plan). The merged text is fully self-describing: a one-line meta header
/// records every stage's identity, hoisted usings, and socket interface; per-stage marker blocks
/// delimit the ID-addressed regions <c>python.replaceBlock</c> edits; seam assignments (the wires
/// that became local variables) live BETWEEN marker blocks so a block edit can never destroy them.
///
/// <para>Scope v1 (documented in docs/heavy-script-plan-2026-08-13.md): C# stages only; top-level
/// type declarations are refused (script-mode statements cannot follow type declarations, so
/// hoisting them would tear a block apart); a cross-stage name collision is healed by a token-wise
/// rename in the later stage unless the name is also used in a member position there — then the
/// merge is refused with a rename instruction (deterministic beats silently wrong).</para>
///
/// <para>Everything here is pure text/syntax transformation — document knowledge (wires, topology,
/// measurements, seam rules) belongs to the caller, which hands fully-resolved
/// <see cref="StageSpec"/>s in topological order.</para>
/// </summary>
internal static class CSharpStageMerger
{
    internal const string CSharpDirective = "// #! csharp";
    internal const string MetaMarkerPrefix = "// <vino:stages v1> ";
    private const string StageBeginPrefix = "// <stage:";
    private const string StageEndPrefix = "// </stage:";
    private const string SeamMarkerPrefix = "// <seam:";

    /// <summary>One socket of a stage as the schema knows it (typeHint/access are wire-format strings).</summary>
    internal sealed record StageSocketSpec(string Name, string TypeHint, string Access, bool Optional);

    /// <summary>
    /// One stage input with its provenance: seam-fed (from an in-group producer's output variable)
    /// or external (null seam — it becomes a merged-component socket).
    /// </summary>
    internal sealed record StageInputSpec(
        StageSocketSpec Socket,
        string? SeamFromBlockId,
        string? SeamFromOutput);

    /// <summary>A stage to merge, already topo-ordered and validated by the caller. Source is the
    /// model-owned (watchdog-stripped) text.</summary>
    internal sealed record StageSpec(
        Guid ComponentId,
        string BlockId,
        string NickName,
        string Source,
        IReadOnlyList<StageInputSpec> Inputs,
        IReadOnlyList<StageSocketSpec> Outputs);

    /// <summary>A merged-component socket: current (possibly renamed) variable name plus where it
    /// came from, so wiring and split can map back.</summary>
    internal sealed record MergedSocket(
        StageSocketSpec Socket,
        string OriginalName,
        string StageBlockId);

    internal sealed record MergeOutcome(
        string Source,
        IReadOnlyList<MergedSocket> Inputs,
        IReadOnlyList<MergedSocket> Outputs);

    // --- meta header wire shape (compact single-line JSON; property names deliberately terse:
    // the header rides inside every merged source the model reads) ---
    internal sealed record MetaSocket(
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("orig")] string OriginalName,
        [property: JsonPropertyName("t")] string TypeHint,
        [property: JsonPropertyName("a")] string Access,
        [property: JsonPropertyName("o")] bool Optional,
        [property: JsonPropertyName("from")] string? From = null);

    internal sealed record MetaStage(
        [property: JsonPropertyName("id")] string BlockId,
        [property: JsonPropertyName("src")] Guid SourceComponentId,
        [property: JsonPropertyName("nick")] string NickName,
        [property: JsonPropertyName("usings")] IReadOnlyList<string> Usings,
        [property: JsonPropertyName("in")] IReadOnlyList<MetaSocket> Inputs,
        [property: JsonPropertyName("out")] IReadOnlyList<MetaSocket> Outputs);

    internal sealed record MetaHeader(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("stages")] IReadOnlyList<MetaStage> Stages);

    internal sealed record MergedBlock(string BlockId, string Text);

    internal sealed record MergedLayout(
        MetaHeader Meta,
        IReadOnlyList<string> Usings,
        IReadOnlyList<MergedBlock> Blocks,
        IReadOnlyDictionary<string, IReadOnlyList<string>> SeamLines);

    private static readonly JsonSerializerOptions MetaJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Merges the stages into one block-structured source. Throws
    /// <see cref="InvalidOperationException"/> with a model-actionable message on every refusal —
    /// the caller surfaces it verbatim, so messages name the offending stage and the fix.
    /// </summary>
    internal static MergeOutcome Merge(IReadOnlyList<StageSpec> stages)
    {
        if (stages.Count < 2)
        {
            throw new InvalidOperationException("Consolidation needs at least two stages.");
        }
        if (stages.Select(stage => stage.BlockId).Distinct(StringComparer.Ordinal).Count() != stages.Count)
        {
            throw new InvalidOperationException("Stage block ids must be unique.");
        }

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        // (blockId, original output name) -> current top-level variable name after renames.
        var producerVariables = new Dictionary<(string BlockId, string Output), string>();
        var usings = new List<string>();
        var usingSeen = new HashSet<string>(StringComparer.Ordinal);
        var metaStages = new List<MetaStage>();
        var blockTexts = new List<(string BlockId, string Text, IReadOnlyList<string> Seams)>();
        var mergedInputs = new List<MergedSocket>();

        foreach (var stage in stages)
        {
            var parsed = ParseStage(stage);
            foreach (var directive in parsed.Usings)
            {
                if (usingSeen.Add(directive))
                {
                    usings.Add(directive);
                }
            }

            // Resolve every input's producer FIRST: a seam input whose name equals its producer's
            // current variable is a pass-through (it reads the earlier declaration directly), so it
            // declares nothing and must not join the collision set.
            var inputPlans = new List<(StageInputSpec Input, string? Producer)>();
            foreach (var input in stage.Inputs)
            {
                string? producer = null;
                if (input.SeamFromBlockId is not null &&
                    !producerVariables.TryGetValue(
                        (input.SeamFromBlockId, input.SeamFromOutput ?? string.Empty), out producer))
                {
                    throw new InvalidOperationException(
                        $"Stage '{stage.NickName}' ({stage.BlockId}) input '{input.Socket.Name}' references " +
                        $"producer {input.SeamFromBlockId}.{input.SeamFromOutput}, which is not an output of an " +
                        "earlier stage in this group — the stages are not in topological order.");
                }
                inputPlans.Add((input, producer));
            }

            // Rhino 8 script-mode PRE-DECLARES socket variables, so real stage sources ASSIGN their
            // outputs ('pts = list;') rather than declare them (live-gated 2026-08-13). Inlined
            // into the merged text a non-sink output loses its socket, so its first top-level
            // assignment is PROMOTED to a declaration ('var pts = list;'); sink outputs stay
            // assignments — they are the merged component's sockets, pre-declared exactly as in
            // the original. Split demotes the promotion back (BuildStageSource).
            var isSink = ReferenceEquals(stage, stages[^1]);
            var bodyRoot = parsed.BodyRoot;
            var promoted = false;
            if (!isSink)
            {
                var undeclared = stage.Outputs
                    .Where(output => !parsed.DeclaredNames.Contains(output.Name))
                    .Select(output => output.Name)
                    .ToArray();
                if (undeclared.Length > 0)
                {
                    bodyRoot = PromoteOutputAssignments(bodyRoot, undeclared, stage);
                    promoted = true;
                }
            }

            // Everything that becomes a TOP-LEVEL declaration once this block is inlined: the
            // block's own declarations, its (possibly promoted) outputs, plus input variables that
            // get a seam assignment or become framework-declared merged sockets (pass-throughs
            // excluded). Sink outputs join too: they must not bind to an earlier stage's local.
            var stageNames = new HashSet<string>(parsed.DeclaredNames, StringComparer.Ordinal);
            stageNames.UnionWith(stage.Outputs.Select(output => output.Name));
            stageNames.UnionWith(inputPlans
                .Where(plan => plan.Producer is null ||
                    !string.Equals(plan.Producer, plan.Input.Socket.Name, StringComparison.Ordinal))
                .Select(plan => plan.Input.Socket.Name));

            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in stageNames.Where(usedNames.Contains).OrderBy(item => item, StringComparer.Ordinal))
            {
                if (parsed.MemberPositionNames.Contains(name))
                {
                    throw new InvalidOperationException(
                        $"Stage '{stage.NickName}' ({stage.BlockId}) declares '{name}', which collides with an " +
                        $"earlier stage, and also uses '{name}' as a member name — an automatic rename would " +
                        $"change meaning. Rename '{name}' in this stage, re-execute it, then consolidate.");
                }
                var fresh = FreshName(name, stage.BlockId, candidate =>
                    usedNames.Contains(candidate) ||
                    stageNames.Contains(candidate) ||
                    parsed.AllIdentifierNames.Contains(candidate));
                renames[name] = fresh;
            }

            var bodyText = renames.Count == 0 && !promoted
                ? parsed.BodyText
                : RenameIdentifiers(bodyRoot, renames).ToFullString();
            bodyText = TrimOuterBlankLines(bodyText);

            string CurrentName(string name) => renames.TryGetValue(name, out var renamed) ? renamed : name;

            foreach (var name in stageNames)
            {
                usedNames.Add(CurrentName(name));
            }
            foreach (var output in stage.Outputs)
            {
                producerVariables[(stage.BlockId, output.Name)] = CurrentName(output.Name);
            }

            var seams = new List<string>();
            var metaInputs = new List<MetaSocket>();
            foreach (var (input, producer) in inputPlans)
            {
                var currentName = CurrentName(input.Socket.Name);
                if (producer is null)
                {
                    mergedInputs.Add(new MergedSocket(
                        input.Socket with { Name = currentName },
                        input.Socket.Name,
                        stage.BlockId));
                    metaInputs.Add(new MetaSocket(
                        currentName, input.Socket.Name, input.Socket.TypeHint,
                        input.Socket.Access, input.Socket.Optional, From: "ext"));
                    continue;
                }
                if (!string.Equals(producer, currentName, StringComparison.Ordinal))
                {
                    seams.Add(FormattableString.Invariant(
                        $"var {currentName} = {producer}; {SeamMarkerPrefix}{stage.BlockId}.{currentName}>"));
                }
                metaInputs.Add(new MetaSocket(
                    currentName, input.Socket.Name, input.Socket.TypeHint,
                    input.Socket.Access, input.Socket.Optional,
                    From: FormattableString.Invariant($"{input.SeamFromBlockId}:{producer}")));
            }

            var metaOutputs = stage.Outputs
                .Select(output => new MetaSocket(
                    CurrentName(output.Name), output.Name, output.TypeHint, output.Access, output.Optional))
                .ToArray();
            metaStages.Add(new MetaStage(
                stage.BlockId, stage.ComponentId, stage.NickName, parsed.Usings, metaInputs, metaOutputs));
            blockTexts.Add((stage.BlockId, bodyText, seams));
        }

        var sink = stages[^1];
        var sinkMeta = metaStages[^1];
        var mergedOutputs = sink.Outputs
            .Select((output, index) => new MergedSocket(
                output with { Name = sinkMeta.Outputs[index].Name },
                output.Name,
                sink.BlockId))
            .ToArray();

        var source = Compose(new MergedLayout(
            new MetaHeader(1, metaStages),
            usings,
            blockTexts.Select(block => new MergedBlock(block.BlockId, block.Text)).ToArray(),
            blockTexts.ToDictionary(
                block => block.BlockId,
                block => block.Seams,
                StringComparer.Ordinal)));

        ValidateComposedSource(source);
        return new MergeOutcome(source, mergedInputs, mergedOutputs);
    }

    /// <summary>
    /// Parses a merged source (watchdog-STRIPPED text) back into its meta header, hoisted usings,
    /// marker blocks, and seam lines. Returns false with a reason when the text is not (or is no
    /// longer) a well-formed merged source — e.g. after a full setSource rewrite dropped the
    /// markers, which honestly makes it a plain component again.
    /// </summary>
    internal static bool TryParseLayout(string source, out MergedLayout? layout, out string? error)
    {
        layout = null;
        error = null;
        var lines = source.Split('\n');
        MetaHeader? meta = null;
        var usings = new List<string>();
        var blocks = new List<MergedBlock>();
        var seams = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var pendingSeams = new List<string>();
        string? openBlock = null;
        var blockLines = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (openBlock is not null)
            {
                if (trimmed.StartsWith(StageEndPrefix, StringComparison.Ordinal))
                {
                    var closed = MarkerId(trimmed, StageEndPrefix);
                    if (!string.Equals(closed, openBlock, StringComparison.Ordinal))
                    {
                        error = $"Stage marker mismatch: '{openBlock}' closed by '{closed}'.";
                        return false;
                    }
                    blocks.Add(new MergedBlock(openBlock, string.Join("\n", blockLines)));
                    openBlock = null;
                    blockLines.Clear();
                    continue;
                }
                blockLines.Add(line);
                continue;
            }
            if (trimmed.Length == 0 || string.Equals(trimmed, CSharpDirective, StringComparison.Ordinal))
            {
                continue;
            }
            if (trimmed.StartsWith(MetaMarkerPrefix, StringComparison.Ordinal))
            {
                try
                {
                    meta = JsonSerializer.Deserialize<MetaHeader>(
                        trimmed[MetaMarkerPrefix.Length..], MetaJson);
                }
                catch (JsonException exception)
                {
                    error = $"The vino:stages meta header does not parse: {exception.Message}";
                    return false;
                }
                continue;
            }
            if (trimmed.StartsWith("using ", StringComparison.Ordinal))
            {
                usings.Add(line.Trim());
                continue;
            }
            if (trimmed.StartsWith(StageBeginPrefix, StringComparison.Ordinal))
            {
                openBlock = MarkerId(trimmed, StageBeginPrefix);
                if (openBlock is null)
                {
                    error = $"Malformed stage marker: '{trimmed}'.";
                    return false;
                }
                seams[openBlock] = pendingSeams.ToArray();
                pendingSeams = new List<string>();
                continue;
            }
            if (trimmed.Contains(SeamMarkerPrefix, StringComparison.Ordinal))
            {
                pendingSeams.Add(line);
                continue;
            }
            error = $"Unexpected content outside stage blocks: '{Truncate(trimmed)}'. " +
                "A merged component's code lives inside its stage markers; use replaceBlock for " +
                "edits, or a full updatePythonSource to stop being a merged component.";
            return false;
        }
        if (openBlock is not null)
        {
            error = $"Stage block '{openBlock}' is never closed.";
            return false;
        }
        if (meta is null || meta.Version != 1)
        {
            error = "No vino:stages v1 meta header found - this is not a merged component.";
            return false;
        }
        if (blocks.Count != meta.Stages.Count ||
            blocks.Select(block => block.BlockId)
                .Except(meta.Stages.Select(stage => stage.BlockId), StringComparer.Ordinal).Any())
        {
            error = "The stage markers and the meta header disagree about the block set.";
            return false;
        }
        layout = new MergedLayout(meta, usings, blocks, seams);
        return true;
    }

    /// <summary>
    /// Replaces one block's text and returns the recomposed full source. Validates the parse-level
    /// interface contract: the new block still declares every output the meta header promises, does
    /// not re-declare its seam-provided inputs, and the recomposed whole still parses without
    /// duplicate top-level declarations.
    /// </summary>
    internal static string ReplaceBlock(string source, string blockId, string newBlockText)
    {
        if (!TryParseLayout(source, out var layout, out var parseError))
        {
            throw new InvalidOperationException(
                $"replaceBlock target is not a well-formed merged component: {parseError}");
        }
        var meta = layout!.Meta.Stages.FirstOrDefault(
            stage => string.Equals(stage.BlockId, blockId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Block '{blockId}' does not exist; this merged component has blocks: " +
                string.Join(", ", layout.Meta.Stages.Select(stage => stage.BlockId)) + ".");

        var replacement = TrimOuterBlankLines(newBlockText.Replace("\r\n", "\n"));
        if (ContainsMarkerLine(replacement))
        {
            throw new InvalidOperationException(
                "The replacement block must not contain stage/seam marker comments - send only the " +
                "block's own statements.");
        }
        var parsed = TryParseStatements(replacement)
            ?? throw new InvalidOperationException(
                "The replacement block does not parse as C# script-mode statements.");
        var declared = CollectTopLevelDeclaredNames(parsed);
        // Non-sink outputs must exist as top-level locals inside the merged text: a declaration is
        // taken as-is, a plain script-mode assignment is promoted exactly like at merge time. The
        // last block's outputs are the merged component's own sockets (framework-declared), so
        // they need no local at all.
        var isLastBlock = string.Equals(
            layout.Meta.Stages[^1].BlockId, blockId, StringComparison.Ordinal);
        if (!isLastBlock)
        {
            var undeclared = meta.Outputs
                .Where(output => !declared.Contains(output.Name))
                .Select(output => output.Name)
                .ToArray();
            if (undeclared.Length > 0)
            {
                var promotedRoot = PromoteOutputAssignments(
                    parsed,
                    undeclared,
                    new StageSpec(
                        meta.SourceComponentId, blockId, meta.NickName, replacement,
                        Array.Empty<StageInputSpec>(), Array.Empty<StageSocketSpec>()));
                replacement = TrimOuterBlankLines(promotedRoot.ToFullString());
            }
        }
        foreach (var input in meta.Inputs)
        {
            if (declared.Contains(input.Name))
            {
                throw new InvalidOperationException(
                    $"The replacement for block '{blockId}' declares '{input.Name}', which is one of " +
                    "its own inputs - inputs arrive from the seam/socket layer and must not be " +
                    "re-declared.");
            }
        }

        var blocks = layout.Blocks
            .Select(block => string.Equals(block.BlockId, blockId, StringComparison.Ordinal)
                ? new MergedBlock(block.BlockId, replacement)
                : block)
            .ToArray();
        var recomposed = Compose(layout with { Blocks = blocks });
        ValidateComposedSource(recomposed);
        return recomposed;
    }

    /// <summary>Assembles the canonical merged text from a layout. Deterministic: same layout ⇒
    /// byte-identical source (idempotent resubmits must hash identically).</summary>
    internal static string Compose(MergedLayout layout)
    {
        var builder = new StringBuilder();
        builder.Append(CSharpDirective).Append('\n');
        builder.Append(MetaMarkerPrefix)
            .Append(JsonSerializer.Serialize(layout.Meta, MetaJson))
            .Append('\n');
        foreach (var directive in layout.Usings)
        {
            builder.Append(directive).Append('\n');
        }
        foreach (var block in layout.Blocks)
        {
            if (layout.SeamLines.TryGetValue(block.BlockId, out var seams))
            {
                foreach (var seam in seams)
                {
                    builder.Append(seam).Append('\n');
                }
            }
            builder.Append(StageBeginPrefix).Append(block.BlockId).Append(">\n");
            builder.Append(block.Text);
            if (block.Text.Length > 0 && !block.Text.EndsWith("\n", StringComparison.Ordinal))
            {
                builder.Append('\n');
            }
            builder.Append(StageEndPrefix).Append(block.BlockId).Append(">\n");
        }
        return builder.ToString();
    }

    /// <summary>Rebuilds each stage's standalone source for a split: its recorded usings above its
    /// block text with output-name declarations demoted back to assignments (recreated as a
    /// component, the outputs are framework-declared sockets again). Socket names stay the current
    /// variable names — correctness over aesthetics.</summary>
    internal static string BuildStageSource(MergedLayout layout, string blockId)
    {
        var stage = layout.Meta.Stages.First(
            item => string.Equals(item.BlockId, blockId, StringComparison.Ordinal));
        var block = layout.Blocks.First(
            item => string.Equals(item.BlockId, blockId, StringComparison.Ordinal));
        var text = DemoteOutputDeclarations(
            block.Text,
            stage.Outputs.Select(output => output.Name).ToArray());
        var builder = new StringBuilder();
        foreach (var directive in stage.Usings)
        {
            builder.Append(directive).Append('\n');
        }
        builder.Append(text);
        if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal))
        {
            builder.Append('\n');
        }
        return builder.ToString();
    }

    // --- stage parsing ---

    private sealed record ParsedStage(
        CompilationUnitSyntax BodyRoot,
        string BodyText,
        IReadOnlyList<string> Usings,
        IReadOnlyCollection<string> DeclaredNames,
        IReadOnlyCollection<string> MemberPositionNames,
        IReadOnlyCollection<string> AllIdentifierNames);

    private static ParsedStage ParseStage(StageSpec stage)
    {
        var text = stage.Source.Replace("\r\n", "\n");
        // Drop the per-stage language directive; the merged head carries the canonical one.
        var lines = text.Split('\n').ToList();
        var firstContent = lines.FindIndex(line => line.Trim().Length > 0);
        if (firstContent >= 0)
        {
            var head = lines[firstContent].Trim();
            if (head.StartsWith("#!", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Stage '{stage.NickName}' ({stage.BlockId}) is not a C# stage (directive '{head}'); " +
                    "v1 consolidation merges C# stages only.");
            }
            if (head.StartsWith("// #!", StringComparison.Ordinal))
            {
                if (!string.Equals(head, CSharpDirective, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Stage '{stage.NickName}' ({stage.BlockId}) carries an incompatible language " +
                        $"directive '{head}'.");
                }
                lines.RemoveAt(firstContent);
            }
        }
        text = string.Join("\n", lines);
        if (ContainsMarkerLine(text))
        {
            throw new InvalidOperationException(
                $"Stage '{stage.NickName}' ({stage.BlockId}) already contains vino stage/seam markers - " +
                "a merged component cannot be a stage; split it first.");
        }

        var root = TryParseStatements(text)
            ?? throw new InvalidOperationException(
                $"Stage '{stage.NickName}' ({stage.BlockId}) does not parse as C# script-mode " +
                "statements; fix and re-execute it before consolidating.");
        var typeDeclaration = root.Members.FirstOrDefault(member => member is BaseTypeDeclarationSyntax
            or DelegateDeclarationSyntax);
        if (typeDeclaration is not null)
        {
            throw new InvalidOperationException(
                $"Stage '{stage.NickName}' ({stage.BlockId}) declares a top-level type " +
                $"('{typeDeclaration.ToString().Split('\n')[0].Trim()}'), which script-mode statement " +
                "order cannot survive a merge - convert it to local functions or leave this stage " +
                "unmerged.");
        }

        var usings = root.Usings.Select(directive => directive.ToString().Trim()).ToArray();
        var bodyRoot = root.WithUsings(SyntaxFactory.List<UsingDirectiveSyntax>());
        // Re-parse the using-stripped text so spans/trivia are self-consistent for renaming.
        var bodyText = TrimOuterBlankLines(RemoveUsings(text, root));
        bodyRoot = TryParseStatements(bodyText)
            ?? throw new InvalidOperationException(
                $"Stage '{stage.NickName}' ({stage.BlockId}): removing its using directives broke the " +
                "parse - keep using directives on their own lines.");

        return new ParsedStage(
            bodyRoot,
            bodyText,
            usings,
            CollectTopLevelDeclaredNames(bodyRoot),
            CollectMemberPositionNames(bodyRoot),
            bodyRoot.DescendantTokens()
                .Where(token => token.IsKind(SyntaxKind.IdentifierToken))
                .Select(token => token.ValueText)
                .ToHashSet(StringComparer.Ordinal));
    }

    private static CompilationUnitSyntax? TryParseStatements(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Regular));
        return tree.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error)
            ? null
            : (CompilationUnitSyntax)tree.GetRoot();
    }

    private static string RemoveUsings(string text, CompilationUnitSyntax root)
    {
        if (root.Usings.Count == 0)
        {
            return text;
        }
        var removals = root.Usings
            .Select(directive => directive.GetLocation().GetLineSpan())
            .SelectMany(span => Enumerable.Range(
                span.StartLinePosition.Line,
                span.EndLinePosition.Line - span.StartLinePosition.Line + 1))
            .ToHashSet();
        var lines = text.Split('\n');
        foreach (var index in removals)
        {
            var remainder = lines[index];
            var stripped = remainder.Trim();
            if (!stripped.StartsWith("using", StringComparison.Ordinal) || !stripped.EndsWith(";", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A using directive shares a line with other code; keep using directives on " +
                    "their own lines.");
            }
        }
        return string.Join("\n", lines.Where((_, index) => !removals.Contains(index)));
    }

    /// <summary>
    /// Names declared at the merged file's top-level scope by this compilation unit: local
    /// declaration variables, deconstruction/out-var designations, and local functions that sit in
    /// global statements — excluding anything nested inside a lambda, anonymous method, or local
    /// function body (those have their own scope).
    /// </summary>
    private static IReadOnlyCollection<string> CollectTopLevelDeclaredNames(CompilationUnitSyntax root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var global in root.Members.OfType<GlobalStatementSyntax>())
        {
            CollectDeclaredNames(global.Statement, names);
        }
        return names;
    }

    private static void CollectDeclaredNames(SyntaxNode node, HashSet<string> names)
    {
        switch (node)
        {
            case LocalFunctionStatementSyntax localFunction:
                names.Add(localFunction.Identifier.ValueText);
                return; // its body is a nested scope
            case AnonymousFunctionExpressionSyntax:
                return; // nested scope
            case VariableDeclaratorSyntax declarator:
                names.Add(declarator.Identifier.ValueText);
                break;
            case SingleVariableDesignationSyntax designation:
                names.Add(designation.Identifier.ValueText);
                break;
        }
        foreach (var child in node.ChildNodes())
        {
            CollectDeclaredNames(child, names);
        }
    }

    /// <summary>Identifiers this unit uses in member positions (obj.Name, ?.Name, Name: argument,
    /// Name = initializer, qualified.Name) — renaming those would change meaning, so a collision on
    /// such a name refuses the merge instead.</summary>
    private static IReadOnlyCollection<string> CollectMemberPositionNames(CompilationUnitSyntax root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identifier in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (IsMemberPosition(identifier))
            {
                names.Add(identifier.Identifier.ValueText);
            }
        }
        return names;
    }

    private static bool IsMemberPosition(SimpleNameSyntax name) => name.Parent switch
    {
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name == name,
        MemberBindingExpressionSyntax => true,
        QualifiedNameSyntax qualified => qualified.Right == name,
        AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name == name,
        _ => name.Parent is NameColonSyntax or NameEqualsSyntax or AttributeSyntax,
    };

    private static CompilationUnitSyntax RenameIdentifiers(
        CompilationUnitSyntax root,
        IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0)
        {
            return root;
        }
        var tokens = root.DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.IdentifierToken) &&
                renames.ContainsKey(token.ValueText) &&
                !IsMemberPositionToken(token))
            .ToArray();
        return root.ReplaceTokens(tokens, (token, _) =>
            SyntaxFactory.Identifier(
                token.LeadingTrivia,
                renames[token.ValueText],
                token.TrailingTrivia));
    }

    /// <summary>
    /// Promotes each undeclared output's FIRST top-level assignment ('pts = expr;') into a
    /// declaration ('var pts = expr;') so the name survives losing its socket. Refuses when the
    /// name's first top-level appearance is not such an assignment (read-before-assign, branch-only
    /// assignment) or the right side references the name itself — a wrong promotion would change
    /// meaning, and the model can always declare the variable explicitly instead.
    /// </summary>
    private static CompilationUnitSyntax PromoteOutputAssignments(
        CompilationUnitSyntax root,
        IReadOnlyList<string> outputNames,
        StageSpec stage)
    {
        foreach (var name in outputNames)
        {
            var first = root.Members.OfType<GlobalStatementSyntax>()
                .FirstOrDefault(global => global.DescendantTokens().Any(token =>
                    token.IsKind(SyntaxKind.IdentifierToken) &&
                    string.Equals(token.ValueText, name, StringComparison.Ordinal)));
            if (first?.Statement is not ExpressionStatementSyntax expression ||
                expression.Expression is not AssignmentExpressionSyntax assignment ||
                !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                assignment.Left is not IdentifierNameSyntax left ||
                !string.Equals(left.Identifier.ValueText, name, StringComparison.Ordinal) ||
                assignment.Right.DescendantTokens().Any(token =>
                    token.IsKind(SyntaxKind.IdentifierToken) &&
                    string.Equals(token.ValueText, name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Stage '{stage.NickName}' ({stage.BlockId}) assigns output '{name}' in a form the " +
                    "merger cannot promote to a local (its first top-level use must be a plain " +
                    $"'{name} = ...;' assignment). Restructure it or declare it explicitly " +
                    $"('var {name} = ...;'), re-execute the stage, then consolidate.");
            }
            var promoted = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.IdentifierName(
                            SyntaxFactory.Identifier(default, "var", SyntaxFactory.TriviaList(SyntaxFactory.Space))),
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(
                                left.Identifier.WithLeadingTrivia().WithTrailingTrivia(SyntaxFactory.Space),
                                null,
                                SyntaxFactory.EqualsValueClause(
                                    assignment.OperatorToken,
                                    assignment.Right)))))
                .WithSemicolonToken(expression.SemicolonToken)
                .WithLeadingTrivia(expression.GetLeadingTrivia())
                .WithTrailingTrivia(expression.GetTrailingTrivia());
            root = root.ReplaceNode(expression, promoted);
        }
        return root;
    }

    /// <summary>
    /// The split-side inverse of promotion: any top-level declaration of one of the stage's OWN
    /// output names becomes a plain assignment — recreated as a component, those names are
    /// framework-declared sockets again and a local declaration would collide.
    /// </summary>
    private static string DemoteOutputDeclarations(string blockText, IReadOnlyList<string> outputNames)
    {
        var root = TryParseStatements(blockText);
        if (root is null)
        {
            return blockText;
        }
        var changed = false;
        foreach (var name in outputNames)
        {
            var declaration = root.Members.OfType<GlobalStatementSyntax>()
                .Select(global => global.Statement)
                .OfType<LocalDeclarationStatementSyntax>()
                .FirstOrDefault(statement =>
                    statement.Declaration.Variables.Count == 1 &&
                    string.Equals(
                        statement.Declaration.Variables[0].Identifier.ValueText,
                        name,
                        StringComparison.Ordinal) &&
                    statement.Declaration.Variables[0].Initializer is not null);
            if (declaration is null)
            {
                continue;
            }
            var declarator = declaration.Declaration.Variables[0];
            var demoted = SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(
                            declarator.Identifier.WithLeadingTrivia().WithTrailingTrivia(SyntaxFactory.Space)),
                        declarator.Initializer!.EqualsToken,
                        declarator.Initializer.Value))
                .WithSemicolonToken(declaration.SemicolonToken)
                .WithLeadingTrivia(declaration.GetLeadingTrivia())
                .WithTrailingTrivia(declaration.GetTrailingTrivia());
            root = root.ReplaceNode(declaration, demoted);
            changed = true;
        }
        return changed ? root.ToFullString() : blockText;
    }

    private static bool IsMemberPositionToken(SyntaxToken token) =>
        token.Parent is SimpleNameSyntax name && IsMemberPosition(name);

    private static string FreshName(string name, string blockId, Func<string, bool> taken)
    {
        var candidate = FormattableString.Invariant($"{name}_{blockId}");
        var suffix = 2;
        while (taken(candidate))
        {
            candidate = FormattableString.Invariant($"{name}_{blockId}_{suffix}");
            suffix++;
        }
        return candidate;
    }

    private static void ValidateComposedSource(string source)
    {
        var root = TryParseStatements(source)
            ?? throw new InvalidOperationException(
                "The merged source does not parse - this is a merge defect, not a stage defect; " +
                "report it. The stage chain is untouched.");
        var declared = new List<string>();
        foreach (var global in root.Members.OfType<GlobalStatementSyntax>())
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            CollectDeclaredNames(global.Statement, names);
            declared.AddRange(names);
        }
        var duplicate = declared
            .GroupBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"The merged source declares '{duplicate.Key}' more than once at the top level - " +
                "this is a merge defect, not a stage defect; report it. The stage chain is untouched.");
        }
    }

    private static string? MarkerId(string trimmedLine, string prefix)
    {
        var end = trimmedLine.IndexOf('>', prefix.Length);
        return end < 0 ? null : trimmedLine[prefix.Length..end];
    }

    private static bool ContainsMarkerLine(string text) =>
        text.Contains(StageBeginPrefix, StringComparison.Ordinal) ||
        text.Contains(StageEndPrefix, StringComparison.Ordinal) ||
        text.Contains(SeamMarkerPrefix, StringComparison.Ordinal) ||
        text.Contains(MetaMarkerPrefix.TrimEnd(), StringComparison.Ordinal);

    private static string TrimOuterBlankLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0)
        {
            lines.RemoveAt(0);
        }
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return string.Join("\n", lines);
    }

    private static string Truncate(string text) =>
        text.Length <= 80 ? text : text[..77] + "...";
}
