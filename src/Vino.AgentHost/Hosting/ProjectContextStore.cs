using System.Text;
using System.Text.Json;

namespace Vino.AgentHost.Hosting;

/// <summary>Folds durable project context into every Codex thread's base instructions.</summary>
public interface IThreadInstructionComposer
{
    string Compose(string baseInstructions);
}

/// <summary>
/// Durable per-project context folder under the runtime data directory (never the user's
/// project folder). Holds human-editable working rules and an append-only memory ledger.
/// Scaffolding only creates missing files, so user edits always survive; composition
/// re-reads the files on every thread start/resume, so edits apply to the next turn
/// without a restart. Context problems must never block a thread from starting.
/// </summary>
public sealed class ProjectContextStore : IThreadInstructionComposer
{
    private const int MaximumContextFileCharacters = 16 * 1024;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    private const string RulesSeed = """
        # Vino working rules (사용자 편집용)

        Rules in this file are appended to every Vino agent session's instructions
        for this project. Edit freely — changes apply from the next message you send.

        ## Conventions (examples — replace with yours)
        - Units and tolerance: follow the Rhino document settings; never change them.
        - Layer naming: keep generated objects on Vino-managed layers.
        - Grasshopper: prefer small, labeled clusters of components over sprawl.
        """;

    // Layer-curation project table: same entries schema as the shipped alias seed
    // (assets/data/layers/alias-seed-ko.json), plus the selected palette preset. Confirmed
    // card answers accumulate here and override the seed on canonical collision. Deliberately
    // NOT folded into Compose(): the 16 KiB context cap would truncate JSON mid-document —
    // the matcher loads this file on demand at proposal time instead.
    private const string LayerStandardSeed = """
        {
          "meta": {
            "description": "Vino layer-curation project table (사용자 편집 가능). entries는 shipped alias seed와 같은 스키마이며 canonical이 겹치면 이 파일이 이깁니다.",
            "schema": "vino-layer-standard-v1"
          },
          "preset": null,
          "entries": []
        }
        """;

    private const string MemorySeed = """
        # Vino project memory (append-only)

        Lessons this project's agent sessions should start warm with.
        Append one entry per non-obvious fix: symptom → cause → fix.

        <!-- example:
        ## Panel boundary rebuilds drift
        Symptom: rebuilt panel boundaries shift by ~2mm.
        Cause: the facade grid is anchored to a moved block instance.
        Fix: always re-read the anchor point from object 7F2A before regenerating.
        -->
        """;

    public ProjectContextStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ContextDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "context");
    }

    public string ContextDirectory { get; }

    public string RulesPath => Path.Combine(ContextDirectory, "rules.md");

    public string MemoryPath => Path.Combine(ContextDirectory, "MEMORY.md");

    /// <summary>
    /// Which language Vino writes its PROSE in (chat answers, findings, explanations).
    /// UI control labels and typed payloads are unaffected — those stay English so the
    /// vocabulary matches the docs and the operation contract. Stored beside the other
    /// context files so it travels with the project, and composed into every thread's
    /// instructions (a change lands on the next turn's thread start/resume).
    /// </summary>
    public string LanguagePath => Path.Combine(ContextDirectory, "language");

    /// <summary>Layer-curation project table (alias entries + selected preset); see LayerStandardSeed.</summary>
    public string LayerStandardPath => Path.Combine(ContextDirectory, "layer-standard.json");

    public string ReadLanguage()
    {
        try
        {
            if (!File.Exists(LanguagePath)) return "en";
            var value = File.ReadAllText(LanguagePath).Trim().ToLowerInvariant();
            return value == "ko" ? "ko" : "en";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "en";
        }
    }

    /// <summary>
    /// Whether this project permits the host's automatic post-turn canvas tidy.
    ///
    /// <para>
    /// rules.md constrains the MODEL — it is appended to the agent's instructions — but the
    /// post-turn tidy is a host-owned hook that never reads it. A user who wrote
    /// "Do not use auto-tidy layout" in their own rules watched the server rearrange 109
    /// components anyway, then spent twenty minutes putting them back. A rule the product
    /// visibly ignores is worse than no rule.
    /// </para>
    /// <para>
    /// Prose matching is deliberate: the opt-out has to work on the sentence the user already
    /// wrote, not on a setting they would have had to discover. Matching is intentionally narrow
    /// (an explicit prohibition, not any mention of tidying) and failure defaults to ENABLED, so
    /// an unreadable file never silently changes behaviour.
    /// </para>
    /// </summary>
    public bool ReadAutoTidyEnabled()
    {
        try
        {
            if (!File.Exists(RulesPath)) return true;
            var rules = File.ReadAllText(RulesPath);
            foreach (var phrase in AutoTidyOptOutPhrases)
            {
                if (rules.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static readonly string[] AutoTidyOptOutPhrases =
    [
        "do not use auto-tidy",
        "do not auto-tidy",
        "no auto-tidy",
        "disable auto-tidy",
        "auto-tidy 금지",
        "자동 정리 금지",
        "자동정리 금지",
        "자동 배치 금지",
    ];

    public void WriteLanguage(string language)
    {
        var normalized = string.Equals(language?.Trim(), "ko", StringComparison.OrdinalIgnoreCase) ? "ko" : "en";
        Directory.CreateDirectory(ContextDirectory);
        File.WriteAllText(LanguagePath, normalized);
    }

    public void EnsureScaffolded(
        Guid projectId,
        string projectName,
        string? rhinoPath,
        string? grasshopperPath)
    {
        Directory.CreateDirectory(ContextDirectory);
        WriteIfAbsent(
            Path.Combine(ContextDirectory, "project.json"),
            JsonSerializer.Serialize(
                new
                {
                    schema = "vino-context-v1",
                    projectId,
                    projectName,
                    rhinoFile = rhinoPath,
                    grasshopperFile = grasshopperPath,
                    createdAt = DateTimeOffset.UtcNow
                },
                ManifestJsonOptions));
        WriteIfAbsent(RulesPath, RulesSeed);
        WriteIfAbsent(MemoryPath, MemorySeed);
        WriteIfAbsent(LayerStandardPath, LayerStandardSeed);
    }

    public string Compose(string baseInstructions)
    {
        ArgumentNullException.ThrowIfNull(baseInstructions);
        try
        {
            var builder = new StringBuilder(baseInstructions);
            if (ReadLanguage() == "ko")
            {
                builder.Append("\n\n## Response language\n")
                    .Append("Write your prose to the user in KOREAN — chat answers, findings, ")
                    .Append("explanations, questions, and reports. Keep these in English regardless: ")
                    .Append("code and comments, tool names and JSON payload keys, operation/predicate ")
                    .Append("vocabulary, component nicknames, and technical identifiers. Do not translate ")
                    .Append("values a tool returned; quote them verbatim.");
            }
            AppendSection(builder, "Project rules", RulesPath);
            AppendSection(builder, "Project memory", MemoryPath);
            return builder.ToString();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return baseInstructions;
        }
    }

    private static void AppendSection(StringBuilder builder, string title, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        var content = File.ReadAllText(path).Trim();
        if (content.Length == 0)
        {
            return;
        }
        var truncated = content.Length > MaximumContextFileCharacters;
        if (truncated)
        {
            content = content[..MaximumContextFileCharacters];
        }
        builder.Append("\n\n## ").Append(title).Append(" (").Append(Path.GetFileName(path)).Append(")\n");
        builder.Append(content);
        if (truncated)
        {
            builder.Append("\n[Truncated: edit the file to stay under ")
                .Append(MaximumContextFileCharacters)
                .Append(" characters.]");
        }
    }

    /// <summary>
    /// Appends an agent-authored entry to the append-only project MEMORY.md. A single File.AppendAllText, so
    /// it interleaves safely with the user hand-editing the same file, and it refuses to grow past the context
    /// cap so the folded memory never overflows a thread's instruction budget. Never throws for the caller:
    /// I/O problems are returned as a failed result, matching the "context must never block a thread" rule.
    /// </summary>
    public MemoryAppendResult AppendMemory(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return new MemoryAppendResult(false, "The memory entry is empty.");
        }
        var trimmed = entry.Trim();
        try
        {
            Directory.CreateDirectory(ContextDirectory);
            var existing = File.Exists(MemoryPath) ? File.ReadAllText(MemoryPath) : string.Empty;
            if (existing.Length + trimmed.Length + 2 > MaximumContextFileCharacters)
            {
                return new MemoryAppendResult(
                    false,
                    $"MEMORY.md is near the {MaximumContextFileCharacters}-character cap; consolidate or remove " +
                    "stale entries before appending.");
            }
            var separator = existing.Length == 0 || existing.EndsWith('\n') ? "\n" : "\n\n";
            File.AppendAllText(MemoryPath, separator + trimmed + "\n");
            return new MemoryAppendResult(true, "Appended to project memory (MEMORY.md).");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new MemoryAppendResult(false, $"Could not write project memory: {exception.Message}");
        }
    }

    private static void WriteIfAbsent(string path, string content)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, content);
        }
    }
}

public sealed record MemoryAppendResult(bool Appended, string Message);
