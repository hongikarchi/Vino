using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// Two axes, resolved independently. Written against the real structural model's shape, where the
/// parent layer declares the material (철골 = steel) and the leaf mark declares the element
/// (SC = column) — the case that proved bundling the two produces a steel column painted concrete.
/// </summary>
public sealed class LayerSchemeTests
{
    private const string RealShapeScheme = """
        {
          "elements": [
            { "canonical": "COLUMN", "aliases": ["기둥"], "prefixes": [], "patterns": ["^SC[- ]?\\d"] },
            { "canonical": "GIRDER", "aliases": [], "prefixes": [], "patterns": ["^SG[- ]?\\d"] },
            { "canonical": "BEAM",   "aliases": [], "prefixes": [], "patterns": ["^SB[- ]?\\d"] },
            { "canonical": "WALL",   "aliases": ["벽"], "prefixes": ["A-Wall"], "patterns": [] }
          ],
          "materials": [
            { "material": "steel", "underPath": "철골" },
            { "material": "concrete", "aliases": ["콘크리트"] }
          ]
        }
        """;

    [Fact]
    public void ParentScopeGivesTheMaterialWhileTheLeafMarkGivesTheElement()
    {
        var scheme = LayerScheme.Parse(RealShapeScheme);

        var column = scheme.Resolve("철골::철골_3D::SC1");
        Assert.Equal("COLUMN", column.Element);
        Assert.Equal("steel", column.Material);
        Assert.Contains("SC", column.ElementEvidence, StringComparison.Ordinal);
        Assert.Contains("철골", column.MaterialEvidence, StringComparison.Ordinal);
        // Both halves certain: the mark is a pattern (medium) and the scope is exact (high), so
        // the row reads as the WEAKER of the two.
        Assert.Equal(LayerMatchConfidence.Medium, column.ElementConfidence);
        Assert.Equal(LayerMatchConfidence.High, column.MaterialConfidence);
    }

    [Fact]
    public void TheSameMarkIsNotForcedToTheSeedsMaterial()
    {
        // The defect this whole separation exists for: SC means column, but in THIS project a
        // column is steel. The scheme must never hand back concrete for it.
        var scheme = LayerScheme.Parse(RealShapeScheme);
        var resolved = scheme.Resolve("철골::철골_3D::SC5 (Bracing)");

        Assert.Equal("COLUMN", resolved.Element);
        Assert.Equal("steel", resolved.Material);
        Assert.NotEqual("concrete", resolved.Material);
    }

    [Fact]
    public void EitherAxisMayBeUnresolvedOnItsOwn()
    {
        var scheme = LayerScheme.Parse(RealShapeScheme);

        // Element known, material unknown — a normal, reportable state.
        var wall = scheme.Resolve("3D::A-Wall");
        Assert.Equal("WALL", wall.Element);
        Assert.Null(wall.Material);
        Assert.True(wall.Resolved);

        // Material known from the name, element unknown.
        var slab = scheme.Resolve("3D::콘크리트슬래브");
        Assert.Null(slab.Element);
        Assert.Equal("concrete", slab.Material);

        // Neither.
        var nothing = scheme.Resolve("3D::0-CONTEXT::대지경계선");
        Assert.Null(nothing.Element);
        Assert.Null(nothing.Material);
        Assert.False(nothing.Resolved);
    }

    [Fact]
    public void TheMostSpecificScopeWins()
    {
        const string nested = """
            {
              "elements": [],
              "materials": [
                { "material": "steel", "underPath": "철골" },
                { "material": "glass", "underPath": "철골::철골_3D::커튼월" }
              ]
            }
            """;
        var scheme = LayerScheme.Parse(nested);

        Assert.Equal("steel", scheme.Resolve("철골::철골_3D::SC1").Material);
        Assert.Equal("glass", scheme.Resolve("철골::철골_3D::커튼월::유리").Material);
    }

    [Fact]
    public void AnEmptySchemeIsRecognisedRatherThanTreatedAsRules()
    {
        Assert.True(LayerScheme.Parse("""{ "preset": "material-realistic" }""").IsEmpty);
        Assert.True(LayerScheme.Parse("""{ "elements": [], "materials": [] }""").IsEmpty);
        Assert.False(LayerScheme.Parse(RealShapeScheme).IsEmpty);
    }

    [Fact]
    public void MalformedRulesAreRefusedInsteadOfSilentlyMatchingNothing()
    {
        // A material rule with no way to match anything is a typo, not a rule.
        Assert.Throws<FormatException>(() => LayerScheme.Parse(
            """{ "materials": [ { "material": "steel" } ] }"""));
        Assert.Throws<FormatException>(() => LayerScheme.Parse(
            """{ "elements": [ { "canonical": "" } ] }"""));
        Assert.Throws<FormatException>(() => LayerScheme.Parse(
            """{ "elements": [ { "canonical": "WALL" }, { "canonical": "wall" } ] }"""));
        Assert.Throws<FormatException>(() => LayerScheme.Parse(
            """{ "elements": [ { "canonical": "WALL", "patterns": ["([unclosed"] } ] }"""));
    }

    [Fact]
    public void ACatastrophicPatternDegradesToNoMatchInsteadOfThrowing()
    {
        var scheme = LayerScheme.Parse(
            """{ "elements": [ { "canonical": "TRAP", "patterns": ["^(a+)+$"] } ] }""");

        Assert.Null(scheme.Resolve(new string('a', 40) + "X").Element);
    }
}
