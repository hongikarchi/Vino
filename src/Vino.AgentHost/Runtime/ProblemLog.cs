using System.Text.Json;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Hosting;
using Vino.Contracts;
using Vino.Core;

namespace Vino.AgentHost.Runtime;

/// <summary>
/// Append-only JSONL trail (problem-log.jsonl in the project data root) of job state transitions
/// and detected conflicts, with the structured fields — conflict kind, resource address — that
/// live-jobs.db flattens into prose. This is the harvestable record for the usage-data pipeline:
/// what blocked or failed, why, and how the run continued. Writes are best-effort and never throw
/// into the broker path.
/// </summary>
public sealed class ProblemLog
{
    private readonly string _path;
    private readonly ILogger<ProblemLog> _logger;
    private readonly object _gate = new();
    private bool _writeFailed;

    public ProblemLog(AgentHostOptions options, ILogger<ProblemLog> logger)
    {
        _path = Path.Combine(options.ResolveDataDirectory(), "problem-log.jsonl");
        _logger = logger;
    }

    public void RecordJobState(
        Guid jobId,
        Guid sessionId,
        string summary,
        JobState state,
        string? message,
        IReadOnlyList<ChangeConflict>? conflicts = null)
    {
        Append(new
        {
            at = DateTimeOffset.UtcNow,
            kind = "job-state",
            jobId,
            sessionId,
            summary,
            state = state.ToString(),
            message,
            conflicts = conflicts?.Select(conflict => new
            {
                kind = conflict.Kind.ToString(),
                resource = FormatResource(conflict.Resource),
                message = conflict.Message
            }).ToArray()
        });
    }

    /// <summary>
    /// An approval grant the SERVER issued without showing a card (fullAuto permission mode or a
    /// standing session consent). One record per issue: full-auto changes who clicks, never what
    /// is recorded.
    /// </summary>
    public void RecordAutoApproval(
        Guid sessionId,
        string origin,
        string mode,
        Guid? jobId,
        int targetCount,
        IReadOnlyList<string>? operations)
    {
        Append(new
        {
            at = DateTimeOffset.UtcNow,
            kind = "auto-approval",
            sessionId,
            origin,
            mode,
            jobId,
            targetCount,
            operations
        });
    }

    /// <summary>
    /// One fresh-eyes visual review verdict (the full-auto post-commit pass). pass=null means the
    /// judge answered but no verdict could be parsed; scores ride along only when the judge gave
    /// them. Every review is recorded, pass or fail — the record is how the pass earns its keep.
    /// </summary>
    public void RecordVisualReview(
        Guid sessionId,
        bool? pass,
        int issuesCount,
        int? taskFit = null,
        int? geometry = null,
        int? craft = null)
    {
        Append(new
        {
            at = DateTimeOffset.UtcNow,
            kind = "visual-review",
            sessionId,
            pass,
            issuesCount,
            scores = taskFit is null && geometry is null && craft is null
                ? null
                : new { taskFit, geometry, craft }
        });
    }

    public void RecordQueuedConflict(Guid jobId, Guid sessionId, Guid otherJobId, ChangeConflict conflict)
    {
        Append(new
        {
            at = DateTimeOffset.UtcNow,
            kind = "queued-conflict",
            jobId,
            sessionId,
            otherJobId,
            conflictKind = conflict.Kind.ToString(),
            resource = FormatResource(conflict.Resource),
            message = conflict.Message
        });
    }

    /// <summary>
    /// A self-attributable stale fingerprint the server auto-rebased to live (the resource was
    /// unchanged since THIS session last wrote it). Logged so we can measure how often the model
    /// submits a stale base for its own resources and keep improving the guidance.
    /// </summary>
    /// <summary>
    /// One gptino:auto expectation the server FILLED instead of declining (read expectations,
    /// stateless wires, execute-only ChangeSets, recovered writes). Logged so the widened fill
    /// policy stays auditable — if a fill ever precedes an overwrite incident, this record names
    /// the resource, the fingerprint used, and the rule that allowed it.
    /// </summary>
    public void RecordAutoFill(
        Guid jobId,
        Guid sessionId,
        ResourceAddress resource,
        string fingerprint,
        string reason)
    {
        Append(new
        {
            at = DateTimeOffset.UtcNow,
            kind = "auto-fill",
            jobId,
            sessionId,
            resource = FormatResource(resource),
            fingerprint,
            reason
        });
    }

    public void RecordSelfStaleRebase(
        Guid jobId,
        Guid sessionId,
        ResourceAddress resource,
        string staleFingerprint,
        string liveFingerprint)
    {
        Append(new
        {
            at = DateTimeOffset.UtcNow,
            kind = "self-stale-rebase",
            jobId,
            sessionId,
            resource = FormatResource(resource),
            staleFingerprint,
            liveFingerprint
        });
    }

    /// <summary>
    /// One acceptance-predicate evaluation outcome, logged so we can later mine which predicates the
    /// model actually declares and whether they catch real problems — the data foundation for tuning
    /// the predicate library over time (kept opt-in and generous meanwhile).
    /// </summary>
    public void RecordPredicateOutcome(
        Guid jobId,
        Guid sessionId,
        string predicateName,
        string predicateKind,
        ResourceAddress? resource,
        string? expectedValue,
        bool passed)
    {
        Append(new
        {
            at = DateTimeOffset.UtcNow,
            kind = "predicate-outcome",
            jobId,
            sessionId,
            predicateName,
            predicateKind,
            resource = FormatResource(resource),
            expectedValue,
            passed
        });
    }

    internal static string? FormatResource(ResourceAddress? resource) =>
        resource is null ? null : $"{resource.Kind}:{resource.Id}:{resource.Field}";

    // Stamped on every record: logs collected from many users are unattributable without the
    // build that wrote them. InformationalVersion carries the package version (e.g. 0.1.0-alpha.7).
    private static readonly string HostVersion =
        typeof(ProblemLog).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            is [System.Reflection.AssemblyInformationalVersionAttribute info, ..]
                ? info.InformationalVersion
                : typeof(ProblemLog).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// Full exception detail for a Failed/RecoveryRequired job. The job-state record carries only
    /// exception.Message; without the concrete type and stack a log shared by a user cannot
    /// localize the fault to a line of code.
    /// </summary>
    public void RecordJobException(Guid jobId, Guid sessionId, JobState state, Exception exception)
    {
        Append(new
        {
            at = DateTimeOffset.UtcNow,
            kind = "job-exception",
            jobId,
            sessionId,
            state = state.ToString(),
            exceptionType = exception.GetType().FullName,
            detail = exception.ToString()
        });
    }

    private void Append(object record)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(record, JsonDefaults.Options)?.AsObject()
                ?? throw new InvalidOperationException("A problem-log record must serialize to a JSON object.");
            node["v"] = HostVersion;
            node["protocol"] = Vino.BridgeContract.BridgeProtocol.Version;
            var line = node.ToJsonString(JsonDefaults.Options);
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            _writeFailed = false;
        }
        catch (Exception exception)
        {
            if (!_writeFailed)
            {
                _writeFailed = true;
                _logger.LogWarning(exception, "Problem log append failed; further failures are suppressed until a write succeeds.");
            }
        }
    }
}
