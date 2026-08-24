using System.Diagnostics;
using System.Text;

namespace Vino.AgentHost.Hosting;

/// <summary>
/// Shared process-spawn hygiene for agent backend CLIs (codex app-server, Claude CLI). Extracted
/// verbatim from CodexAppServerClient so every backend spawns children the same battle-tested way:
/// fully redirected UTF-8 stdio, no window, an explicit working directory (NEVER a user project
/// folder — an open cwd handle blocks Rhino's .3dm save-rename), and no VINO_* configuration
/// leaking into the child.
/// </summary>
internal static class AgentProcessHygiene
{
    public static ProcessStartInfo CreateBaseProcessStartInfo(
        string executable,
        string workingDirectory,
        bool redirectStandardInput,
        IEnumerable<KeyValuePair<string, string?>>? environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetFullPath(workingDirectory)
        };

        if (environment is not null)
        {
            startInfo.Environment.Clear();
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }
        RemoveVinoEnvironment(startInfo);
        return startInfo;
    }

    /// <summary>Strips VINO_*/VINO:* keys so host configuration never rides into a child.</summary>
    public static void RemoveVinoEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var key in startInfo.Environment.Keys
                     .Where(IsVinoEnvironmentKey)
                     .ToArray())
        {
            startInfo.Environment.Remove(key);
        }
    }

    public static bool IsVinoEnvironmentKey(string key) =>
        key.StartsWith("VINO_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("VINO:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Best-effort kill of a child and its whole tree (node/npx shims included), bounded wait.
    /// Safe on already-exited or never-started processes.
    /// </summary>
    public static void KillProcessTree(Process? process, int waitMilliseconds = 3000)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(waitMilliseconds);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
