using System.Collections.Concurrent;

namespace Vino.AgentHost.Runtime;

/// <summary>
/// Viewport captures waiting to be SHOWN to the model. A dynamic tool result cannot carry an
/// image (contentItems is text-only, and unechoed results are invisible in code-mode anyway),
/// so the only guaranteed channel is the next turn's localImage input — the same one user
/// attachments ride. The capture tool enqueues here; RunTurnAsync drains into imagePaths.
/// In-memory on purpose: a restart loses pending previews, never work.
/// </summary>
public sealed class PendingViewCaptures
{
    // Bounded per session: a runaway capture loop must not stack an unbounded image backlog
    // onto one turn. Oldest entries are dropped first — the newest capture is the relevant one.
    private const int MaxPerSession = 4;

    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<string>> _pending = new();

    public void Enqueue(Guid sessionId, string absolutePath)
    {
        var queue = _pending.GetOrAdd(sessionId, static _ => new ConcurrentQueue<string>());
        queue.Enqueue(absolutePath);
        while (queue.Count > MaxPerSession && queue.TryDequeue(out _))
        {
        }
    }

    /// <summary>Removes and returns every pending capture path for the session, oldest first.</summary>
    public IReadOnlyList<string> Drain(Guid sessionId)
    {
        if (!_pending.TryRemove(sessionId, out var queue))
        {
            return [];
        }
        var drained = new List<string>();
        while (queue.TryDequeue(out var path))
        {
            drained.Add(path);
        }
        return drained;
    }
}
