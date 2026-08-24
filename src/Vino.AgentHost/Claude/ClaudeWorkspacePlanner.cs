using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Vino.AgentHost.Claude;

public enum ClaudeHomeTier
{
    /// <summary>The project data directory itself is ASCII — the common (English-account) case.</summary>
    DataDirectory = 1,

    /// <summary>The data directory's 8.3 short path is ASCII (non-ASCII profile, 8.3 enabled).</summary>
    ShortPath = 2,

    /// <summary>%ProgramData% fallback (locale-independent ASCII), hardened with an explicit DACL.</summary>
    ProgramData = 3,
}

/// <summary>
/// Picks an ASCII-only home for a session's Claude CLI children. Why it must be ASCII: the CLI
/// derives its conversation-JSONL project slug from the cwd by replacing every non-alphanumeric
/// rune with '-', so Korean (or any non-ASCII) path segments collapse into colliding slugs and
/// sessions bleed into each other's history (spike 2026-08-19 §카탈로그·저장소). The project data
/// directory lives under %LOCALAPPDATA%, which embeds the Windows account name — ASCII for most
/// accounts, but not guaranteed.
///
/// Three tiers, first ASCII wins:
///   1. &lt;dataDirectory&gt;\claude              — everything else about the session already lives here
///   2. 8.3 short path of the data directory  — same location through an ASCII alias
///   3. %ProgramData%\Vino\claude-homes\&lt;sidHash8&gt;\&lt;projectHash16&gt; — locale-independent ASCII;
///      sidHash8 partitions users, and EnsureCreated best-effort hardens the tree with an
///      owner+SYSTEM+Administrators DACL (inheritance stripped) because CLAUDE.md lives here and a
///      shared location must not let another local account inject prompt text.
/// </summary>
public sealed class ClaudeWorkspacePlanner
{
    private readonly Func<string, string?> _shortPathResolver;

    public ClaudeWorkspacePlanner()
        : this(TryGetShortPath)
    {
    }

    /// <summary>Test hook: 8.3 resolution is machine/volume dependent (it can be disabled).</summary>
    public ClaudeWorkspacePlanner(Func<string, string?> shortPathResolver)
    {
        _shortPathResolver = shortPathResolver;
    }

    /// <summary>Pure planning — no IO beyond the injected short-path lookup.</summary>
    public (string Path, ClaudeHomeTier Tier) Plan(string dataDirectory)
    {
        var tier1 = Path.Combine(Path.GetFullPath(dataDirectory), "claude");
        if (IsAscii(tier1))
        {
            return (tier1, ClaudeHomeTier.DataDirectory);
        }

        // GetShortPathName only answers for EXISTING paths; the data directory always exists by
        // the time a session spawns (ResolveDataDirectory creates it).
        var shortBase = _shortPathResolver(Path.GetFullPath(dataDirectory));
        if (shortBase is not null && IsAscii(shortBase) &&
            !string.Equals(shortBase, Path.GetFullPath(dataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return (Path.Combine(shortBase, "claude"), ClaudeHomeTier.ShortPath);
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var tier3 = Path.Combine(
            programData,
            "Vino",
            "claude-homes",
            HashHex(CurrentUserSid(), 8),
            HashHex(Path.GetFullPath(dataDirectory).ToUpperInvariant(), 16));
        return (tier3, ClaudeHomeTier.ProgramData);
    }

    /// <summary>Creates the planned home; tier-3 trees get the explicit DACL (best effort).</summary>
    public string EnsureCreated(string dataDirectory, ILogger logger)
    {
        var (path, tier) = Plan(dataDirectory);
        var existed = Directory.Exists(path);
        Directory.CreateDirectory(path);
        if (tier == ClaudeHomeTier.ProgramData && !existed)
        {
            HardenProgramDataTree(path, logger);
        }
        if (!existed)
        {
            logger.LogInformation("Claude session home created at {Path} (tier {Tier}).", path, tier);
        }
        return path;
    }

    internal static bool IsAscii(string value)
    {
        foreach (var ch in value)
        {
            if (ch > 0x7F)
            {
                return false;
            }
        }
        return true;
    }

    private static string CurrentUserSid()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.User?.Value is { Length: > 0 } sid)
                {
                    return sid;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Fall through: the user profile path still partitions.
            }
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string HashHex(string value, int hexLength) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..hexLength]
            .ToLowerInvariant();

    /// <summary>
    /// icacls: strip inheritance, grant only SYSTEM / Administrators / the current user. Best
    /// effort — on failure the default %ProgramData% ACLs still deny other users MODIFY on our
    /// files (they could only add siblings, which --setting-sources "" ignores).
    /// </summary>
    private static void HardenProgramDataTree(string path, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if (sid is null)
            {
                return;
            }
            var startInfo = new ProcessStartInfo("icacls")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add("/inheritance:r");
            foreach (var grantee in new[] { "*S-1-5-18", "*S-1-5-32-544", $"*{sid}" })
            {
                startInfo.ArgumentList.Add("/grant:r");
                startInfo.ArgumentList.Add($"{grantee}:(OI)(CI)F");
            }
            using var process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit(5000) || process.ExitCode != 0)
            {
                logger.LogWarning("icacls hardening of {Path} did not complete cleanly.", path);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "icacls hardening of {Path} failed; relying on default ACLs.", path);
        }
    }

    private static string? TryGetShortPath(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }
        var buffer = new StringBuilder(260);
        var length = GetShortPathNameW(path, buffer, buffer.Capacity);
        if (length == 0 || length > buffer.Capacity)
        {
            return null;
        }
        return buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathNameW(string longPath, StringBuilder shortPath, int bufferLength);
}
