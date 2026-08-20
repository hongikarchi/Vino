namespace Vino.AgentHost.Runtime;

/// <summary>
/// Per-session standing consent minted by the approval card's "허용 + 이 세션에서 계속 허용"
/// button: later destructive submits from that session auto-issue their grant without a card.
/// Deliberately in-memory — a host restart clears every consent, so the next destructive write
/// after a restart asks again. Scope is the whole session (not per operation kind): the card
/// that mints it does not know which operations will follow, and the narrower slicing the user
/// rejected in design is covered by the permission-mode ladder instead.
/// </summary>
public sealed class StandingApprovals
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _sessions = new();

    public void Grant(Guid sessionId) => _sessions[sessionId] = DateTimeOffset.UtcNow;

    public bool Release(Guid sessionId) => _sessions.TryRemove(sessionId, out _);

    public bool IsGranted(Guid sessionId) => _sessions.ContainsKey(sessionId);
}
