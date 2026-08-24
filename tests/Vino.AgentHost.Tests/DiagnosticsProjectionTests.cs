using System.Text.Json;
using Vino.AgentHost.Runtime;
using Vino.BridgeContract;

namespace Vino.AgentHost.Tests;

public class DiagnosticsProjectionTests
{
    private static LiveDocumentBackend.JobDiagnostic Diagnostic(
        BridgeDiagnosticSeverity severity, string code, string op = "op-1") =>
        new(op, severity, code, $"{code} message");

    private static JsonElement Project(object value) =>
        JsonSerializer.SerializeToElement(value);

    [Fact]
    public void UnderTheCapEverythingSurvivesUntrimmed()
    {
        var source = Enumerable.Range(0, LiveDocumentBackend.ProjectedDiagnosticsCap)
            .Select(index => Diagnostic(BridgeDiagnosticSeverity.Information, $"code-{index}"))
            .ToArray();

        var (items, omitted) = LiveDocumentBackend.ProjectDiagnostics(source);

        Assert.NotNull(items);
        Assert.Equal(source.Length, items!.Length);
        Assert.Null(omitted);
    }

    [Fact]
    public void OverTheCapErrorsSurviveAndTheDropIsSummarized()
    {
        // 120 information rows drowning 3 errors and 10 warnings — the flood scenario the cap is
        // for. Errors and warnings must all survive; only information rows may be trimmed.
        var source = new List<LiveDocumentBackend.JobDiagnostic>();
        source.AddRange(Enumerable.Range(0, 60).Select(i => Diagnostic(BridgeDiagnosticSeverity.Information, $"info-a{i}")));
        source.AddRange(Enumerable.Range(0, 3).Select(i => Diagnostic(BridgeDiagnosticSeverity.Error, $"error-{i}")));
        source.AddRange(Enumerable.Range(0, 10).Select(i => Diagnostic(BridgeDiagnosticSeverity.Warning, $"warn-{i}")));
        source.AddRange(Enumerable.Range(0, 60).Select(i => Diagnostic(BridgeDiagnosticSeverity.Information, $"info-b{i}")));

        var (items, omitted) = LiveDocumentBackend.ProjectDiagnostics(source);

        Assert.Equal(LiveDocumentBackend.ProjectedDiagnosticsCap, items!.Length);
        var projected = items.Select(Project).ToArray();
        Assert.Equal(3, projected.Count(item => item.GetProperty("severity").GetString() == "error"));
        Assert.Equal(10, projected.Count(item => item.GetProperty("severity").GetString() == "warning"));
        var summary = Project(omitted!);
        Assert.Equal(0, summary.GetProperty("errors").GetInt32());
        Assert.Equal(0, summary.GetProperty("warnings").GetInt32());
        Assert.Equal(120 - (LiveDocumentBackend.ProjectedDiagnosticsCap - 13), summary.GetProperty("information").GetInt32());
    }

    [Fact]
    public void KeptRowsStayInSubmissionOrder()
    {
        var source = new List<LiveDocumentBackend.JobDiagnostic>
        {
            Diagnostic(BridgeDiagnosticSeverity.Information, "first-info")
        };
        source.AddRange(Enumerable.Range(0, 60).Select(i => Diagnostic(BridgeDiagnosticSeverity.Warning, $"warn-{i}")));
        source.Add(Diagnostic(BridgeDiagnosticSeverity.Error, "last-error"));

        var (items, _) = LiveDocumentBackend.ProjectDiagnostics(source);

        // The error sits last in submission order and must stay last even though severity
        // admission considered it first.
        var projected = items!.Select(Project).ToArray();
        Assert.Equal("last-error", projected[^1].GetProperty("code").GetString());
        var warnCodes = projected
            .Where(item => item.GetProperty("severity").GetString() == "warning")
            .Select(item => item.GetProperty("code").GetString())
            .ToArray();
        Assert.Equal(warnCodes.OrderBy(code => int.Parse(code!.Split('-')[1])).ToArray(), warnCodes);
    }

    [Fact]
    public void DuplicateRowsAreCountedByPositionNotEquality()
    {
        // 80 value-identical warnings: a per-item warning repeated across a data tree. Record
        // equality must not collapse them — exactly cap rows kept, the rest counted as dropped.
        var source = Enumerable.Range(0, 80)
            .Select(_ => Diagnostic(BridgeDiagnosticSeverity.Warning, "same-warning"))
            .ToArray();

        var (items, omitted) = LiveDocumentBackend.ProjectDiagnostics(source);

        Assert.Equal(LiveDocumentBackend.ProjectedDiagnosticsCap, items!.Length);
        var summary = Project(omitted!);
        Assert.Equal(80 - LiveDocumentBackend.ProjectedDiagnosticsCap, summary.GetProperty("warnings").GetInt32());
    }
}
