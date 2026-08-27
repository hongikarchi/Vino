using System.Text.Json;
using Vino.AgentHost.Api;
using Vino.AgentHost.Runtime;
using Vino.BridgeContract;
using Vino.Contracts;
using Vino.CanvasSceneAdapter;

namespace Vino.AgentHost.Tests;

/// <summary>
/// The budget-overrun 자동 화해 and the read-back failure code (log review B06+A11, approved
/// 2026-08-27). A 45s overrun used to latch the session as recoveryRequired even when the outcome
/// was knowable — the live repro froze a real user's document for 5.7 minutes over a one-line
/// change. Now: re-read, judge, and only halt when the document truly cannot testify.
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class BudgetReconciliationTests
{
    /// <summary>
    /// The op times out, the document comes back readable, and its fingerprints say the write
    /// never landed: a clean Failed with the judged manifest — and no halt, proven by the SAME
    /// session immediately submitting again.
    /// </summary>
    [Fact]
    public async Task TimeoutWithUnchangedDocumentReconcilesToFailedWithoutHalting()
    {
        LiveDocumentBackend.BridgeRequestTimeoutOverride = TimeSpan.FromMilliseconds(400);
        LiveDocumentBackend.ReconcileRetryDelay = TimeSpan.FromMilliseconds(50);
        try
        {
            await using var harness = await LiveDocumentBackendHarness.CreateAsync();
            // Writes stall past the shrunken budget; snapshots keep answering instantly, so the
            // reconciliation can re-read and judge.
            await using var responder = harness.StartResponder(writeDelay: TimeSpan.FromSeconds(3));
            var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Reconcile"));
            var snapshot = await harness.CaptureSnapshotViewAsync();
            var changeSet = await harness.CreateChangeSetAsync(session, "overrun-move", snapshot.Revision);

            var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "overrun-key", "Overrun move"),
                CancellationToken.None));
            var jobId = submitted.GetProperty("jobId").GetGuid();
            var final = await WaitForTerminalAsync(harness, jobId);

            Assert.Equal(JobState.Failed, final.State);
            Assert.Contains("reconciled by re-reading the document", final.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("did NOT land", final.Message, StringComparison.Ordinal);
            // No halt: the same session submits again and the job is accepted, not refused.
            var next = await harness.CreateChangeSetAsync(session, "after-overrun", snapshot.Revision);
            var second = ToElement(await harness.Backend.SubmitChangeAsync(
                session,
                Submission(next, snapshot.Id, "after-overrun-key", "After overrun"),
                CancellationToken.None));
            Assert.False(string.IsNullOrEmpty(second.GetProperty("jobId").GetString()));
        }
        finally
        {
            LiveDocumentBackend.BridgeRequestTimeoutOverride = null;
            LiveDocumentBackend.ReconcileRetryDelay = TimeSpan.FromSeconds(5);
        }
    }

    /// <summary>
    /// The adapter read the post-write state back and reported the requested change absent
    /// (write_not_applied — the layer-visibility shape that produced 3 of 4 alpha.7 halts):
    /// a deterministic Failed, never recoveryRequired.
    /// </summary>
    [Fact]
    public async Task ReadBackNotAppliedClassifiesAsFailedNotRecoveryRequired()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder(
            failureFactory: request => request.Operation == "canvas.move"
                ? new BridgeFailure(
                    "write_not_applied",
                    "Rhino did not apply visible (requested True, own flag False) to layer 'P::C'.",
                    Retryable: false,
                    request.OperationId)
                : null);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("ReadBack"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await harness.CreateChangeSetAsync(session, "readback-move", snapshot.Revision);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "readback-key", "Readback move"),
            CancellationToken.None));
        var final = await WaitForTerminalAsync(harness, submitted.GetProperty("jobId").GetGuid());

        Assert.Equal(JobState.Failed, final.State);
        Assert.Contains("did not apply visible", final.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read the state back", final.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When the document cannot be re-read at all (Rhino truly wedged: the responder answers the
    /// before-snapshot and then goes silent), the honest outcome is still recoveryRequired.
    /// </summary>
    [Fact]
    public async Task TimeoutWithUnreadableDocumentStaysRecoveryRequired()
    {
        LiveDocumentBackend.BridgeRequestTimeoutOverride = TimeSpan.FromMilliseconds(400);
        LiveDocumentBackend.ReconcileRetryDelay = TimeSpan.FromMilliseconds(50);
        try
        {
            await using var harness = await LiveDocumentBackendHarness.CreateAsync();
            // Exactly the snapshots BEFORE the write get answers; everything after goes dark.
            await using var responder = harness.StartResponder(
                writeDelay: TimeSpan.FromSeconds(3),
                automaticSnapshotResponses: 3);
            var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Wedged"));
            var snapshot = await harness.CaptureSnapshotViewAsync();
            var changeSet = await harness.CreateChangeSetAsync(session, "wedged-move", snapshot.Revision);

            var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "wedged-key", "Wedged move"),
                CancellationToken.None));
            var final = await WaitForTerminalAsync(harness, submitted.GetProperty("jobId").GetGuid(), TimeSpan.FromSeconds(30));

            Assert.Equal(JobState.RecoveryRequired, final.State);
            Assert.Contains("Unknown outcome", final.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            LiveDocumentBackend.BridgeRequestTimeoutOverride = null;
            LiveDocumentBackend.ReconcileRetryDelay = TimeSpan.FromSeconds(5);
        }
    }

    private static async Task<(JobState State, string Message)> WaitForTerminalAsync(
        LiveDocumentBackendHarness harness,
        Guid jobId,
        TimeSpan? budget = null)
    {
        var deadline = DateTime.UtcNow + (budget ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            var arguments = JsonSerializer.SerializeToElement(
                new { jobId, wait = false },
                BridgeProtocol.JsonOptions);
            var view = JsonSerializer.SerializeToElement(
                await harness.Backend.ReadJobAsync(arguments, CancellationToken.None),
                typeof(object),
                BridgeProtocol.JsonOptions);
            // ProjectJob lowercases the whole state string ("recoveryrequired"), so match that.
            var state = view.GetProperty("state").GetString();
            if (state is "committed" or "failed" or "recoveryrequired" or "blocked" or "cancelled")
            {
                return (Enum.Parse<JobState>(state, ignoreCase: true), view.GetProperty("message").GetString() ?? "");
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("job never reached a terminal state");
    }

    private static JsonElement Submission(ChangeSet changeSet, string snapshotId, string key, string summary) =>
        JsonSerializer.SerializeToElement(
            new { changeSet, expectedSnapshotId = snapshotId, idempotencyKey = key, summary },
            BridgeProtocol.JsonOptions);

    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);
}
