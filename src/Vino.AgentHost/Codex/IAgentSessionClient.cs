using System.Text.Json;

namespace Vino.AgentHost.Codex;

public interface IAgentSessionClient
{
    /// <summary>
    /// Raised for backend events the orchestrator consumes. The (method, params) shapes are a
    /// HOST-OWNED dialect ("notification dialect v1") — historically codex app-server wire names,
    /// now the contract every backend must emit (synthesizing them if its native protocol
    /// differs). The orchestrator decodes exactly these:
    /// <list type="bullet">
    /// <item><c>item/completed</c> — {threadId, turnId?, item:{type:"agentMessage", text, phase?}}
    ///   (assistant prose; also item.type "contextCompaction" marks a finished compaction)</item>
    /// <item><c>turn/completed</c> — {threadId?, turn:{id, status, error?, usage?}} (terminal
    ///   status: completed|failed|interrupted|canceled; usage feeds SessionUsageState)</item>
    /// <item><c>thread/compacted</c> — {threadId} (compaction completion signal)</item>
    /// <item><c>*tokenUsage/updated</c> / <c>*tokenCount</c> / <c>*rateLimits/updated</c> —
    ///   usage/quota telemetry, parsed leniently by SessionUsageState.TryParse</item>
    /// </list>
    /// Unknown methods are ignored, so a backend may emit more than this.
    /// </summary>
    event Func<string, JsonElement, Task>? NotificationReceived;

    bool IsRunning { get; }

    /// <summary>
    /// Whether <see cref="CompactThreadAsync"/> is meaningful for this backend. When false the
    /// orchestrator never calls it (Claude CLI compacts its own context; asking would stall a
    /// 90s completion wait that can never be signaled).
    /// </summary>
    bool SupportsCompaction { get; }

    Task<string> StartThreadAsync(
        string cwd,
        string? model,
        CancellationToken cancellationToken = default);

    Task ResumeThreadAsync(
        string threadId,
        string cwd,
        string? model,
        CancellationToken cancellationToken = default);

    Task<string> StartTurnAsync(
        string threadId,
        string message,
        string? model,
        string? effort,
        IReadOnlyList<string>? imagePaths = null,
        CancellationToken cancellationToken = default);

    Task InterruptTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the backend to compact (summarize in place) the thread so later turns run against a
    /// smaller context. Only called when <see cref="SupportsCompaction"/> is true. The call
    /// acknowledges the start; completion is signaled by a thread/compacted notification or a
    /// contextCompaction thread item.
    /// </summary>
    Task CompactThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default);

    Task<AgentTurnReadResult?> ReadTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default);

    Task StopAsync();
}

// ---------------------------------------------------------------------------------------------
// Backend-neutral turn wire types. Every IAgentSessionClient implementation returns these; the
// orchestrator never sees a provider-specific shape.
// ---------------------------------------------------------------------------------------------

public sealed record AgentTurnReadResult(
    string TurnId,
    string Status,
    AgentTurnError? Error,
    IReadOnlyList<AgentTurnMessage> AgentMessages);

public sealed record AgentTurnError(
    string Message,
    string? AdditionalDetails,
    // Raw provider-side error payload, kept verbatim for diagnostics (e.g. codex serverOverloaded).
    JsonElement? ProviderErrorInfo);

public sealed record AgentTurnMessage(
    string Id,
    string Text,
    string? Phase);

/// <summary>A malformed or contract-violating reply from the backend process/CLI.</summary>
public sealed class AgentProtocolException(string message) : InvalidOperationException(message);
