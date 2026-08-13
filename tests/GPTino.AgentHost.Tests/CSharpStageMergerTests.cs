using GPTino.AgentHost.Runtime;
using static GPTino.AgentHost.Runtime.CSharpStageMerger;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// The deterministic stage merger (W3). Invariants: the merged text always parses; every refusal
/// is a thrown message naming the offending stage and the fix; Compose(TryParseLayout(s)) == s
/// (block edits round-trip byte-exactly); renames never touch member positions.
/// </summary>
public sealed class CSharpStageMergerTests
{
    private static StageSpec Stage(
        string blockId,
        string source,
        (string Name, string? FromBlock, string? FromOutput)[]? inputs = null,
        string[]? outputs = null,
        string nick = "") =>
        new(
            Guid.NewGuid(),
            blockId,
            string.IsNullOrEmpty(nick) ? blockId : nick,
            source,
            (inputs ?? Array.Empty<(string, string?, string?)>())
                .Select(input => new StageInputSpec(
                    new StageSocketSpec(input.Name, "object", "item", false),
                    input.FromBlock,
                    input.FromOutput))
                .ToArray(),
            (outputs ?? Array.Empty<string>())
                .Select(output => new StageSocketSpec(output, "object", "item", false))
                .ToArray());

    [Fact]
    public void TwoStageMergeEmitsSeamMarkersAndMeta()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var pts = 12;\n", outputs: new[] { "pts" }),
            Stage("s2", "b = points * 2;\n", inputs: new[] { ("points", (string?)"s1", (string?)"pts") }, outputs: Array.Empty<string>()),
        });

        Assert.StartsWith("// #! csharp\n// <gptino:stages v1> ", merged.Source, StringComparison.Ordinal);
        Assert.Contains("// <stage:s1>\nvar pts = 12;\n// </stage:s1>", merged.Source, StringComparison.Ordinal);
        Assert.Contains("var points = pts; // <seam:s2.points>", merged.Source, StringComparison.Ordinal);
        Assert.Contains("// <stage:s2>\nb = points * 2;\n// </stage:s2>", merged.Source, StringComparison.Ordinal);
        Assert.Empty(merged.Inputs);
    }

    [Fact]
    public void MatchingSeamNameNeedsNoAssignment()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var pts = 7;\n", outputs: new[] { "pts" }),
            Stage("s2", "var b = pts + 1;\n", inputs: new[] { ("pts", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        });

        Assert.DoesNotContain("<seam:", merged.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void CollidingDeclarationIsRenamedThroughout()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var result = 1.0;\nvar pts = result * 2;\n", outputs: new[] { "pts" }),
            Stage(
                "s2",
                "var result = pts + 1;\nvar final = result * result;\n",
                inputs: new[] { ("pts", (string?)"s1", (string?)"pts") },
                outputs: new[] { "final" }),
        });

        Assert.Contains("var result_s2 = pts + 1;", merged.Source, StringComparison.Ordinal);
        Assert.Contains("var final = result_s2 * result_s2;", merged.Source, StringComparison.Ordinal);
        // Stage 1's declaration is untouched.
        Assert.Contains("var result = 1.0;", merged.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void RenameSkipsMemberAccessPositionsOfOtherNames()
    {
        // Stage 2's local 'count' collides; its usage 'list.Count' must stay untouched even though
        // the identifier text differs only by case here — and 'item.count' (same text, member
        // position) forces a refusal instead of a wrong rename.
        var merged = Merge(new[]
        {
            Stage("s1", "var count = 5;\nvar pts = count;\n", outputs: new[] { "pts" }),
            Stage(
                "s2",
                "var count = pts + 1;\nvar b = System.Math.Max(count, 2);\n",
                inputs: new[] { ("pts", (string?)"s1", (string?)"pts") },
                outputs: new[] { "b" }),
        });

        Assert.Contains("var count_s2 = pts + 1;", merged.Source, StringComparison.Ordinal);
        Assert.Contains("System.Math.Max(count_s2, 2)", merged.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void CollisionUsedAsMemberNameRefusesWithRenameInstruction()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Merge(new[]
        {
            Stage("s1", "var Length = 1;\nvar pts = Length;\n", outputs: new[] { "pts" }),
            Stage(
                "s2",
                "var Length = pts.ToString().Length;\nvar b = Length;\n",
                inputs: new[] { ("pts", (string?)"s1", (string?)"pts") },
                outputs: new[] { "b" }),
        }));

        Assert.Contains("member name", exception.Message, StringComparison.Ordinal);
        Assert.Contains("s2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssignedOutputIsPromotedToDeclaration()
    {
        // Real script-mode sources ASSIGN their outputs (sockets are framework-declared) — the
        // merger promotes the non-sink form to a local declaration.
        var merged = Merge(new[]
        {
            Stage("s1", "var list = 3;\npts = list;\n", outputs: new[] { "pts" }),
            Stage("s2", "b = pts;\n", inputs: new[] { ("pts", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        });

        Assert.Contains("var pts = list;", merged.Source, StringComparison.Ordinal);
        // The sink's output stays an assignment: 'b' is the merged component's own socket.
        Assert.Contains("\nb = pts;", merged.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("var b =", merged.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotedOutputDemotesBackOnSplit()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var list = 3;\npts = list;\n", outputs: new[] { "pts" }),
            Stage("s2", "b = pts;\n", inputs: new[] { ("pts", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        });

        Assert.True(TryParseLayout(merged.Source, out var layout, out var error), error);
        var stage1 = BuildStageSource(layout!, "s1");

        Assert.Contains("pts = list;", stage1, StringComparison.Ordinal);
        Assert.DoesNotContain("var pts", stage1, StringComparison.Ordinal);
    }

    [Fact]
    public void BranchOnlyOutputAssignmentRefusesPromotion()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Merge(new[]
        {
            Stage("s1", "if (System.DateTime.Now.Year > 0) pts = 3;\n", outputs: new[] { "pts" }),
            Stage("s2", "b = pts;\n", inputs: new[] { ("pts", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        }));

        Assert.Contains("cannot promote", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'pts'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfReferencingOutputAssignmentRefusesPromotion()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Merge(new[]
        {
            Stage("s1", "pts = pts ?? new object();\n", outputs: new[] { "pts" }),
            Stage("s2", "b = pts;\n", inputs: new[] { ("pts", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        }));

        Assert.Contains("cannot promote", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TopLevelTypeDeclarationRefuses()
    {
        // Statements first, then the class — the parseable order; class-before-statements already
        // fails the parse itself, which is also a refusal, just a less specific one.
        var exception = Assert.Throws<InvalidOperationException>(() => Merge(new[]
        {
            Stage("s1", "var pts = 1;\nclass Helper { public int V; }\n", outputs: new[] { "pts" }),
            Stage("s2", "var b = pts;\n", inputs: new[] { ("pts", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        }));

        Assert.Contains("top-level type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UsingsAreHoistedAndDeduped()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "using System.Linq;\nvar pts = new[] { 1, 2 }.Sum();\n", outputs: new[] { "pts" }),
            Stage(
                "s2",
                "using System.Linq;\nusing System.Text;\nvar b = new[] { pts }.Max();\n",
                inputs: new[] { ("pts", (string?)"s1", (string?)"pts") },
                outputs: new[] { "b" }),
        });

        var firstMarker = merged.Source.IndexOf("// <stage:s1>", StringComparison.Ordinal);
        var headLines = merged.Source[..firstMarker].Split('\n');
        var tailLines = merged.Source[firstMarker..].Split('\n');
        Assert.Equal(1, headLines.Count(line => line.Trim() == "using System.Linq;"));
        Assert.Equal(1, headLines.Count(line => line.Trim() == "using System.Text;"));
        // No using directive survives inside any block (they are hoisted, recorded in meta only).
        Assert.DoesNotContain(tailLines, line => line.TrimStart().StartsWith("using ", StringComparison.Ordinal));
    }

    [Fact]
    public void CSharpDirectiveIsStrippedFromStagesAndCanonicalAtHead()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "// #! csharp\nvar pts = 1;\n", outputs: new[] { "pts" }),
            Stage("s2", "// #! csharp\nvar b = pts;\n", inputs: new[] { ("pts", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        });

        Assert.Equal(1, CountOccurrences(merged.Source, "// #! csharp"));
        Assert.StartsWith("// #! csharp\n", merged.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void PythonStageRefuses()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Merge(new[]
        {
            Stage("s1", "#! python 3\npts = 1\n", outputs: new[] { "pts" }),
            Stage("s2", "var b = pts;\n", inputs: new[] { ("pts", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        }));

        Assert.Contains("C# stages only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalInputCollisionRenamesMergedSocket()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var pts = count * 2;\n", inputs: new[] { ("count", (string?)null, (string?)null) }, outputs: new[] { "pts" }),
            Stage(
                "s2",
                "var b = pts + count;\n",
                inputs: new[]
                {
                    ("pts", (string?)"s1", (string?)"pts"),
                    ("count", (string?)null, (string?)null),
                },
                outputs: new[] { "b" }),
        });

        Assert.Equal(2, merged.Inputs.Count);
        Assert.Equal("count", merged.Inputs[0].Socket.Name);
        Assert.Equal("count_s2", merged.Inputs[1].Socket.Name);
        Assert.Equal("count", merged.Inputs[1].OriginalName);
        Assert.Contains("var b = pts + count_s2;", merged.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void SinkOutputsBecomeMergedOutputs()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var pts = 1;\n", outputs: new[] { "pts" }),
            Stage(
                "s2",
                "var area = pts * 2.0;\nvar report = area.ToString();\n",
                inputs: new[] { ("pts", (string?)"s1", (string?)"pts") },
                outputs: new[] { "area", "report" }),
        });

        Assert.Equal(new[] { "area", "report" }, merged.Outputs.Select(output => output.Socket.Name).ToArray());
        Assert.All(merged.Outputs, output => Assert.Equal("s2", output.StageBlockId));
    }

    [Fact]
    public void LayoutRoundTripsByteExact()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "using System.Linq;\nvar result = 1.0;\nvar pts = new[] { result }.Sum();\n", outputs: new[] { "pts" }),
            Stage(
                "s2",
                "var result = points + 1;\nvar b = result;\n",
                inputs: new[] { ("points", (string?)"s1", (string?)"pts") },
                outputs: new[] { "b" }),
        });

        Assert.True(TryParseLayout(merged.Source, out var layout, out var error), error);
        Assert.Equal(merged.Source, Compose(layout!));
        Assert.Equal(new[] { "s1", "s2" }, layout!.Blocks.Select(block => block.BlockId).ToArray());
        Assert.Equal(2, layout.Meta.Stages.Count);
    }

    [Fact]
    public void ReplaceBlockSplicesAndKeepsSeams()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var pts = 12;\n", outputs: new[] { "pts" }),
            Stage("s2", "var b = points * 2;\n", inputs: new[] { ("points", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        });

        var replaced = ReplaceBlock(merged.Source, "s1", "var pts = 99; // heavier\n");

        Assert.Contains("var pts = 99; // heavier", replaced, StringComparison.Ordinal);
        Assert.DoesNotContain("var pts = 12;", replaced, StringComparison.Ordinal);
        Assert.Contains("var points = pts; // <seam:s2.points>", replaced, StringComparison.Ordinal);
        Assert.Contains("var b = points * 2;", replaced, StringComparison.Ordinal);
        Assert.True(TryParseLayout(replaced, out _, out var error), error);
    }

    [Fact]
    public void ReplaceBlockDroppingAnOutputRefuses()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var pts = 12;\n", outputs: new[] { "pts" }),
            Stage("s2", "var b = points;\n", inputs: new[] { ("points", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => ReplaceBlock(merged.Source, "s1", "var other = 1;\n"));

        Assert.Contains("'pts'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceBlockPromotesAssignmentFormOutputs()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var list = 12;\npts = list;\n", outputs: new[] { "pts" }),
            Stage("s2", "b = points * 2;\n", inputs: new[] { ("points", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        });

        var replaced = ReplaceBlock(merged.Source, "s1", "var big = 99;\npts = big;\n");

        Assert.Contains("var pts = big;", replaced, StringComparison.Ordinal);
        Assert.True(TryParseLayout(replaced, out _, out var error), error);
    }

    [Fact]
    public void ReplaceBlockRedeclaringSeamInputRefuses()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var pts = 12;\n", outputs: new[] { "pts" }),
            Stage("s2", "var b = points;\n", inputs: new[] { ("points", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => ReplaceBlock(merged.Source, "s2", "var points = 1;\nvar b = points;\n"));

        Assert.Contains("'points'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceBlockUnknownBlockRefusesListingBlocks()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var pts = 12;\n", outputs: new[] { "pts" }),
            Stage("s2", "var b = points;\n", inputs: new[] { ("points", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => ReplaceBlock(merged.Source, "s9", "var x = 1;\n"));

        Assert.Contains("s1, s2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceBlockWithMarkerContentRefuses()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var pts = 12;\n", outputs: new[] { "pts" }),
            Stage("s2", "var b = points;\n", inputs: new[] { ("points", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        });

        Assert.Throws<InvalidOperationException>(
            () => ReplaceBlock(merged.Source, "s1", "var pts = 1;\n// <stage:s9>\n"));
    }

    [Fact]
    public void MergedSourceSurvivesWatchdogRoundTrip()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var pts = 0.0;\nfor (var i = 0; i < 100; i++) pts += i;\n", outputs: new[] { "pts" }),
            Stage(
                "s2",
                "var b = 0.0;\nforeach (var i in new[] { 1, 2 }) b += points;\n",
                inputs: new[] { ("points", (string?)"s1", (string?)"pts") },
                outputs: new[] { "b" }),
        });

        var injected = CSharpWatchdogInjector.Inject(merged.Source, 30_000);

        Assert.NotEqual(merged.Source, injected);
        Assert.Equal(merged.Source, CSharpWatchdogInjector.Strip(injected));
        // The guarded text still parses as a merged layout after stripping.
        Assert.True(TryParseLayout(CSharpWatchdogInjector.Strip(injected), out _, out var error), error);
    }

    [Fact]
    public void BuildStageSourceRestoresUsings()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "using System.Linq;\nvar pts = new[] { 1 }.Sum();\n", outputs: new[] { "pts" }),
            Stage("s2", "var b = pts;\n", inputs: new[] { ("pts", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        });

        Assert.True(TryParseLayout(merged.Source, out var layout, out _));
        var stage1 = BuildStageSource(layout!, "s1");

        Assert.StartsWith("using System.Linq;\n", stage1, StringComparison.Ordinal);
        // The output declaration demotes to an assignment: recreated as a component, 'pts' is a
        // framework-declared socket again.
        Assert.Contains("pts = new[] { 1 }.Sum();", stage1, StringComparison.Ordinal);
        Assert.DoesNotContain("var pts", stage1, StringComparison.Ordinal);
        Assert.DoesNotContain("<stage:", stage1, StringComparison.Ordinal);
    }

    [Fact]
    public void MergedStageAsInputRefuses()
    {
        var merged = Merge(new[]
        {
            Stage("s1", "var pts = 12;\n", outputs: new[] { "pts" }),
            Stage("s2", "var b = points;\n", inputs: new[] { ("points", (string?)"s1", (string?)"pts") }, outputs: new[] { "b" }),
        });

        var exception = Assert.Throws<InvalidOperationException>(() => Merge(new[]
        {
            Stage("m1", merged.Source, outputs: new[] { "b" }),
            Stage("m2", "var c = b;\n", inputs: new[] { ("b", (string?)"m1", (string?)"b") }, outputs: new[] { "c" }),
        }));

        Assert.Contains("split it first", exception.Message, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }
}
