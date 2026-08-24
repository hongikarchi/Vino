using System.Collections.Concurrent;

namespace Vino.AgentHost.Runtime;

/// <summary>
/// Compact terminal-job notes waiting to ride the session's NEXT turn input. In code-mode the
/// model only sees tool results its own script echoes, so a job that ended in a state worth
/// acting on (failed, needs recovery, committed with warnings or empty outputs) must ALSO travel
/// a guaranteed channel — the same next-turn input that carries approval grants and viewport
/// captures. In-memory on purpose: a restart loses pending notes, never work.
/// </summary>
public sealed class PendingJobDigests
{
    // Bounded per session. Oldest notes drop first but are counted, so a flood surfaces as
    // "(+N earlier)" in the delivered block rather than vanishing.
    private const int MaxPerSession = 6;

    private sealed class SessionNotes
    {
        public readonly Queue<string> Notes = new();
        public int Dropped;
    }

    private readonly ConcurrentDictionary<Guid, SessionNotes> _pending = new();

    public void Enqueue(Guid sessionId, string note)
    {
        var notes = _pending.GetOrAdd(sessionId, static _ => new SessionNotes());
        lock (notes)
        {
            notes.Notes.Enqueue(note);
            while (notes.Notes.Count > MaxPerSession)
            {
                notes.Notes.Dequeue();
                notes.Dropped++;
            }
        }
    }

    /// <summary>
    /// Removes and returns the pending notes oldest-first, plus how many older notes overflowed
    /// the per-session cap since the last drain.
    /// </summary>
    public (IReadOnlyList<string> Notes, int Dropped) Drain(Guid sessionId)
    {
        if (!_pending.TryRemove(sessionId, out var notes))
        {
            return (Array.Empty<string>(), 0);
        }
        lock (notes)
        {
            return (notes.Notes.ToArray(), notes.Dropped);
        }
    }
}
