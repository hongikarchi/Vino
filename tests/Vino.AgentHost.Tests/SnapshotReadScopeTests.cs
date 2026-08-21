using System.Text.Json;
using Vino.BridgeContract;

namespace Vino.AgentHost.Tests;

/// <summary>
/// The snapshot_read scope contract (v3): the default is a cheap meta orientation read, discovery
/// goes through "index", detail through components:&lt;ids&gt;, topology through "wires"/"groups",
/// and the back-compat "canvas" dump — like every heavy list — is byte-capped with explicit
/// continuation instead of silent cuts. The full-document-by-default response was the read path's
/// only uncapped surface (~2.2KB per component; ~1.4MB for a 500-component document).
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class SnapshotReadScopeTests
{
    private const int ByteCap = 256 * 1024;

    [Fact]
    public async Task DefaultReadReturnsMetaEnvelopeOnly()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        harness.WireFirstTwoObjects = true;
        harness.IncludeGroup = true;
        await using var responder = harness.StartResponder();

        var read = ToElement(await harness.Backend.ReadSnapshotAsync(
            EmptyArguments(),
            CancellationToken.None));

        // Envelope stays intact.
        Assert.False(string.IsNullOrEmpty(read.GetProperty("snapshotId").GetString()));
        Assert.Equal(1, read.GetProperty("revision").GetInt64());
        Assert.False(read.GetProperty("unchanged").GetBoolean());
        Assert.Equal(JsonValueKind.Object, read.GetProperty("target").ValueKind);
        // Meta is the whole body: counts plus group membership summaries.
        var meta = read.GetProperty("meta");
        Assert.Equal(2, meta.GetProperty("componentCount").GetInt32());
        Assert.Equal(1, meta.GetProperty("wireCount").GetInt32());
        Assert.Equal(1, meta.GetProperty("groupCount").GetInt32());
        var group = Assert.Single(meta.GetProperty("groups").EnumerateArray());
        Assert.Equal(harness.GroupId, group.GetProperty("groupId").GetGuid());
        Assert.Equal("Cluster", group.GetProperty("name").GetString());
        Assert.Equal(1, group.GetProperty("memberCount").GetInt32());
        // No other body — and the per-domain resources list is gone from the response entirely.
        Assert.False(read.TryGetProperty("canvas", out _));
        Assert.False(read.TryGetProperty("resources", out _));
        Assert.False(read.TryGetProperty("index", out _));
        Assert.False(read.TryGetProperty("components", out _));
        Assert.False(read.TryGetProperty("wires", out _));
        Assert.False(read.TryGetProperty("groups", out _));
        Assert.False(read.TryGetProperty("truncated", out _));

        // The explicit "meta" scope is the same read as the default.
        var explicitMeta = ToElement(await harness.Backend.ReadSnapshotAsync(
            Arguments("meta"),
            CancellationToken.None));
        Assert.Equal(2, explicitMeta.GetProperty("meta").GetProperty("componentCount").GetInt32());
        Assert.False(explicitMeta.TryGetProperty("canvas", out _));
    }

    [Fact]
    public async Task IndexRowsAreCompactAndCarryGroupMembership()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        harness.IncludeGroup = true;
        await using var responder = harness.StartResponder();

        var read = ToElement(await harness.Backend.ReadSnapshotAsync(
            Arguments("index"),
            CancellationToken.None));

        var rows = read.GetProperty("index").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        var grouped = Assert.Single(
            rows,
            row => row.GetProperty("id").GetGuid() == harness.CanvasObjectId);
        Assert.Equal("Point", grouped.GetProperty("name").GetString());
        Assert.Equal(
            Guid.Parse("29322931-96ae-4d34-874b-a722bc3a0e4a"),
            grouped.GetProperty("typeId").GetGuid());
        Assert.Equal(
            harness.GroupId,
            Assert.Single(grouped.GetProperty("groupIds").EnumerateArray()).GetGuid());
        var ungrouped = Assert.Single(
            rows,
            row => row.GetProperty("id").GetGuid() == harness.SecondCanvasObjectId);
        Assert.Empty(ungrouped.GetProperty("groupIds").EnumerateArray());
        // Compact rows only: no sockets, values, fingerprints, or geometry.
        foreach (var row in rows)
        {
            Assert.False(row.TryGetProperty("fingerprint", out _));
            Assert.False(row.TryGetProperty("inputs", out _));
            Assert.False(row.TryGetProperty("outputs", out _));
            Assert.False(row.TryGetProperty("pivot", out _));
            Assert.False(row.TryGetProperty("bounds", out _));
            Assert.False(row.TryGetProperty("valueJson", out _));
        }
        // Meta is the default body only — a scoped read does not carry it implicitly.
        Assert.False(read.TryGetProperty("meta", out _));
    }

    [Fact]
    public async Task ComponentsScopeServesFullDetailAndReportsMissing()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        await using var responder = harness.StartResponder();
        var unknownId = Guid.Parse("00000000-0000-4000-8000-00000000dead");

        // Combined with another scope on purpose: components: composes with index.
        var read = ToElement(await harness.Backend.ReadSnapshotAsync(
            Arguments("index", $"components:{harness.CanvasObjectId:D},{unknownId:D}"),
            CancellationToken.None));

        var component = Assert.Single(read.GetProperty("components").EnumerateArray());
        Assert.Equal(harness.CanvasObjectId, component.GetProperty("objectId").GetGuid());
        // The full CanvasObjectState projection, exactly as the old canvas dump serialized it.
        Assert.Equal(harness.ObjectFingerprint, component.GetProperty("fingerprint").GetString());
        Assert.Equal(JsonValueKind.Object, component.GetProperty("pivot").ValueKind);
        Assert.Equal(JsonValueKind.Array, component.GetProperty("inputs").ValueKind);
        Assert.False(string.IsNullOrEmpty(component.GetProperty("valueJson").GetString()));
        Assert.Equal(
            unknownId,
            Assert.Single(read.GetProperty("missingComponents").EnumerateArray()).GetGuid());
        Assert.Equal(2, read.GetProperty("index").GetArrayLength());
        Assert.False(read.TryGetProperty("truncated", out _));
    }

    [Fact]
    public async Task UnknownScopeErrorListsValidScopes()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.ReadSnapshotAsync(Arguments("bogus"), CancellationToken.None));

        Assert.Contains("bogus", exception.Message, StringComparison.Ordinal);
        Assert.Contains("meta", exception.Message, StringComparison.Ordinal);
        Assert.Contains("index", exception.Message, StringComparison.Ordinal);
        Assert.Contains("components:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("canvas", exception.Message, StringComparison.Ordinal);
        Assert.Contains("script:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnchangedReadOmitsBodies()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var first = ToElement(await harness.Backend.ReadSnapshotAsync(
            Arguments("canvas"),
            CancellationToken.None));
        var snapshotId = first.GetProperty("snapshotId").GetString()!;
        Assert.True(first.TryGetProperty("canvas", out _));

        var second = ToElement(await harness.Backend.ReadSnapshotAsync(
            JsonSerializer.SerializeToElement(
                new { scopes = new[] { "canvas" }, knownSnapshotId = snapshotId },
                BridgeProtocol.JsonOptions),
            CancellationToken.None));

        Assert.True(second.GetProperty("unchanged").GetBoolean());
        Assert.Equal(snapshotId, second.GetProperty("snapshotId").GetString());
        // Envelope only: the caller already holds this snapshot, so no body is repeated.
        Assert.False(second.TryGetProperty("canvas", out _));
        Assert.False(second.TryGetProperty("meta", out _));
        Assert.False(second.TryGetProperty("index", out _));
        Assert.Equal(JsonValueKind.Array, second.GetProperty("inspections").ValueKind);
    }

    [Fact]
    public async Task CanvasScopeReturnsFullDumpUnderCap()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        harness.WireFirstTwoObjects = true;
        harness.IncludeGroup = true;
        await using var responder = harness.StartResponder();

        var read = ToElement(await harness.Backend.ReadSnapshotAsync(
            Arguments("canvas"),
            CancellationToken.None));

        var canvas = read.GetProperty("canvas");
        Assert.Equal(
            harness.Target.GrasshopperDocumentId!.Value,
            canvas.GetProperty("grasshopperDocumentId").GetGuid());
        Assert.Equal("document-v1", canvas.GetProperty("documentFingerprint").GetString());
        Assert.Equal(2, canvas.GetProperty("objects").GetArrayLength());
        Assert.Equal(1, canvas.GetProperty("wires").GetArrayLength());
        Assert.Equal(1, canvas.GetProperty("groups").GetArrayLength());
        var first = canvas.GetProperty("objects").EnumerateArray().First();
        Assert.Equal(harness.ObjectFingerprint, first.GetProperty("fingerprint").GetString());
        // A document this small is never cut, and resources stay gone even on the full dump.
        Assert.False(read.TryGetProperty("truncated", out _));
        Assert.False(read.TryGetProperty("resources", out _));
    }

    [Fact]
    public async Task WiresAndGroupsScopesReturnTopologyLists()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        harness.WireFirstTwoObjects = true;
        harness.IncludeGroup = true;
        await using var responder = harness.StartResponder();

        var read = ToElement(await harness.Backend.ReadSnapshotAsync(
            Arguments("wires", "groups"),
            CancellationToken.None));

        var wire = Assert.Single(read.GetProperty("wires").EnumerateArray());
        Assert.Equal(harness.CanvasObjectId, wire.GetProperty("sourceObjectId").GetGuid());
        Assert.Equal(harness.SecondCanvasObjectId, wire.GetProperty("targetObjectId").GetGuid());
        var group = Assert.Single(read.GetProperty("groups").EnumerateArray());
        Assert.Equal(harness.GroupId, group.GetProperty("groupId").GetGuid());
        Assert.Equal("Cluster", group.GetProperty("name").GetString());
        Assert.Equal(
            harness.CanvasObjectId,
            Assert.Single(group.GetProperty("objectIds").EnumerateArray()).GetGuid());
        Assert.Equal(unchecked((int)0xFF336699), group.GetProperty("argbColor").GetInt32());
        Assert.False(read.TryGetProperty("canvas", out _));
    }

    [Fact]
    public async Task ByteCapTruncatesCanvasObjectsButKeepsTopologyAndMeta()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.LargeDocumentComponentCount = 300;
        await using var responder = harness.StartResponder();

        var read = ToElement(await harness.Backend.ReadSnapshotAsync(
            Arguments("canvas"),
            CancellationToken.None));

        Assert.True(read.GetProperty("truncated").GetBoolean());
        var nextOffset = read.GetProperty("nextOffset").GetInt32();
        Assert.InRange(nextOffset, 1, 299);
        var canvas = read.GetProperty("canvas");
        // Objects stop exactly at the continuation position; the resume order is the snapshot's
        // own stable object order (same snapshotId, same order).
        Assert.Equal(nextOffset, canvas.GetProperty("objects").GetArrayLength());
        Assert.Equal(
            LiveDocumentBackendHarness.LargeDocumentObjectId(0),
            canvas.GetProperty("objects").EnumerateArray().First().GetProperty("objectId").GetGuid());
        // Topology is never dropped by the cap.
        Assert.Equal(2, canvas.GetProperty("wires").GetArrayLength());
        Assert.Equal(1, canvas.GetProperty("groups").GetArrayLength());
        // The whole response honors the budget (small slack for separators + continuation fields).
        Assert.InRange(read.GetRawText().Length, 1, ByteCap + 4096);

        // The default meta orientation read stays tiny on the very same document.
        var meta = ToElement(await harness.Backend.ReadSnapshotAsync(
            EmptyArguments(),
            CancellationToken.None));
        Assert.Equal(300, meta.GetProperty("meta").GetProperty("componentCount").GetInt32());
        Assert.False(meta.TryGetProperty("truncated", out _));
        Assert.InRange(meta.GetRawText().Length, 1, 8192);
    }

    [Fact]
    public async Task ByteCapTruncatesIndexWithNextOffset()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.LargeDocumentComponentCount = 300;
        await using var responder = harness.StartResponder();

        var read = ToElement(await harness.Backend.ReadSnapshotAsync(
            Arguments("index"),
            CancellationToken.None));

        Assert.True(read.GetProperty("truncated").GetBoolean());
        var nextOffset = read.GetProperty("nextOffset").GetInt32();
        Assert.InRange(nextOffset, 1, 299);
        Assert.Equal(nextOffset, read.GetProperty("index").GetArrayLength());
        Assert.InRange(read.GetRawText().Length, 1, ByteCap + 4096);
    }

    [Fact]
    public async Task ByteCapReportsRemainingComponentIds()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.LargeDocumentComponentCount = 300;
        await using var responder = harness.StartResponder();
        var requested = Enumerable.Range(0, 300)
            .Select(LiveDocumentBackendHarness.LargeDocumentObjectId)
            .ToArray();

        var read = ToElement(await harness.Backend.ReadSnapshotAsync(
            Arguments("components:" + string.Join(',', requested.Select(id => id.ToString("D")))),
            CancellationToken.None));

        Assert.True(read.GetProperty("truncated").GetBoolean());
        var served = read.GetProperty("components").EnumerateArray()
            .Select(component => component.GetProperty("objectId").GetGuid())
            .ToArray();
        Assert.NotEmpty(served);
        var remaining = read.GetProperty("remainingComponentIds").EnumerateArray()
            .Select(id => id.GetGuid())
            .ToArray();
        Assert.NotEmpty(remaining);
        // Served + remaining partition the request: nothing was dropped silently, and every id
        // exists in the snapshot so nothing is missing.
        Assert.Equal(requested, served.Concat(remaining));
        Assert.Empty(read.GetProperty("missingComponents").EnumerateArray());
        Assert.InRange(read.GetRawText().Length, 1, ByteCap + 4096);
    }

    private static JsonElement EmptyArguments() =>
        JsonSerializer.SerializeToElement(new { }, BridgeProtocol.JsonOptions);

    private static JsonElement Arguments(params string[] scopes) =>
        JsonSerializer.SerializeToElement(new { scopes }, BridgeProtocol.JsonOptions);

    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);
}
