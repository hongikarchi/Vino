using Vino.AgentHost.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Vino.AgentHost.Tests;

/// <summary>
/// The server-injected C# solve watchdog. Two invariants carry the whole design: the injected
/// output must remain parseable C# (a guard that breaks compilation is worse than no guard), and
/// Strip(Inject(s)) == s byte-exactly (the model owns its text — every model-facing read returns
/// exactly what it wrote).
/// </summary>
public sealed class CSharpWatchdogInjectorTests
{
    private const int Budget = 30_000;

    [Fact]
    public void ForLoopGainsPrologueAndLoopCheck()
    {
        var source = "using Rhino.Geometry;\nvar total = 0.0;\nfor (var i = 0; i < 100; i++)\n{\n    total += i;\n}\na = total;\n";

        var injected = CSharpWatchdogInjector.Inject(source, Budget);

        Assert.Contains("__vino_sw = System.Diagnostics.Stopwatch.StartNew()", injected, StringComparison.Ordinal);
        Assert.Contains("__vino_i", injected, StringComparison.Ordinal);
        Assert.Contains("System.TimeoutException", injected, StringComparison.Ordinal);
        Assert.Contains("auto-injected solve watchdog", injected, StringComparison.Ordinal);
        // The check sits INSIDE the loop body, after the opening brace.
        var bodyStart = injected.IndexOf("i++)", StringComparison.Ordinal);
        var check = injected.IndexOf("ElapsedMilliseconds", bodyStart, StringComparison.Ordinal);
        var bodyEnd = injected.IndexOf("total += i", bodyStart, StringComparison.Ordinal);
        Assert.True(check > bodyStart && check < bodyEnd);
        AssertParses(injected);
    }

    [Theory]
    [InlineData("var x = 1;\nwhile (x < 10)\n{\n    x++;\n}\n")]
    [InlineData("var x = 1;\nwhile (x < 10) x++;\n")]
    [InlineData("var t = 0;\ndo\n{\n    t++;\n}\nwhile (t < 5);\n")]
    [InlineData("using System.Linq;\nvar items = new[] { 1, 2, 3 };\nvar total = 0;\nforeach (var item in items) total += item;\n")]
    [InlineData("for (var i = 0; i < 3; i++)\n    for (var j = 0; j < 3; j++)\n        System.Console.WriteLine(i * j);\n")]
    [InlineData("double Area(double r)\n{\n    var s = 0.0;\n    for (var i = 0; i < 10; i++) s += r;\n    return s;\n}\na = Area(2.0);\n")]
    [InlineData("var worker = () =>\n{\n    for (var i = 0; i < 5; i++) System.Console.WriteLine(i);\n};\nworker();\n")]
    [InlineData("// #! csharp\nusing Rhino.Geometry;\nfor (var i = 0; i < 4; i++)\n{\n    var p = new Point3d(i, 0, 0);\n}\n")]
    public void StripRoundTripsByteExact(string source)
    {
        var injected = CSharpWatchdogInjector.Inject(source, Budget);

        Assert.NotEqual(source, injected);
        Assert.Equal(source, CSharpWatchdogInjector.Strip(injected));
        AssertParses(injected);
    }

    [Fact]
    public void InjectIsIdempotent()
    {
        var source = "for (var i = 0; i < 100; i++)\n{\n    System.Console.WriteLine(i);\n}\n";

        var once = CSharpWatchdogInjector.Inject(source, Budget);
        var twice = CSharpWatchdogInjector.Inject(once, Budget);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void StraightLineScriptIsUntouched()
    {
        var source = "using Rhino.Geometry;\nvar p = new Point3d(1, 2, 3);\na = p;\n";

        Assert.Equal(source, CSharpWatchdogInjector.Inject(source, Budget));
    }

    [Fact]
    public void SingleStatementLoopBodyIsWrappedAndUnwrapsExactly()
    {
        var source = "var x = 0;\nwhile (x < 1000) x += 1;\n";

        var injected = CSharpWatchdogInjector.Inject(source, Budget);

        Assert.Contains("gptino:guard-braced", injected, StringComparison.Ordinal);
        Assert.Equal(source, CSharpWatchdogInjector.Strip(injected));
        AssertParses(injected);
    }

    [Fact]
    public void TypeDeclarationBodiesAreSkipped()
    {
        // The prologue locals are not in scope inside a type, so its loops must NOT be
        // instrumented (a check there would not compile). The top-level loop still is.
        var source =
            "for (var i = 0; i < 10; i++)\n{\n    System.Console.WriteLine(i);\n}\n" +
            "class Helper\n{\n    public static int Sum(int n)\n    {\n        var s = 0;\n        for (var i = 0; i < n; i++) s += i;\n        return s;\n    }\n}\n";

        var injected = CSharpWatchdogInjector.Inject(source, Budget);

        var helper = injected.IndexOf("class Helper", StringComparison.Ordinal);
        Assert.True(helper > 0);
        Assert.DoesNotContain("__vino_", injected[helper..], StringComparison.Ordinal);
        Assert.Contains("__vino_", injected[..helper], StringComparison.Ordinal);
        AssertParses(injected);
    }

    [Fact]
    public void UnparseableSourceIsReturnedUnchanged()
    {
        var source = "for (var i = 0; i < ; i++) {\n";

        Assert.Equal(source, CSharpWatchdogInjector.Inject(source, Budget));
    }

    [Fact]
    public void GuardIdentifierCollisionSkipsInjection()
    {
        var source = "var __vino_sw = 1;\nfor (var i = 0; i < 10; i++)\n{\n    System.Console.WriteLine(__vino_sw);\n}\n";

        Assert.Equal(source, CSharpWatchdogInjector.Inject(source, Budget));
    }

    [Fact]
    public void ExpressionLambdaIsUntouchedButStatementLambdaIsGuarded()
    {
        var source =
            "using System.Linq;\n" +
            "var items = new[] { 1, 2, 3 };\n" +
            "var doubled = items.Select(item => item * 2).ToArray();\n" +
            "System.Threading.Tasks.Parallel.For(0, 10, index =>\n{\n    System.Console.WriteLine(index);\n});\n";

        var injected = CSharpWatchdogInjector.Inject(source, Budget);

        // Expression lambda body carries no check; the statement lambda's block does.
        var expressionLambda = injected.IndexOf("item * 2", StringComparison.Ordinal);
        Assert.True(expressionLambda > 0);
        Assert.DoesNotContain("__vino_", injected.Substring(expressionLambda, 20), StringComparison.Ordinal);
        var parallelBody = injected.IndexOf("index =>", StringComparison.Ordinal);
        Assert.Contains("ElapsedMilliseconds", injected[parallelBody..], StringComparison.Ordinal);
        Assert.Equal(source, CSharpWatchdogInjector.Strip(injected));
        AssertParses(injected);
    }

    [Fact]
    public void BudgetLiteralAppearsInCheckAndMessage()
    {
        var source = "for (var i = 0; i < 10; i++)\n{\n    System.Console.WriteLine(i);\n}\n";

        var injected = CSharpWatchdogInjector.Inject(source, 12_345);

        Assert.Contains("> 12345L", injected, StringComparison.Ordinal);
        Assert.Contains("(12345 ms) exceeded", injected, StringComparison.Ordinal);
    }

    [Fact]
    public void PythonSourcePassesStripUntouched()
    {
        // Strip is called on every script inspection regardless of runtime; the marker fast-path
        // must make that safe for Python text (which a C# parse would mangle if it ever ran).
        var source = "import rhinoscriptsyntax as rs\nfor i in range(10):\n    print(i)\n";

        Assert.Same(source, CSharpWatchdogInjector.Strip(source));
    }

    [Fact]
    public void LeadingHeaderCommentStaysAboveTheGuardAndRoundTrips()
    {
        var source = "// #! csharp\n// stage 2: panelize\nfor (var i = 0; i < 10; i++)\n{\n    System.Console.WriteLine(i);\n}\n";

        var injected = CSharpWatchdogInjector.Inject(source, Budget);

        // The user's header comments stay ABOVE the injected guard block.
        Assert.True(
            injected.IndexOf("stage 2: panelize", StringComparison.Ordinal) <
            injected.IndexOf("__vino_sw", StringComparison.Ordinal));
        Assert.Equal(source, CSharpWatchdogInjector.Strip(injected));
    }

    private static void AssertParses(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        Assert.DoesNotContain(
            tree.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
