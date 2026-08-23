using System.Collections.Concurrent;

namespace Vino.AgentHost.Runtime;

/// <summary>
/// Tracks full-auto sessions whose goal/ask card was auto-resolved mid-turn so the orchestrator
/// can nudge a parked model with a follow-up turn.
///
/// <para>
/// Why this exists: in code-mode exec the model sees a tool's return value only if its script
/// echoes it — verified live 08-18 for BOTH the Ok channel and a success=false Steer payload
/// (three consecutive bench A-T2 attempts parked on "the card is on screen" while the tool
/// result said "auto-confirmed, continue now"). No in-turn payload is guaranteed to reach the
/// model, so the only deterministic channel is the NEXT turn. The dispatcher marks the thread
/// when it auto-resolves; any later write-path call clears the mark (the model did continue);
/// a turn that ends with the mark still set gets one synthetic continuation message.
/// </para>
/// </summary>
public sealed class FullAutoContinuation
{
    // Ping-pong bound: a model that re-proposes and re-parks every nudged turn gets at most
    // this many nudges per thread, then the session is left parked for a human to look at.
    private const int MaxNudgesPerThread = 3;

    // Capture deliveries have their OWN budget. Delivering an image the model explicitly
    // requested is mechanical content transport, not card ping-pong — sharing the card budget
    // starved the model's announced final visual check in 2 of 3 T5 bench cells (08-20).
    private const int MaxCaptureNudgesPerThread = 3;

    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _nudges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _capturePending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _captureNudges = new(StringComparer.Ordinal);

    /// <summary>A full-auto goal/ask was auto-resolved in this thread's active turn.</summary>
    public void MarkAutoResolved(string threadId) => _pending[threadId] = 1;

    /// <summary>
    /// The model kept working after the auto-resolve — no card nudge needed. Real write progress
    /// also re-arms the CAPTURE budget: the ping-pong bound only guards consecutive delivery
    /// turns with no work between them, not a long session's lifetime total (a 3-round quality
    /// cell exhausted the lifetime budget and starved its own final capture check, 08-22).
    /// </summary>
    public void MarkProgress(string threadId)
    {
        _pending.TryRemove(threadId, out _);
        _captureNudges.TryRemove(threadId, out _);
    }

    /// <summary>
    /// True when the turn ended with an unconsumed auto-resolve and the nudge budget allows
    /// one more synthetic continuation. Consumes the pending mark either way.
    /// </summary>
    public bool TryConsumeNudge(string threadId)
    {
        if (!_pending.TryRemove(threadId, out _))
        {
            return false;
        }
        return _nudges.AddOrUpdate(threadId, 1, (_, used) => used + 1) <= MaxNudgesPerThread;
    }

    /// <summary>A full-auto viewport capture was queued; its image can only ride a NEXT turn.</summary>
    public void MarkCapturePending(string threadId) => _capturePending[threadId] = 1;

    /// <summary>
    /// True when the turn ended with a requested capture still undelivered and the capture
    /// budget allows another synthetic delivery turn. Consumes the pending mark either way.
    /// Write-path progress does NOT clear the capture mark — the image still needs a next turn
    /// no matter how much the model wrote after requesting it.
    /// </summary>
    public bool TryConsumeCaptureNudge(string threadId)
    {
        if (!_capturePending.TryRemove(threadId, out _))
        {
            return false;
        }
        return _captureNudges.AddOrUpdate(threadId, 1, (_, used) => used + 1) <= MaxCaptureNudgesPerThread;
    }
}
