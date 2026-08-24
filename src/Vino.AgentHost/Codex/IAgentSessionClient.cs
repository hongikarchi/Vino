using System.Text.Json;

namespace Vino.AgentHost.Codex;

public interface IAgentSessionClient
{
    event Func<string, JsonElement, Task>? NotificationReceived;

    bool IsRunning { get; }

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
    /// Asks the app-server to compact (summarize in place) the thread so later turns run against a
    /// smaller context. The RPC acknowledges the start; completion is signaled by a
    /// thread/compacted notification or a contextCompaction thread item.
    /// </summary>
    Task CompactThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Injects input into an already-running turn (turn/steer) so the model course-corrects without
    /// a restart. Fails if <paramref name="turnId"/> is not the thread's active turn — callers fall
    /// back to starting a fresh turn.
    /// </summary>
    Task SteerTurnAsync(
        string threadId,
        string turnId,
        string message,
        IReadOnlyList<string>? imagePaths = null,
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
