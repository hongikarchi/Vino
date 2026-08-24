namespace Vino.AgentHost.Claude;

/// <summary>
/// Writes a session home's CLAUDE.md — the Claude CLI's instruction delivery vector. Codex takes
/// instructions as an RPC string parameter; Claude reads CLAUDE.md from the cwd on every launch,
/// so the composed instructions (~38KB with house rules — far past the 32KB Windows argument
/// limit, which rules out --append-system-prompt) land here as a managed block, re-rendered on
/// EVERY spawn for the same reason codex re-composes on resume: rules.md/memory.md are living
/// sources. Text outside the markers is preserved verbatim (wireify HomeScaffolder pattern).
/// </summary>
public sealed class ClaudeHomeScaffolder
{
    public const string BeginMarker = "<!-- vino:managed:begin -->";
    public const string EndMarker = "<!-- vino:managed:end -->";

    /// <summary>Renders/refreshes the managed block in &lt;home&gt;\CLAUDE.md. Idempotent.</summary>
    public string ScaffoldSessionHome(string homeDirectory, string composedInstructions)
    {
        Directory.CreateDirectory(homeDirectory);
        var claudeMd = Path.Combine(homeDirectory, "CLAUDE.md");
        var managed = $"{BeginMarker}\n{composedInstructions.TrimEnd()}\n{EndMarker}";
        string content;
        if (File.Exists(claudeMd))
        {
            var existing = File.ReadAllText(claudeMd);
            var begin = existing.IndexOf(BeginMarker, StringComparison.Ordinal);
            var end = existing.IndexOf(EndMarker, StringComparison.Ordinal);
            content = begin >= 0 && end > begin
                ? existing[..begin] + managed + existing[(end + EndMarker.Length)..]
                // Markers lost (hand-edited away): lead with the managed block, keep their text.
                : managed + "\n\n" + existing;
        }
        else
        {
            content = managed + "\n";
        }
        File.WriteAllText(claudeMd, content);
        return claudeMd;
    }
}
