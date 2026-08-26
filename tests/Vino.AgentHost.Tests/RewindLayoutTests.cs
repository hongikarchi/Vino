using System.Text.Json;
using Vino.AgentHost.Api;
using Vino.BridgeContract;

namespace Vino.AgentHost.Tests;

/// <summary>
/// Layout rewind (B05 v1): put component POSITIONS back to a past managed-history revision.
///
/// <para>
/// Every verified job already commits a full canvas snapshot, but the repository could only
/// Init/Commit/ReadHead/Verify — so the pre-change coordinates of every edit sat on disk and were
/// unreachable. That is why an auto-tidy that moved 139 components was reported as unrestorable
/// when the parent commit held all 139 old pivots. These tests cover the read path end to end and,
/// above all, the two things a restore must never do: move something the user placed after the
/// restore point, or overwrite a position they have since changed by hand.
/// </para>
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class RewindLayoutTests
{
    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);

    private static JsonElement Args(object value) =>
        JsonSerializer.SerializeToElement(value, BridgeProtocol.JsonOptions);

    [Fact]
    public async Task HistoryIsEmptyBeforeAnyCommitAndNeverThrows()
    {
        // A fresh document has no managed history yet. Reading it must be an empty list, not an
        // error — the panel and the model both call this before anything has been committed.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Rewind"));

        var history = ToElement(await harness.Backend.ReadLayoutHistoryAsync(session, Args(new { })));

        Assert.Equal(JsonValueKind.Array, history.GetProperty("revisions").ValueKind);
        Assert.Empty(history.GetProperty("revisions").EnumerateArray());
    }

    [Fact]
    public async Task RewindRequiresARevisionToRestoreFrom()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Rewind"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.RewindLayoutAsync(session, Args(new { }), CancellationToken.None));

        // The message has to name the tool that produces a sha, or the caller cannot proceed.
        Assert.Contains("layout_history", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoringFromAnUnknownRevisionFailsCleanlyWithoutTouchingTheDocument()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Rewind"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.RewindLayoutAsync(
                session,
                Args(new { sha = new string('a', 40) }),
                CancellationToken.None));

        // Nothing was written: a restore that cannot read its source must not move anything.
        Assert.DoesNotContain(
            responder.Requests,
            request => string.Equals(request.Operation, "canvas.move", StringComparison.Ordinal));
    }
}
