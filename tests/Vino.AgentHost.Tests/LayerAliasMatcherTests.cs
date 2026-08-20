using Vino.AgentHost.Hosting;

namespace Vino.AgentHost.Tests;

public sealed class LayerAliasMatcherTests
{
    private const string Seed = """
        {
          "entries": [
            {
              "canonical": "WALL",
              "material": "concrete",
              "aliases": ["벽", "벽체", "WALL", "W"],
              "prefixes": ["W-"],
              "patterns": []
            },
            {
              "canonical": "COLUMN",
              "material": "concrete",
              "aliases": ["기둥", "COL"],
              "prefixes": ["SC"],
              "patterns": ["^SC\\d+"]
            },
            {
              "canonical": "FINISH",
              "material": "plaster",
              "aliases": ["마감", "FIN"],
              "prefixes": [],
              "patterns": []
            },
            {
              "canonical": "XBRACE",
              "material": "steel",
              "aliases": [],
              "prefixes": [],
              "patterns": ["^XX\\d+"]
            }
          ]
        }
        """;

    [Fact]
    public void ExactKoreanAliasIsHighConfidence()
    {
        var match = LayerAliasMatcher.Parse(Seed).Match("벽");
        Assert.NotNull(match);
        Assert.Equal("WALL", match.Canonical);
        Assert.Equal("concrete", match.Material);
        Assert.Equal(LayerMatchConfidence.High, match.Confidence);
        Assert.Contains("alias exact", match.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" 마감 ", "FINISH")]
    [InlineData("wall", "WALL")]
    [InlineData("Building::벽", "WALL")]
    public void TrimsCaseFoldsAndUsesTheLastFullPathSegment(string name, string expectedCanonical)
    {
        var match = LayerAliasMatcher.Parse(Seed).Match(name);
        Assert.NotNull(match);
        Assert.Equal(expectedCanonical, match.Canonical);
        Assert.Equal(LayerMatchConfidence.High, match.Confidence);
    }

    [Fact]
    public void VariantMarkMatchesByPrefixAtMediumConfidence()
    {
        // The real-model regression shape: "SC5 (Bracing)" must not fall through to defaults.
        var match = LayerAliasMatcher.Parse(Seed).Match("SC5 (Bracing)");
        Assert.NotNull(match);
        Assert.Equal("COLUMN", match.Canonical);
        Assert.Equal(LayerMatchConfidence.Medium, match.Confidence);
        Assert.Contains("prefix", match.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void PatternOnlyEntriesMatchAtMediumConfidence()
    {
        var match = LayerAliasMatcher.Parse(Seed).Match("XX12 brace");
        Assert.NotNull(match);
        Assert.Equal("XBRACE", match.Canonical);
        Assert.Equal(LayerMatchConfidence.Medium, match.Confidence);
        Assert.Contains("pattern", match.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void UnmatchedNamesReturnNullForModelTriage()
    {
        Assert.Null(LayerAliasMatcher.Parse(Seed).Match("misc-stuff-01"));
        Assert.Null(LayerAliasMatcher.Parse(Seed).Match("   "));
        Assert.Null(LayerAliasMatcher.Parse(Seed).Match(null));
    }

    [Fact]
    public void AnyExactAliasBeatsAnyPrefixOrPattern()
    {
        // "SC-WALL"-style collisions: an exact alias elsewhere in the table must win over
        // an earlier entry's prefix, because confidence passes dominate entry order.
        const string collidingSeed = """
            {
              "entries": [
                { "canonical": "COLUMN", "material": "concrete", "aliases": [], "prefixes": ["SC"], "patterns": [] },
                { "canonical": "SPECIAL", "material": "steel", "aliases": ["SC5"], "prefixes": [], "patterns": [] }
              ]
            }
            """;
        var match = LayerAliasMatcher.Parse(collidingSeed).Match("SC5");
        Assert.NotNull(match);
        Assert.Equal("SPECIAL", match.Canonical);
        Assert.Equal(LayerMatchConfidence.High, match.Confidence);
    }

    [Fact]
    public void ProjectOverrideWinsMaterialButUnionsTheVocabulary()
    {
        // The minimal entry a card-answer accumulator writes: new alias, new material,
        // NO copy of the shipped lists. The shipped vocabulary must survive the merge.
        const string project = """
            {
              "entries": [
                { "canonical": "WALL", "material": "brick", "aliases": ["RC-W"], "prefixes": [], "patterns": [] }
              ]
            }
            """;
        var shipped = LayerAliasMatcher.Parse(Seed);
        Assert.True(shipped.TryWithProjectEntries(project, out var merged, out var error));
        Assert.Null(error);

        var projectAlias = merged.Match("RC-W");
        Assert.NotNull(projectAlias);
        Assert.Equal("WALL", projectAlias.Canonical);
        Assert.Equal("brick", projectAlias.Material);

        // The shipped alias still matches and carries the PROJECT's material.
        var shippedAlias = merged.Match("벽");
        Assert.NotNull(shippedAlias);
        Assert.Equal("brick", shippedAlias.Material);

        // Shipped prefixes survive too, and non-overridden entries are untouched.
        Assert.Equal("WALL", merged.Match("W-1")?.Canonical);
        Assert.Equal("COLUMN", merged.Match("기둥")?.Canonical);
    }

    [Fact]
    public void NullOrMissingProjectTableReturnsFalseInsteadOfThrowing()
    {
        var shipped = LayerAliasMatcher.Parse(Seed);
        Assert.False(shipped.TryWithProjectEntries(null, out var merged, out var error));
        Assert.NotNull(error);
        Assert.NotNull(merged.Match("벽"));
    }

    [Fact]
    public void CatastrophicProjectPatternDegradesToTriageInsteadOfThrowing()
    {
        // "^(a+)+$" is syntactically valid (compiles, merges) but backtracks catastrophically;
        // the 250ms timeout must surface as "no deterministic match", never as an exception
        // that kills the whole proposal pass.
        const string project = """
            {
              "entries": [
                { "canonical": "TRAP", "material": "steel", "aliases": [], "prefixes": [], "patterns": ["^(a+)+$"] }
              ]
            }
            """;
        var shipped = LayerAliasMatcher.Parse(Seed);
        Assert.True(shipped.TryWithProjectEntries(project, out var merged, out _));
        Assert.Null(merged.Match(new string('a', 40) + "X"));
    }

    [Fact]
    public void FullWidthImeNamesFoldToTheirAsciiAliases()
    {
        var match = LayerAliasMatcher.Parse(Seed).Match("ＷＡＬＬ");
        Assert.NotNull(match);
        Assert.Equal("WALL", match.Canonical);
        Assert.Equal(LayerMatchConfidence.High, match.Confidence);
    }

    [Fact]
    public void DuplicateCanonicalsAreCaughtEvenWithStraySpaces()
    {
        const string sloppy = """
            {
              "entries": [
                { "canonical": "WALL", "material": "concrete", "aliases": ["벽"], "prefixes": [], "patterns": [] },
                { "canonical": " WALL ", "material": "brick", "aliases": ["RC"], "prefixes": [], "patterns": [] }
              ]
            }
            """;
        Assert.Throws<FormatException>(() => LayerAliasMatcher.Parse(sloppy));
    }

    [Fact]
    public void MalformedProjectTableNeverTakesTheMatcherDown()
    {
        var shipped = LayerAliasMatcher.Parse(Seed);
        Assert.False(shipped.TryWithProjectEntries("{ not json", out var merged, out var error));
        Assert.NotNull(error);
        // The matcher is unchanged and still works.
        Assert.NotNull(merged.Match("벽"));
    }

    [Fact]
    public void RejectsDuplicateCanonicalsAndInvalidPatternsAtParseTime()
    {
        var duplicate = Seed.Replace("\"canonical\": \"FINISH\"", "\"canonical\": \"wall\"");
        Assert.Throws<FormatException>(() => LayerAliasMatcher.Parse(duplicate));

        var invalidPattern = Seed.Replace("^XX\\\\d+", "([unclosed");
        Assert.Throws<FormatException>(() => LayerAliasMatcher.Parse(invalidPattern));
    }

    [Fact]
    public void ShippedSeedParsesAndCoversTheStructuralMarks()
    {
        var library = new DataLibrary();
        if (!Directory.Exists(library.Root) ||
            !library.List().Contains(LayerAliasMatcher.ShippedRelativePath))
        {
            return;
        }
        var matcher = LayerAliasMatcher.LoadShipped(library.Root);
        // The structural-firm marks the real-model pipeline reads must resolve.
        Assert.Equal("COLUMN", matcher.Match("SC5 (Bracing)")?.Canonical);
        Assert.Equal("GIRDER", matcher.Match("SG3")?.Canonical);
        Assert.Equal("BEAM", matcher.Match("SB1")?.Canonical);
        Assert.Equal("WALL", matcher.Match("벽")?.Canonical);
        Assert.Equal("WALL", matcher.Match("W-1")?.Canonical);

        // ...and the known Korean-practice false positives must NOT resolve: 'W' alone is a
        // window-schedule mark, and fit-out names sharing the SC/SG/SB letters are unrelated
        // to structural members. These fall to model triage by design.
        Assert.Null(matcher.Match("W"));
        Assert.Null(matcher.Match("SCREEN"));
        Assert.Null(matcher.Match("SGP칸막이"));
        Assert.Null(matcher.Match("SBS방수"));
    }
}
