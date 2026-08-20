using System.Text.Json;
using Vino.AgentHost.Data;
using Vino.AgentHost.Runtime;
using Vino.CanvasSceneAdapter;

namespace Vino.AgentHost.Tests;

/// <summary>
/// W2 measurement-driven predicted-solve gate: the durable measurement store round-trips, the
/// inspection parser reads real canvas.inspectOutputs payload shapes, the input-volume estimator
/// sums table-known upstream counts, and the gate decision scales the measured solve by the
/// volume ratio (falling back to the raw measurement when no scaling basis exists).
/// </summary>
public sealed class ComponentMeasurementTests
{
    [Fact]
    public async Task StoreRoundTripsMeasurementsPerDocument()
    {
        using var directory = new TestDirectory();
        var store = new ComponentMeasurementStore(Path.Combine(directory.Path, "measurements.db"));
        await store.InitializeAsync();
        var componentId = Guid.NewGuid();
        var outputId = Guid.NewGuid();
        await store.UpsertAsync("DocA", new[]
        {
            new ComponentMeasurementRecord(
                componentId,
                SolveMilliseconds: 1_850,
                InputItems: 240,
                new Dictionary<Guid, long> { [outputId] = 1_200 },
                Revision: 7,
                DateTimeOffset.UtcNow),
            new ComponentMeasurementRecord(
                Guid.NewGuid(),
                SolveMilliseconds: null,
                InputItems: null,
                new Dictionary<Guid, long>(),
                Revision: 7,
                DateTimeOffset.UtcNow),
        });

        var records = await store.ReadDocumentAsync("doca");

        Assert.Equal(2, records.Count);
        var measured = Assert.Single(records, record => record.ComponentId == componentId);
        Assert.Equal(1_850, measured.SolveMilliseconds);
        Assert.Equal(240, measured.InputItems);
        Assert.Equal(1_200, measured.OutputCounts[outputId]);
        Assert.Null(records.Single(record => record.ComponentId != componentId).SolveMilliseconds);

        // Upsert replaces in place (same key) and other documents stay isolated.
        await store.UpsertAsync("docA", new[]
        {
            new ComponentMeasurementRecord(
                componentId, 3_000, 480, new Dictionary<Guid, long>(), 9, DateTimeOffset.UtcNow),
        });
        Assert.Equal(3_000, (await store.ReadDocumentAsync("doca"))
            .Single(record => record.ComponentId == componentId).SolveMilliseconds);
        Assert.Empty(await store.ReadDocumentAsync("other"));
    }

    [Fact]
    public async Task StoreFollowsDocKeyRemap()
    {
        using var directory = new TestDirectory();
        var store = new ComponentMeasurementStore(Path.Combine(directory.Path, "measurements.db"));
        await store.InitializeAsync();
        var componentId = Guid.NewGuid();
        await store.UpsertAsync("oldkey", new[]
        {
            new ComponentMeasurementRecord(
                componentId, 500, 10, new Dictionary<Guid, long>(), 1, DateTimeOffset.UtcNow),
        });

        Assert.Equal(1, await store.RemapDocKeyAsync("OLDKEY", "newkey"));

        Assert.Empty(await store.ReadDocumentAsync("oldkey"));
        Assert.Equal(componentId, (await store.ReadDocumentAsync("newkey")).Single().ComponentId);
    }

    [Fact]
    public void ParsesInspectionOutputCounts()
    {
        var parameterId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        using var document = JsonDocument.Parse($$"""
            {
              "grasshopperDocumentId": "{{Guid.NewGuid()}}",
              "objectId": "{{Guid.NewGuid()}}",
              "outputs": [
                { "parameterId": "{{parameterId}}", "name": "a", "dataCount": 1200, "sampleValues": [] },
                { "parameterId": "{{otherId}}", "name": "out", "dataCount": 0 },
                { "name": "malformed-no-id", "dataCount": 5 }
              ],
              "fingerprint": "abc"
            }
            """);

        var counts = LiveDocumentBackend.ParseInspectionOutputCounts(document.RootElement);

        Assert.Equal(2, counts.Count);
        Assert.Equal(1_200, counts[parameterId]);
        Assert.Equal(0, counts[otherId]);
    }

    [Fact]
    public void EstimatesInputItemsFromTableKnownSources()
    {
        var componentId = Guid.NewGuid();
        var knownSource = Guid.NewGuid();
        var unknownSource = Guid.NewGuid();
        var canvas = new CanvasSnapshot(
            Guid.NewGuid(),
            "doc-fingerprint",
            new[]
            {
                new CanvasObjectState(
                    componentId, Guid.NewGuid(), "Stage", new CanvasPoint(0, 0),
                    new CanvasSize(10, 10), "fp")
                {
                    Inputs = new[]
                    {
                        new CanvasParameterState(
                            componentId, Guid.NewGuid(), "geo", "geo",
                            CanvasParameterDirection.Input, "Generic", null,
                            CanvasParameterAccess.List, false,
                            new[]
                            {
                                new CanvasParameterEndpoint(Guid.NewGuid(), knownSource),
                                new CanvasParameterEndpoint(Guid.NewGuid(), unknownSource),
                            }),
                    },
                },
            },
            Array.Empty<WireState>(),
            Array.Empty<GroupState>());

        var estimate = LiveDocumentBackend.EstimateComponentInputItems(
            canvas, componentId, id => id == knownSource ? 850 : null);

        Assert.Equal(850, estimate.Total);
        Assert.Equal(1, estimate.KnownSources);
        Assert.Equal(2, estimate.TotalSources);
        var missing = LiveDocumentBackend.EstimateComponentInputItems(canvas, Guid.NewGuid(), _ => 1);
        Assert.Equal(0, missing.Total);
        Assert.Equal(0, missing.TotalSources);
    }

    [Theory]
    // Scaled: 2s measured on 100 items -> 15,000 current items predicts 300s: block.
    [InlineData(2_000, 100L, 15_000, 1, true)]
    // Scaled: 2s on 100 -> 500 predicts 10s: allowed.
    [InlineData(2_000, 100L, 500, 1, false)]
    // No current measured sources: prediction falls back to the raw last solve (21s): block.
    [InlineData(21_000, 100L, 0, 0, true)]
    // No last-volume basis: raw last solve under the ceiling: allowed.
    [InlineData(19_000, null, 9_999, 3, false)]
    public void GateDecisionScalesByVolumeRatio(
        long solveMs, long? lastItems, long currentItems, int knownSources, bool expectBlocked)
    {
        var blocked = LiveDocumentBackend.ShouldBlockPredictedSolve(
            solveMs, lastItems, currentItems, knownSources, 20_000, out var predicted);

        Assert.Equal(expectBlocked, blocked);
        Assert.True(predicted > 0);
    }
}
