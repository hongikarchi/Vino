using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vino.AgentHost.Api;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Data;
using Vino.AgentHost.Hosting;
using Vino.AgentHost.Security;
using Vino.BridgeContract;
using Vino.Contracts;
using Vino.CanvasSceneAdapter;
using Vino.Core;
using Vino.History;
using Vino.ScriptAdapter;

namespace Vino.AgentHost.Runtime;

// Post-write verification: acceptance-predicate evaluation, commit-quality reporting, and the recovery manifest.
public sealed partial class LiveDocumentBackend
{
    internal readonly record struct PredicateOutcome(
        string Name,
        PredicateKind Kind,
        ResourceAddress? Resource,
        string? ExpectedValue,
        bool Passed,
        // An output-observing predicate on a ChangeSet that cannot recompute is neither passed nor
        // failed — there is no solve for it to read. It is recorded, never counted as a problem.
        bool Unverifiable = false);

    /// <summary>
    /// Predicate kinds that read the POST-SOLVE output inspection. They can only be answered when the
    /// ChangeSet actually recomputed something.
    /// </summary>
    private static bool ObservesSolvedOutputs(PredicateKind kind) => kind is
        PredicateKind.OutputCountInRange or
        PredicateKind.AreaInRange or
        PredicateKind.DataTreeBranchCountInRange or
        PredicateKind.GeometryClosed or
        PredicateKind.VolumeInRange or
        PredicateKind.BoundingBoxInRange or
        PredicateKind.OutputEquals;

    /// <summary>
    /// False when NO operation in the set can drive a Grasshopper solution, so every output the
    /// verifier reads is whatever the last solve left behind.
    /// <para>
    /// The only such kind today is <see cref="OperationKind.UpdatePythonSource"/>: the adapter writes
    /// the source and calls <c>ExpireSolution(recompute: false)</c> under an explicit "Never recompute
    /// the document here" rule, so the component is marked dirty and nothing runs. Every other write
    /// path (execute, wire, value, schema) issues <c>NewSolution</c>.
    /// </para>
    /// Measured: 13 of the 103 OutputCountInRange failures in the 07-21..08-26 corpus were
    /// source-only ChangeSets — claims that could not have been true, failing a job whose writes had
    /// already landed.
    /// </summary>
    internal static bool CanChangeSetSolve(ChangeSet changeSet) =>
        changeSet.Operations.Count == 0 ||
        changeSet.Operations.Any(operation => operation.Kind != OperationKind.UpdatePythonSource);

    private static IReadOnlyList<string> Verify(
        ChangeSet changeSet,
        SnapshotEnvelope snapshot,
        IReadOnlyList<JobDiagnostic> diagnostics,
        IReadOnlyList<ResourceObservation> operationObservations,
        IReadOnlyList<JobComponentOutputs>? componentOutputs,
        ICollection<PredicateOutcome>? outcomes = null,
        // Caveats that must reach the commit message without failing the job.
        ICollection<string>? advisories = null)
    {
        var problems = diagnostics
            .Where(item => item.Severity == BridgeDiagnosticSeverity.Error)
            .Select(item => $"{item.OperationId}: {item.Code}: {item.Message}")
            .ToList();
        var canSolve = CanChangeSetSolve(changeSet);
        var unverified = new List<string>();
        foreach (var predicate in changeSet.AcceptancePredicates)
        {
            var observation = predicate.Resource is null
                ? null
                : operationObservations.LastOrDefault(item =>
                    ExactDomainOverlaps(item.Resource, predicate.Resource) ||
                    ConflictDetector.SharesPythonStateFingerprint(item.Resource, predicate.Resource));
            var resource = predicate.Resource is null || observation is not null
                ? null
                : snapshot.State.Resources.FirstOrDefault(item =>
                    ExactDomainOverlaps(item.Resource, predicate.Resource));
            var observedFingerprint = observation?.Fingerprint ?? resource?.Fingerprint;
            var exists = observation is not null
                ? observation.Fingerprint is not null
                : resource is not null;
            var passed = predicate.Kind switch
            {
                PredicateKind.FingerprintEquals => observedFingerprint is not null &&
                    string.Equals(observedFingerprint, predicate.ExpectedValue, StringComparison.Ordinal),
                PredicateKind.RuntimeErrorAbsent => diagnostics.All(item =>
                    item.Severity != BridgeDiagnosticSeverity.Error),
                PredicateKind.WireExists => exists,
                PredicateKind.WireAbsent => !exists,
                PredicateKind.ObjectExists => exists,
                PredicateKind.ObjectAbsent => !exists,
                PredicateKind.OutputCountInRange =>
                    predicate.Resource is not null &&
                    Guid.TryParse(predicate.Resource.Id, out var countComponentId) &&
                    TryParseOutputCountRange(predicate.ExpectedValue, out var countRange) &&
                    EvaluateOutputCountInRange(componentOutputs, countComponentId, countRange),
                PredicateKind.AreaInRange =>
                    predicate.Resource is not null &&
                    Guid.TryParse(predicate.Resource.Id, out var areaComponentId) &&
                    TryParseNumericOutputRange(predicate.ExpectedValue, out var areaName, out var areaMin, out var areaMax) &&
                    EvaluateNumericOutputInRange(componentOutputs, areaComponentId, areaName, "area", areaMin, areaMax),
                PredicateKind.DataTreeBranchCountInRange =>
                    predicate.Resource is not null &&
                    Guid.TryParse(predicate.Resource.Id, out var branchComponentId) &&
                    TryParseNumericOutputRange(predicate.ExpectedValue, out var branchName, out var branchMin, out var branchMax) &&
                    EvaluateNumericOutputInRange(componentOutputs, branchComponentId, branchName, "branchCount", branchMin, branchMax),
                PredicateKind.GeometryClosed =>
                    predicate.Resource is not null &&
                    Guid.TryParse(predicate.Resource.Id, out var closedComponentId) &&
                    !string.IsNullOrWhiteSpace(predicate.ExpectedValue) &&
                    EvaluateGeometryClosed(componentOutputs, closedComponentId, predicate.ExpectedValue!.Trim()),
                PredicateKind.VolumeInRange =>
                    predicate.Resource is not null &&
                    Guid.TryParse(predicate.Resource.Id, out var volumeComponentId) &&
                    TryParseNumericOutputRange(predicate.ExpectedValue, out var volumeName, out var volumeMin, out var volumeMax) &&
                    EvaluateNumericOutputInRange(componentOutputs, volumeComponentId, volumeName, "volume", volumeMin, volumeMax),
                PredicateKind.BoundingBoxInRange =>
                    predicate.Resource is not null &&
                    Guid.TryParse(predicate.Resource.Id, out var bboxComponentId) &&
                    TryParseBoundingBoxRange(predicate.ExpectedValue, out var bboxName, out var bboxAxis, out var bboxMin, out var bboxMax) &&
                    EvaluateBoundingBoxInRange(componentOutputs, bboxComponentId, bboxName, bboxAxis, bboxMin, bboxMax),
                _ => false
            };
            // A claim about solved outputs is unanswerable when nothing in this ChangeSet recomputed.
            // Failing it punished the model for a claim the set could never have satisfied and left
            // the writes applied under a "failed" job; passing it would be a lie. Record it as what
            // it is, and tell the model what to add.
            var unverifiable = !passed && ObservesSolvedOutputs(predicate.Kind) && !canSolve;
            outcomes?.Add(new PredicateOutcome(
                predicate.Name,
                predicate.Kind,
                predicate.Resource,
                predicate.ExpectedValue,
                passed,
                unverifiable));
            if (unverifiable)
            {
                unverified.Add(predicate.Name);
            }
            else if (!passed)
            {
                problems.Add(
                    $"Acceptance predicate '{predicate.Name}' ({predicate.Kind}) was not satisfied. " +
                    "The write landed; the claim did not hold. Re-read the component's outputs " +
                    "(inspect_outputs) to see what it actually produced, then either fix the change or " +
                    "restate the predicate to match what this change can guarantee.");
            }
        }
        if (unverified.Count > 0)
        {
            // Deliberately NOT a problem: problems fail the job. This is a caveat that rides the
            // commit message so the commit cannot read as if the claim had been checked.
            advisories?.Add(UnverifiablePredicateNote(unverified));
        }
        return problems;
    }

    /// <summary>
    /// The advisory a source-only ChangeSet gets instead of a false failure. Prefixed so callers can
    /// tell it from a real problem.
    /// </summary>
    internal const string UnverifiableNotePrefix = "gptino:unverified ";

    internal static string UnverifiablePredicateNote(IReadOnlyCollection<string> names) =>
        UnverifiableNotePrefix +
        $"predicate(s) {string.Join(", ", names.Select(name => $"'{name}'"))} were not evaluated: this " +
        "ChangeSet only writes source, which never recomputes the document, so there are no solved " +
        "outputs to read. Add an executePython operation (or submit one next) to make the claim checkable.";

    /// <summary>
    /// Informational commit-quality summary: runtime warning count plus the names of solved-empty
    /// outputs (from the post-solve output inspections), e.g. "1 runtime warning(s); output(s)
    /// 'Ceiling' empty." Null when there is nothing to report. Strictly informational — the caller
    /// appends it to the commit message and MUST NOT let it change the job state (an intentionally
    /// empty output is legal; this only makes the canvas-visible state survive in records).
    /// </summary>
    internal readonly record struct OutputCountRange(string OutputName, int Min, int Max);

    /// <summary>Parses an OutputCountInRange ExpectedValue of the form "outputName:min:max" (max may be "*").</summary>
    internal static bool TryParseOutputCountRange(string? expectedValue, out OutputCountRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(expectedValue))
        {
            return false;
        }
        var parts = expectedValue.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }
        var name = parts[0].Trim();
        if (name.Length == 0 ||
            !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var min) ||
            min < 0)
        {
            return false;
        }
        int max;
        if (string.Equals(parts[2].Trim(), "*", StringComparison.Ordinal))
        {
            max = int.MaxValue;
        }
        else if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out max))
        {
            return false;
        }
        if (max < min)
        {
            return false;
        }
        range = new OutputCountRange(name, min, max);
        return true;
    }

    /// <summary>
    /// True when the named output of the component solved to an item count within [Min,Max]. Reads
    /// the post-solve inspection Vino already collects (name + dataCount per output). Fails closed:
    /// a missing component, missing output, or absent inspection returns false so an unverifiable
    /// claim never passes.
    /// </summary>
    internal static bool EvaluateOutputCountInRange(
        IReadOnlyList<JobComponentOutputs>? componentOutputs,
        Guid componentId,
        OutputCountRange range)
    {
        var component = componentOutputs?.FirstOrDefault(item => item.ComponentId == componentId);
        if (component is null ||
            component.Inspection.ValueKind != JsonValueKind.Object ||
            !component.Inspection.TryGetProperty("outputs", out var inspected) ||
            inspected.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (var output in inspected.EnumerateArray())
        {
            if (output.ValueKind == JsonValueKind.Object &&
                output.TryGetProperty("name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String &&
                string.Equals(nameElement.GetString(), range.OutputName, StringComparison.Ordinal) &&
                output.TryGetProperty("dataCount", out var countElement) &&
                countElement.ValueKind == JsonValueKind.Number)
            {
                var count = countElement.GetInt32();
                return count >= range.Min && count <= range.Max;
            }
        }
        return false;
    }

    /// <summary>Parses "outputName:min:max" (max may be "*") into a name and a double range.</summary>
    internal static bool TryParseNumericOutputRange(string? expectedValue, out string outputName, out double min, out double max)
    {
        outputName = string.Empty;
        min = 0;
        max = 0;
        if (string.IsNullOrWhiteSpace(expectedValue))
        {
            return false;
        }
        var parts = expectedValue.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }
        outputName = parts[0].Trim();
        if (outputName.Length == 0 ||
            !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out min) ||
            min < 0)
        {
            return false;
        }
        max = string.Equals(parts[2].Trim(), "*", StringComparison.Ordinal)
            ? double.PositiveInfinity
            : double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMax)
                ? parsedMax
                : double.NaN;
        return !double.IsNaN(max) && max >= min;
    }

    /// <summary>
    /// True when the named output's numeric <paramref name="field"/> ("area", "branchCount", ...) is
    /// within [min,max]. Reads the post-solve inspection; fails closed on any missing data.
    /// </summary>
    internal static bool EvaluateNumericOutputInRange(
        IReadOnlyList<JobComponentOutputs>? componentOutputs,
        Guid componentId,
        string outputName,
        string field,
        double min,
        double max)
    {
        var output = FindInspectedOutput(componentOutputs, componentId, outputName);
        if (output is not { } value ||
            !value.TryGetProperty(field, out var fieldElement) ||
            fieldElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }
        var measured = fieldElement.GetDouble();
        return measured >= min && measured <= max;
    }

    /// <summary>True when every geometry in the named output is closed (inspection "closed" == true).</summary>
    internal static bool EvaluateGeometryClosed(
        IReadOnlyList<JobComponentOutputs>? componentOutputs,
        Guid componentId,
        string outputName)
    {
        var output = FindInspectedOutput(componentOutputs, componentId, outputName);
        return output is { } value &&
            value.TryGetProperty("closed", out var closedElement) &&
            closedElement.ValueKind == JsonValueKind.True;
    }

    /// <summary>Parses "outputName:axis:min:max" (axis = x|y|z|diagonal, max may be "*").</summary>
    internal static bool TryParseBoundingBoxRange(string? expectedValue, out string outputName, out string axis, out double min, out double max)
    {
        outputName = string.Empty;
        axis = string.Empty;
        min = 0;
        max = 0;
        if (string.IsNullOrWhiteSpace(expectedValue))
        {
            return false;
        }
        var parts = expectedValue.Split(':');
        if (parts.Length != 4)
        {
            return false;
        }
        outputName = parts[0].Trim();
        axis = parts[1].Trim().ToLowerInvariant();
        if (outputName.Length == 0 ||
            axis is not ("x" or "y" or "z" or "diagonal") ||
            !double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out min) ||
            min < 0)
        {
            return false;
        }
        max = string.Equals(parts[3].Trim(), "*", StringComparison.Ordinal)
            ? double.PositiveInfinity
            : double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMax)
                ? parsedMax
                : double.NaN;
        return !double.IsNaN(max) && max >= min;
    }

    /// <summary>True when the named output's bounding-box extent on the axis (or diagonal) is within [min,max].</summary>
    internal static bool EvaluateBoundingBoxInRange(
        IReadOnlyList<JobComponentOutputs>? componentOutputs,
        Guid componentId,
        string outputName,
        string axis,
        double min,
        double max)
    {
        if (FindInspectedOutput(componentOutputs, componentId, outputName) is not { } output ||
            !output.TryGetProperty("geometryBounds", out var boundsElement) ||
            boundsElement.ValueKind != JsonValueKind.Object ||
            !boundsElement.TryGetProperty("size", out var size) ||
            size.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        double extent;
        if (string.Equals(axis, "diagonal", StringComparison.Ordinal))
        {
            if (!size.TryGetProperty("x", out var sx) || sx.ValueKind != JsonValueKind.Number ||
                !size.TryGetProperty("y", out var sy) || sy.ValueKind != JsonValueKind.Number ||
                !size.TryGetProperty("z", out var sz) || sz.ValueKind != JsonValueKind.Number)
            {
                return false;
            }
            double x = sx.GetDouble(), y = sy.GetDouble(), z = sz.GetDouble();
            extent = Math.Sqrt((x * x) + (y * y) + (z * z));
        }
        else if (size.TryGetProperty(axis, out var axisElement) && axisElement.ValueKind == JsonValueKind.Number)
        {
            extent = axisElement.GetDouble();
        }
        else
        {
            return false;
        }
        return extent >= min && extent <= max;
    }

    private static JsonElement? FindInspectedOutput(
        IReadOnlyList<JobComponentOutputs>? componentOutputs,
        Guid componentId,
        string outputName)
    {
        var component = componentOutputs?.FirstOrDefault(item => item.ComponentId == componentId);
        if (component is null ||
            component.Inspection.ValueKind != JsonValueKind.Object ||
            !component.Inspection.TryGetProperty("outputs", out var inspected) ||
            inspected.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var output in inspected.EnumerateArray())
        {
            if (output.ValueKind == JsonValueKind.Object &&
                output.TryGetProperty("name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String &&
                string.Equals(nameElement.GetString(), outputName, StringComparison.Ordinal))
            {
                return output;
            }
        }
        return null;
    }

    internal static string? DescribeCommitQuality(
        IReadOnlyList<JobDiagnostic> diagnostics,
        IReadOnlyList<JobComponentOutputs>? outputs,
        // False when nothing in the ChangeSet could recompute. An output is then empty because no
        // solve ran, which is the expected state of a source write — not a defect to report.
        bool canSolve = true)
    {
        try
        {
            var warningCount = diagnostics.Count(item =>
                item.Severity == BridgeDiagnosticSeverity.Warning);
            var emptyOutputs = new List<string>();
            foreach (var component in outputs ?? Array.Empty<JobComponentOutputs>())
            {
                if (component.Inspection.ValueKind != JsonValueKind.Object ||
                    !component.Inspection.TryGetProperty("outputs", out var inspected) ||
                    inspected.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var output in inspected.EnumerateArray())
                {
                    if (output.ValueKind != JsonValueKind.Object ||
                        !output.TryGetProperty("name", out var nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String ||
                        !output.TryGetProperty("dataCount", out var countElement) ||
                        countElement.ValueKind != JsonValueKind.Number ||
                        countElement.GetInt32() != 0)
                    {
                        continue;
                    }
                    var name = nameElement.GetString();
                    // The script console output 'out' is routinely empty; reporting it would be
                    // noise, not signal.
                    if (string.IsNullOrWhiteSpace(name) ||
                        string.Equals(name, "out", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    emptyOutputs.Add(name);
                }
            }
            var parts = new List<string>();
            if (warningCount > 0)
            {
                parts.Add($"{warningCount} runtime warning(s)");
            }
            if (emptyOutputs.Count > 0 && canSolve)
            {
                var names = string.Join(
                    ", ",
                    emptyOutputs.Distinct(StringComparer.Ordinal).Select(name => $"'{name}'"));
                parts.Add($"output(s) {names} empty");
            }
            return parts.Count == 0 ? null : $"{string.Join("; ", parts)}.";
        }
        catch (Exception)
        {
            // Never-demote discipline: a quality-summary bug must never affect the commit.
            return null;
        }
    }

    /// <summary>
    /// RecoveryRequired manifest: which operations completed their bridge round trip, which one
    /// was in flight when the failure surfaced (outcome honestly unknown — never reported as
    /// failed), and which never dispatched. Returned as message text plus the same facts as
    /// Information diagnostics so the job projection and problem log both carry them.
    /// </summary>
    internal static (string Message, IReadOnlyList<JobDiagnostic> Diagnostics) BuildRecoveryManifest(
        IReadOnlyList<TypedOperation> operations,
        IReadOnlyList<string> completedOperationIds,
        string? inFlightOperationId,
        bool inFlightRefusedBeforeWrite = false,
        bool inFlightRolledBack = false)
    {
        var completed = new HashSet<string>(completedOperationIds, StringComparer.Ordinal);
        var notDispatched = operations
            .Select(operation => operation.OperationId)
            .Where(id => !completed.Contains(id) &&
                !string.Equals(id, inFlightOperationId, StringComparison.Ordinal))
            .ToArray();
        var applied = completedOperationIds.Count > 0
            ? string.Join(", ", completedOperationIds)
            : "none";
        // When the in-flight failure carried a refusal or verified-rollback code, the adapter
        // PROVED the operation left no net change — reporting it as "unknown" would send the
        // session on a needless document review. The two proofs stay labeled distinctly: a
        // refusal never touched the document, while a verified rollback DID write and then
        // restored the pre-write state.
        var rolledBack = inFlightOperationId is not null && inFlightRolledBack;
        var refused = inFlightOperationId is not null && inFlightRefusedBeforeWrite && !rolledBack;
        var unknown = inFlightOperationId is null
            ? "none"
            : rolledBack
                ? $"{inFlightOperationId} (write rolled back — no net change)"
                : refused
                    ? $"{inFlightOperationId} (refused before write — no change applied)"
                    : $"{inFlightOperationId} (in flight at failure)";
        var pending = notDispatched.Length > 0 ? string.Join(", ", notDispatched) : "none";
        var message = $"Applied: {applied}. Unknown outcome: {unknown}. Not dispatched: {pending}.";
        var manifestDiagnostics = new List<JobDiagnostic>
        {
            new(string.Empty, BridgeDiagnosticSeverity.Information, "recovery_applied", applied),
            new(
                inFlightOperationId ?? string.Empty,
                BridgeDiagnosticSeverity.Information,
                rolledBack
                    ? "recovery_rolled_back"
                    : refused
                        ? "recovery_refused_before_write"
                        : "recovery_unknown",
                unknown),
            new(string.Empty, BridgeDiagnosticSeverity.Information, "recovery_not_dispatched", pending),
        };
        return (message, manifestDiagnostics);
    }

}
