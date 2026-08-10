using System.Text.Json;
using System.Text.Json.Nodes;

namespace GPTino.AgentHost.Hosting;

/// <summary>
/// The layer-curation tables as one loaded unit: the shipped palette, the alias matcher already
/// layered with the project table, and the active preset. Loaded fresh per use so a hand edit to
/// the project table applies on the next scan (the context-store reload convention), and shared by
/// the audit path (proposal synthesis) and the approval path (re-deriving colors when the user
/// switches preset) so both sides can never compute a color from different tables.
/// </summary>
public sealed record LayerCurationTables(
    MaterialPalette Palette,
    LayerAliasMatcher Matcher,
    string PresetId)
{
    private const string PresetProperty = "preset";

    public PalettePreset ActivePreset => Palette.Presets.First(
        preset => string.Equals(preset.Id, PresetId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Family -> opaque ARGB under the active preset: the ONLY color source for ops.</summary>
    public IReadOnlyDictionary<string, int> FamilyColors() => ActivePreset.Families.ToDictionary(
        family => family.Family,
        family => Palette.BaseArgb(PresetId, family.Family),
        StringComparer.Ordinal);

    public static LayerCurationTables Load(DataLibrary data, ProjectContextStore? context)
    {
        ArgumentNullException.ThrowIfNull(data);
        var palette = MaterialPalette.LoadShipped(data.Root);
        var matcher = LayerAliasMatcher.LoadShipped(data.Root);
        var presetId = palette.DefaultPreset.Id;
        if (ReadProjectTable(context) is { } projectJson)
        {
            if (matcher.TryWithProjectEntries(projectJson, out var merged, out _))
            {
                matcher = merged;
            }
            if (ReadPreset(projectJson) is { } stored &&
                palette.Presets.Any(preset => string.Equals(preset.Id, stored, StringComparison.OrdinalIgnoreCase)))
            {
                presetId = stored;
            }
        }
        return new LayerCurationTables(palette, matcher, presetId);
    }

    /// <summary>
    /// Persists the chosen preset into the project table, preserving everything else in the file
    /// (the accumulated alias entries live there too). Returns false when the table cannot be read
    /// or written — a preset preference must never take an approval down.
    /// </summary>
    public static bool TryWritePreset(ProjectContextStore? context, string presetId)
    {
        if (context is null || string.IsNullOrWhiteSpace(presetId))
        {
            return false;
        }
        try
        {
            var path = context.LayerStandardPath;
            var root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? []
                : [];
            root[PresetProperty] = presetId;
            Directory.CreateDirectory(context.ContextDirectory);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static string? ReadProjectTable(ProjectContextStore? context)
    {
        if (context is null)
        {
            return null;
        }
        try
        {
            var path = context.LayerStandardPath;
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Context problems never block a scan — the shipped tables carry on alone.
            return null;
        }
    }

    private static string? ReadPreset(string projectJson)
    {
        try
        {
            using var document = JsonDocument.Parse(projectJson);
            return document.RootElement.TryGetProperty(PresetProperty, out var preset) &&
                preset.ValueKind == JsonValueKind.String
                    ? preset.GetString()
                    : null;
        }
        catch (JsonException)
        {
            // A malformed table already failed the alias merge the same way.
            return null;
        }
    }
}
