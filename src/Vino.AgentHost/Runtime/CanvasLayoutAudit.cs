using System.Security.Cryptography;
using System.Text;
using Vino.CanvasSceneAdapter;

namespace Vino.AgentHost.Runtime;

/// <summary>
/// Deterministic, server-computed measurements of how a Grasshopper canvas is actually laid out.
///
/// <para>
/// This exists because the acceptance criteria for a tidy job were, in effect, "no runtime error":
/// an arrangement that stacked 46 of 109 components into one 3400px column committed as a success,
/// and nothing in the system could tell that apart from a good result. The model cannot fill the
/// gap either — it never sees pivots or bounds, so "left-to-right flow" was a rule it was asked to
/// follow blind.
/// </para>
/// <para>
/// Everything here is computed from the snapshot the panel and the broker already share (pivot,
/// bounds, wires, groups) — no new bridge operation, no screenshot, no model inference. Detection
/// is deterministic and server-owned; the model only ever triages what this reports.
/// </para>
/// </summary>
internal static class CanvasLayoutAudit
{
    /// <param name="BackwardWires">Wires whose source sits to the RIGHT of its target — the one
    /// rule ("linear left-to-right flow only") that is unambiguous and checkable.</param>
    /// <param name="LongWires">Wires spanning more than <see cref="LongWireThreshold"/> px, whose
    /// two ends cannot be seen on one screen.</param>
    /// <param name="WidestColumnShare">Fraction of the laid-out nodes sharing the single most
    /// crowded x position. High means sources were parked far from what they feed.</param>
    /// <param name="TallestColumnHeight">Vertical extent of that column, in px.</param>
    /// <param name="UngroupedCount">Components belonging to no group.</param>
    /// <param name="RightEdgeScatter">Worst spread of right edges within a column, in px. Zero
    /// when output sockets line up, which is what the wires leave from.</param>
    internal sealed record Report(
        int NodeCount,
        int BackwardWires,
        int LongWires,
        double WidestColumnShare,
        float TallestColumnHeight,
        int UngroupedCount,
        float RightEdgeScatter)
    {
        /// <summary>Human-readable violations, most load-bearing first. Empty means it looks tidy.</summary>
        internal IReadOnlyList<string> Findings()
        {
            var findings = new List<string>();
            if (BackwardWires > 0)
            {
                findings.Add($"{BackwardWires} wire(s) run right-to-left (a source sits to the right of its target).");
            }
            // A share is only meaningful once there are enough nodes to spread out: in a 3-node
            // cluster "33% share a column" is simply what a column is.
            if (NodeCount >= CrowdedColumnMinimumNodes && WidestColumnShare >= CrowdedColumnShare)
            {
                findings.Add(
                    $"{WidestColumnShare:P0} of components share one column, {TallestColumnHeight:F0}px tall — " +
                    "inputs are stacked far from what they feed instead of sitting beside it.");
            }
            if (LongWires > 0)
            {
                findings.Add($"{LongWires} wire(s) span more than {LongWireThreshold:F0}px, so their two ends are never on screen together.");
            }
            if (RightEdgeScatter > EdgeScatterTolerance)
            {
                findings.Add($"Output edges in a column are misaligned by up to {RightEdgeScatter:F0}px.");
            }
            if (UngroupedCount > 0)
            {
                findings.Add($"{UngroupedCount} component(s) belong to no group.");
            }
            return findings;
        }
    }

    /// <summary>Beyond this, a wire's two ends cannot be read on one screen at 100% zoom.</summary>
    internal const float LongWireThreshold = 1500f;

    /// <summary>A column holding this share of the document is a pile, not a stage.</summary>
    internal const double CrowdedColumnShare = 0.30;

    /// <summary>Below this many nodes the crowding ratio carries no signal.</summary>
    internal const int CrowdedColumnMinimumNodes = 8;

    /// <summary>Edges within this distance read as aligned.</summary>
    internal const float EdgeScatterTolerance = 8f;

    /// <summary>
    /// Measures <paramref name="canvas"/>, restricted to <paramref name="scope"/> when given
    /// (the components a tidy actually touched). Groups are excluded: they are containers, and
    /// their union rectangle would dominate every column statistic.
    /// </summary>
    internal static Report Measure(CanvasSnapshot canvas, IReadOnlyCollection<Guid>? scope = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var groupIds = canvas.Groups.Select(group => group.GroupId).ToHashSet();
        var inScope = scope is { Count: > 0 } ? scope.ToHashSet() : null;
        var nodes = canvas.Objects
            .Where(item => !groupIds.Contains(item.ObjectId))
            .Where(item => inScope is null || inScope.Contains(item.ObjectId))
            .GroupBy(item => item.ObjectId)
            .ToDictionary(group => group.Key, group => group.First());
        if (nodes.Count == 0)
        {
            return new Report(0, 0, 0, 0, 0, 0, 0);
        }

        var backward = 0;
        var longWires = 0;
        foreach (var wire in canvas.Wires)
        {
            if (!nodes.TryGetValue(wire.SourceObjectId, out var source) ||
                !nodes.TryGetValue(wire.TargetObjectId, out var target))
            {
                continue;
            }
            // Right edge of the source vs left edge of the target: the sockets, not the centres.
            var sourceRight = source.Pivot.X + source.Bounds.Width / 2f;
            var targetLeft = target.Pivot.X - target.Bounds.Width / 2f;
            if (sourceRight > targetLeft)
            {
                backward++;
            }
            if (Math.Abs(target.Pivot.X - source.Pivot.X) > LongWireThreshold)
            {
                longWires++;
            }
        }

        var columns = ClusterColumns(nodes.Values);
        var widest = columns.OrderByDescending(column => column.Count).First();
        var widestShare = (double)widest.Count / nodes.Count;
        var tallest = 0f;
        foreach (var column in columns)
        {
            var top = column.Min(item => item.Pivot.Y - item.Bounds.Height / 2f);
            var bottom = column.Max(item => item.Pivot.Y + item.Bounds.Height / 2f);
            tallest = Math.Max(tallest, bottom - top);
        }

        var scatter = 0f;
        foreach (var column in columns)
        {
            var edges = column.Select(item => item.Pivot.X + item.Bounds.Width / 2f).ToArray();
            if (edges.Length > 1)
            {
                scatter = Math.Max(scatter, edges.Max() - edges.Min());
            }
        }

        var grouped = canvas.Groups.SelectMany(group => group.ObjectIds).ToHashSet();
        var ungrouped = nodes.Keys.Count(id => !grouped.Contains(id));

        return new Report(
            nodes.Count,
            backward,
            longWires,
            widestShare,
            tallest,
            ungrouped,
            scatter);
    }

    /// <summary>A horizontal gap wider than this starts a new column.</summary>
    private const float ColumnGap = 120f;

    /// <summary>
    /// Groups nodes into visual columns by SINGLE-LINKAGE on x — a new column begins wherever the
    /// sorted horizontal gap exceeds <see cref="ColumnGap"/>.
    ///
    /// <para>
    /// Quantizing x instead (round(x / 24)) looks equivalent and is not: the members of one column
    /// do not share a pivot. They share a right EDGE — that is where the output sockets are — so
    /// their pivots differ by half the difference in width, measured at up to 140px on the real
    /// definition. Quantizing shattered the one 34-node column of a real arrangement into seven
    /// and reported 1.5% crowding where the truth was 52.3%, i.e. the predicate could never fire
    /// on the very case it exists to catch.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<CanvasObjectState>> ClusterColumns(
        IEnumerable<CanvasObjectState> nodes)
    {
        var ordered = nodes.OrderBy(item => item.Pivot.X).ToArray();
        var columns = new List<IReadOnlyList<CanvasObjectState>>();
        var current = new List<CanvasObjectState>();
        float? previous = null;
        foreach (var node in ordered)
        {
            if (previous is { } last && node.Pivot.X - last > ColumnGap)
            {
                columns.Add(current);
                current = new List<CanvasObjectState>();
            }
            current.Add(node);
            previous = node.Pivot.X;
        }
        if (current.Count > 0)
        {
            columns.Add(current);
        }
        return columns;
    }

    // ── detailed audit (GET /dev/canvas-layout-audit) ────────────────────────────────────────
    // The coarse Report above grades a tidy job pass/fail; this one enumerates every defect WITH
    // its addresses, so a later repair can act on a finding instead of re-deriving it. Same data
    // (the shared canvas snapshot), same size source as the tidy layout, no bridge operation.

    /// <summary>
    /// One addressable layout defect. <paramref name="Wires"/> carries full endpoint addressing
    /// (object + parameter ids) when the defect is a wire; empty otherwise.
    /// </summary>
    internal sealed record LayoutAuditFinding(
        string FindingId,
        string Kind,
        IReadOnlyList<Guid> ObjectIds,
        double? Measure,
        string Message,
        IReadOnlyList<WireState> Wires);

    /// <param name="MedianWireLength">Median straight-line wire length, px; the longWire
    /// threshold is <see cref="LongWireMedianFactor"/> times this.</param>
    /// <param name="ColumnMeanAbsoluteXDeviation">Mean absolute deviation of RIGHT edges from
    /// their column's mean, px, over multi-member columns. Right edges, not pivots: a column's
    /// members share the edge the output sockets sit on, not a pivot (see ClusterColumns).</param>
    /// <param name="Truncated">The findings list hit a per-kind cap; the counts stay exact.</param>
    internal sealed record DetailedReport(
        int ComponentCount,
        int WireCount,
        int BackwardWireCount,
        int WireCrossingCount,
        int OverlappingPairCount,
        int LongWireCount,
        double MedianWireLength,
        int ColumnCount,
        double ColumnMeanAbsoluteXDeviation,
        int UngroupedCount,
        IReadOnlyList<LayoutAuditFinding> Findings,
        bool Truncated);

    /// <summary>Wires right-to-left beyond this are backward; smaller overhangs are jitter.</summary>
    internal const float BackwardWireTolerance = 5f;

    /// <summary>A wire longer than this multiple of the median wire length is an outlier.</summary>
    internal const double LongWireMedianFactor = 3.0;

    /// <summary>Per-kind findings cap: counts stay exact, the sample stays readable.</summary>
    internal const int MaxFindingsPerKind = 50;

    internal static DetailedReport MeasureDetailed(CanvasSnapshot canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var groupIds = canvas.Groups.Select(group => group.GroupId).ToHashSet();
        var nodes = canvas.Objects
            .Where(item => !groupIds.Contains(item.ObjectId))
            .GroupBy(item => item.ObjectId)
            .ToDictionary(group => group.Key, group => group.First());

        // Rectangles from the SAME size source the tidy layout places with (CanvasLayout.TopLeft):
        // BoundsOrigin when the adapter reported it, pivot-centred bounds otherwise.
        var rects = nodes.Values.ToDictionary(
            item => item.ObjectId,
            item =>
            {
                var topLeft = CanvasLayout.TopLeft(item);
                return (Left: topLeft.X,
                    Top: topLeft.Y,
                    Right: topLeft.X + item.Bounds.Width,
                    Bottom: topLeft.Y + item.Bounds.Height);
            });

        var findings = new List<LayoutAuditFinding>();
        var truncated = false;
        void Emit(ref int emitted, LayoutAuditFinding finding)
        {
            if (emitted++ < MaxFindingsPerKind)
            {
                findings.Add(finding);
            }
            else
            {
                truncated = true;
            }
        }

        // The snapshot carries no socket positions, so a wire is approximated as a straight line
        // from the source's RIGHT edge to the target's LEFT edge, each at its rectangle's vertical
        // centre — the edges the real sockets sit on.
        var segments = new List<(WireState Wire, float FromX, float FromY, float ToX, float ToY)>();
        foreach (var wire in canvas.Wires)
        {
            if (!rects.TryGetValue(wire.SourceObjectId, out var source) ||
                !rects.TryGetValue(wire.TargetObjectId, out var target))
            {
                continue;
            }
            segments.Add((wire,
                source.Right, (source.Top + source.Bottom) / 2f,
                target.Left, (target.Top + target.Bottom) / 2f));
        }

        var backwardCount = 0;
        var backwardEmitted = 0;
        foreach (var segment in segments)
        {
            var overhang = segment.FromX - segment.ToX;
            if (overhang > BackwardWireTolerance)
            {
                backwardCount++;
                Emit(ref backwardEmitted, new LayoutAuditFinding(
                    Hash($"backwardWire|{segment.Wire.SourceParameterId:D}|{segment.Wire.TargetParameterId:D}")[..16],
                    "backwardWire",
                    new[] { segment.Wire.SourceObjectId, segment.Wire.TargetObjectId },
                    Math.Round(overhang, 1),
                    $"Wire runs right-to-left: the source output sits {overhang:F0}px right of its target input.",
                    new[] { segment.Wire }));
            }
        }

        // Proper (strict interior) crossings only: wires meeting at a shared endpoint touch,
        // they do not cross — and the edge proxy collapses a component's sockets to one point,
        // so fan-outs and fan-ins never count against themselves. O(n^2) by design.
        var crossingCount = 0;
        var crossingEmitted = 0;
        for (var first = 0; first < segments.Count; first++)
        {
            for (var second = first + 1; second < segments.Count; second++)
            {
                var a = segments[first];
                var b = segments[second];
                if (!SegmentsProperlyCross(
                        a.FromX, a.FromY, a.ToX, a.ToY,
                        b.FromX, b.FromY, b.ToX, b.ToY))
                {
                    continue;
                }
                crossingCount++;
                Emit(ref crossingEmitted, new LayoutAuditFinding(
                    Hash($"wireCrossing|{a.Wire.SourceParameterId:D}|{a.Wire.TargetParameterId:D}|" +
                        $"{b.Wire.SourceParameterId:D}|{b.Wire.TargetParameterId:D}")[..16],
                    "wireCrossing",
                    new[] { a.Wire.SourceObjectId, a.Wire.TargetObjectId, b.Wire.SourceObjectId, b.Wire.TargetObjectId },
                    null,
                    "Two wires cross when drawn as straight lines between their components.",
                    new[] { a.Wire, b.Wire }));
            }
        }

        var lengths = segments
            .Select(segment => Math.Sqrt(
                Math.Pow(segment.ToX - segment.FromX, 2) + Math.Pow(segment.ToY - segment.FromY, 2)))
            .OrderBy(length => length)
            .ToArray();
        var medianLength = lengths.Length == 0 ? 0.0 : lengths[lengths.Length / 2];
        var longCount = 0;
        var longEmitted = 0;
        if (medianLength > 0)
        {
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                var length = Math.Sqrt(
                    Math.Pow(segment.ToX - segment.FromX, 2) + Math.Pow(segment.ToY - segment.FromY, 2));
                if (length <= medianLength * LongWireMedianFactor)
                {
                    continue;
                }
                longCount++;
                Emit(ref longEmitted, new LayoutAuditFinding(
                    Hash($"longWire|{segment.Wire.SourceParameterId:D}|{segment.Wire.TargetParameterId:D}")[..16],
                    "longWire",
                    new[] { segment.Wire.SourceObjectId, segment.Wire.TargetObjectId },
                    Math.Round(length, 1),
                    $"Wire spans {length:F0}px — more than {LongWireMedianFactor:F0}x the median wire length ({medianLength:F0}px).",
                    new[] { segment.Wire }));
            }
        }

        var overlapCount = 0;
        var overlapEmitted = 0;
        var orderedIds = rects.Keys.OrderBy(id => id).ToArray();
        for (var first = 0; first < orderedIds.Length; first++)
        {
            for (var second = first + 1; second < orderedIds.Length; second++)
            {
                var a = rects[orderedIds[first]];
                var b = rects[orderedIds[second]];
                var width = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                var height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                if (width <= 0f || height <= 0f)
                {
                    continue;
                }
                overlapCount++;
                Emit(ref overlapEmitted, new LayoutAuditFinding(
                    Hash($"overlap|{orderedIds[first]:D}|{orderedIds[second]:D}")[..16],
                    "overlappingComponents",
                    new[] { orderedIds[first], orderedIds[second] },
                    Math.Round(width * height, 1),
                    $"Component bounds overlap by {width:F0}x{height:F0}px.",
                    Array.Empty<WireState>()));
            }
        }

        var columns = ClusterColumns(nodes.Values);
        var deviationTotal = 0.0;
        var deviationSamples = 0;
        var columnEmitted = 0;
        foreach (var column in columns)
        {
            if (column.Count < 2)
            {
                continue;
            }
            var edges = column.Select(item => rects[item.ObjectId].Right).ToArray();
            var mean = edges.Average();
            foreach (var edge in edges)
            {
                deviationTotal += Math.Abs(edge - mean);
                deviationSamples++;
            }
            var spread = edges.Max() - edges.Min();
            if (spread > EdgeScatterTolerance)
            {
                var members = column.Select(item => item.ObjectId).OrderBy(id => id).ToArray();
                Emit(ref columnEmitted, new LayoutAuditFinding(
                    Hash($"columnMisalignment|{string.Join(",", members.Select(id => id.ToString("D")))}")[..16],
                    "columnMisalignment",
                    members,
                    Math.Round(spread, 1),
                    $"Output edges within one column are misaligned by up to {spread:F0}px.",
                    Array.Empty<WireState>()));
            }
        }

        // Group membership IS visible in the snapshot (canvas.Groups carries ObjectIds), so this
        // metric is always supported. One aggregate finding: the repair (grouping) wants the id
        // list, not one entry per component.
        var grouped = canvas.Groups.SelectMany(group => group.ObjectIds).ToHashSet();
        var ungroupedIds = nodes.Keys.Where(id => !grouped.Contains(id)).OrderBy(id => id).ToArray();
        if (ungroupedIds.Length > 0)
        {
            const int MaxUngroupedIds = 100;
            if (ungroupedIds.Length > MaxUngroupedIds)
            {
                truncated = true;
            }
            findings.Add(new LayoutAuditFinding(
                Hash($"ungrouped|{canvas.GrasshopperDocumentId:N}")[..16],
                "ungroupedComponents",
                ungroupedIds.Take(MaxUngroupedIds).ToArray(),
                ungroupedIds.Length,
                $"{ungroupedIds.Length} component(s) belong to no group.",
                Array.Empty<WireState>()));
        }

        return new DetailedReport(
            nodes.Count,
            segments.Count,
            backwardCount,
            crossingCount,
            overlapCount,
            longCount,
            medianLength,
            columns.Count,
            deviationSamples == 0 ? 0.0 : deviationTotal / deviationSamples,
            ungroupedIds.Length,
            findings,
            truncated);
    }

    /// <summary>
    /// Strict-interior segment intersection: endpoints touching (t or u at 0/1) and parallel or
    /// collinear overlaps do NOT count, so wires that merely meet at a socket are never crossings.
    /// </summary>
    private static bool SegmentsProperlyCross(
        float aFromX, float aFromY, float aToX, float aToY,
        float bFromX, float bFromY, float bToX, float bToY)
    {
        var rX = aToX - aFromX;
        var rY = aToY - aFromY;
        var sX = bToX - bFromX;
        var sY = bToY - bFromY;
        var denominator = rX * sY - rY * sX;
        if (denominator == 0f)
        {
            return false;
        }
        var qpX = bFromX - aFromX;
        var qpY = bFromY - aFromY;
        var t = (qpX * sY - qpY * sX) / denominator;
        var u = (qpX * rY - qpY * rX) / denominator;
        return t > 0f && t < 1f && u > 0f && u < 1f;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
