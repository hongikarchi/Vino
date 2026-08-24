using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Hosting;
using Vino.AgentHost.Mcp;
using Vino.AgentHost.Security;

namespace Vino.AgentHost.Claude;

/// <summary>
/// The Claude Code CLI backend: <see cref="IAgentSessionClient"/> over per-turn respawns of the
/// subscription CLI. Every design point here is measured, not guessed (spike 2026-08-19 +
/// step-0 probe 2026-08-24, CLI v2.1.241):
///
///   - A thread is a CLI session id WE mint (`--session-id` on the first spawn, `--resume`
///     after); the conversation itself lives in the CLI's local JSONL, keyed by a slug of the
///     process cwd — hence the ASCII home from ClaudeWorkspacePlanner, one subdirectory per
///     thread so slugs never collide.
///   - A turn = one spawn: stdin receives a single stream-json user message (text + base64
///     image blocks — the ONLY image channel, since `--tools ""` removes Read) and closes;
///     stdout streams stream-json events; process exit ends the turn.
///   - Tools reach the model exclusively through the loopback /mcp endpoint: `--strict-mcp-config`
///     + `--tools ""` + an explicit `--allowedTools mcp__vino__*` list (25 names enumerated, not
///     a wildcard — prefix-match semantics are unmeasured). The per-thread secret is minted here
///     and written into the thread's mcp.json before every spawn.
///   - Error verdicts come from exit code + `is_error`/`terminal_reason`/`api_error_status`;
///     `subtype` LIES (it reads "success" on a 404) and is never consulted.
///   - The orchestrator-facing surface speaks notification dialect v1: assistant text becomes
///     `item/completed` (agentMessage) and the result becomes `turn/completed` — the orchestrator
///     decodes Claude turns with zero diff.
///   - ReadTurnAsync answers from the in-memory turn buffer and never throws, so the
///     orchestrator's polling loop always sees a healthy snapshot and its codex-shaped
///     restart/notification-fallback recovery stays dormant.
/// </summary>
public sealed class ClaudeCliSessionClient : IAgentSessionClient, IMcpTurnContext, IAsyncDisposable
{
    private static readonly TimeSpan StartupFailureCooldown = TimeSpan.FromSeconds(30);

    private readonly AgentHostOptions _options;
    private readonly EndpointRegistry _endpoints;
    private readonly McpSessionSecretStore _secrets;
    private readonly ClaudeWorkspacePlanner _planner;
    private readonly ClaudeHomeScaffolder _scaffolder;
    private readonly IThreadInstructionComposer? _instructionComposer;
    private readonly ILogger<ClaudeCliSessionClient> _logger;
    private readonly ConcurrentDictionary<string, ThreadState> _threads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TurnExecution> _activeTurns = new(StringComparer.Ordinal);
    private DateTimeOffset _startupRetryNotBeforeUtc;
    private long _turnCounter;
    private bool _disposed;

    public ClaudeCliSessionClient(
        AgentHostOptions options,
        EndpointRegistry endpoints,
        McpSessionSecretStore secrets,
        ClaudeWorkspacePlanner planner,
        ClaudeHomeScaffolder scaffolder,
        ILogger<ClaudeCliSessionClient> logger,
        IThreadInstructionComposer? instructionComposer = null)
    {
        _options = options;
        _endpoints = endpoints;
        _secrets = secrets;
        _planner = planner;
        _scaffolder = scaffolder;
        _instructionComposer = instructionComposer;
        _logger = logger;
    }

    public event Func<string, JsonElement, Task>? NotificationReceived;

    /// <summary>
    /// A respawn client has no long-lived process to be "running": it is available whenever it
    /// can spawn. The orchestrator's IsRunning checks sit on codex-shaped recovery paths that a
    /// never-throwing ReadTurnAsync keeps dormant.
    /// </summary>
    public bool IsRunning => !_disposed;

    /// <summary>Claude Code auto-compacts its own context; host-driven compaction is meaningless.</summary>
    public bool SupportsCompaction => false;

    /// <summary>
    /// The newest rate_limit_event's info across all threads (account-scoped: the CLI reports the
    /// subscription window, not a per-conversation one). Projected as runtime.claudeLimits.
    /// </summary>
    public JsonElement? LatestAccountRateLimit { get; private set; }

    public Task<string> StartThreadAsync(
        string cwd,
        string? model,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // We mint the session id up front (--session-id accepts pre-issued UUIDs) — no init-event
        // parsing race, and the id doubles as the durable external conversation id.
        var threadId = Guid.NewGuid().ToString("D");
        var state = RegisterThread(threadId);
        ScaffoldHome(state);
        _logger.LogInformation(
            "Claude thread {ThreadId} homed at {Home} (model {Model}).",
            threadId,
            state.Home,
            model ?? "default");
        return Task.FromResult(threadId);
    }

    public Task ResumeThreadAsync(
        string threadId,
        string cwd,
        string? model,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Same reason codex re-composes instructions on resume: rules.md/memory.md are living
        // sources, so the managed CLAUDE.md block refreshes before the next spawn reads it.
        var state = RegisterThread(threadId);
        ScaffoldHome(state);
        return Task.CompletedTask;
    }

    public async Task<string> StartTurnAsync(
        string threadId,
        string message,
        string? model,
        string? effort,
        IReadOnlyList<string>? imagePaths = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = DateTimeOffset.UtcNow;
        if (now < _startupRetryNotBeforeUtc)
        {
            throw new AgentProtocolException(
                "Claude CLI startup is temporarily paused after a recent failure.");
        }
        if (!ClaudeInstallation.TryLocateExecutable(_options, out var executable))
        {
            _startupRetryNotBeforeUtc = now + StartupFailureCooldown;
            throw new AgentProtocolException(
                "The Claude CLI executable could not be located (install claude or set --claude-executable).");
        }
        var state = RegisterThread(threadId);
        ScaffoldHome(state);
        var mcpConfigPath = await WriteMcpConfigAsync(state, cancellationToken).ConfigureAwait(false);

        var turnId = $"claude-turn-{Interlocked.Increment(ref _turnCounter):D6}";
        var startInfo = CreateTurnStartInfo(executable, state, mcpConfigPath, model, effort);
        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new AgentProtocolException("The Claude CLI process failed to start.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _startupRetryNotBeforeUtc = DateTimeOffset.UtcNow + StartupFailureCooldown;
            throw new AgentProtocolException($"The Claude CLI process failed to start: {exception.Message}");
        }
        _startupRetryNotBeforeUtc = default;

        var execution = new TurnExecution(threadId, turnId, process);
        _activeTurns[threadId] = execution;
        try
        {
            // Single user message, then EOF — the measured "respawn turn" shape (probe 3/3).
            await process.StandardInput.WriteLineAsync(
                BuildUserMessageJson(message, imagePaths)).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AgentProcessHygiene.KillProcessTree(process);
            _activeTurns.TryRemove(new KeyValuePair<string, TurnExecution>(threadId, execution));
            throw new AgentProtocolException($"Writing the turn input to the Claude CLI failed: {exception.Message}");
        }
        execution.Pump = Task.Run(() => PumpTurnAsync(state, execution), CancellationToken.None);
        return turnId;
    }

    public Task InterruptTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default)
    {
        if (_activeTurns.TryGetValue(threadId, out var execution) &&
            string.Equals(execution.TurnId, turnId, StringComparison.Ordinal))
        {
            execution.Interrupted = true;
            AgentProcessHygiene.KillProcessTree(execution.Process);
        }
        return Task.CompletedTask;
    }

    public Task CompactThreadAsync(string threadId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Claude Code compacts its own context; SupportsCompaction=false keeps callers away.");

    public Task<AgentTurnReadResult?> ReadTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default)
    {
        // Non-throwing by contract: the authoritative state IS this buffer (there is no server to
        // re-ask), so the polling loop always reads a healthy snapshot and codex-shaped recovery
        // never fires. Unknown turn -> null, which the orchestrator treats as "not persisted yet".
        if (_activeTurns.TryGetValue(threadId, out var execution) &&
            string.Equals(execution.TurnId, turnId, StringComparison.Ordinal))
        {
            return Task.FromResult<AgentTurnReadResult?>(execution.Snapshot());
        }
        return Task.FromResult<AgentTurnReadResult?>(null);
    }

    public async Task StopAsync()
    {
        foreach (var execution in _activeTurns.Values)
        {
            execution.Interrupted = true;
            AgentProcessHygiene.KillProcessTree(execution.Process);
        }
        foreach (var execution in _activeTurns.Values)
        {
            if (execution.Pump is { } pump)
            {
                try
                {
                    await pump.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "A Claude turn pump faulted during StopAsync.");
                }
            }
        }
    }

    public bool TryGetActiveTurn(string threadId, out string turnId)
    {
        if (_activeTurns.TryGetValue(threadId, out var execution))
        {
            turnId = execution.TurnId;
            return true;
        }
        turnId = string.Empty;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        foreach (var state in _threads.Values)
        {
            _secrets.Revoke(state.ThreadId);
        }
    }

    // ------------------------------------------------------------------ spawn plumbing

    internal ThreadState RegisterThread(string threadId) =>
        _threads.GetOrAdd(threadId, id =>
        {
            var projectHome = _planner.EnsureCreated(_options.ResolveDataDirectory(), _logger);
            var home = Path.Combine(projectHome, "sessions", Guid.Parse(id).ToString("N"));
            Directory.CreateDirectory(home);
            return new ThreadState(id, home);
        });

    private void ScaffoldHome(ThreadState state)
    {
        var instructions = _instructionComposer?.Compose(ClaudeThreadInstructions) ?? ClaudeThreadInstructions;
        _scaffolder.ScaffoldSessionHome(state.Home, instructions);
    }

    private async Task<string> WriteMcpConfigAsync(ThreadState state, CancellationToken cancellationToken)
    {
        // A fresh secret every spawn: rotation is free, and a leaked older mcp.json dies with it.
        var secret = _secrets.Issue(state.ThreadId);
        var baseUri = await _endpoints.WhenReady.ConfigureAwait(false);
        var config = new
        {
            mcpServers = new
            {
                vino = new
                {
                    type = "http",
                    url = new Uri(baseUri, "/mcp").ToString(),
                    headers = new Dictionary<string, string> { ["X-Vino-Secret"] = secret }
                }
            }
        };
        var path = Path.Combine(state.Home, "mcp.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(config, JsonDefaults.Options),
            cancellationToken).ConfigureAwait(false);
        return path;
    }

    internal ProcessStartInfo CreateTurnStartInfo(
        string executable,
        ThreadState state,
        string mcpConfigPath,
        string? model,
        string? effort)
    {
        var startInfo = AgentProcessHygiene.CreateBaseProcessStartInfo(
            executable,
            state.Home,
            redirectStandardInput: true,
            environment: null);
        // A background auto-update swapping the exe mid-session is a real failure mode; the
        // resolver already picked the binary deliberately.
        startInfo.Environment["DISABLE_AUTOUPDATER"] = "1";

        var arguments = startInfo.ArgumentList;
        arguments.Add("-p");
        arguments.Add("--output-format");
        arguments.Add("stream-json");
        arguments.Add("--input-format");
        arguments.Add("stream-json");
        arguments.Add("--verbose");
        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model);
        }
        if (!string.IsNullOrWhiteSpace(effort))
        {
            arguments.Add("--effort");
            arguments.Add(effort);
        }
        // First spawn creates the conversation under OUR pre-minted id; later spawns resume it.
        // The JSONL's existence is the durable truth for "has this conversation ever been
        // created" — it survives host restarts, which an in-memory flag would not.
        if (ConversationFileExists(state))
        {
            arguments.Add("--resume");
        }
        else
        {
            arguments.Add("--session-id");
        }
        arguments.Add(state.ThreadId);
        arguments.Add("--mcp-config");
        arguments.Add(mcpConfigPath);
        arguments.Add("--strict-mcp-config");
        // No built-in tools at all (Task/Bash/Read/Edit/Write gone) — vino_v1 over /mcp is the
        // whole tool surface, so no permission prompts and no side channels.
        arguments.Add("--tools");
        arguments.Add("");
        arguments.Add("--allowedTools");
        arguments.Add(string.Join(
            ",",
            VinoMcpEndpoint.ConvertSpecs().Select(tool => $"mcp__{VinoMcpEndpoint.ServerName}__{tool.Name}")));
        // "project" and nothing else — measured 2026-08-24: --setting-sources "" also blocks
        // CLAUDE.md (the entire instruction vector goes dark), while "project" reads it and still
        // keeps the user's global settings (hooks, personal MCP connectors) out. The project
        // scope is the thread home WE scaffold, so nothing foreign can ride it.
        arguments.Add("--setting-sources");
        arguments.Add("project");
        return startInfo;
    }

    internal static string BuildUserMessageJson(string message, IReadOnlyList<string>? imagePaths)
    {
        var content = new List<object>();
        foreach (var imagePath in imagePaths ?? [])
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(imagePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A missing attachment must not sink the turn; the text still travels.
                continue;
            }
            content.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = MediaTypeFor(imagePath),
                    data = Convert.ToBase64String(bytes)
                }
            });
        }
        content.Add(new { type = "text", text = message });
        return JsonSerializer.Serialize(
            new { type = "user", message = new { role = "user", content } },
            JsonDefaults.Options);
    }

    private static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/png"
    };

    private static bool ConversationFileExists(ThreadState state)
    {
        // The CLI stores conversations at <claude-home>/projects/<slug>/<session-id>.jsonl where
        // slug = the cwd with every non-alphanumeric rune replaced by '-' (measured; the reason
        // the home must be ASCII in the first place).
        var slug = SlugForCwd(state.Home);
        var jsonl = Path.Combine(
            ClaudeInstallation.ResolveClaudeHome(),
            "projects",
            slug,
            state.ThreadId + ".jsonl");
        return File.Exists(jsonl);
    }

    internal static string SlugForCwd(string cwd)
    {
        var builder = new StringBuilder(cwd.Length);
        foreach (var ch in Path.GetFullPath(cwd))
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        }
        return builder.ToString();
    }

    // ------------------------------------------------------------------ stream pump

    private async Task PumpTurnAsync(ThreadState state, TurnExecution execution)
    {
        var process = execution.Process;
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                execution.RecordStderr(line);
            }
        });
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    await ProcessStreamLineAsync(state, execution, line).ConfigureAwait(false);
                }
                catch (JsonException exception)
                {
                    // Fail-closed per LINE, not per turn: hooks and future event types must not
                    // sink a running turn.
                    _logger.LogWarning(
                        "Malformed Claude stream-json line on turn {TurnId}: {Message}",
                        execution.TurnId,
                        exception.Message);
                }
            }
            await stderrTask.ConfigureAwait(false);
            process.WaitForExit();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "The Claude turn pump for {TurnId} faulted.", execution.TurnId);
        }
        finally
        {
            await FinalizeTurnAsync(state, execution).ConfigureAwait(false);
            _activeTurns.TryRemove(new KeyValuePair<string, TurnExecution>(state.ThreadId, execution));
            process.Dispose();
        }
    }

    internal async Task ProcessStreamLineAsync(ThreadState state, TurnExecution execution, string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        switch (type)
        {
            case "system":
                HandleSystemEvent(execution, root);
                break;
            case "assistant":
                await HandleAssistantEventAsync(state, execution, root).ConfigureAwait(false);
                break;
            case "rate_limit_event":
                // The shape has status/resetsAt/overageStatus but NO usedPercent, so it cannot
                // feed the codex percent-meter; it surfaces as runtime.claudeLimits verbatim.
                // Never routed into SessionUsageState's account snapshot — that single slot is
                // codex's, and cross-backend overwrites were the R14 hazard.
                if (root.TryGetProperty("rate_limit_info", out var info))
                {
                    var clone = info.Clone();
                    state.LatestRateLimit = clone;
                    LatestAccountRateLimit = clone;
                }
                break;
            case "result":
                HandleResultEvent(state, execution, root);
                break;
            default:
                // Unknown event types are expected (user hooks fire even under -p, new CLI
                // versions add types); tolerance is part of the contract.
                break;
        }
    }

    private void HandleSystemEvent(TurnExecution execution, JsonElement root)
    {
        if (!root.TryGetProperty("subtype", out var subtypeElement) ||
            subtypeElement.GetString() is not "init")
        {
            return;
        }
        if (root.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in servers.EnumerateArray())
            {
                var name = server.TryGetProperty("name", out var n) ? n.GetString() : null;
                var status = server.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (name == VinoMcpEndpoint.ServerName &&
                    !string.Equals(status, "connected", StringComparison.OrdinalIgnoreCase))
                {
                    // The turn continues — the model may still answer — but every tool call will
                    // fail, so say why in the log while it is diagnosable.
                    _logger.LogWarning(
                        "Claude turn {TurnId}: MCP server '{Server}' is '{Status}' (expected connected).",
                        execution.TurnId,
                        name,
                        status);
                }
            }
        }
    }

    private async Task HandleAssistantEventAsync(ThreadState state, TurnExecution execution, JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message))
        {
            return;
        }
        // One event per content block, all sharing message.id — usage must count each id ONCE.
        var messageId = message.TryGetProperty("id", out var idElement)
            ? idElement.GetString() ?? execution.TurnId
            : execution.TurnId;
        if (message.TryGetProperty("usage", out var usage) && execution.CountMessageOnce(messageId))
        {
            state.AddTokens(ClaudeUsageParser.ReadTurnTokens(usage));
        }
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var blockType) &&
                blockType.GetString() == "text" &&
                block.TryGetProperty("text", out var textElement) &&
                textElement.GetString() is { Length: > 0 } text)
            {
                var index = execution.AppendMessage(messageId, text);
                // Dialect v1: assistant prose is an item/completed agentMessage. phase
                // "final_answer" mirrors what the persistence path expects from codex.
                await RaiseNotificationAsync("item/completed", new
                {
                    threadId = state.ThreadId,
                    turnId = execution.TurnId,
                    item = new { type = "agentMessage", id = $"{messageId}:{index}", text, phase = "final_answer" }
                }).ConfigureAwait(false);
            }
        }
    }

    private void HandleResultEvent(ThreadState state, TurnExecution execution, JsonElement root)
    {
        // Verdict from the three honest signals; subtype reads "success" even on a 404 (measured).
        var isError = root.TryGetProperty("is_error", out var isErrorElement) &&
            isErrorElement.ValueKind == JsonValueKind.True;
        var terminalReason = root.TryGetProperty("terminal_reason", out var reasonElement)
            ? reasonElement.GetString()
            : null;
        var apiErrorStatus = root.TryGetProperty("api_error_status", out var apiStatusElement) &&
            apiStatusElement.ValueKind is JsonValueKind.Number or JsonValueKind.String
                ? apiStatusElement.ToString()
                : null;
        if (root.TryGetProperty("usage", out var usage) && execution.CountMessageOnce("result"))
        {
            state.AddTokens(ClaudeUsageParser.ReadTurnTokens(usage));
        }

        var failed = isError ||
            apiErrorStatus is not null ||
            (terminalReason is not null &&
             !string.Equals(terminalReason, "completed", StringComparison.OrdinalIgnoreCase));
        if (failed)
        {
            // The CLI's own text verbatim: the orchestrator's context-overflow retry matches on
            // the provider message ("prompt is too long"), so no paraphrasing here.
            var resultText = root.TryGetProperty("result", out var resultElement)
                ? resultElement.GetString()
                : null;
            var detailParts = new List<string>();
            if (terminalReason is not null)
            {
                detailParts.Add($"terminal_reason={terminalReason}");
            }
            if (apiErrorStatus is not null)
            {
                detailParts.Add($"api_error_status={apiErrorStatus}");
            }
            execution.SetOutcome(
                "failed",
                new AgentTurnError(
                    string.IsNullOrWhiteSpace(resultText) ? "The Claude CLI reported a failed turn." : resultText,
                    detailParts.Count > 0 ? string.Join(", ", detailParts) : null,
                    null));
        }
        else
        {
            execution.SetOutcome("completed", null);
        }
    }

    internal async Task FinalizeTurnAsync(ThreadState state, TurnExecution execution)
    {
        // Precedence: a result that fully arrived before the kill is a completed turn (the work
        // happened; SetOutcome is sticky). An interrupt that beat the result -> interrupted; no
        // result and no interrupt (crash, EOF) -> failure carrying exit code and stderr tail.
        if (execution.Interrupted)
        {
            execution.SetOutcome("interrupted", null);
        }
        else if (execution.Status is not ("completed" or "failed"))
        {
            var exitCode = TryReadExitCode(execution.Process);
            execution.SetOutcome(
                "failed",
                new AgentTurnError(
                    $"The Claude CLI exited (code {exitCode?.ToString() ?? "unknown"}) without a result event.",
                    execution.StderrTail(),
                    null));
        }

        var snapshot = execution.Snapshot();
        await RaiseNotificationAsync("turn/completed", new
        {
            threadId = state.ThreadId,
            turn = new
            {
                id = execution.TurnId,
                status = snapshot.Status,
                error = snapshot.Error is null
                    ? null
                    : new { message = snapshot.Error.Message, additionalDetails = snapshot.Error.AdditionalDetails },
                // Cumulative thread tokens (codex semantics); ContextWindow deliberately absent —
                // Claude manages its own window, so the pre-turn compaction gate stays dark.
                usage = new { totalTokens = state.CumulativeTokens }
            }
        }).ConfigureAwait(false);
    }

    private static int? TryReadExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task RaiseNotificationAsync(string method, object parameters)
    {
        if (NotificationReceived is not { } handler)
        {
            return;
        }
        try
        {
            await handler(method, JsonSerializer.SerializeToElement(parameters, JsonDefaults.Options))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "A Claude notification handler failed for {Method}.", method);
        }
    }

    // ------------------------------------------------------------------ state records

    internal sealed class ThreadState(string threadId, string home)
    {
        private long _cumulativeTokens;

        public string ThreadId { get; } = threadId;
        public string Home { get; } = home;
        public JsonElement? LatestRateLimit { get; set; }
        public long CumulativeTokens => Volatile.Read(ref _cumulativeTokens);
        public void AddTokens(long tokens) => Interlocked.Add(ref _cumulativeTokens, tokens);
    }

    internal sealed class TurnExecution(string threadId, string turnId, Process process)
    {
        private readonly object _gate = new();
        private readonly List<AgentTurnMessage> _messages = [];
        private readonly HashSet<string> _countedMessageIds = new(StringComparer.Ordinal);
        private readonly Queue<string> _stderr = new();
        private string _status = "inProgress";
        private AgentTurnError? _error;

        public string ThreadId { get; } = threadId;
        public string TurnId { get; } = turnId;
        public Process Process { get; } = process;
        public Task? Pump { get; set; }
        public volatile bool Interrupted;

        public string Status
        {
            get { lock (_gate) { return _status; } }
        }

        public bool CountMessageOnce(string messageId)
        {
            lock (_gate) { return _countedMessageIds.Add(messageId); }
        }

        public int AppendMessage(string messageId, string text)
        {
            lock (_gate)
            {
                _messages.Add(new AgentTurnMessage($"{messageId}:{_messages.Count}", text, "final_answer"));
                return _messages.Count - 1;
            }
        }

        public void SetOutcome(string status, AgentTurnError? error)
        {
            lock (_gate)
            {
                // Terminal states are sticky: a late result event cannot overwrite an interrupt.
                if (_status is "inProgress")
                {
                    _status = status;
                    _error = error;
                }
            }
        }

        public void RecordStderr(string line)
        {
            lock (_gate)
            {
                _stderr.Enqueue(line);
                while (_stderr.Count > 32)
                {
                    _stderr.Dequeue();
                }
            }
        }

        public string? StderrTail()
        {
            lock (_gate) { return _stderr.Count == 0 ? null : string.Join("\n", _stderr); }
        }

        public AgentTurnReadResult Snapshot()
        {
            lock (_gate)
            {
                return new AgentTurnReadResult(TurnId, _status, _error, [.. _messages]);
            }
        }
    }

    /// <summary>
    /// The Claude edition of the session charter. Deltas from the codex ThreadInstructions are
    /// exactly the surface differences: no scratch shell, no web tools, no sub-agents — the
    /// vino_v1 tools over MCP are the entire tool surface. The broker discipline, the
    /// gptino:auto sentinel (server-parsed, byte-stable), and the verification order are shared
    /// verbatim so both backends live under one behavioral contract.
    /// </summary>
    internal const string ClaudeThreadInstructions = """
        You are a Vino modeling session attached to one explicit Rhino/Grasshopper document pair.
        You may inspect immutable state in parallel with other sessions. The vino_v1 tools are your entire tool
        surface in this session: there is no shell, no file access, no web access, and no sub-agents or Task tool.
        Never try to mutate Rhino or Grasshopper by any other means; use only vino_v1 tools for document state and
        change submission.
        Start modeling work with snapshot_read; its sessionId and target.projectId are the exact IDs required by ChangeSet.
        Use component_catalog to find a component's type GUID only when you do not already know it (skip it for the
        well-known GUIDs in the gh-authoring skill); use rhino_list before broad Rhino scene edits.
        Verify before you submit: draft payloads and scripts in session artifacts (artifact_write / artifact_read),
        work through formulas, point counts, domains, and geometry math in your reasoning, and read committed results
        back with inspect_outputs and data_flow_read after each job. A failed submitted job costs a full solve-verify
        round trip; careful drafting is free.
        Draft and validate code before calling change_submit. The central broker owns ordering, conflict checks, the writer
        lease, live execution, verification, and history. A submitted change is not successful until job_status reports a
        verified terminal result. Preserve document units, tolerances, data trees, and existing wiring unless requested.
        For complex work, iterate in session artifacts, inspect runtime messages, and correct deterministic pre-write failures
        instead of guessing. Iterate from each job result: a failed job with an applied block is resubmitted with gptino:auto
        after fixing the content (new idempotency key for changed content) — do not re-read the canvas to recover.
        If a job reports recoveryRequired, stop automatic mutation and explain the uncertain live state to the user.
        """;
}

/// <summary>Token accounting for Claude usage payloads (result/assistant usage objects).</summary>
internal static class ClaudeUsageParser
{
    /// <summary>
    /// Total tokens the API processed for one message/result: input + output + both cache
    /// directions (shape measured from live result events, step-0 probe).
    /// </summary>
    public static long ReadTurnTokens(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }
        long total = 0;
        foreach (var field in (string[])
                 ["input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens"])
        {
            if (usage.TryGetProperty(field, out var value) && value.TryGetInt64(out var tokens))
            {
                total += tokens;
            }
        }
        return total;
    }
}
