using Vino.AgentHost.Runtime;
using Vino.CanvasSceneAdapter;

namespace Vino.AgentHost.Tests;

/// <summary>
/// The detailed layout audit behind GET /dev/canvas-layout-audit. Where the coarse Report grades
/// a tidy pass/fail, this one must enumerate each defect with addresses a repair can act on — so
/// the math (crossing counter, backward tolerance, gap clustering) is what these tests pin down.
/// </summary>
public sealed class CanvasLayoutDetailedAuditTests
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
    public void CountsAProperWireCrossing()
    {
        // Two left-column sources feeding the diagonally opposite right-column targets: the
        // straight-line approximations form an X with a strictly interior intersection.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(a, 0, 0), Obj(c, 0, 300), Obj(b, 400, 0), Obj(d, 400, 300)],
            [Wire(a, d), Wire(c, b)]);

        var report = CanvasLayoutAudit.MeasureDetailed(canvas);

        Assert.Equal(1, report.WireCrossingCount);
        var finding = Assert.Single(report.Findings, item => item.Kind == "wireCrossing");
        Assert.Equal(2, finding.Wires.Count);
        Assert.Equal(new HashSet<Guid> { a, b, c, d }, finding.ObjectIds.ToHashSet());
    }

    [Fact]
    public void FanOutFromOneSourceDoesNotCross()
    {
        // Both wires leave the same output-edge proxy point; meeting at a shared endpoint is not
        // a crossing, and the strict-interior test must never count it as one.
        var source = Guid.NewGuid();
        var upper = Guid.NewGuid();
        var lower = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(source, 0, 0), Obj(upper, 400, -100), Obj(lower, 400, 100)],
            [Wire(source, upper), Wire(source, lower)]);

        var report = CanvasLayoutAudit.MeasureDetailed(canvas);

        Assert.Equal(0, report.WireCrossingCount);
    }

    [Fact]
    public void FlagsBackwardWiresBeyondToleranceOnly()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var near = Guid.NewGuid();
        var nearTarget = Guid.NewGuid();
        var canvas = Snapshot(
            [
                // b feeds a from 500px to its right: clearly backward.
                Obj(a, 0, 0), Obj(b, 500, 0),
                // near feeds nearTarget whose input edge sits only 3px left of the output edge:
                // inside the 5px tolerance, so it is jitter, not a backward wire.
                Obj(near, 0, 300), Obj(nearTarget, 97, 300),
            ],
            [Wire(b, a), Wire(near, nearTarget)]);

        var report = CanvasLayoutAudit.MeasureDetailed(canvas);

        Assert.Equal(1, report.BackwardWireCount);
        var finding = Assert.Single(report.Findings, item => item.Kind == "backwardWire");
        Assert.Equal(new[] { b, a }, finding.ObjectIds);
        Assert.Equal(600.0, finding.Measure);
        var wire = Assert.Single(finding.Wires);
        Assert.Equal(b, wire.SourceObjectId);
        Assert.Equal(a, wire.TargetObjectId);
    }

    [Fact]
    public void FlagsOverlappingComponentPairs()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var clear = Guid.NewGuid();
        var canvas = Snapshot([Obj(a, 0, 0), Obj(b, 40, 10), Obj(clear, 300, 0)]);

        var report = CanvasLayoutAudit.MeasureDetailed(canvas);

        Assert.Equal(1, report.OverlappingPairCount);
        var finding = Assert.Single(report.Findings, item => item.Kind == "overlappingComponents");
        Assert.Equal(new HashSet<Guid> { a, b }, finding.ObjectIds.ToHashSet());
        // 60px of shared width, 30px of shared height.
        Assert.Equal(1800.0, finding.Measure);
    }

    [Fact]
    public void FlagsWiresLongerThanThreeTimesTheMedian()
    {
        // Three 200px wires and one 1000px wire: median 200, threshold 600.
        var pairs = Enumerable.Range(0, 3)
            .Select(index => (Source: Guid.NewGuid(), Target: Guid.NewGuid(), Y: index * 100f))
            .ToArray();
        var farSource = Guid.NewGuid();
        var farTarget = Guid.NewGuid();
        var objects = pairs
            .SelectMany(pair => new[] { Obj(pair.Source, 0, pair.Y), Obj(pair.Target, 300, pair.Y) })
            .Append(Obj(farSource, 0, 300))
            .Append(Obj(farTarget, 1100, 300))
            .ToList();
        var wires = pairs.Select(pair => Wire(pair.Source, pair.Target))
            .Append(Wire(farSource, farTarget))
            .ToList();

        var report = CanvasLayoutAudit.MeasureDetailed(Snapshot(objects, wires));

        Assert.Equal(200.0, report.MedianWireLength);
        Assert.Equal(1, report.LongWireCount);
        var finding = Assert.Single(report.Findings, item => item.Kind == "longWire");
        Assert.Equal(1000.0, finding.Measure);
    }

    [Fact]
    public void ClusterColumnsUsesGapLinkageNotQuantizedX()
    {
        // Three members sharing a right edge at x=50 but pivoting 70px apart (the width
        // difference), plus one node a real gap away: single-linkage keeps the column whole
        // where quantized x shattered it (the 5620cef regression).
        var wide = Obj(Guid.NewGuid(), -50, 0, w: 200f);
        var mid = Obj(Guid.NewGuid(), 0, 60, w: 100f);
        var slim = Obj(Guid.NewGuid(), 20, 120, w: 60f);
        var lone = Obj(Guid.NewGuid(), 500, 0);

        var columns = CanvasLayoutAudit.ClusterColumns(new[] { wide, mid, slim, lone });

        Assert.Equal(2, columns.Count);
        Assert.Equal(3, columns[0].Count);
        Assert.Single(columns[1]);
    }

    [Fact]
    public void AlignedColumnHasZeroDeviationAndNoMisalignmentFinding()
    {
        var wide = Obj(Guid.NewGuid(), -50, 0, w: 200f);
        var mid = Obj(Guid.NewGuid(), 0, 60, w: 100f);
        var slim = Obj(Guid.NewGuid(), 20, 120, w: 60f);
        var canvas = Snapshot([wide, mid, slim, Obj(Guid.NewGuid(), 500, 0)]);

        var report = CanvasLayoutAudit.MeasureDetailed(canvas);

        Assert.Equal(2, report.ColumnCount);
        Assert.Equal(0.0, report.ColumnMeanAbsoluteXDeviation);
        Assert.DoesNotContain(report.Findings, item => item.Kind == "columnMisalignment");
    }

    [Fact]
    public void ScatteredColumnEdgesProduceDeviationAndAFinding()
    {
        // Same-width members whose pivots differ by 30px: right edges at 50 and 80, so the mean
        // absolute deviation is 15 and the 30px spread clears the 8px alignment tolerance.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var canvas = Snapshot([Obj(a, 0, 0), Obj(b, 30, 60)]);

        var report = CanvasLayoutAudit.MeasureDetailed(canvas);

        Assert.Equal(15.0, report.ColumnMeanAbsoluteXDeviation);
        var finding = Assert.Single(report.Findings, item => item.Kind == "columnMisalignment");
        Assert.Equal(30.0, finding.Measure);
        Assert.Equal(new HashSet<Guid> { a, b }, finding.ObjectIds.ToHashSet());
    }

    [Fact]
    public void ReportsUngroupedComponentsWithTheirIds()
    {
        var grouped = Guid.NewGuid();
        var loose = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(grouped, 0, 0), Obj(loose, 300, 0)],
            groups: [new GroupState(groupId, "stage", new[] { grouped }, 0)]);

        var report = CanvasLayoutAudit.MeasureDetailed(canvas);

        Assert.Equal(1, report.UngroupedCount);
        var finding = Assert.Single(report.Findings, item => item.Kind == "ungroupedComponents");
        Assert.Equal(new[] { loose }, finding.ObjectIds);
    }

    [Fact]
    public void EmptyCanvasMeasuresAllZeroes()
    {
        var report = CanvasLayoutAudit.MeasureDetailed(Snapshot(Array.Empty<CanvasObjectState>()));

        Assert.Equal(0, report.ComponentCount);
        Assert.Equal(0, report.WireCount);
        Assert.Equal(0, report.BackwardWireCount);
        Assert.Equal(0, report.WireCrossingCount);
        Assert.Equal(0, report.OverlappingPairCount);
        Assert.Equal(0, report.LongWireCount);
        Assert.Equal(0, report.ColumnCount);
        Assert.Equal(0, report.UngroupedCount);
        Assert.Empty(report.Findings);
        Assert.False(report.Truncated);
    }
}
