using GPTino.AgentHost.Runtime;
using GPTino.CanvasSceneAdapter;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// Pure-function tests for the deterministic tidy layout: layered left→right dataflow, real-bounds
/// spacing (no overlap), crossing reduction, group contiguity, connected-cluster scoping, and
/// idempotency (re-tidying an already-tidy graph is a no-op).
/// </summary>
public sealed class CanvasLayoutTests
{
    private const float W = 100f;
    private const float H = 40f;

    private static CanvasObjectState Obj(Guid id, float x, float y, float w = W, float h = H) =>
        new(id, Guid.NewGuid(), "n", new CanvasPoint(x, y), new CanvasSize(w, h), "fp")
        {
            BoundsOrigin = new CanvasPoint(x - w / 2f, y - h / 2f),
        };

    private static WireState Wire(Guid source, Guid target) =>
        new(source, Guid.NewGuid(), target, Guid.NewGuid());

    private static CanvasSnapshot Snapshot(
        IReadOnlyList<CanvasObjectState> objects,
        IReadOnlyList<WireState>? wires = null,
        IReadOnlyList<GroupState>? groups = null) =>
        new(Guid.NewGuid(), "doc", objects, wires ?? Array.Empty<WireState>(), groups ?? Array.Empty<GroupState>());

    // Applies computed moves back onto the snapshot (pivot + bounds-origin shift) so idempotency can be
    // checked the way the live pipeline would see it after a canvas.move commits.
    private static CanvasSnapshot ApplyMoves(CanvasSnapshot canvas, IReadOnlyDictionary<Guid, CanvasPoint> moves)
    {
        var objects = canvas.Objects.Select(o =>
        {
            if (!moves.TryGetValue(o.ObjectId, out var pivot))
            {
                return o;
            }
            var dx = pivot.X - o.Pivot.X;
            var dy = pivot.Y - o.Pivot.Y;
            return o with
            {
                Pivot = pivot,
                BoundsOrigin = o.BoundsOrigin is { } b ? new CanvasPoint(b.X + dx, b.Y + dy) : null,
            };
        }).ToList();
        return canvas with { Objects = objects };
    }

    // Resolved position: the engine omits no-op moves, so a node absent from the result stayed put.
    private static CanvasPoint Pos(CanvasSnapshot canvas, IReadOnlyDictionary<Guid, CanvasPoint> moves, Guid id) =>
        moves.TryGetValue(id, out var p) ? p : canvas.Objects.First(o => o.ObjectId == id).Pivot;

    [Fact]
    public void LinearChainFlowsLeftToRight()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        // All piled at the origin so every node must move; wires a -> b -> c.
        var canvas = Snapshot(
            [Obj(a, 0, 0), Obj(b, 5, 5), Obj(c, 10, 10)],
            [Wire(a, b), Wire(b, c)]);

        var moves = CanvasLayout.Arrange(canvas, new[] { b });

        Assert.True(Pos(canvas, moves, a).X < Pos(canvas, moves, b).X, "input must be left of script");
        Assert.True(Pos(canvas, moves, b).X < Pos(canvas, moves, c).X, "script must be left of output");
    }

    [Fact]
    public void ParallelInputsShareTheLeftColumnAndDoNotOverlap()
    {
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var script = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(s1, 0, 0), Obj(s2, 0, 0), Obj(script, 0, 0)],
            [Wire(s1, script), Wire(s2, script)]);

        var moves = CanvasLayout.Arrange(canvas, new[] { script });

        // Two sliders share one column (equal X), the script sits to their right.
        Assert.Equal(Pos(canvas, moves, s1).X, Pos(canvas, moves, s2).X, 3);
        Assert.True(Pos(canvas, moves, script).X > Pos(canvas, moves, s1).X);
        // Column members do not overlap vertically (centers at least one node-height apart).
        Assert.True(Math.Abs(Pos(canvas, moves, s1).Y - Pos(canvas, moves, s2).Y) >= H);
    }

    [Fact]
    public void IsDeterministic()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var canvas = Snapshot([Obj(a, 3, 7), Obj(b, 1, 2)], [Wire(a, b)]);

        var first = CanvasLayout.Arrange(canvas, new[] { a });
        var second = CanvasLayout.Arrange(canvas, new[] { a });

        Assert.Equal(first.Count, second.Count);
        foreach (var (id, pivot) in first)
        {
            Assert.Equal(pivot.X, second[id].X, 4);
            Assert.Equal(pivot.Y, second[id].Y, 4);
        }
    }

    [Fact]
    public void ReTidyingAnAlreadyTidyGraphIsANoOp()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(a, 0, 0), Obj(b, 5, 40), Obj(c, 12, 80)],
            [Wire(a, b), Wire(b, c)]);

        var first = CanvasLayout.Arrange(canvas, new[] { a });
        var tidied = ApplyMoves(canvas, first);
        var second = CanvasLayout.Arrange(tidied, new[] { a });

        Assert.Empty(second);
    }

    [Fact]
    public void OnlyMovesTheClusterContainingTheSeed()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var lonelyUserNode = Guid.NewGuid(); // wired to nothing — a separate hand-built node
        var canvas = Snapshot(
            [Obj(a, 0, 0), Obj(b, 0, 0), Obj(lonelyUserNode, 500, 500)],
            [Wire(a, b)]);

        var moves = CanvasLayout.Arrange(canvas, new[] { a });

        Assert.False(moves.ContainsKey(lonelyUserNode));
        Assert.True(moves.ContainsKey(a) || moves.ContainsKey(b));
    }

    [Fact]
    public void RelatedUserNodesAreIncludedInTheCluster()
    {
        var userSlider = Guid.NewGuid();   // user placed it; agent wired it in
        var agentScript = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(userSlider, 0, 0), Obj(agentScript, 0, 0)],
            [Wire(userSlider, agentScript)]);

        // Seed is only the agent's node, but the wired user slider is part of the same dataflow cluster.
        var moves = CanvasLayout.Arrange(canvas, new[] { agentScript });

        Assert.True(Pos(canvas, moves, userSlider).X < Pos(canvas, moves, agentScript).X);
    }

    [Fact]
    public void KeepsSameGroupNodesContiguousInAColumn()
    {
        // Three sliders all feed one script, so all three land in layer 0. Two of them are grouped; their
        // initial Y ordering interleaves the odd one out.
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var ungrouped = Guid.NewGuid();
        var script = Guid.NewGuid();
        var group = new GroupState(Guid.NewGuid(), "pair", new[] { g1, g2 }, 0);
        var canvas = Snapshot(
            [Obj(g1, 0, 0), Obj(ungrouped, 0, 50), Obj(g2, 0, 100), Obj(script, 0, 50)],
            [Wire(g1, script), Wire(ungrouped, script), Wire(g2, script)],
            [group]);

        var moves = CanvasLayout.Arrange(canvas, new[] { script });

        // Order the layer-0 nodes by their resulting Y; the two group members must be adjacent.
        var column = new[] { g1, g2, ungrouped }
            .OrderBy(id => Pos(canvas, moves, id).Y)
            .ToArray();
        var i1 = Array.IndexOf(column, g1);
        var i2 = Array.IndexOf(column, g2);
        Assert.Equal(1, Math.Abs(i1 - i2));
    }

    [Fact]
    public void StraightensAMergeNodeOntoTheMeanOfItsInputs()
    {
        // t1,t2 feed m; t3 feeds m2; m and m2 both feed out — one cluster, so t3 shares the input column but
        // is NOT wired to m. Pure column-centering would park m at the shared band center (mean of all three
        // t's); wire-straightening must instead sit m on the mean of ONLY its two real inputs.
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var t3 = Guid.NewGuid();
        var m = Guid.NewGuid();
        var m2 = Guid.NewGuid();
        var output = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(t1, 0, 0), Obj(t2, 0, 0), Obj(t3, 0, 0), Obj(m, 0, 0), Obj(m2, 0, 0), Obj(output, 0, 0)],
            [Wire(t1, m), Wire(t2, m), Wire(t3, m2), Wire(m, output), Wire(m2, output)]);

        var moves = CanvasLayout.Arrange(canvas, new[] { m });

        var meanOfInputs = (Pos(canvas, moves, t1).Y + Pos(canvas, moves, t2).Y) / 2f;
        Assert.True(Math.Abs(Pos(canvas, moves, m).Y - meanOfInputs) < 1.0f,
            "merge node must align to the mean Y of exactly its two inputs");
        // ... and therefore not simply share the band center with the unrelated m2 (fed by t3 alone).
        Assert.True(Math.Abs(Pos(canvas, moves, m).Y - Pos(canvas, moves, m2).Y) > 1.0f);
    }

    [Fact]
    public void SeparatesDifferentGroupsInAColumnByTheGroupGap()
    {
        // Two groups of two sliders all feed one script: all four share layer 0. Members of a group sit one
        // RowGap apart; the boundary between the two groups gets an extra GroupGap of clearance.
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var b1 = Guid.NewGuid();
        var b2 = Guid.NewGuid();
        var script = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(a1, 0, 0), Obj(a2, 0, 0), Obj(b1, 0, 0), Obj(b2, 0, 0), Obj(script, 0, 0)],
            [Wire(a1, script), Wire(a2, script), Wire(b1, script), Wire(b2, script)],
            [new GroupState(Guid.NewGuid(), "A", new[] { a1, a2 }, 0),
             new GroupState(Guid.NewGuid(), "B", new[] { b1, b2 }, 0)]);

        var moves = CanvasLayout.Arrange(canvas, new[] { script });

        var ys = new[] { a1, a2, b1, b2 }.Select(id => Pos(canvas, moves, id).Y).OrderBy(y => y).ToArray();
        var gaps = new[] { ys[1] - ys[0], ys[2] - ys[1], ys[3] - ys[2] };
        // The group boundary is the single widest gap; it exceeds the within-group gaps by exactly
        // GroupGap. Read from Options rather than hard-coded: the spacing constants are tuned
        // against measured canvases, and this test is about the RELATIONSHIP, not the number.
        var groupGap = CanvasLayout.Options.Default.GroupGap;
        Assert.True(gaps.Max() - gaps.Min() > 1f, "the between-group gap must exceed the within-group gap");
        Assert.True(
            Math.Abs((gaps.Max() - gaps.Min()) - groupGap) < 2f,
            $"the extra clearance equals GroupGap ({groupGap})");
    }

    [Fact]
    public void EmptySeedSetProducesNoMoves()
    {
        var canvas = Snapshot([Obj(Guid.NewGuid(), 0, 0)]);
        Assert.Empty(CanvasLayout.Arrange(canvas, Array.Empty<Guid>()));
    }

    [Fact]
    public void GroupObjectsAreNeverMoved()
    {
        // A GH_Group arrives in Objects AS WELL AS Groups, with no discriminator, a union-rectangle
        // Bounds and a (0,0) Pivot. Treated as a node it (a) has no wires so it lands in the source
        // column, (b) its width becomes that column's width, and (c) its pivot never matches the
        // computed one, so it is "moved" again every single turn. One real arrange payload was
        // 7 pivots, all of them groups. No test ever put a group in Objects, which is why this
        // survived.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var canvas = Snapshot(
            [
                Obj(a, 0, 0),
                Obj(b, 0, 0),
                // The group as the adapter really emits it: huge union bounds, pivot at origin.
                Obj(groupId, 0, 0, w: 1900f, h: 400f),
            ],
            [Wire(a, b)],
            [new GroupState(groupId, "stage", new[] { a, b }, 0)]);

        var moves = CanvasLayout.Arrange(canvas, new[] { a });

        Assert.False(moves.ContainsKey(groupId), "a group is a container, not a layout node");
    }

    [Fact]
    public void SourcesAreParkedBesideTheirConsumerNotInColumnZero()
    {
        // Two stages. `late` feeds only the SECOND stage, so it belongs beside that stage — not in
        // the same column as `early`. Longest-path layering put every in-degree-0 node in column 0,
        // which on real definitions (66% sources) built one 3400px tower.
        var early = Guid.NewGuid();
        var stage1 = Guid.NewGuid();
        var stage2 = Guid.NewGuid();
        var late = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(early, 0, 0), Obj(stage1, 0, 0), Obj(stage2, 0, 0), Obj(late, 0, 0)],
            [Wire(early, stage1), Wire(stage1, stage2), Wire(late, stage2)]);

        var moves = CanvasLayout.Arrange(canvas, new[] { stage1 });

        var lateX = Pos(canvas, moves, late).X;
        var earlyX = Pos(canvas, moves, early).X;
        var stage1X = Pos(canvas, moves, stage1).X;
        var stage2X = Pos(canvas, moves, stage2).X;
        Assert.True(lateX > earlyX, "a source consumed later must not share the first column");
        Assert.True(lateX < stage2X, "it still sits left of what it feeds — flow stays left-to-right");
        Assert.True(Math.Abs(lateX - stage1X) < 1f, "it lands in the column immediately before its consumer");
    }

    [Fact]
    public void ColumnMembersShareOneRightEdge()
    {
        // Output sockets sit on the right edge, so that is the line that must agree. Centring nodes
        // of different widths splayed those edges by (widest - own)/2 — measured at 140px on a real
        // canvas whose centres agreed to 1.24px.
        var narrow = Guid.NewGuid();
        var wide = Guid.NewGuid();
        var script = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(narrow, 0, 0, w: 60f), Obj(wide, 0, 0, w: 300f), Obj(script, 0, 0)],
            [Wire(narrow, script), Wire(wide, script)]);

        var moves = CanvasLayout.Arrange(canvas, new[] { script });

        var narrowRight = Pos(canvas, moves, narrow).X + 60f / 2f;
        var wideRight = Pos(canvas, moves, wide).X + 300f / 2f;
        Assert.True(
            Math.Abs(narrowRight - wideRight) < 1f,
            $"right edges must align; got {narrowRight} vs {wideRight}");
    }
}
