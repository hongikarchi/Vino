using System.Diagnostics;
using Vino.AgentHost.Hosting;

namespace Vino.AgentHost.Claude;

/// <summary>One located Claude CLI candidate: the native executable and where it came from.</summary>
public sealed record ClaudeCandidate(string Path, string Source);

/// <summary>
/// Single source of truth for locating the Claude Code CLI — the mirror of
/// CodexExecutableResolver, and for the same reason: the UI probe and execution must converge on
/// the SAME binary. Priority (first match wins):
///
///   1. explicit app setting (<c>--claude-executable</c> / <see cref="AgentHostOptions.ClaudeExecutable"/>)
///   2. <c>CLAUDE_EXECUTABLE</c> environment variable
///   3. a native <c>claude.exe</c> on PATH
///   4. <c>%USERPROFILE%\.local\bin\claude.exe</c> — the native installer's landing spot
///      (a real PE, Bun bundle; spike 2026-08-19 §4)
///   5. the newest exe under <c>%USERPROFILE%\.local\share\claude\versions\</c>
///
/// An explicit choice (1/2) is AUTHORITATIVE — no fallback past it. Unlike codex there is no npm
/// shim mapping: the npm claude-code package is a node CLI with no platform-native exe, so a
/// <c>claude.cmd</c> shim cannot be Process.Start'ed with redirected stdio; users on npm-only
/// installs surface as cli-missing and the login launcher offers the native install. Enumeration
/// is pure file-existence (cheap UI probe); version discovery is best-effort logging only.
/// </summary>
public static class ClaudeExecutableResolver
{
    public static IReadOnlyList<ClaudeCandidate> EnumerateCandidates(AgentHostOptions options)
    {
        var results = new List<ClaudeCandidate>();

        void Add(string? rawPath, string source)
        {
            var native = ResolveNative(rawPath);
            if (native is not null &&
                !results.Any(existing => string.Equals(existing.Path, native, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new ClaudeCandidate(native, source));
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ClaudeExecutable))
        {
            Add(options.ClaudeExecutable, "app setting");
            return results;
        }
        var configuredEnv = Environment.GetEnvironmentVariable("CLAUDE_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configuredEnv))
        {
            Add(configuredEnv, "CLAUDE_EXECUTABLE");
            return results;
        }

        foreach (var onPath in EnumeratePathClaude())
        {
            Add(onPath, "PATH (terminal)");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(Path.Combine(userProfile, ".local", "bin", "claude.exe"), "native install");

        // Version store: full exes, one folder per version. Newest write wins — with
        // DISABLE_AUTOUPDATER=1 on every child the chosen exe cannot be swapped mid-session.
        var versionsRoot = Path.Combine(userProfile, ".local", "share", "claude", "versions");
        if (Directory.Exists(versionsRoot))
        {
            var newest = Directory.EnumerateFiles(versionsRoot, "*.exe", SearchOption.AllDirectories)
                .Select(file => new FileInfo(file))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            Add(newest?.FullName, "version store");
        }

        return results;
    }

    public static bool TryResolve(AgentHostOptions options, out ClaudeCandidate candidate)
    {
        candidate = EnumerateCandidates(options).FirstOrDefault()!;
        return candidate is not null;
    }

    /// <summary>Best-effort <c>claude --version</c> for logging. Null never blocks launch.</summary>
    public static string? ProbeVersion(string executablePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executablePath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }
            var text = output.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumeratePathClaude()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                     .Select(value => value.Trim().Trim('"'))
                     .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            string full;
            try
            {
                full = Path.GetFullPath(directory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }
            var executable = Path.Combine(full, "claude.exe");
            if (File.Exists(executable))
            {
                yield return executable;
            }
        }
    }

    /// <summary>Only a real claude.exe is launchable (see class remarks on npm shims).</summary>
    private static string? ResolveNative(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }
        string path;
        try
        {
            path = Path.GetFullPath(rawPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
        if (!File.Exists(path))
        {
            return null;
        }
        return string.Equals(Path.GetFileName(path), "claude.exe", StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }
}
