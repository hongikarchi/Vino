using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vino.AgentHost.Hosting;

/// <summary>
/// The material→color tables behind layer curation. Canonical colors are OKLCH — one hue per
/// material family, variants as discrete lightness stops — grouped into user-selectable presets
/// (e.g. material-realistic vs drafting-traditional; the two conventions conflict, so neither is
/// hardcoded). The server computes every color deterministically from this table: the model only
/// ever names a family, never a color. sRGB/ARGB leaves this class solely via
/// <see cref="OklchColor.ToArgb"/> at apply time.
/// </summary>
public sealed class MaterialPalette
{
    /// <summary>Single source for the shipped table's path relative to the data-library root.</summary>
    public const string ShippedRelativePath = "layers/material-palette.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IReadOnlyDictionary<string, PalettePreset> _presetsById;

    private MaterialPalette(
        IReadOnlyList<double> variantStopsL,
        IReadOnlyList<PalettePreset> presets,
        PalettePreset defaultPreset)
    {
        VariantStopsL = variantStopsL;
        Presets = presets;
        DefaultPreset = defaultPreset;
        _presetsById = presets.ToDictionary(
            preset => preset.Id,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Discrete lightness stops shared by every family (no continuous interpolation).</summary>
    public IReadOnlyList<double> VariantStopsL { get; }

    public IReadOnlyList<PalettePreset> Presets { get; }

    public PalettePreset DefaultPreset { get; }

    public static MaterialPalette Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<PaletteDocument>(json, JsonOptions)
            ?? throw new FormatException("The material palette document is empty.");
        if (document.VariantStopsL is not { Count: > 0 })
        {
            throw new FormatException("The material palette must declare at least one variant L stop.");
        }
        foreach (var stop in document.VariantStopsL)
        {
            if (!double.IsFinite(stop) || stop < 0 || stop > 1)
            {
                throw new FormatException($"Variant L stop {stop} is outside [0, 1].");
            }
        }
        if (document.Presets is not { Count: > 0 })
        {
            throw new FormatException("The material palette must declare at least one preset.");
        }
        var presets = new List<PalettePreset>(document.Presets.Count);
        foreach (var preset in document.Presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Id))
            {
                throw new FormatException("Every palette preset needs a non-empty id.");
            }
            if (preset.Families is not { Count: > 0 })
            {
                throw new FormatException($"Palette preset '{preset.Id}' declares no families.");
            }
            var familyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var family in preset.Families)
            {
                if (string.IsNullOrWhiteSpace(family.Family))
                {
                    throw new FormatException($"Palette preset '{preset.Id}' has a family without a name.");
                }
                if (!familyIds.Add(family.Family))
                {
                    throw new FormatException(
                        $"Palette preset '{preset.Id}' declares family '{family.Family}' twice.");
                }
                // Range validation is the only place authoring typos surface: OklchColor degrades
                // silently for out-of-range inputs (negative chroma flips hue 180°; L outside
                // [0, 1] saturates to white/black), and "deterministically wrong" would then be
                // re-derived and confirmed by every later audit.
                if (!double.IsFinite(family.HueDeg))
                {
                    throw new FormatException(
                        $"Family '{family.Family}' in preset '{preset.Id}' has a non-finite hue.");
                }
                if (!double.IsFinite(family.Chroma) || family.Chroma < 0)
                {
                    throw new FormatException(
                        $"Family '{family.Family}' in preset '{preset.Id}' needs chroma >= 0.");
                }
                if (!double.IsFinite(family.BaseL) || family.BaseL < 0 || family.BaseL > 1)
                {
                    throw new FormatException(
                        $"Family '{family.Family}' in preset '{preset.Id}' needs baseL within [0, 1].");
                }
            }
            presets.Add(preset);
        }
        if (presets.GroupBy(preset => preset.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            throw new FormatException("Palette preset ids must be unique.");
        }
        var defaults = presets.Where(preset => preset.Default).ToArray();
        if (defaults.Length > 1)
        {
            throw new FormatException("At most one palette preset may be marked default.");
        }
        return new MaterialPalette(
            document.VariantStopsL,
            presets,
            defaults.Length == 1 ? defaults[0] : presets[0]);
    }

    /// <summary>Reads the shipped table beside the executable (same root the DataLibrary serves).</summary>
    public static MaterialPalette LoadShipped(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Parse(File.ReadAllText(Path.Combine(dataRoot, ShippedRelativePath)));
    }

    public bool TryGetFamily(string presetId, string family, out PaletteFamily found)
    {
        found = default!;
        if (string.IsNullOrWhiteSpace(presetId) || string.IsNullOrWhiteSpace(family))
        {
            return false;
        }
        if (!_presetsById.TryGetValue(presetId, out var preset))
        {
            return false;
        }
        var match = preset.Families.FirstOrDefault(
            candidate => string.Equals(candidate.Family, family, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }
        found = match;
        return true;
    }

    /// <summary>The family's base color (its own base L) as opaque ARGB.</summary>
    public int BaseArgb(string presetId, string family)
    {
        if (!TryGetFamily(presetId, family, out var found))
        {
            throw new KeyNotFoundException($"Palette family '{family}' is not in preset '{presetId}'.");
        }
        return new OklchColor(found.BaseL, found.Chroma, found.HueDeg).ToArgb();
    }

    /// <summary>
    /// A family variant pinned to one of the shared discrete L stops — same hue and chroma as
    /// the family, so variants of one material stay one visual family in the viewport.
    /// </summary>
    public int VariantArgb(string presetId, string family, int stopIndex)
    {
        if (stopIndex < 0 || stopIndex >= VariantStopsL.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stopIndex),
                $"Variant stop {stopIndex} is outside the {VariantStopsL.Count} declared L stops.");
        }
        if (!TryGetFamily(presetId, family, out var found))
        {
            throw new KeyNotFoundException($"Palette family '{family}' is not in preset '{presetId}'.");
        }
        return new OklchColor(VariantStopsL[stopIndex], found.Chroma, found.HueDeg).ToArgb();
    }

    private sealed record PaletteDocument(
        [property: JsonPropertyName("variantStopsL")] IReadOnlyList<double>? VariantStopsL,
        [property: JsonPropertyName("presets")] IReadOnlyList<PalettePreset>? Presets);
}

public sealed record PalettePreset(
    string Id,
    string Label,
    bool Default,
    IReadOnlyList<PaletteFamily> Families);

public sealed record PaletteFamily(
    string Family,
    double HueDeg,
    double Chroma,
    double BaseL);
