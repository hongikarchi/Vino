using System.Text.Json;
using Vino.AgentHost.Runtime;
using Vino.BridgeContract;
using Vino.CanvasSceneAdapter;

namespace Vino.AgentHost.Tests;

/// <summary>
/// W3 contract surfaces that live outside the merger: the replaceBlock payload record must accept
/// exactly the documented shape under the bridge's Disallow-unmapped options (a shape drift would
/// die as an opaque JsonException mid-job), and the consolidation equivalence comparer must judge
/// field-wise with numeric tolerance — never via the inspection fingerprint, which folds in
/// ParameterId and can never match across two components.
/// </summary>
public sealed class ConsolidationContractTests
{
    [Fact]
    public void ReplaceBlockPayloadDeserializesDocumentedShape()
    {
        const string payload = """
            {
              "operationId": "edit-1",
              "componentId": "0e984a8c-9c3b-4a53-9e07-0a6f72d3f001",
              "expectedSourceSha256": "gptino:auto",
              "blockId": "s2",
              "source": "var b = points * 3;\n",
              "expireSolution": true
            }
            """;

        var request = JsonSerializer.Deserialize<ReplaceSourceBlockRequest>(payload, BridgeProtocol.JsonOptions);

        Assert.NotNull(request);
        Assert.Equal("edit-1", request!.OperationId);
        Assert.Equal("s2", request.BlockId);
        Assert.True(request.ExpireSolution);
    }

    [Fact]
    public void ReplaceBlockPayloadRejectsUnknownMembers()
    {
        const string payload = """
            {
              "operationId": "edit-1",
              "componentId": "0e984a8c-9c3b-4a53-9e07-0a6f72d3f001",
              "expectedSourceSha256": "gptino:auto",
              "blockId": "s2",
              "source": "var b = 1;\n",
              "expireSolution": false,
              "runtime": "csharp"
            }
            """;

        Assert.ThrowsAny<JsonException>(
            () => JsonSerializer.Deserialize<ReplaceSourceBlockRequest>(payload, BridgeProtocol.JsonOptions));
    }

    private static CanvasOutputParameterInspection Output(
        string name,
        int dataCount = 3,
        string type = "Grasshopper.Kernel.Types.GH_Number",
        CanvasBoundingBox3d? bounds = null,
        string[]? samples = null,
        int branchCount = 1,
        bool? closed = null) =>
        new(
            Guid.NewGuid(),
            name,
            name,
            dataCount,
            new[] { type },
            bounds,
            samples ?? new[] { "1.0", "2.0", "3.0" },
            branchCount,
            closed);

    [Fact]
    public void IdenticalOutputsMatchAcrossDifferentParameterIds()
    {
        var diffs = new List<string>();

        LiveDocumentBackend.CompareOutputPair("area", Output("area"), Output("area"), diffs);

        Assert.Empty(diffs);
    }

    [Fact]
    public void CountTypeAndBranchDifferencesAreReported()
    {
        var diffs = new List<string>();

        LiveDocumentBackend.CompareOutputPair(
            "area",
            Output("area", dataCount: 4, branchCount: 2, type: "Grasshopper.Kernel.Types.GH_Integer"),
            Output("area"),
            diffs);

        Assert.Contains(diffs, diff => diff.Contains("dataCount", StringComparison.Ordinal));
        Assert.Contains(diffs, diff => diff.Contains("branchCount", StringComparison.Ordinal));
        Assert.Contains(diffs, diff => diff.Contains("types", StringComparison.Ordinal));
    }

    [Fact]
    public void NumericSamplesCompareWithToleranceNotBytes()
    {
        var diffs = new List<string>();

        LiveDocumentBackend.CompareOutputPair(
            "area",
            Output("area", samples: new[] { "1.0000000001", "2", "3.00" }),
            Output("area", samples: new[] { "1.0", "2.0", "3" }),
            diffs);

        Assert.Empty(diffs);
        Assert.False(LiveDocumentBackend.SampleValuesEquivalent("1.0", "1.5"));
        Assert.True(LiveDocumentBackend.SampleValuesEquivalent("abc", "abc"));
        Assert.False(LiveDocumentBackend.SampleValuesEquivalent("abc", "abd"));
    }

    [Fact]
    public void BoundsBeyondToleranceAreReported()
    {
        var diffs = new List<string>();
        var left = new CanvasBoundingBox3d(
            new CanvasPoint3d(0, 0, 0), new CanvasPoint3d(10, 10, 10), new CanvasPoint3d(10, 10, 10));
        var right = new CanvasBoundingBox3d(
            new CanvasPoint3d(0, 0, 0), new CanvasPoint3d(10, 10, 10.01), new CanvasPoint3d(10, 10, 10.01));

        LiveDocumentBackend.CompareOutputPair(
            "panels", Output("panels", bounds: left), Output("panels", bounds: right), diffs);

        Assert.Contains(diffs, diff => diff.Contains("bounds", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosednessDifferenceIsReported()
    {
        var diffs = new List<string>();

        LiveDocumentBackend.CompareOutputPair(
            "panels",
            Output("panels", closed: true),
            Output("panels", closed: false),
            diffs);

        Assert.Contains(diffs, diff => diff.Contains("closed", StringComparison.Ordinal));
    }
}
