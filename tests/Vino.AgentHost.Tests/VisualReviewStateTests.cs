using Vino.AgentHost.Runtime;

namespace Vino.AgentHost.Tests;

public sealed class VisualReviewStateTests
{
    [Fact]
    public void ReviewsACommittedThreadExactlyOnce()
    {
        var state = new VisualReviewState();
        state.MarkCommitted("thread-1");
        Assert.True(state.TryBeginReview("thread-1"));
        Assert.False(state.TryBeginReview("thread-1"));
    }

    [Fact]
    public void NeverReviewsAThreadThatDidNotCommit()
    {
        var state = new VisualReviewState();
        Assert.False(state.TryBeginReview("thread-1"));
    }

    /// <summary>
    /// The repair turn commits again — if that commit re-armed the review, judge turns would
    /// loop forever. Reviewed is terminal per thread.
    /// </summary>
    [Fact]
    public void ALaterCommitDoesNotReArmAReviewedThread()
    {
        var state = new VisualReviewState();
        state.MarkCommitted("thread-1");
        Assert.True(state.TryBeginReview("thread-1"));
        state.MarkCommitted("thread-1");
        Assert.False(state.TryBeginReview("thread-1"));
    }

    [Fact]
    public void RepeatedCommitsBeforeTheReviewStillYieldOneReview()
    {
        var state = new VisualReviewState();
        state.MarkCommitted("thread-1");
        state.MarkCommitted("thread-1");
        Assert.True(state.TryBeginReview("thread-1"));
        Assert.False(state.TryBeginReview("thread-1"));
    }

    [Fact]
    public void ThreadsAreIndependent()
    {
        var state = new VisualReviewState();
        state.MarkCommitted("thread-a");
        Assert.False(state.TryBeginReview("thread-b"));
        Assert.True(state.TryBeginReview("thread-a"));
    }
}
