using Vino.AgentHost.Codex;
using Vino.AgentHost.Hosting;

namespace Vino.AgentHost.Claude;

/// <summary>
/// Claude edition of <see cref="CodexAuthProbe"/>: is the CLI installed and signed in, for the
/// panel's chip/gate. Same file-level heuristic discipline — a non-empty
/// <c>~/.claude/.credentials.json</c> (spike §3: credentials are a file, not the OS keychain) —
/// and the same CLI-presence-first ordering: stored credentials with no launchable CLI must read
/// as cli-missing, never signed-in. Token values are never read, only file presence/size.
/// Cached a few seconds so SSE re-projection does not stat the filesystem per event.
/// </summary>
public sealed class ClaudeAuthProbe
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(3);

    private readonly AgentHostOptions _options;
    private readonly object _gate = new();
    private CodexAuthSnapshot? _cached;
    private DateTime _checkedAtUtc = DateTime.MinValue;

    public ClaudeAuthProbe(AgentHostOptions options) => _options = options;

    public CodexAuthSnapshot Read()
    {
        lock (_gate)
        {
            if (_cached is not null && DateTime.UtcNow - _checkedAtUtc < CacheDuration)
            {
                return _cached;
            }
            _cached = Evaluate();
            _checkedAtUtc = DateTime.UtcNow;
            return _cached;
        }
    }

    private CodexAuthSnapshot Evaluate()
    {
        if (!ClaudeInstallation.TryLocateExecutable(_options, out _))
        {
            return new CodexAuthSnapshot(
                CodexAuthStatus.CliMissing,
                "Claude CLI not found — click to open a terminal that installs it and signs in.");
        }
        if (ClaudeInstallation.HasStoredCredentials())
        {
            return new CodexAuthSnapshot(CodexAuthStatus.LoggedIn, "Signed in to Claude.");
        }
        return new CodexAuthSnapshot(
            CodexAuthStatus.LoggedOut,
            "Not signed in — click to run 'claude auth login'.");
    }
}
