using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vino.AgentHost.Claude;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Hosting;
using Vino.AgentHost.Runtime;
using Vino.AgentHost.Security;

namespace Vino.AgentHost.Tests;

/// <summary>
/// ClaudeCliSessionClient against raw stream-json fixtures shaped like the live CLI (spike +
/// step-0 probe): spawn arguments, dialect-v1 synthesis, the subtype trap, malformed-line
/// tolerance, and turn finalization.
/// </summary>
[Collection(ClaudeConfigDirCollection.Name)]
public sealed class ClaudeCliSessionClientTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private readonly string? _priorConfigDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");

    public ClaudeCliSessionClientTests() =>
        // Isolate the conversation-file probe from the machine's real ~/.claude.
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _directory.GetPath("claude-config"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _priorConfigDir);
        _directory.Dispose();
    }

    [Fact]
    public async Task TurnArgumentsMatchTheMeasuredSpawnShape()
    {
        Environment.SetEnvironmentVariable("VINO_LEAK_PROBE", "must-not-ride");
        try
        {
            var client = CreateClient();
            var threadId = await client.StartThreadAsync(_directory.GetPath("cwd"), "claude-fable-5");
            var state = client.RegisterThread(threadId);

            var startInfo = client.CreateTurnStartInfo(
                @"C:\fake\claude.exe", state, Path.Combine(state.Home, "mcp.json"), "claude-fable-5", "xhigh");

            var arguments = startInfo.ArgumentList.ToList();
            // The measured contract, in order of importance:
            Assert.Contains("-p", arguments);
            AssertPair(arguments, "--output-format", "stream-json");
            AssertPair(arguments, "--input-format", "stream-json");
            Assert.Contains("--verbose", arguments);
            AssertPair(arguments, "--model", "claude-fable-5");
            AssertPair(arguments, "--effort", "xhigh");
            AssertPair(arguments, "--session-id", threadId); // fresh conversation -> not --resume
            Assert.DoesNotContain("--resume", arguments);
            Assert.Contains("--strict-mcp-config", arguments);
            AssertPair(arguments, "--tools", "");            // built-ins gone entirely
            AssertPair(arguments, "--setting-sources", "project"); // CLAUDE.md needs project scope; user/local stay out
            Assert.DoesNotContain("--bare", arguments);      // --bare skips OAuth: subscription killer
            Assert.DoesNotContain("--permission-mode", arguments);

            // allowedTools: every vino tool enumerated explicitly, no wildcard.
            var allowed = arguments[arguments.IndexOf("--allowedTools") + 1].Split(',');
            Assert.Contains("mcp__vino__snapshot_read", allowed);
            Assert.Contains("mcp__vino__change_submit", allowed);
            Assert.True(allowed.Length >= 20, $"Expected the full tool surface, got {allowed.Length}.");
            Assert.All(allowed, name => Assert.StartsWith("mcp__vino__", name, StringComparison.Ordinal));

            // Hygiene: redirected stdin (3s stall without it), pinned updater, VINO_* scrubbed,
            // ASCII cwd.
            Assert.True(startInfo.RedirectStandardInput);
            Assert.Equal("1", startInfo.Environment["DISABLE_AUTOUPDATER"]);
            Assert.DoesNotContain(startInfo.Environment.Keys, key =>
                key.StartsWith("VINO_", StringComparison.OrdinalIgnoreCase));
            Assert.True(ClaudeWorkspacePlanner.IsAscii(startInfo.WorkingDirectory));
            Assert.Equal(state.Home, startInfo.WorkingDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINO_LEAK_PROBE", null);
        }
    }

    [Fact]
    public async Task ResumeIsChosenOnceTheConversationFileExists()
    {
        var client = CreateClient();
        var threadId = await client.StartThreadAsync(_directory.GetPath("cwd"), null);
        var state = client.RegisterThread(threadId);

        // Materialize the CLI's conversation JSONL exactly where the slug rule puts it.
        var slug = ClaudeCliSessionClient.SlugForCwd(state.Home);
        var jsonl = Path.Combine(_directory.GetPath("claude-config"), "projects", slug, threadId + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonl)!);
        File.WriteAllText(jsonl, "{}\n");

        var arguments = client.CreateTurnStartInfo(
            @"C:\fake\claude.exe", state, "mcp.json", null, null).ArgumentList.ToList();
        AssertPair(arguments, "--resume", threadId);
        Assert.DoesNotContain("--session-id", arguments);
    }

    [Fact]
    public async Task AssistantEventsSynthesizeDialectV1AndCountUsageOncePerMessage()
    {
        var client = CreateClient();
        var notifications = CaptureNotifications(client);
        var threadId = await client.StartThreadAsync(_directory.GetPath("cwd"), null);
        var state = client.RegisterThread(threadId);
        var execution = NewExecution(threadId);

        // Two events share one message.id (thinking block then text block — the live shape):
        // usage counts once, only the text block becomes an item.
        await client.ProcessStreamLineAsync(state, execution, """
            {"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":10,"output_tokens":5},"content":[{"type":"thinking","thinking":"..."}]},"session_id":"s"}
            """.Trim());
        await client.ProcessStreamLineAsync(state, execution, """
            {"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":10,"output_tokens":5},"content":[{"type":"text","text":"the answer"}]},"session_id":"s"}
            """.Trim());
        await client.ProcessStreamLineAsync(state, execution, """
            {"type":"result","is_error":false,"terminal_reason":"completed","usage":{"input_tokens":10,"output_tokens":49,"cache_creation_input_tokens":6824,"cache_read_input_tokens":0},"result":"the answer"}
            """.Trim());
        await client.FinalizeTurnAsync(state, execution);

        var item = Assert.Single(notifications, n => n.Method == "item/completed");
        Assert.Equal(threadId, item.Parameters.GetProperty("threadId").GetString());
        Assert.Equal("agentMessage", item.Parameters.GetProperty("item").GetProperty("type").GetString());
        Assert.Equal("the answer", item.Parameters.GetProperty("item").GetProperty("text").GetString());
        Assert.Equal("final_answer", item.Parameters.GetProperty("item").GetProperty("phase").GetString());

        var completed = Assert.Single(notifications, n => n.Method == "turn/completed");
        var turn = completed.Parameters.GetProperty("turn");
        Assert.Equal(execution.TurnId, turn.GetProperty("id").GetString());
        Assert.Equal("completed", turn.GetProperty("status").GetString());
        // msg_1 counted once (15) + result usage (10+49+6824+0 = 6883) = 6898 cumulative tokens,
        // and the synthesized usage round-trips through the codex-side parser untouched.
        var usageSnapshot = SessionUsageState.TryParse(turn);
        Assert.Equal(6898, usageSnapshot?.TotalTokens);
        // The window and the LAST message's footprint feed the panel's ctx meter (08-27, J5).
        // Reporting the window is safe: the pre-turn compaction gate exits on
        // SupportsCompaction=false before it ever reads these fields.
        Assert.Equal(200_000, usageSnapshot?.ContextWindow);
        Assert.Equal(6883, usageSnapshot?.ContextUsedTokens); // the result message: 10+49+6824+0

        var snapshot = execution.Snapshot();
        Assert.Equal("completed", snapshot.Status);
        Assert.Equal("the answer", Assert.Single(snapshot.AgentMessages).Text);
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"boom"}""", "boom")]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"api_error_status":404,"result":"nf"}""", "nf")]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"terminal_reason":"max_turns","result":"cut"}""", "cut")]
    public async Task SubtypeIsNeverTrustedForTheVerdict(string resultLine, string expectedMessage)
    {
        // The trap this pins: subtype says "success" in every one of these — a 404 included
        // (measured). The three honest signals decide, and the CLI's message text is preserved
        // verbatim (the orchestrator's context-overflow retry matches on provider wording).
        var client = CreateClient();
        var notifications = CaptureNotifications(client);
        var threadId = await client.StartThreadAsync(_directory.GetPath("cwd"), null);
        var state = client.RegisterThread(threadId);
        var execution = NewExecution(threadId);

        await client.ProcessStreamLineAsync(state, execution, resultLine);
        await client.FinalizeTurnAsync(state, execution);

        var turn = Assert.Single(notifications, n => n.Method == "turn/completed").Parameters.GetProperty("turn");
        Assert.Equal("failed", turn.GetProperty("status").GetString());
        Assert.Equal(expectedMessage, turn.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("failed", execution.Snapshot().Status);
    }

    [Fact]
    public async Task MalformedLinesFailClosedPerLineNotPerTurn()
    {
        var client = CreateClient();
        var threadId = await client.StartThreadAsync(_directory.GetPath("cwd"), null);
        var state = client.RegisterThread(threadId);
        var execution = NewExecution(threadId);

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            client.ProcessStreamLineAsync(state, execution, "{broken"));
        // The turn is untouched; the pump's per-line catch lets the stream continue.
        Assert.Equal("inProgress", execution.Snapshot().Status);

        // Unknown event types (hooks fire even under -p, future CLI versions add types) are data.
        await client.ProcessStreamLineAsync(state, execution, """{"type":"hook_started","name":"x"}""");
        Assert.Equal("inProgress", execution.Snapshot().Status);
    }

    [Fact]
    public async Task ExitWithoutAResultFailsWithExitCodeAndStderrTail()
    {
        var client = CreateClient();
        var notifications = CaptureNotifications(client);
        var threadId = await client.StartThreadAsync(_directory.GetPath("cwd"), null);
        var state = client.RegisterThread(threadId);
        var execution = NewExecution(threadId);
        execution.RecordStderr("warning: something");
        execution.RecordStderr("fatal: it died");

        await client.FinalizeTurnAsync(state, execution);

        var turn = Assert.Single(notifications, n => n.Method == "turn/completed").Parameters.GetProperty("turn");
        Assert.Equal("failed", turn.GetProperty("status").GetString());
        var error = turn.GetProperty("error");
        Assert.Contains("without a result event", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("fatal: it died", error.GetProperty("additionalDetails").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterruptBeatsAMissingResultButNotACompletedOne()
    {
        var client = CreateClient();
        var threadId = await client.StartThreadAsync(_directory.GetPath("cwd"), null);
        var state = client.RegisterThread(threadId);

        // Killed mid-turn, no result -> interrupted.
        var killed = NewExecution(threadId);
        killed.Interrupted = true;
        await client.FinalizeTurnAsync(state, killed);
        Assert.Equal("interrupted", killed.Snapshot().Status);

        // The result fully arrived before the kill -> the work happened; completed is honest.
        var raced = NewExecution(threadId);
        await client.ProcessStreamLineAsync(state, raced, """{"type":"result","is_error":false,"result":"done"}""");
        raced.Interrupted = true;
        await client.FinalizeTurnAsync(state, raced);
        Assert.Equal("completed", raced.Snapshot().Status);
    }

    [Fact]
    public void UserMessageJsonCarriesImagesAsBase64Blocks()
    {
        var imagePath = _directory.GetPath("shot.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        File.WriteAllBytes(imagePath, [0x89, 0x50, 0x4E, 0x47]);

        var json = ClaudeCliSessionClient.BuildUserMessageJson("look at this", [imagePath]);
        using var document = JsonDocument.Parse(json);
        var content = document.RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        var image = content[0];
        Assert.Equal("image", image.GetProperty("type").GetString());
        Assert.Equal("base64", image.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("image/png", image.GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("look at this", content[1].GetProperty("text").GetString());

        // A missing attachment must not sink the turn; the text still travels alone.
        var textOnly = ClaudeCliSessionClient.BuildUserMessageJson("no image", [_directory.GetPath("gone.png")]);
        using var fallback = JsonDocument.Parse(textOnly);
        Assert.Equal(1, fallback.RootElement.GetProperty("message").GetProperty("content").GetArrayLength());
    }

    [Fact]
    public void SlugMirrorsTheCliRule()
    {
        Assert.Equal(
            "C--Users-dev-vino-claude",
            ClaudeCliSessionClient.SlugForCwd(@"C:\Users\dev\vino\claude"));
    }

    // ------------------------------------------------------------------ harness

    private ClaudeCliSessionClient CreateClient()
    {
        var options = new AgentHostOptions
        {
            ProjectDirectory = _directory.Path,
            DataDirectory = _directory.GetPath("data")
        };
        var endpoints = new EndpointRegistry();
        endpoints.Set(new Uri("http://127.0.0.1:5001"));
        return new ClaudeCliSessionClient(
            options,
            endpoints,
            new McpSessionSecretStore(),
            new ClaudeWorkspacePlanner(_ => null),
            new ClaudeHomeScaffolder(),
            NullLogger<ClaudeCliSessionClient>.Instance);
    }

    private static ClaudeCliSessionClient.TurnExecution NewExecution(string threadId) =>
        new(threadId, $"claude-turn-{Guid.NewGuid():N}", new System.Diagnostics.Process());

    private static List<(string Method, JsonElement Parameters)> CaptureNotifications(ClaudeCliSessionClient client)
    {
        var captured = new List<(string, JsonElement)>();
        client.NotificationReceived += (method, parameters) =>
        {
            lock (captured)
            {
                captured.Add((method, parameters.Clone()));
            }
            return Task.CompletedTask;
        };
        return captured;
    }

    private static void AssertPair(List<string> arguments, string flag, string value)
    {
        var index = arguments.IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"Missing {flag}.");
        Assert.Equal(value, arguments[index + 1]);
    }
}
