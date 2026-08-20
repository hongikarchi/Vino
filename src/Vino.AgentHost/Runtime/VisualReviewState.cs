using System.Collections.Concurrent;

namespace Vino.AgentHost.Runtime;

/// <summary>
/// Tracks which threads have earned their ONE fresh-eyes visual review: a change_submit whose
/// job COMMITTED arms the thread, and the first quiet full-auto turn-end after that consumes
/// the review.
///
/// <para>
/// Why this exists: bench T5 (08-20) showed a context-free judge looking at a viewport capture
/// catches visual defects (jagged arches, floating panels, debug-preview state) the authoring
/// model misses. The review is advisory only — one judge turn, at most one synthetic repair
/// turn — so this state's job is to make "once per thread" airtight: the repair turn commits
/// again, and re-arming on that commit would loop judge turns forever.
/// </para>
/// </summary>
public sealed class VisualReviewState
{
    private const int Committed = 1;
    private const int Reviewed = 2;

    private readonly ConcurrentDictionary<string, int> _threads = new(StringComparer.Ordinal);

    /// <summary>
    /// A change_submit in this thread committed. Idempotent, and never re-arms a thread whose
    /// one review already ran — the repair turn's own commits must not buy a second review.
    /// </summary>
    public void MarkCommitted(string threadId) =>
        _threads.AddOrUpdate(threadId, Committed, (_, state) => state == Reviewed ? Reviewed : Committed);

    /// <summary>
    /// True exactly once per thread, and only after a commit. Atomically flips the thread to
    /// reviewed, so two racing turn-ends can never both start a judge.
    /// </summary>
    public bool TryBeginReview(string threadId) =>
        _threads.TryUpdate(threadId, Reviewed, Committed);
}
