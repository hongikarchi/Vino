using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Vino.AgentHost.Hosting;

namespace Vino.AgentHost.Claude;

/// <summary>
/// Drafts the user's likely NEXT message as ghost text for the composer (Tab accepts). One cheap
/// haiku call per completed turn, requested by the panel when a session goes idle — never during a
/// running turn, so the suggestion appears exactly when the user is deciding what to say.
/// </summary>
/// <remarks>
/// Everything here fails SILENT and returns null: no claude executable, no auth, a slow call, a
/// refusal-shaped reply — a ghost that does not appear costs nothing, while an error toast for a
/// convenience feature would cost attention. Results are cached per (session, conversation tail)
/// so re-renders and idle re-fetches never re-bill.
/// </remarks>
public sealed class PromptSuggestionService
{
    private static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(15);

    private readonly AgentHostOptions _options;
    private readonly ILogger<PromptSuggestionService> _logger;
    private readonly ConcurrentDictionary<Guid, (string Key, string? Suggestion)> _cache = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public PromptSuggestionService(AgentHostOptions options, ILogger<PromptSuggestionService> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// One suggested next prompt for this conversation tail, or null when none can be made.
    /// <paramref name="messages"/> is oldest-first (role, text) — only the tail is used.
    /// </summary>
    public async Task<string?> SuggestAsync(
        Guid sessionId,
        IReadOnlyList<(string Role, string Text)> messages,
        string? goal,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0 ||
            !ClaudeExecutableResolver.TryResolve(_options, out var candidate))
        {
            return null;
        }
        var executable = candidate.Path;

        var key = CacheKey(messages);
        if (_cache.TryGetValue(sessionId, out var cached) && cached.Key == key)
        {
            return cached.Suggestion;
        }

        // Serialized on purpose: this is a convenience, and N idle sessions must not fan out into
        // N concurrent CLI spawns on one workstation.
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(sessionId, out cached) && cached.Key == key)
            {
                return cached.Suggestion;
            }
            var suggestion = await GenerateAsync(executable, messages, goal, cancellationToken)
                .ConfigureAwait(false);
            _cache[sessionId] = (key, suggestion);
            return suggestion;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<string?> GenerateAsync(
        string executable,
        IReadOnlyList<(string Role, string Text)> messages,
        string? goal,
        CancellationToken cancellationToken)
    {
        var prompt = BuildPrompt(messages, goal);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add("claude-haiku-4-5");
        // No tools and one turn: this is a text completion, not an agent.
        startInfo.ArgumentList.Add("--tools");
        startInfo.ArgumentList.Add("");
        startInfo.ArgumentList.Add("--max-turns");
        startInfo.ArgumentList.Add("1");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CallBudget);
            string output;
            try
            {
                output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone.
                }
                return null;
            }
            return process.ExitCode == 0 ? Normalize(output) : null;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Prompt suggestion call failed; the ghost simply does not appear.");
            return null;
        }
    }

    private static string BuildPrompt(IReadOnlyList<(string Role, string Text)> messages, string? goal)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "You draft the USER's next message in a Rhino/Grasshopper modeling session with an AI " +
            "assistant. Reply with EXACTLY ONE plausible next instruction the user might send — " +
            "their voice, their language (Korean if the conversation is Korean), one line, no " +
            "quotes, no explanation, no numbering. Prefer the concrete next step over pleasantries.");
        if (!string.IsNullOrWhiteSpace(goal))
        {
            builder.AppendLine("Session goal: " + Clip(goal!, 300));
        }
        builder.AppendLine("Conversation tail:");
        foreach (var (role, text) in messages.TakeLast(6))
        {
            builder.AppendLine(FormattableString.Invariant($"[{role}] {Clip(text, 700)}"));
        }
        return builder.ToString();
    }

    private static string? Normalize(string output)
    {
        var line = output.Trim();
        var firstBreak = line.IndexOf('\n');
        if (firstBreak >= 0)
        {
            line = line[..firstBreak].Trim();
        }
        line = line.Trim('"', '“', '”');
        if (line.Length is 0 or > 200)
        {
            return null;
        }
        return line;
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max];

    private static string CacheKey(IReadOnlyList<(string Role, string Text)> messages)
    {
        var tail = messages[^1];
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                FormattableString.Invariant($"{messages.Count}|{tail.Role}|{tail.Text}"))));
    }
}
