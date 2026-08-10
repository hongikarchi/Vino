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
/// <summary>One confirmed element rule on its way into the project table.</summary>
public sealed record SchemeElementRule(
    string Canonical,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Patterns);

/// <summary>
/// One confirmed material rule. UnderPath scopes it to a layer branch — the strongest and most
/// common form, because a parent layer is how a file usually declares what its contents are made of.
/// </summary>
public sealed record SchemeMaterialRule(
    string Material,
    string? UnderPath,
    IReadOnlyList<string> Aliases);

public sealed record LayerCurationTables(
    MaterialPalette Palette,
    LayerAliasMatcher Matcher,
    string PresetId,
    LayerScheme? Scheme = null)
{
    /// <summary>True when the project has its own confirmed scheme, which outranks the seed.</summary>
    public bool HasScheme => Scheme is { IsEmpty: false };

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
        LayerScheme? scheme = null;
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
            try
            {
                var parsed = LayerScheme.Parse(projectJson);
                scheme = parsed.IsEmpty ? null : parsed;
            }
            catch (Exception exception) when (exception is JsonException or FormatException)
            {
                // A broken scheme falls back to the seed rather than taking curation down — but it
                // stays null so callers can report that they ran WITHOUT the project's scheme.
            }
        }
        return new LayerCurationTables(palette, matcher, presetId, scheme);
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

    /// <summary>
    /// Upserts confirmed scheme rules into the project table, keyed by canonical (elements) and by
    /// material+scope (materials), preserving the preset, the accumulated aliases and every rule
    /// the card did not touch. MERGE, not replace: the scheme is refined across conversations, and
    /// approving three rows today must not delete what was settled last week. Returns false when
    /// the table cannot be read or written — a scheme write must never take a session down.
    /// </summary>
    public static bool TryWriteScheme(
        ProjectContextStore? context,
        IReadOnlyList<SchemeElementRule> elements,
        IReadOnlyList<SchemeMaterialRule> materials)
    {
        if (context is null || (elements.Count == 0 && materials.Count == 0))
        {
            return false;
        }
        try
        {
            var path = context.LayerStandardPath;
            var root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? []
                : [];

            // Rebuilt from clones, never mutated in place: assigning an array that is already this
            // object's child throws (the node still has a parent), which only shows up on the
            // SECOND write — exactly when a conversation refines a scheme that already exists.
            var existingElements = CloneArray(root["elements"] as JsonArray);
            foreach (var element in elements)
            {
                var index = IndexOf(existingElements, node =>
                    string.Equals(
                        node["canonical"]?.GetValue<string>(),
                        element.Canonical,
                        StringComparison.OrdinalIgnoreCase));
                var node = new JsonObject
                {
                    ["canonical"] = element.Canonical,
                    ["aliases"] = ToArray(element.Aliases),
                    ["patterns"] = ToArray(element.Patterns),
                };
                if (index >= 0) existingElements[index] = node; else existingElements.Add(node);
            }
            root["elements"] = existingElements;

            var existingMaterials = CloneArray(root["materials"] as JsonArray);
            foreach (var material in materials)
            {
                var index = IndexOf(existingMaterials, node =>
                    string.Equals(
                        node["material"]?.GetValue<string>(),
                        material.Material,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        node["underPath"]?.GetValue<string>() ?? string.Empty,
                        material.UnderPath ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase));
                var node = new JsonObject { ["material"] = material.Material };
                if (material.UnderPath is { Length: > 0 }) node["underPath"] = material.UnderPath;
                if (material.Aliases.Count > 0) node["aliases"] = ToArray(material.Aliases);
                if (index >= 0) existingMaterials[index] = node; else existingMaterials.Add(node);
            }
            root["materials"] = existingMaterials;

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

    private static JsonArray CloneArray(JsonArray? source)
    {
        var clone = new JsonArray();
        foreach (var node in source ?? [])
        {
            if (node is not null) clone.Add(node.DeepClone());
        }
        return clone;
    }

    private static int IndexOf(JsonArray array, Func<JsonNode, bool> predicate)
    {
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is { } node && predicate(node)) return index;
        }
        return -1;
    }

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        // Cast to JsonNode so the implicit string conversion builds a PRIMITIVE value. The generic
        // Add<T> overload makes a customized JsonValue instead, which then refuses to serialize
        // without a TypeInfoResolver — a failure that only appears at write time.
        foreach (var value in values) array.Add((JsonNode)value);
        return array;
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
