using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// The scheme draft is derived from the user's own layer names, so these tests are written
/// against the name shapes real documents carry — Korean compounds that share no separator,
/// structural marks, and names nothing can place.
/// </summary>
public sealed class LayerNameAnalyzerTests
{
    private static LayerNameGroup? GroupFor(LayerNameAnalysis analysis, string layer) =>
        analysis.Groups.FirstOrDefault(group => group.Members.Contains(layer));

    [Fact]
    public void KoreanCompoundsGroupOnTheSharedSubstringThatNoSeparatorReveals()
    {
        // 콘크리트 벽 / 외벽-콘크리트 / 벽 read as one family to a person, but token splitting
        // finds nothing shared: 외벽 does not split into 외 + 벽. The substring pass is the point.
        var analysis = LayerNameAnalyzer.Analyze(["콘크리트 벽", "외벽-콘크리트", "벽", "misc-stuff-01"]);

        var wall = GroupFor(analysis, "외벽-콘크리트");
        Assert.NotNull(wall);
        Assert.Equal("벽", wall.Key);
        Assert.Equal(LayerNameAnalyzer.KindSubstring, wall.Kind);
        Assert.Equal(
            new[] { "벽", "외벽-콘크리트", "콘크리트 벽" }.OrderBy(name => name, StringComparer.Ordinal),
            wall.Members.OrderBy(name => name, StringComparer.Ordinal));

        // The material axis is not lost — it rides along as a key the layer ALSO matched, which is
        // what lets a later pass ask "should 콘크리트 be its own axis?".
        Assert.Contains("콘크리트", analysis.AlsoMatched!["외벽-콘크리트"]);

        // A name nothing can place stays out rather than being forced into the nearest bucket.
        Assert.Contains("misc-stuff-01", analysis.Ungrouped);
    }

    [Fact]
    public void StructuralMarksFormTheirOwnFamilies()
    {
        var analysis = LayerNameAnalyzer.Analyze(["SC1", "SC2", "SC5 (Bracing)", "SG1", "SG3"]);

        var columns = GroupFor(analysis, "SC1");
        var girders = GroupFor(analysis, "SG1");
        Assert.Equal("SC", columns?.Key);
        Assert.Equal(LayerNameAnalyzer.KindMarkFamily, columns?.Kind);
        Assert.Equal(3, columns!.Members.Count);
        Assert.Equal("SG", girders?.Key);
        Assert.Equal(2, girders!.Members.Count);
    }

    [Fact]
    public void AMarkFamilyOutranksTheParentThatContainsIt()
    {
        // Over-splitting is the cheaper mistake for a draft: merging two groups is one
        // instruction, re-splitting a group that swallowed the SC/SG distinction is manual work.
        var analysis = LayerNameAnalyzer.Analyze(["구조::SC1", "구조::SC2", "구조::SG1", "구조::SG2"]);

        Assert.Equal("SC", GroupFor(analysis, "구조::SC1")?.Key);
        Assert.Equal("SG", GroupFor(analysis, "구조::SG1")?.Key);
        Assert.Empty(analysis.Ungrouped);
    }

    [Fact]
    public void AParentStillGroupsChildrenNoOtherRuleSplits()
    {
        var analysis = LayerNameAnalyzer.Analyze(["Site::Trees", "Site::Roads", "Tower"]);

        var site = GroupFor(analysis, "Site::Trees");
        Assert.Equal("Site", site?.Key);
        Assert.Equal(LayerNameAnalyzer.KindParent, site?.Kind);
        Assert.Equal(["Tower"], analysis.Ungrouped);
    }

    [Fact]
    public void ShippedSeedOnlyHintsAndNeverDecides()
    {
        const string seed = """
            {
              "entries": [
                { "canonical": "WALL", "material": "concrete", "aliases": ["벽"], "prefixes": [], "patterns": [] }
              ]
            }
            """;
        var analysis = LayerNameAnalyzer.Analyze(
            ["콘크리트 벽", "벽"], LayerAliasMatcher.Parse(seed));

        var wall = Assert.Single(analysis.Groups);
        Assert.Equal("벽", wall.Key);
        // The hint rides WITH the group; it did not create it, and the group would exist without it.
        Assert.Equal("WALL", wall.HintCanonical);
        Assert.Equal("concrete", wall.HintMaterial);

        var unhinted = LayerNameAnalyzer.Analyze(["콘크리트 벽", "벽"]);
        Assert.Equal("벽", Assert.Single(unhinted.Groups).Key);
        Assert.Null(Assert.Single(unhinted.Groups).HintCanonical);
    }

    [Fact]
    public void CrossScriptSynonymsAreSuggestedSeparatelyAndNeverMergedIn()
    {
        // `wall` and `벽` share no character; only a vocabulary can see they are one thing. That
        // suggestion must arrive as its own list, so the user can accept or reject the join
        // instead of finding two kinds of evidence silently welded together.
        const string seed = """
            {
              "entries": [
                { "canonical": "WALL", "material": "concrete", "aliases": ["벽", "wall"], "prefixes": [], "patterns": [] },
                { "canonical": "COLUMN", "material": "concrete", "aliases": ["기둥"], "prefixes": [], "patterns": ["^SC[- ]?\\d"] }
              ]
            }
            """;
        var analysis = LayerNameAnalyzer.Analyze(
            ["벽", "wall", "콘크리트 벽", "기둥", "SC5 (Bracing)", "misc-01"],
            LayerAliasMatcher.Parse(seed));

        // Observed overlap still groups only what the names actually share.
        var observed = Assert.Single(analysis.Groups);
        Assert.Equal("벽", observed.Key);
        Assert.DoesNotContain("wall", observed.Members);

        var concepts = analysis.ConceptGroups!;
        var wall = concepts.Single(group => group.Concept == "WALL");
        var column = concepts.Single(group => group.Concept == "COLUMN");
        // The cross-script pair the characters could never link.
        Assert.Equal(["wall", "벽"], wall.Members.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(["SC5 (Bracing)", "기둥"], column.Members.OrderBy(name => name, StringComparer.Ordinal));
        // A concept only one layer matches is not a join worth proposing.
        Assert.DoesNotContain(concepts, group => group.Members.Count < 2);
    }

    [Fact]
    public void WithoutASeedThereAreNoConceptSuggestionsAtAll()
    {
        var analysis = LayerNameAnalyzer.Analyze(["벽", "wall", "기둥"]);
        Assert.Null(analysis.ConceptGroups?.FirstOrDefault());
    }

    [Fact]
    public void RedundantSubstringKeysCollapseToTheRecognisableOne()
    {
        // 콘크리, 콘크리트, 크리트 all cover the same two layers; only the longest survives.
        var analysis = LayerNameAnalyzer.Analyze(["콘크리트A", "콘크리트B"]);

        var group = Assert.Single(analysis.Groups);
        Assert.Equal("콘크리트", group.Key);
    }

    [Fact]
    public void SingletonsAndEmptyInputAreHandledWithoutInventingGroups()
    {
        var singles = LayerNameAnalyzer.Analyze(["Default", "misc-01", "잡동사니"]);
        Assert.Empty(singles.Groups);
        Assert.Equal(3, singles.Ungrouped.Count);

        var empty = LayerNameAnalyzer.Analyze([]);
        Assert.Equal(0, empty.LayerCount);
        Assert.Empty(empty.Groups);
        Assert.Empty(empty.Ungrouped);
    }

    [Fact]
    public void EveryLayerIsAccountedForExactlyOnce()
    {
        string[] names =
        [
            "구조::SC1", "구조::SC2", "구조::SG1",
            "콘크리트 벽", "외벽-콘크리트", "벽", "마감", "마감재",
            "misc-stuff-01", "Default", "BlockOnly",
        ];

        var analysis = LayerNameAnalyzer.Analyze(names);

        var placed = analysis.Groups.SelectMany(group => group.Members).ToArray();
        Assert.Equal(placed.Length, placed.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            names.OrderBy(name => name, StringComparer.Ordinal),
            placed.Concat(analysis.Ungrouped).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(names.Length, analysis.LayerCount);
    }

    [Fact]
    public void AnalysisIsDeterministicRegardlessOfInputOrder()
    {
        string[] names = ["구조::SC1", "구조::SC2", "콘크리트 벽", "벽", "misc-01"];
        var forward = LayerNameAnalyzer.Analyze(names);
        var reversed = LayerNameAnalyzer.Analyze(names.Reverse().ToArray());

        Assert.Equal(
            forward.Groups.Select(group => $"{group.Kind}:{group.Key}:{string.Join(",", group.Members)}"),
            reversed.Groups.Select(group => $"{group.Kind}:{group.Key}:{string.Join(",", group.Members)}"));
        Assert.Equal(forward.Ungrouped, reversed.Ungrouped);
    }
}
