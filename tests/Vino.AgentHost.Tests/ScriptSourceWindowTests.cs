using Vino.AgentHost.Runtime;

namespace Vino.AgentHost.Tests;

/// <summary>
/// A script component's source used to be an all-or-nothing read: the server's own 256KiB budget
/// exempted inspections entirely, so a large source was emitted whole — and the CLIENT then cut it
/// (codex code-mode at 40,000 chars, Claude Code at 25,000 tokens) with no resume point. Measured
/// 2026-08-26: a 50,032-character source, zero characters delivered, the user pasted it into chat.
/// These fix the window and, above all, the continuation contract.
/// </summary>
public sealed class ScriptSourceWindowTests
{
    private const string Component = "e858daad-d4d6-45a3-840b-9565071cf3cf";

    private static string Source(int length) => new('x', length);

    [Fact]
    public void ShortSourceIsNotWindowedAtAll()
    {
        var source = Source(500);
        var window = LiveDocumentBackend.BuildScriptSourceWindow($"script:{Component}", source);

        Assert.False(window.Windowed);
        Assert.False(window.HasMore);
        Assert.Equal(source, window.Source);
        Assert.Equal(500, window.Total);
    }

    [Fact]
    public void LongSourceIsCutAtTheWindowAndSaysWhereToContinue()
    {
        var source = Source(50_032); // the source that was lost
        var window = LiveDocumentBackend.BuildScriptSourceWindow($"script:{Component}", source);

        Assert.True(window.Windowed);
        Assert.True(window.HasMore);
        Assert.Equal(LiveDocumentBackend.ScriptSourceWindow, window.Source.Length);
        Assert.Equal(50_032, window.Total);
        Assert.Equal(0, window.Offset);
        Assert.Equal(LiveDocumentBackend.ScriptSourceWindow, window.NextOffset);
        Assert.Equal($"script:{Component}:{LiveDocumentBackend.ScriptSourceWindow}", window.ContinueWith);
    }

    [Fact]
    public void FollowingTheContinuationReadsTheWholeSourceExactlyOnce()
    {
        // The contract that matters: following continueWith until it stops must reassemble the
        // source byte for byte, with no gap and no overlap.
        var source = string.Concat(Enumerable.Range(0, 60_000).Select(i => (char)('a' + i % 26)));
        var scope = $"script:{Component}";
        var assembled = new System.Text.StringBuilder();
        var reads = 0;
        while (true)
        {
            var window = LiveDocumentBackend.BuildScriptSourceWindow(scope, source);
            assembled.Append(window.Source);
            reads++;
            if (!window.HasMore)
            {
                break;
            }
            Assert.Equal(assembled.Length, window.NextOffset);
            scope = window.ContinueWith;
            Assert.True(reads < 20, "continuation did not terminate");
        }

        Assert.Equal(source, assembled.ToString());
        Assert.Equal(3, reads); // 60,000 / 24,000
    }

    [Fact]
    public void OffsetPastTheEndReturnsAnEmptyWindowInsteadOfThrowing()
    {
        // A stale offset (the source shrank between reads) must not turn a read into an error.
        var window = LiveDocumentBackend.BuildScriptSourceWindow($"script:{Component}:900000", Source(100));

        Assert.Equal(string.Empty, window.Source);
        Assert.False(window.HasMore);
        Assert.Equal(100, window.Offset);
    }

    [Theory]
    [InlineData("script:e858daad-d4d6-45a3-840b-9565071cf3cf", 0)]
    [InlineData("script:e858daad-d4d6-45a3-840b-9565071cf3cf:24000", 24000)]
    [InlineData("script:e858daad-d4d6-45a3-840b-9565071cf3cf:0", 0)]
    [InlineData("script:e858daad-d4d6-45a3-840b-9565071cf3cf:-5", 0)]
    [InlineData("script:e858daad-d4d6-45a3-840b-9565071cf3cf:abc", 0)]
    public void OffsetIsReadFromTheScopeTail(string scope, int expected) =>
        Assert.Equal(expected, LiveDocumentBackend.ReadScriptSourceOffset(scope));
}
