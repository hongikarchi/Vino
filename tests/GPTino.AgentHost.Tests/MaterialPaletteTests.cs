using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

public sealed class MaterialPaletteTests
{
    private const string MinimalPalette = """
        {
          "variantStopsL": [0.8, 0.6],
          "presets": [
            {
              "id": "a",
              "label": "A",
              "default": true,
              "families": [
                { "family": "concrete", "hueDeg": 75, "chroma": 0.025, "baseL": 0.65 }
              ]
            },
            {
              "id": "b",
              "label": "B",
              "default": false,
              "families": [
                { "family": "concrete", "hueDeg": 145, "chroma": 0.1, "baseL": 0.6 }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void ResolvesTheDeclaredDefaultPreset()
    {
        var palette = MaterialPalette.Parse(MinimalPalette);
        Assert.Equal("a", palette.DefaultPreset.Id);
        Assert.Equal(2, palette.Presets.Count);
        Assert.Equal([0.8, 0.6], palette.VariantStopsL);
    }

    [Fact]
    public void FallsBackToTheFirstPresetWhenNoneIsDefault()
    {
        var palette = MaterialPalette.Parse(MinimalPalette.Replace("\"default\": true", "\"default\": false"));
        Assert.Equal("a", palette.DefaultPreset.Id);
    }

    [Fact]
    public void BaseColorIsOpaqueAndDeterministic()
    {
        var palette = MaterialPalette.Parse(MinimalPalette);
        var argb = palette.BaseArgb("a", "concrete");
        Assert.Equal(argb, palette.BaseArgb("a", "concrete"));
        Assert.Equal(0xFF, (argb >> 24) & 0xFF);
        // The same family reads differently under another preset — presets are real alternatives.
        Assert.NotEqual(argb, palette.BaseArgb("b", "concrete"));
    }

    [Fact]
    public void VariantStopsProduceDistinctColorsWithinOneFamily()
    {
        var palette = MaterialPalette.Parse(MinimalPalette);
        var light = palette.VariantArgb("a", "concrete", 0);
        var dark = palette.VariantArgb("a", "concrete", 1);
        Assert.NotEqual(light, dark);
        Assert.Throws<ArgumentOutOfRangeException>(() => palette.VariantArgb("a", "concrete", 2));
    }

    [Fact]
    public void UnknownPresetOrFamilyIsAnExplicitMiss()
    {
        var palette = MaterialPalette.Parse(MinimalPalette);
        Assert.False(palette.TryGetFamily("a", "kryptonite", out _));
        Assert.False(palette.TryGetFamily("nope", "concrete", out _));
        Assert.Throws<KeyNotFoundException>(() => palette.BaseArgb("a", "kryptonite"));
    }

    [Theory]
    [InlineData("""{ "variantStopsL": [0.8], "presets": [] }""", "at least one preset")]
    [InlineData(
        """{ "variantStopsL": [], "presets": [{ "id": "a", "label": "A", "default": true, "families": [{ "family": "concrete", "hueDeg": 75, "chroma": 0.025, "baseL": 0.65 }] }] }""",
        "variant L stop")]
    public void RejectsStructurallyEmptyDocuments(string json, string expectedFragment)
    {
        var exception = Assert.Throws<FormatException>(() => MaterialPalette.Parse(json));
        Assert.Contains(expectedFragment, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsDuplicateFamiliesAndPresetIdsAndDoubleDefaults()
    {
        var duplicateFamily = MinimalPalette.Replace(
            "{ \"family\": \"concrete\", \"hueDeg\": 75, \"chroma\": 0.025, \"baseL\": 0.65 }",
            "{ \"family\": \"concrete\", \"hueDeg\": 75, \"chroma\": 0.025, \"baseL\": 0.65 }, " +
            "{ \"family\": \"CONCRETE\", \"hueDeg\": 80, \"chroma\": 0.03, \"baseL\": 0.6 }");
        Assert.Throws<FormatException>(() => MaterialPalette.Parse(duplicateFamily));

        var duplicateId = MinimalPalette.Replace("\"id\": \"b\"", "\"id\": \"A\"");
        Assert.Throws<FormatException>(() => MaterialPalette.Parse(duplicateId));

        var doubleDefault = MinimalPalette.Replace("\"default\": false", "\"default\": true");
        Assert.Throws<FormatException>(() => MaterialPalette.Parse(doubleDefault));
    }

    [Theory]
    [InlineData("\"chroma\": 0.025", "\"chroma\": -0.09", "chroma")]
    [InlineData("\"baseL\": 0.65", "\"baseL\": 9.2", "baseL")]
    [InlineData("\"variantStopsL\": [0.8, 0.6]", "\"variantStopsL\": [0.8, 1.2]", "L stop")]
    public void RejectsOutOfRangeNumbersThatWouldDegradeSilently(
        string original, string mutated, string expectedFragment)
    {
        // OklchColor degrades silently for these (negative chroma flips hue 180°; L outside
        // [0, 1] saturates to white/black), so the parser is the only surface where an
        // authoring typo can become an error instead of a deterministic wrong color.
        var json = MinimalPalette.Replace(original, mutated);
        var exception = Assert.Throws<FormatException>(() => MaterialPalette.Parse(json));
        Assert.Contains(expectedFragment, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShippedPresetsKeepFamiliesVisuallySeparable()
    {
        // The plan's family-separation rule, scoped to where it matters: families sharing a
        // chroma band collide at the shared variant L stops, so chromatic near-equals need
        // >= 25° of hue between them; near-neutral pairs (hue barely visible at C < 0.05)
        // separate by warm/cool hue or by base lightness instead.
        var library = new DataLibrary();
        if (!Directory.Exists(library.Root) ||
            !library.List().Contains(MaterialPalette.ShippedRelativePath))
        {
            return;
        }
        var palette = MaterialPalette.LoadShipped(library.Root);
        foreach (var preset in palette.Presets)
        {
            for (var i = 0; i < preset.Families.Count; i++)
            {
                for (var j = i + 1; j < preset.Families.Count; j++)
                {
                    var a = preset.Families[i];
                    var b = preset.Families[j];
                    var delta = Math.Abs(a.HueDeg - b.HueDeg) % 360;
                    var hueGap = Math.Min(delta, 360 - delta);
                    var pair = $"{preset.Id}: {a.Family}({a.HueDeg}°) vs {b.Family}({b.HueDeg}°)";
                    if (a.Chroma >= 0.05 && b.Chroma >= 0.05 && Math.Abs(a.Chroma - b.Chroma) < 0.05)
                    {
                        Assert.True(hueGap >= 25, $"{pair}: chromatic near-equals only {hueGap}° apart.");
                    }
                    else if (a.Chroma < 0.05 && b.Chroma < 0.05)
                    {
                        Assert.True(
                            hueGap >= 90 || Math.Abs(a.BaseL - b.BaseL) >= 0.2,
                            $"{pair}: near-neutrals separated by neither hue nor lightness.");
                    }
                }
            }
        }
    }

    [Fact]
    public void ShippedPaletteParsesAndEveryFamilyEmitsOpaqueColors()
    {
        var library = new DataLibrary();
        if (!Directory.Exists(library.Root) ||
            !library.List().Contains(MaterialPalette.ShippedRelativePath))
        {
            return;
        }
        var palette = MaterialPalette.LoadShipped(library.Root);
        Assert.Equal("material-realistic", palette.DefaultPreset.Id);
        Assert.True(palette.Presets.Count >= 2);
        foreach (var preset in palette.Presets)
        {
            foreach (var family in preset.Families)
            {
                var argb = palette.BaseArgb(preset.Id, family.Family);
                Assert.Equal(0xFF, (argb >> 24) & 0xFF);
                for (var stop = 0; stop < palette.VariantStopsL.Count; stop++)
                {
                    Assert.Equal(0xFF, (palette.VariantArgb(preset.Id, family.Family, stop) >> 24) & 0xFF);
                }
            }
        }
    }
}
