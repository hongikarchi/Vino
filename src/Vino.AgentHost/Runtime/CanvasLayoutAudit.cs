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
    private static IReadOnlyList<IReadOnlyList<CanvasObjectState>> ClusterColumns(
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
}
