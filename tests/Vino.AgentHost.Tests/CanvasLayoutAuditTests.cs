using Vino.AgentHost.Runtime;
using Vino.CanvasSceneAdapter;

namespace Vino.AgentHost.Tests;

/// <summary>
/// The server-side measurement of a canvas arrangement. It exists because a tidy job's only
/// acceptance criterion used to be "no runtime error", so an arrangement that stacked half the
/// document into one column committed as a success — and the model, which never sees pivots,
/// could not tell either.
/// </summary>
public sealed class CanvasLayoutAuditTests
{
    private static CanvasObjectState Obj(Guid id, float x, float y, float w = 100f, float h = 40f) =>
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

    [Fact]
    public void FlagsABackwardWire()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        // b feeds a, but b sits to the RIGHT of a: the wire runs right-to-left on screen.
        var canvas = Snapshot([Obj(a, 0, 0), Obj(b, 500, 0)], [Wire(b, a)]);

        var report = CanvasLayoutAudit.Measure(canvas);

        Assert.Equal(1, report.BackwardWires);
        Assert.Contains(report.Findings(), finding => finding.Contains("right-to-left", StringComparison.Ordinal));
    }

    [Fact]
    public void CleanLeftToRightLayoutHasNoFindings()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(a, 0, 0), Obj(b, 300, 0)],
            [Wire(a, b)],
            [new GroupState(groupId, "stage", new[] { a, b }, 0)]);

        var report = CanvasLayoutAudit.Measure(canvas);

        Assert.Equal(0, report.BackwardWires);
        Assert.Equal(0, report.LongWires);
        Assert.Equal(0, report.UngroupedCount);
        Assert.Empty(report.Findings());
    }

    [Fact]
    public void FlagsAColumnHoldingMostOfTheDocument()
    {
        // Sources stacked in one column feeding one consumer far to the right: the shape the old
        // layering produced on every real definition. Enough of them to clear the minimum-sample
        // guard, since a crowding ratio is meaningless on a handful of nodes.
        var consumer = Guid.NewGuid();
        var sources = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToArray();
        var objects = sources
            .Select((id, index) => Obj(id, 0, index * 600f))
            .Append(Obj(consumer, 2000, 0))
            .ToList();
        var canvas = Snapshot(objects, sources.Select(id => Wire(id, consumer)).ToList());

        var report = CanvasLayoutAudit.Measure(canvas);

        Assert.True(report.WidestColumnShare > CanvasLayoutAudit.CrowdedColumnShare);
        Assert.Equal(9, report.LongWires);
        Assert.Contains(report.Findings(), finding => finding.Contains("share one column", StringComparison.Ordinal));
    }

    [Fact]
    public void GroupsAreExcludedFromTheMeasurements()
    {
        // The group's union rectangle is wider than anything else on the canvas; counting it as a
        // node would dominate every column statistic it appears in.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(a, 0, 0), Obj(b, 300, 0), Obj(groupId, 0, 0, w: 1900f, h: 400f)],
            [Wire(a, b)],
            [new GroupState(groupId, "stage", new[] { a, b }, 0)]);

        var report = CanvasLayoutAudit.Measure(canvas);

        Assert.Equal(2, report.NodeCount);
        Assert.Equal(0, report.UngroupedCount);
    }

    [Fact]
    public void CountsOneColumnEvenWhenItsMembersHaveDifferentWidths()
    {
        // Right-edge alignment puts members of a column at DIFFERENT pivots — the pivot offset is
        // half the width difference. Quantizing x therefore split one real 34-node column into
        // seven and reported 1.5% crowding where the truth was 52.3%: the predicate could not fire
        // on the exact case it exists to catch.
        var consumer = Guid.NewGuid();
        var widths = new[] { 60f, 120f, 200f, 300f, 60f, 120f, 200f, 300f, 90f };
        var sources = widths.Select(_ => Guid.NewGuid()).ToArray();
        var objects = sources
            .Select((id, index) =>
            {
                // Common right edge at x = 0, so pivots spread across 120px of x.
                var width = widths[index];
                return Obj(id, -width / 2f, index * 60f, w: width);
            })
            .Append(Obj(consumer, 2000, 0))
            .ToList();
        var canvas = Snapshot(objects, sources.Select(id => Wire(id, consumer)).ToList());

        var report = CanvasLayoutAudit.Measure(canvas);

        Assert.Equal(0.9, report.WidestColumnShare, 3);
        Assert.Equal(0f, report.RightEdgeScatter);
    }

    [Fact]
    public void FlagsMisalignedRightEdges()
    {
        // Same column (same x), different widths: their right edges disagree by exactly the width
        // difference — the "정렬도 안 맞고" symptom, in one number.
        var narrow = Guid.NewGuid();
        var wide = Guid.NewGuid();
        var canvas = Snapshot([Obj(narrow, 0, 0, w: 60f), Obj(wide, 0, 100, w: 300f)]);

        var report = CanvasLayoutAudit.Measure(canvas);

        Assert.True(report.RightEdgeScatter > CanvasLayoutAudit.EdgeScatterTolerance);
        Assert.Contains(report.Findings(), finding => finding.Contains("Output edges", StringComparison.Ordinal));
    }
}
