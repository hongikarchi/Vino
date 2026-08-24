using Vino.AgentHost.Runtime;

namespace Vino.AgentHost.Tests;

public class PendingJobDigestsTests
{
    [Fact]
    public void DrainReturnsNotesOldestFirstAndClears()
    {
        var digests = new PendingJobDigests();
        var sessionId = Guid.NewGuid();
        digests.Enqueue(sessionId, "first");
        digests.Enqueue(sessionId, "second");

        var (notes, dropped) = digests.Drain(sessionId);

        Assert.Equal(new[] { "first", "second" }, notes);
        Assert.Equal(0, dropped);
        Assert.Empty(digests.Drain(sessionId).Notes);
    }

    [Fact]
    public void OverflowDropsOldestAndCountsThem()
    {
        var digests = new PendingJobDigests();
        var sessionId = Guid.NewGuid();
        for (var index = 0; index < 9; index++)
        {
            digests.Enqueue(sessionId, $"note-{index}");
        }

        var (notes, dropped) = digests.Drain(sessionId);

        Assert.Equal(6, notes.Count);
        Assert.Equal("note-3", notes[0]);
        Assert.Equal("note-8", notes[^1]);
        Assert.Equal(3, dropped);
    }

    [Fact]
    public void SessionsAreIsolated()
    {
        var digests = new PendingJobDigests();
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        digests.Enqueue(one, "for-one");

        Assert.Empty(digests.Drain(two).Notes);
        Assert.Equal(new[] { "for-one" }, digests.Drain(one).Notes);
    }

    [Fact]
    public void DigestBlockNamesEveryNoteAndTheOverflow()
    {
        var block = SessionOrchestrator.ComposeJobDigestBlock(
            new[] { "Job A FAILED: boom", "Job B committed WITH ISSUES: output 'P' empty" },
            dropped: 2);

        Assert.StartsWith("<vino_job_results>", block);
        Assert.EndsWith("</vino_job_results>", block);
        Assert.Contains("Job A FAILED: boom", block);
        Assert.Contains("Job B committed WITH ISSUES: output 'P' empty", block);
        Assert.Contains("(+2 earlier note(s) dropped)", block);
        Assert.Contains("job_status", block);
    }
}
