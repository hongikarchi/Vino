using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vino.AgentHost.Hosting;

/// <summary>
/// Deterministic layer-name → semantic-label matching for layer curation. Confidence is decided
/// HERE, by which rule matched — exact alias = high, prefix/pattern = medium — never by model
/// self-report; names the matcher cannot place return null and go to the model as triage
/// candidates (low, and only after the user confirms). Real-world names are messy on purpose
/// ("SC5 (Bracing)", " 마감 ", mixed case), so matching is trim + case-insensitive over the
/// FullPath's last segment, and every match carries a provenance string naming the rule.
/// Project-table entries override shipped seed entries on canonical collision and are consulted
/// first within each confidence pass.
/// </summary>
public sealed class LayerAliasMatcher
{
    /// <summary>Single source for the shipped seed's path relative to the data-library root.</summary>
    public const string ShippedRelativePath = "layers/alias-seed-ko.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

    private readonly IReadOnlyList<CompiledEntry> _entries;

    private LayerAliasMatcher(IReadOnlyList<CompiledEntry> entries) => _entries = entries;

    public static LayerAliasMatcher Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return new LayerAliasMatcher(CompileEntries(json));
    }

    /// <summary>Reads the shipped seed beside the executable (same root the DataLibrary serves).</summary>
    public static LayerAliasMatcher LoadShipped(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Parse(File.ReadAllText(Path.Combine(dataRoot, ShippedRelativePath)));
    }

    /// <summary>
    /// Layers this matcher over a project table (same entries schema). On canonical collision the
    /// project entry wins the MATERIAL, and the alias/prefix/pattern lists are UNIONED (project
    /// items first) — a project override must never silently un-teach the shipped vocabulary,
    /// because the card-answer accumulator writes minimal entries ("WALL += RC-W") that would
    /// otherwise stop '벽' from matching. A malformed or missing project file must never take
    /// layer curation down — failures return false and leave the matcher as-is, mirroring the
    /// context store's "context problems never block a thread" rule.
    /// </summary>
    public bool TryWithProjectEntries(string? projectJson, out LayerAliasMatcher merged, out string? error)
    {
        merged = this;
        if (string.IsNullOrWhiteSpace(projectJson))
        {
            error = "The project table is empty or missing.";
            return false;
        }
        error = null;
        IReadOnlyList<CompiledEntry> projectEntries;
        try
        {
            projectEntries = CompileEntries(projectJson);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            error = exception.Message;
            return false;
        }
        var shippedByCanonical = _entries.ToDictionary(
            entry => entry.Canonical,
            StringComparer.OrdinalIgnoreCase);
        var overridden = new HashSet<string>(
            projectEntries.Select(entry => entry.Canonical),
            StringComparer.OrdinalIgnoreCase);
        var combined = new List<CompiledEntry>(projectEntries.Count + _entries.Count);
        foreach (var project in projectEntries)
        {
            combined.Add(shippedByCanonical.TryGetValue(project.Canonical, out var shipped)
                ? new CompiledEntry(
                    project.Canonical,
                    project.Material,
                    Union(project.Aliases, shipped.Aliases),
                    Union(project.Prefixes, shipped.Prefixes),
                    UnionPatterns(project.Patterns, shipped.Patterns))
                : project);
        }
        combined.AddRange(_entries.Where(entry => !overridden.Contains(entry.Canonical)));
        merged = new LayerAliasMatcher(combined);
        return true;
    }

    private static IReadOnlyList<string> Union(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        var seen = new HashSet<string>(first, StringComparer.OrdinalIgnoreCase);
        return first.Concat(second.Where(seen.Add)).ToArray();
    }

    private static IReadOnlyList<Regex> UnionPatterns(IReadOnlyList<Regex> first, IReadOnlyList<Regex> second)
    {
        var seen = new HashSet<string>(first.Select(pattern => pattern.ToString()), StringComparer.Ordinal);
        return first.Concat(second.Where(pattern => seen.Add(pattern.ToString()))).ToArray();
    }

    /// <summary>
    /// Matches one layer name (or FullPath — the last :: segment is what gets matched).
    /// Null means "no deterministic rule applies": the caller may ask the model to triage,
    /// and a model-chosen family becomes low confidence only after user confirmation.
    /// </summary>
    public LayerMatch? Match(string? layerNameOrFullPath)
    {
        if (string.IsNullOrWhiteSpace(layerNameOrFullPath))
        {
            return null;
        }
        var segments = layerNameOrFullPath.Split("::", StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }
        // FormKC folds the full-width Latin/digits Korean and Japanese IMEs produce ('ＷＡＬＬ')
        // into their ASCII forms, so the messiest real-world names still match deterministically.
        var name = segments[^1].Trim().Normalize(System.Text.NormalizationForm.FormKC);
        if (name.Length == 0)
        {
            return null;
        }

        // Confidence passes dominate entry order: any exact alias beats any prefix/pattern.
        foreach (var entry in _entries)
        {
            foreach (var alias in entry.Aliases)
            {
                if (string.Equals(alias, name, StringComparison.OrdinalIgnoreCase))
                {
                    return new LayerMatch(
                        entry.Canonical,
                        entry.Material,
                        LayerMatchConfidence.High,
                        $"alias exact: '{alias}'");
                }
            }
        }
        foreach (var entry in _entries)
        {
            foreach (var prefix in entry.Prefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return new LayerMatch(
                        entry.Canonical,
                        entry.Material,
                        LayerMatchConfidence.Medium,
                        $"prefix: '{prefix}'");
                }
            }
        }
        foreach (var entry in _entries)
        {
            foreach (var pattern in entry.Patterns)
            {
                bool isMatch;
                try
                {
                    isMatch = pattern.IsMatch(name);
                }
                catch (RegexMatchTimeoutException)
                {
                    // A user-authored project pattern with catastrophic backtracking must degrade
                    // to "no deterministic match" (model triage), never crash the proposal pass —
                    // the 250ms timeout exists to bound hostile patterns, not to throw through us.
                    continue;
                }
                if (isMatch)
                {
                    return new LayerMatch(
                        entry.Canonical,
                        entry.Material,
                        LayerMatchConfidence.Medium,
                        $"pattern: {pattern}");
                }
            }
        }
        return null;
    }

    private static IReadOnlyList<CompiledEntry> CompileEntries(string json)
    {
        var document = JsonSerializer.Deserialize<SeedDocument>(json, JsonOptions)
            ?? throw new FormatException("The alias table document is empty.");
        var entries = document.Entries ?? [];
        var compiled = new List<CompiledEntry>(entries.Count);
        var canonicals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Canonical))
            {
                throw new FormatException("Every alias entry needs a non-empty canonical label.");
            }
            if (string.IsNullOrWhiteSpace(entry.Material))
            {
                throw new FormatException($"Alias entry '{entry.Canonical}' needs a material family.");
            }
            // Trim BEFORE the duplicate check: hand-edited JSON is exactly where " WALL" appears,
            // and an untrimmed check would let two WALL entries coexist past validation.
            var canonical = entry.Canonical.Trim();
            if (!canonicals.Add(canonical))
            {
                throw new FormatException($"Alias entry '{canonical}' is declared twice.");
            }
            var patterns = new List<Regex>();
            foreach (var pattern in entry.Patterns ?? [])
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    continue;
                }
                try
                {
                    patterns.Add(new Regex(
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        PatternTimeout));
                }
                catch (ArgumentException exception)
                {
                    throw new FormatException(
                        $"Alias entry '{entry.Canonical}' has an invalid pattern '{pattern}': {exception.Message}");
                }
            }
            compiled.Add(new CompiledEntry(
                canonical,
                entry.Material.Trim(),
                (entry.Aliases ?? []).Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Select(alias => alias.Trim().Normalize(System.Text.NormalizationForm.FormKC))
                    .ToArray(),
                (entry.Prefixes ?? []).Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                    .Select(prefix => prefix.Trim().Normalize(System.Text.NormalizationForm.FormKC))
                    .ToArray(),
                patterns));
        }
        return compiled;
    }

    private sealed record CompiledEntry(
        string Canonical,
        string Material,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string> Prefixes,
        IReadOnlyList<Regex> Patterns);

    private sealed record SeedDocument(IReadOnlyList<SeedEntry>? Entries);

    private sealed record SeedEntry(
        string? Canonical,
        string? Material,
        IReadOnlyList<string>? Aliases,
        IReadOnlyList<string>? Prefixes,
        IReadOnlyList<string>? Patterns);
}

/// <summary>Confidence vocabulary shared with the proposal card and layer UserText.</summary>
public static class LayerMatchConfidence
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
}

public sealed record LayerMatch(
    string Canonical,
    string Material,
    string Confidence,
    string Evidence);
