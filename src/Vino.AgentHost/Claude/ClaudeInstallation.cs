using Vino.AgentHost.Hosting;

namespace Vino.AgentHost.Claude;

/// <summary>
/// Claude Code CLI install facts (CodexInstallation mirror): config home, stored-credentials
/// heuristic, executable location. Credentials are a FILE, not the OS keychain
/// (<c>~/.claude/.credentials.json</c>, spike 2026-08-19 §3); its presence + non-emptiness is the
/// logged-in heuristic — token values are never read into logs.
/// </summary>
public static class ClaudeInstallation
{
    /// <summary>
    /// The CLI's config home: <c>CLAUDE_CONFIG_DIR</c> when set (also the test-isolation hook),
    /// else <c>~/.claude</c>.
    /// </summary>
    public static string ResolveClaudeHome()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude");
    }

    public static string CredentialsFilePath() =>
        Path.Combine(ResolveClaudeHome(), ".credentials.json");

    public static bool HasStoredCredentials()
    {
        try
        {
            var info = new FileInfo(CredentialsFilePath());
            return info.Exists && info.Length > 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static bool TryLocateExecutable(AgentHostOptions options, out string executablePath)
    {
        if (ClaudeExecutableResolver.TryResolve(options, out var candidate))
        {
            executablePath = candidate.Path;
            return true;
        }
        executablePath = string.Empty;
        return false;
    }
}
