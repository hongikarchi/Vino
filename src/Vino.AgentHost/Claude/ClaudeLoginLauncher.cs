using System.Diagnostics;
using Vino.AgentHost.Hosting;

namespace Vino.AgentHost.Claude;

/// <summary>
/// CodexLoginLauncher mirror for Claude: opens a visible console running
/// <c>claude auth login</c> (subcommand verified against CLI v2.1.241 — not the /login REPL
/// command), or, with the CLI missing, the official native installer chained into login.
/// AgentHost is windowless with redirected stdio, so the flow needs its own console-owning
/// process; the spawned shell inherits AgentHost's environment (including CLAUDE_CONFIG_DIR),
/// so credentials land where the backend client reads them.
/// </summary>
public sealed class ClaudeLoginLauncher
{
    private readonly AgentHostOptions _options;
    private readonly ILogger<ClaudeLoginLauncher>? _logger;

    public ClaudeLoginLauncher(AgentHostOptions options, ILogger<ClaudeLoginLauncher>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public bool TryLaunch(out string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            message = "Opening a Claude login terminal is only supported on Windows.";
            return false;
        }
        var hasCli = ClaudeInstallation.TryLocateExecutable(_options, out var claudePath);
        try
        {
            var arguments = hasCli
                ? $"/k \"\"{claudePath}\" auth login\""
                : $"/k \"\"{WriteInstallScript()}\"\"";
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                Arguments = arguments,
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            using var process = Process.Start(startInfo);
            message = hasCli
                ? "Opened a terminal running 'claude auth login'."
                : "Opened a terminal installing the Claude CLI, then signing in.";
            return true;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Could not open the Claude login terminal.");
            message = $"Could not open the login terminal: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// The official native installer (PowerShell one-liner) chained into login. No npm here: the
    /// npm claude-code package is a node CLI with no launchable native exe (see
    /// ClaudeExecutableResolver), so the native installer is the only remediation that produces
    /// a binary the backend can spawn.
    /// </summary>
    private static string WriteInstallScript()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "vino-claude-setup.cmd");
        File.WriteAllText(scriptPath, string.Join("\r\n", new[]
        {
            "@echo off",
            "echo Installing the Claude Code CLI (official installer)...",
            "powershell -NoProfile -ExecutionPolicy Bypass -Command \"irm https://claude.ai/install.ps1 | iex\"",
            "if errorlevel 1 (",
            "  echo.",
            "  echo Install failed. See https://docs.anthropic.com/claude-code for manual steps.",
            "  goto :eof",
            ")",
            "set \"CLAUDE_CMD=%USERPROFILE%\\.local\\bin\\claude.exe\"",
            "if not exist \"%CLAUDE_CMD%\" set \"CLAUDE_CMD=claude\"",
            "call \"%CLAUDE_CMD%\" auth login",
            "",
        }));
        return scriptPath;
    }
}
