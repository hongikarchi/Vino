using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GPTino.AgentHost.Hosting;

/// <summary>
/// A project's own layer scheme, on TWO independent axes: what a layer IS (element) and what it is
/// MADE OF (material). The shipped seed bundles the two — SC means "column, concrete" in one
/// entry — and the real structural model showed why that fails: there SC is a column made of
/// STEEL, filed under a 철골 parent, so accepting the element forced accepting the wrong material,
/// and colour comes from material.
///
/// Separating them also matches how the file was already organised: the parent layer declares the
/// material for everything beneath it while the leaf mark declares the element. Two sentences —
/// "everything under 철골 is steel", "SB/SG/SC are beam/girder/column" — then settle thirty layers.
///
/// This is the USER's scheme. It is only ever written from a confirmed card, and when it exists it
/// outranks the shipped vocabulary entirely (which is reduced to drafting hints).
/// </summary>
public sealed class LayerScheme
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

    private readonly IReadOnlyList<ElementRule> _elements;
    private readonly IReadOnlyList<MaterialRule> _materials;

    private LayerScheme(IReadOnlyList<ElementRule> elements, IReadOnlyList<MaterialRule> materials)
    {
        _elements = elements;
        _materials = materials;
    }

    /// <summary>True when the scheme carries no rule at all — nothing to resolve with.</summary>
    public bool IsEmpty => _elements.Count == 0 && _materials.Count == 0;

    public int ElementCount => _elements.Count;

    public int MaterialCount => _materials.Count;

    /// <summary>
    /// Reads the scheme out of a project table. Absent axes simply yield an empty scheme; a
    /// malformed one throws so the caller can fall back and SAY it fell back, never silently.
    /// </summary>
    public static LayerScheme Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<SchemeDocument>(json, JsonOptions)
            ?? throw new FormatException("The layer scheme document is empty.");

        var elements = new List<ElementRule>();
        var seenElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document.Elements ?? [])
        {
            if (string.IsNullOrWhiteSpace(element.Canonical))
            {
                throw new FormatException("Every element rule needs a non-empty canonical name.");
            }
            var canonical = element.Canonical.Trim();
            if (!seenElements.Add(canonical))
            {
                throw new FormatException($"Element rule '{canonical}' is declared twice.");
            }
            elements.Add(new ElementRule(
                canonical,
                Clean(element.Aliases),
                Clean(element.Prefixes),
                Compile(element.Patterns, canonical)));
        }

        var materials = new List<MaterialRule>();
        foreach (var material in document.Materials ?? [])
        {
            if (string.IsNullOrWhiteSpace(material.Material))
            {
                throw new FormatException("Every material rule needs a non-empty material family.");
            }
            var underPath = string.IsNullOrWhiteSpace(material.UnderPath)
                ? null
                : material.UnderPath.Trim();
            if (underPath is null &&
                material.Aliases is not { Count: > 0 } &&
                material.Patterns is not { Count: > 0 })
            {
                throw new FormatException(
                    $"Material rule '{material.Material}' matches nothing: give it underPath, " +
                    "aliases or patterns.");
            }
            materials.Add(new MaterialRule(
                material.Material.Trim(),
                underPath,
                Clean(material.Aliases),
                Compile(material.Patterns, material.Material!)));
        }

        return new LayerScheme(elements, materials);
    }

    /// <summary>
    /// Resolves one layer on both axes independently. Either half may come back null — a layer
    /// whose element is known but whose material is not is a normal, reportable state, and far
    /// more honest than borrowing a material from the element's default.
    /// </summary>
    public LayerSchemeMatch Resolve(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return new LayerSchemeMatch(null, null, null, null, null, null);
        }
        var leaf = LeafOf(fullPath);

        string? element = null, elementEvidence = null, elementConfidence = null;
        foreach (var rule in _elements)
        {
            if (rule.Aliases.Any(alias => string.Equals(alias, leaf, StringComparison.OrdinalIgnoreCase)))
            {
                (element, elementEvidence, elementConfidence) =
                    (rule.Canonical, $"element alias: '{leaf}'", LayerMatchConfidence.High);
                break;
            }
        }
        if (element is null)
        {
            foreach (var rule in _elements)
            {
                var prefix = rule.Prefixes.FirstOrDefault(
                    value => leaf.StartsWith(value, StringComparison.OrdinalIgnoreCase));
                if (prefix is not null)
                {
                    (element, elementEvidence, elementConfidence) =
                        (rule.Canonical, $"element prefix: '{prefix}'", LayerMatchConfidence.Medium);
                    break;
                }
                var pattern = rule.Patterns.FirstOrDefault(value => IsMatch(value, leaf));
                if (pattern is not null)
                {
                    (element, elementEvidence, elementConfidence) =
                        (rule.Canonical, $"element pattern: {pattern}", LayerMatchConfidence.Medium);
                    break;
                }
            }
        }

        // Material by SCOPE first, longest path wins: the parent layer is the strongest statement
        // a file makes about material, and it is how the real model was organised.
        string? material = null, materialEvidence = null, materialConfidence = null;
        var scoped = _materials
            .Where(rule => rule.UnderPath is { Length: > 0 } && IsUnder(fullPath, rule.UnderPath))
            .OrderByDescending(rule => rule.UnderPath!.Length)
            .FirstOrDefault();
        if (scoped is not null)
        {
            (material, materialEvidence, materialConfidence) =
                (scoped.Material, $"material scope: under '{scoped.UnderPath}'", LayerMatchConfidence.High);
        }
        else
        {
            foreach (var rule in _materials)
            {
                var alias = rule.Aliases.FirstOrDefault(
                    value => leaf.Contains(value, StringComparison.OrdinalIgnoreCase));
                if (alias is not null)
                {
                    (material, materialEvidence, materialConfidence) =
                        (rule.Material, $"material name: '{alias}'", LayerMatchConfidence.Medium);
                    break;
                }
                var pattern = rule.Patterns.FirstOrDefault(value => IsMatch(value, leaf));
                if (pattern is not null)
                {
                    (material, materialEvidence, materialConfidence) =
                        (rule.Material, $"material pattern: {pattern}", LayerMatchConfidence.Medium);
                    break;
                }
            }
        }

        return new LayerSchemeMatch(
            element, elementEvidence, elementConfidence,
            material, materialEvidence, materialConfidence);
    }

    private static bool IsUnder(string fullPath, string underPath) =>
        fullPath.StartsWith(underPath + "::", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fullPath, underPath, StringComparison.OrdinalIgnoreCase);

    private static bool IsMatch(Regex pattern, string value)
    {
        try
        {
            return pattern.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            // A hand-written pattern that backtracks must degrade to "no match", never take the
            // scan down — the same rule the alias matcher follows.
            return false;
        }
    }

    private static string LeafOf(string fullPath)
    {
        var segments = fullPath.Split("::", StringSplitOptions.RemoveEmptyEntries);
        var leaf = segments.Length > 0 ? segments[^1] : fullPath;
        return leaf.Trim().Normalize(NormalizationForm.FormKC);
    }

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().Normalize(NormalizationForm.FormKC))
            .ToArray();

    private static IReadOnlyList<Regex> Compile(IReadOnlyList<string>? patterns, string owner)
    {
        var compiled = new List<Regex>();
        foreach (var pattern in patterns ?? [])
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                compiled.Add(new Regex(
                    pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout));
            }
            catch (ArgumentException exception)
            {
                throw new FormatException(
                    $"Rule '{owner}' has an invalid pattern '{pattern}': {exception.Message}");
            }
        }
        return compiled;
    }

    private sealed record ElementRule(
        string Canonical,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string> Prefixes,
        IReadOnlyList<Regex> Patterns);

    private sealed record MaterialRule(
        string Material,
        string? UnderPath,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<Regex> Patterns);

    private sealed record SchemeDocument(
        IReadOnlyList<ElementDocument>? Elements,
        IReadOnlyList<MaterialDocument>? Materials);

    private sealed record ElementDocument(
        string? Canonical,
        IReadOnlyList<string>? Aliases,
        IReadOnlyList<string>? Prefixes,
        IReadOnlyList<string>? Patterns);

    private sealed record MaterialDocument(
        string? Material,
        string? UnderPath,
        IReadOnlyList<string>? Aliases,
        IReadOnlyList<string>? Patterns);
}

/// <summary>
/// One layer resolved on both axes. Each half carries its own evidence and confidence, so a row
/// can say "the element is certain, the material is a guess" instead of collapsing to one verdict.
/// </summary>
public sealed record LayerSchemeMatch(
    string? Element,
    string? ElementEvidence,
    string? ElementConfidence,
    string? Material,
    string? MaterialEvidence,
    string? MaterialConfidence)
{
    public bool Resolved => Element is not null || Material is not null;
}
