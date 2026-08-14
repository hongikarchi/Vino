using System.Diagnostics;
using System.Text.Json;
using Vino.AgentHost.Api;
using Vino.AgentHost.Data;
using Vino.BridgeContract;
using Vino.CanvasSceneAdapter;
using Vino.Contracts;
using Vino.Core;
using Vino.ScriptAdapter;
using Microsoft.Extensions.Logging;

namespace Vino.AgentHost.Runtime;

/// <summary>
/// W3 consolidation lifecycle (docs/heavy-script-plan-2026-08-13.md): the <c>consolidate_stages</c>
/// tool's backend. Merge = plan (validate group + cap math + build the merged source via
/// <see cref="CSharpStageMerger"/>) then a sequence of ordinary server-authored ChangeSets through
/// <c>SubmitChangeAsync</c> — create scaffold, schema, wires, source+execute — followed by a
/// field-wise output-equivalence check of the merged component against the old chain's sink, and
/// only on a match the consumer rewire + old-stage deletion (own destructive-intent ChangeSet).
/// Split is the mechanical inverse driven by the merged source's meta header. Every phase failure
/// stops the lifecycle and reports honestly; the old chain is never touched before equivalence.
/// </summary>
public sealed partial class LiveDocumentBackend
{
    private const int ConsolidationCapMilliseconds = 2_000; // D3, user-confirmed 2026-08-13
    private const long ConsolidationJobWaitMilliseconds = 25_000;

    /// <summary>One phase operation with the writeSet expectations that describe it — kept together
    /// so a phase's ChangeSet is assembled from explicit data, never from shared mutable state.</summary>
    private sealed record PhaseOp(TypedOperation Operation, IReadOnlyList<ResourceExpectation> Expectations);

    public async Task<object> ConsolidateStagesAsync(
        SessionRecord session,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var action = arguments.TryGetProperty("action", out var actionElement) &&
            actionElement.ValueKind == JsonValueKind.String
                ? actionElement.GetString()
                : "merge";
        var dryRun = arguments.TryGetProperty("dryRun", out var dryElement) &&
            dryElement.ValueKind == JsonValueKind.True;
        return action switch
        {
            "merge" => await ConsolidateMergeAsync(session, arguments, dryRun, cancellationToken)
                .ConfigureAwait(false),
            "split" => await ConsolidateSplitAsync(session, arguments, dryRun, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"consolidate_stages action must be 'merge' or 'split', not '{action}'."),
        };
    }

    // ----- merge ---------------------------------------------------------------------------------

    private sealed record StagePlan(
        Guid ComponentId,
        string BlockId,
        string NickName,
        PythonComponentState State,
        CanvasObjectState Canvas,
        long SolveMilliseconds,
        double PredictedMilliseconds);

    private sealed record ConsumerWire(
        Guid SinkOutputParameterId,
        string SinkOutputName,
        Guid ConsumerObjectId,
        Guid ConsumerParameterId);

    private sealed record MergePlan(
        TargetState TargetState,
        IReadOnlyList<StagePlan> Stages,
        StagePlan Sink,
        CSharpStageMerger.MergeOutcome Merged,
        double PredictedTotalMilliseconds,
        IReadOnlyList<ConsumerWire> Consumers,
        SnapshotEnvelope Snapshot);

    private async Task<object> ConsolidateMergeAsync(
        SessionRecord session,
        JsonElement arguments,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var stageIds = ReadStageComponentIds(arguments);
        MergePlan plan;
        try
        {
            plan = await PlanMergeAsync(session, stageIds, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException refusal)
        {
            return new { status = "refused", action = "merge", message = refusal.Message };
        }

        var planReport = new
        {
            stages = plan.Stages.Select(stage => new
            {
                componentId = stage.ComponentId,
                blockId = stage.BlockId,
                nickName = stage.NickName,
                lastSolveMs = stage.SolveMilliseconds,
                predictedMs = Math.Round(stage.PredictedMilliseconds, 1),
            }).ToArray(),
            predictedTotalMs = Math.Round(plan.PredictedTotalMilliseconds, 1),
            capMs = ConsolidationCapMilliseconds,
            mergedInputs = plan.Merged.Inputs.Select(socket => socket.Socket.Name).ToArray(),
            mergedOutputs = plan.Merged.Outputs.Select(socket => socket.Socket.Name).ToArray(),
            downstreamConsumers = plan.Consumers.Count,
        };
        if (dryRun)
        {
            return new
            {
                status = "plan",
                action = "merge",
                plan = planReport,
                mergedSource = plan.Merged.Source,
                message = "Dry run: no writes. Re-call with dryRun:false to consolidate.",
            };
        }

        var phases = new List<object>();
        var scaffoldId = Guid.NewGuid();
        var mergedId = Guid.NewGuid();
        var nickName = arguments.TryGetProperty("nickName", out var nickElement) &&
            nickElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(nickElement.GetString())
                ? nickElement.GetString()!
                : $"Merged {plan.Sink.NickName}";

        // CS1 — scaffold. The sink's pivot with a lateral offset: both chains stay visible until
        // the verified cleanup, and the post-turn tidy re-arranges afterwards anyway.
        var createOutcome = await SubmitPhaseAsync(
            session,
            "create scaffold",
            phases,
            [
                await BuildOperationAsync(
                    session.Id,
                    "consolidate-create",
                    OperationKind.CreateComponent,
                    AdapterOwner.Canvas,
                    "canvas.create",
                    new
                    {
                        operationId = "consolidate-create",
                        objectId = scaffoldId,
                        componentTypeId = CSharpScriptComponentTypeId,
                        pivot = new { x = plan.Sink.Canvas.Pivot.X, y = plan.Sink.Canvas.Pivot.Y + 120 },
                        nickName,
                        resultOutput = (string?)null,
                    },
                    writes: [Component(scaffoldId)],
                    expectations: [new ResourceExpectation(Component(scaffoldId), ResourceExpectation.AbsentFingerprint)],
                    cancellationToken).ConfigureAwait(false),
            ],
            intent: null,
            cancellationToken).ConfigureAwait(false);
        if (!createOutcome.Committed)
        {
            return MergeFailureReport("create scaffold did not commit", planReport, phases, createOutcome);
        }

        // CS2 — exact schema via replaceSchema (rides alone per contract). setSchema is append-only
        // and the fresh scaffold carries the factory default sockets, so an exact rebuild is the
        // only honest declaration (the F1-regate path). The replacement id BECOMES the merged
        // component. The source set here is a comment-only STUB, not the merged text: the factory
        // template reads sockets the rebuild removes (compile error at the next solve), the real
        // merged source must not solve before its inputs are wired, and script-mode PRE-DECLARES
        // socket variables (so a declaring stub would collide) — an empty body solves clean,
        // leaves the outputs default, and gives the component a committed value state.
        const string stubSource = "// Vino consolidation stub - the merged source lands after wiring.\n";
        var schemaOutcome = await SubmitPhaseAsync(
            session,
            "declare merged sockets",
            phases,
            [
                await BuildOperationAsync(
                    session.Id,
                    "consolidate-schema",
                    OperationKind.ReplaceComponentIo,
                    AdapterOwner.Script,
                    "python.replaceSchema",
                    new
                    {
                        operationId = "consolidate-schema",
                        componentId = scaffoldId,
                        newComponentId = mergedId,
                        inputs = plan.Merged.Inputs.Select(SchemaSocket).ToArray(),
                        outputs = plan.Merged.Outputs.Select(SchemaSocket).ToArray(),
                        source = stubSource,
                        resultOutput = (string?)null,
                    },
                    writes: [new ResourceAddress(ResourceKind.GrasshopperComponentIo, scaffoldId.ToString("D"))],
                    expectations:
                    [
                        new ResourceExpectation(
                            new ResourceAddress(ResourceKind.GrasshopperComponentIo, scaffoldId.ToString("D")),
                            ResourceExpectation.AutoFingerprint)
                    ],
                    cancellationToken).ConfigureAwait(false),
            ],
            intent: null,
            cancellationToken).ConfigureAwait(false);
        if (!schemaOutcome.Committed)
        {
            await TryDiscardMergedAsync(session, plan, scaffoldId, phases, cancellationToken).ConfigureAwait(false);
            return MergeFailureReport("socket declaration did not commit", planReport, phases, schemaOutcome);
        }

        // The merged component's live socket ids (Grasshopper owns them; assigned just now).
        var mergedState = await ReadScriptStateAsync(plan.TargetState, mergedId, cancellationToken)
            .ConfigureAwait(false);

        // CS3 — external input wires.
        var wireOperations = await BuildMergedInputWiresAsync(
            session, plan, mergedId, mergedState, cancellationToken).ConfigureAwait(false);
        if (wireOperations.Count > 0)
        {
            var wireOutcome = await SubmitPhaseAsync(
                session, "wire external inputs", phases, wireOperations, intent: null, cancellationToken)
                .ConfigureAwait(false);
            if (!wireOutcome.Committed)
            {
                await TryDiscardMergedAsync(session, plan, mergedId, phases, cancellationToken).ConfigureAwait(false);
                return MergeFailureReport("input wiring did not commit", planReport, phases, wireOutcome);
            }
        }

        // Seed the measurement table with the plan's measured sum so the W2 predicted-solve gate
        // judges this first execute by the stages' REAL calibration instead of refusing an
        // unmeasured solve. The execute's own Verify overwrites the seed with live measurements.
        await SeedMergedMeasurementAsync(plan, mergedId, cancellationToken).ConfigureAwait(false);

        // CS4 — source + execute (same component, contiguous python family: legal in one ChangeSet).
        var sourceOutcome = await SubmitPhaseAsync(
            session,
            "set merged source and execute",
            phases,
            [
                await BuildOperationAsync(
                    session.Id,
                    "consolidate-source",
                    OperationKind.UpdatePythonSource,
                    AdapterOwner.Script,
                    "python.setSource",
                    new
                    {
                        operationId = "consolidate-source",
                        componentId = mergedId,
                        expectedSourceSha256 = ResourceExpectation.AutoFingerprint,
                        source = plan.Merged.Source,
                        runtime = "csharp",
                        expireSolution = false,
                    },
                    writes: [new ResourceAddress(ResourceKind.GrasshopperComponentSource, mergedId.ToString("D"))],
                    expectations:
                    [
                        new ResourceExpectation(
                            new ResourceAddress(ResourceKind.GrasshopperComponentSource, mergedId.ToString("D")),
                            ResourceExpectation.AutoFingerprint)
                    ],
                    cancellationToken).ConfigureAwait(false),
                await BuildOperationAsync(
                    session.Id,
                    "consolidate-execute",
                    OperationKind.ExecutePython,
                    AdapterOwner.Script,
                    "python.execute",
                    new
                    {
                        operationId = "consolidate-execute",
                        componentId = mergedId,
                        expireUpstream = false,
                        recomputeDocument = false,
                    },
                    writes: [new ResourceAddress(ResourceKind.GrasshopperComponentValue, mergedId.ToString("D"))],
                    expectations:
                    [
                        new ResourceExpectation(
                            new ResourceAddress(ResourceKind.GrasshopperComponentValue, mergedId.ToString("D")),
                            ResourceExpectation.AutoFingerprint)
                    ],
                    cancellationToken).ConfigureAwait(false),
            ],
            intent: null,
            cancellationToken).ConfigureAwait(false);
        if (!sourceOutcome.Committed)
        {
            await TryDiscardMergedAsync(session, plan, mergedId, phases, cancellationToken).ConfigureAwait(false);
            return MergeFailureReport(
                "merged source/execute did not commit (the stage chain is untouched)",
                planReport, phases, sourceOutcome);
        }

        // Equivalence: merged vs old sink, live, field-wise, BEFORE any consumer moves.
        var pairs = plan.Merged.Outputs
            .Select(socket => (MergedName: socket.Socket.Name, ChainName: socket.OriginalName))
            .ToArray();
        var equivalence = await CompareOutputsAsync(
            plan.TargetState, mergedId, plan.Sink.ComponentId, pairs, cancellationToken).ConfigureAwait(false);
        if (!equivalence.Matched)
        {
            await TryDiscardMergedAsync(session, plan, mergedId, phases, cancellationToken).ConfigureAwait(false);
            return new
            {
                status = "verificationFailed",
                action = "merge",
                plan = planReport,
                phases,
                equivalence,
                message = "The merged component's outputs do not match the stage chain's sink - the " +
                    "merged component was discarded and the chain is untouched. See equivalence.diffs.",
            };
        }

        // CS5 — move the sink's downstream consumers onto the merged outputs.
        if (plan.Consumers.Count > 0)
        {
            var rewire = await BuildConsumerRewireAsync(
                session, plan, mergedId, mergedState, cancellationToken).ConfigureAwait(false);
            var rewireOutcome = await SubmitPhaseAsync(
                session, "rewire consumers", phases, rewire, intent: null, cancellationToken)
                .ConfigureAwait(false);
            if (!rewireOutcome.Committed)
            {
                return new
                {
                    status = "partial",
                    action = "merge",
                    mergedComponentId = mergedId,
                    plan = planReport,
                    phases,
                    equivalence,
                    message = "Merged component is live and VERIFIED, but the consumer rewire did not " +
                        "commit - both chains currently coexist. Rewire manually (or delete the merged " +
                        "component) and clean up the stages afterwards.",
                };
            }
        }

        // CS6 — delete the old stages, in their own destructive-intent ChangeSet. Seam wires all
        // end inside the delete set (orphan rule); external-input wires make a stage LIVE, so the
        // Layer-1 delete guard applies its normal 3-branch decision (own-session/approval/refuse).
        var deleteOutcome = await SubmitPhaseAsync(
            session,
            "delete old stages",
            phases,
            await BuildStageDeletesAsync(session, plan, cancellationToken).ConfigureAwait(false),
            intent: CleanupIntents.Destructive,
            cancellationToken).ConfigureAwait(false);

        return new
        {
            status = deleteOutcome.Committed ? "consolidated" : "consolidatedKeepingStages",
            action = "merge",
            mergedComponentId = mergedId,
            blockIds = plan.Stages.Select(stage => stage.BlockId).ToArray(),
            plan = planReport,
            phases,
            equivalence,
            message = deleteOutcome.Committed
                ? "Consolidated and verified: outputs equivalent, consumers rewired, old stages " +
                  "deleted. Edit blocks with replaceSourceBlock (python.replaceBlock); split back " +
                  "with consolidate_stages action:split."
                : "Merged component is live, verified, and carries the dataflow, but the old-stage " +
                  "deletion did not commit (see phases - a live-foreign stage needs approval). The " +
                  "stages are now redundant; delete them when possible.",
        };
    }

    private async Task<MergePlan> PlanMergeAsync(
        SessionRecord session,
        IReadOnlyList<Guid> stageIds,
        CancellationToken cancellationToken)
    {
        var targetState = ResolveSessionTargetState(session);
        await HydrateComponentMeasurementsAsync(targetState.DocKey, cancellationToken).ConfigureAwait(false);
        SnapshotEnvelope snapshot;
        using (await _documentGate.EnterReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshot = await CaptureSnapshotAsync(targetState, force: true, cancellationToken)
                .ConfigureAwait(false);
        }
        var stageSet = stageIds.ToHashSet();
        var canvasById = snapshot.Canvas.Objects.ToDictionary(item => item.ObjectId);
        var states = new Dictionary<Guid, PythonComponentState>();
        foreach (var stageId in stageIds)
        {
            if (!canvasById.TryGetValue(stageId, out var canvas))
            {
                throw new InvalidOperationException(
                    $"Stage component {stageId:D} does not exist on the canvas.");
            }
            if (canvas.ComponentTypeId != CSharpScriptComponentTypeId)
            {
                throw new InvalidOperationException(
                    $"Stage '{canvas.Name}' ({stageId:D}) is not a C# script component - v1 " +
                    "consolidation merges C# stages only.");
            }
            states[stageId] = await ReadScriptStateAsync(targetState, stageId, cancellationToken)
                .ConfigureAwait(false);
        }

        // Seam edges (in-set producer -> consumer) + per-input source resolution.
        var edges = new List<(Guid From, Guid To)>();
        foreach (var stageId in stageIds)
        {
            var canvas = canvasById[stageId];
            foreach (var input in states[stageId].Inputs)
            {
                var canvasInput = canvas.Inputs.FirstOrDefault(item => item.ParameterId == input.ParameterId);
                var sources = canvasInput?.CurrentSources ?? Array.Empty<CanvasParameterEndpoint>();
                if (sources.Count > 1)
                {
                    throw new InvalidOperationException(
                        $"Stage '{canvas.Name}' input '{input.Name}' merges {sources.Count} wires - " +
                        "multi-source inputs are not consolidatable; combine them upstream first.");
                }
                if (sources.Count == 0 && !input.Optional)
                {
                    throw new InvalidOperationException(
                        $"Stage '{canvas.Name}' input '{input.Name}' is unwired and not optional - " +
                        "its value may live in parameter-local persistent data, which a merge cannot " +
                        "carry. Wire it or declare it optional, then consolidate.");
                }
                if (sources.Count == 1 && stageSet.Contains(sources[0].OwnerObjectId))
                {
                    edges.Add((sources[0].OwnerObjectId, stageId));
                }
            }
        }

        var ordered = TopologicalOrder(stageIds, edges);
        RequireWeaklyConnected(stageIds, edges, canvasById);

        // Sink = the single stage whose outputs feed no in-set consumer; intermediate outputs must
        // not leak outside the group (the multi-consumer seam rule).
        var consumersInSet = stageIds.ToDictionary(id => id, _ => 0);
        foreach (var (from, _) in edges)
        {
            consumersInSet[from]++;
        }
        var sinks = stageIds.Where(id => consumersInSet[id] == 0).ToArray();
        if (sinks.Length != 1)
        {
            throw new InvalidOperationException(
                sinks.Length == 0
                    ? "The stage group has no sink (a wire cycle?) - it must form a chain."
                    : "The stage group has " + sinks.Length + " sinks (" +
                      string.Join(", ", sinks.Select(id => canvasById[id].Name)) +
                      ") - v1 consolidation merges a chain ending in exactly ONE component.");
        }
        var sinkId = sinks[0];
        var consumers = new List<ConsumerWire>();
        foreach (var stageId in stageIds)
        {
            var stageOutputs = states[stageId].Outputs.ToDictionary(item => item.ParameterId, item => item.Name);
            foreach (var other in snapshot.Canvas.Objects)
            {
                if (stageSet.Contains(other.ObjectId))
                {
                    continue;
                }
                foreach (var input in other.Inputs)
                {
                    foreach (var source in input.CurrentSources)
                    {
                        if (source.OwnerObjectId != stageId ||
                            !stageOutputs.TryGetValue(source.ParameterId, out var outputName))
                        {
                            continue;
                        }
                        if (stageId != sinkId)
                        {
                            throw new InvalidOperationException(
                                $"Stage '{canvasById[stageId].Name}' output '{outputName}' is consumed " +
                                $"outside the group (by '{other.Name}') - an intermediate output is a " +
                                "seam that must stay a socket. Leave that stage out of the group or " +
                                "include its consumer.");
                        }
                        consumers.Add(new ConsumerWire(
                            source.ParameterId, outputName, other.ObjectId, input.ParameterId));
                    }
                }
            }
        }

        // Cap math (D3): predicted per stage at the CURRENT wired volume, from W2's measurements.
        var lookup = BuildOutputCountLookup(targetState.DocKey);
        var plans = new List<StagePlan>();
        double predictedTotal = 0;
        for (var index = 0; index < ordered.Count; index++)
        {
            var stageId = ordered[index];
            var key = MeasurementKey(targetState.DocKey, stageId);
            if (!_componentMeasurements.TryGetValue(key, out var measurement) ||
                measurement.SolveMilliseconds is not { } solveMilliseconds)
            {
                throw new InvalidOperationException(
                    $"Stage '{canvasById[stageId].Name}' ({stageId:D}) has no measured solve - execute " +
                    "it (committed) first; the merge cap is computed from real measurements only.");
            }
            var estimate = EstimateComponentInputItems(snapshot.Canvas, stageId, lookup);
            ShouldBlockPredictedSolve(
                solveMilliseconds,
                measurement.InputItems,
                estimate.Total,
                estimate.KnownSources,
                int.MaxValue,
                out var predicted);
            predictedTotal += predicted;
            plans.Add(new StagePlan(
                stageId,
                FormattableString.Invariant($"s{index + 1}"),
                canvasById[stageId].Name,
                states[stageId],
                canvasById[stageId],
                solveMilliseconds,
                predicted));
        }
        if (predictedTotal > ConsolidationCapMilliseconds)
        {
            var breakdown = string.Join(" + ", plans.Select(plan =>
                FormattableString.Invariant($"{plan.NickName} {plan.PredictedMilliseconds:F0}ms")));
            throw new InvalidOperationException(
                FormattableString.Invariant($"The group's predicted merged solve is ~{predictedTotal:F0} ms ") +
                $"({breakdown}), over the {ConsolidationCapMilliseconds} ms consolidation cap - a merged " +
                "component must stay cheap to re-solve. Merge a smaller subset or reduce the wired volumes.");
        }

        // Build the merger's stage specs (sources are the model's own text: watchdog-stripped).
        var blockIdByComponent = plans.ToDictionary(plan => plan.ComponentId, plan => plan.BlockId);
        var specs = plans.Select(plan => new CSharpStageMerger.StageSpec(
            plan.ComponentId,
            plan.BlockId,
            plan.NickName,
            CSharpWatchdogInjector.Strip(plan.State.Source),
            plan.State.Inputs.Select(input =>
            {
                var canvasInput = plan.Canvas.Inputs.FirstOrDefault(item => item.ParameterId == input.ParameterId);
                var source = canvasInput?.CurrentSources is { Count: 1 } sources ? sources[0] : null;
                var seamOwner = source is not null && blockIdByComponent.ContainsKey(source.OwnerObjectId)
                    ? source.OwnerObjectId
                    : (Guid?)null;
                return new CSharpStageMerger.StageInputSpec(
                    ToMergerSocket(input),
                    seamOwner is { } owner ? blockIdByComponent[owner] : null,
                    seamOwner is { } ownerId
                        ? states[ownerId].Outputs.First(output => output.ParameterId == source!.ParameterId).Name
                        : null);
            }).ToArray(),
            plan.State.Outputs.Select(ToMergerSocket).ToArray())).ToArray();

        var merged = CSharpStageMerger.Merge(specs);
        return new MergePlan(
            targetState,
            plans,
            plans.First(plan => plan.ComponentId == sinkId),
            merged,
            predictedTotal,
            consumers,
            snapshot);
    }

    // ----- split ---------------------------------------------------------------------------------

    private async Task<object> ConsolidateSplitAsync(
        SessionRecord session,
        JsonElement arguments,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("componentId", out var componentElement) ||
            componentElement.ValueKind != JsonValueKind.String ||
            !componentElement.TryGetGuid(out var mergedId))
        {
            throw new InvalidOperationException("consolidate_stages action:split requires componentId.");
        }
        var targetState = ResolveSessionTargetState(session);
        var mergedState = await ReadScriptStateAsync(targetState, mergedId, cancellationToken)
            .ConfigureAwait(false);
        var stripped = CSharpWatchdogInjector.Strip(mergedState.Source);
        if (!CSharpStageMerger.TryParseLayout(stripped, out var layout, out var parseError))
        {
            return new
            {
                status = "refused",
                action = "split",
                message = $"Component {mergedId:D} is not a well-formed merged component: {parseError}",
            };
        }
        SnapshotEnvelope snapshot;
        using (await _documentGate.EnterReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshot = await CaptureSnapshotAsync(targetState, force: true, cancellationToken)
                .ConfigureAwait(false);
        }
        var mergedCanvas = snapshot.Canvas.Objects.FirstOrDefault(item => item.ObjectId == mergedId)
            ?? throw new InvalidOperationException($"Component {mergedId:D} is not on the canvas.");

        var meta = layout!.Meta;
        var stageIds = meta.Stages.ToDictionary(
            stage => stage.BlockId,
            _ => Guid.NewGuid(),
            StringComparer.Ordinal);
        if (dryRun)
        {
            return new
            {
                status = "plan",
                action = "split",
                blocks = meta.Stages.Select(stage => new
                {
                    blockId = stage.BlockId,
                    nickName = stage.NickName,
                    inputs = stage.Inputs.Select(socket => socket.Name).ToArray(),
                    outputs = stage.Outputs.Select(socket => socket.Name).ToArray(),
                }).ToArray(),
                message = "Dry run: no writes. Re-call with dryRun:false to split.",
            };
        }

        var phases = new List<object>();
        // CS1 — create all stage scaffolds in one canvas ChangeSet, laid out left-to-right below
        // the merged component.
        var createOps = new List<PhaseOp>();
        for (var index = 0; index < meta.Stages.Count; index++)
        {
            var stage = meta.Stages[index];
            var stageId = stageIds[stage.BlockId];
            var operationId = $"split-create-{stage.BlockId}";
            createOps.Add(await BuildOperationAsync(
                session.Id,
                operationId,
                OperationKind.CreateComponent,
                AdapterOwner.Canvas,
                "canvas.create",
                new
                {
                    operationId,
                    objectId = stageId,
                    componentTypeId = CSharpScriptComponentTypeId,
                    pivot = new { x = mergedCanvas.Pivot.X + index * 220, y = mergedCanvas.Pivot.Y + 150 },
                    nickName = stage.NickName,
                    resultOutput = (string?)null,
                },
                writes: [Component(stageId)],
                expectations: [new ResourceExpectation(Component(stageId), ResourceExpectation.AbsentFingerprint)],
                cancellationToken).ConfigureAwait(false));
        }
        var createOutcome = await SubmitPhaseAsync(
            session, "create stage scaffolds", phases, createOps, intent: null, cancellationToken)
            .ConfigureAwait(false);
        if (!createOutcome.Committed)
        {
            return SplitFailureReport("stage scaffolds did not commit", phases, createOutcome);
        }

        // One replaceSchema ChangeSet PER stage (rides alone; a fresh scaffold's factory sockets
        // make an exact rebuild the only honest declaration — the same F1-regate path merge uses).
        // The source set here is a null-assigning STUB: the real block code must not solve before
        // its inputs are wired, and the factory template would break compile after the rebuild.
        // The replacement id becomes the stage's final id.
        var finalIds = meta.Stages.ToDictionary(
            stage => stage.BlockId,
            _ => Guid.NewGuid(),
            StringComparer.Ordinal);
        foreach (var stage in meta.Stages)
        {
            var scaffold = stageIds[stage.BlockId];
            var stageId = finalIds[stage.BlockId];
            // Comment-only stub for the same reasons as the merge scaffold: socket variables are
            // framework-declared, so an empty body solves clean with default outputs.
            const string stubSource = "// Vino split stub - the stage source lands after wiring.\n";
            var outcome = await SubmitPhaseAsync(
                session,
                $"declare stage {stage.BlockId} sockets",
                phases,
                [
                    await BuildOperationAsync(
                        session.Id,
                        $"split-schema-{stage.BlockId}",
                        OperationKind.ReplaceComponentIo,
                        AdapterOwner.Script,
                        "python.replaceSchema",
                        new
                        {
                            operationId = $"split-schema-{stage.BlockId}",
                            componentId = scaffold,
                            newComponentId = stageId,
                            inputs = stage.Inputs.Select(SchemaSocketFromMeta).ToArray(),
                            outputs = stage.Outputs.Select(SchemaSocketFromMeta).ToArray(),
                            source = stubSource,
                            resultOutput = (string?)null,
                        },
                        writes: [new ResourceAddress(ResourceKind.GrasshopperComponentIo, scaffold.ToString("D"))],
                        expectations:
                        [
                            new ResourceExpectation(
                                new ResourceAddress(ResourceKind.GrasshopperComponentIo, scaffold.ToString("D")),
                                ResourceExpectation.AutoFingerprint)
                        ],
                        cancellationToken).ConfigureAwait(false),
                ],
                intent: null,
                cancellationToken).ConfigureAwait(false);
            if (!outcome.Committed)
            {
                return SplitFailureReport($"stage {stage.BlockId} socket declaration did not commit", phases, outcome);
            }
        }
        stageIds = finalIds;

        // Wires: seams from meta; externals from the merged component's live input wires; nothing
        // touches the merged component's own wiring yet.
        var stageStates = new Dictionary<string, PythonComponentState>(StringComparer.Ordinal);
        foreach (var stage in meta.Stages)
        {
            stageStates[stage.BlockId] = await ReadScriptStateAsync(
                targetState, stageIds[stage.BlockId], cancellationToken).ConfigureAwait(false);
        }
        var wireOps = new List<PhaseOp>();
        var wireIndex = 0;
        foreach (var stage in meta.Stages)
        {
            foreach (var input in stage.Inputs)
            {
                var targetParam = stageStates[stage.BlockId].Inputs
                    .First(item => string.Equals(item.Name, input.Name, StringComparison.Ordinal)).ParameterId;
                Guid sourceObject;
                Guid sourceParameter;
                if (string.Equals(input.From, "ext", StringComparison.Ordinal))
                {
                    var mergedParam = mergedState.Inputs.FirstOrDefault(
                        item => string.Equals(item.Name, input.Name, StringComparison.Ordinal))?.ParameterId;
                    var canvasInput = mergedParam is { } param
                        ? mergedCanvas.Inputs.FirstOrDefault(item => item.ParameterId == param)
                        : null;
                    if (canvasInput?.CurrentSources is not { Count: > 0 } sources)
                    {
                        continue; // an unwired optional external stays unwired
                    }
                    sourceObject = sources[0].OwnerObjectId;
                    sourceParameter = sources[0].ParameterId;
                }
                else if (input.From is { } from && from.Contains(':', StringComparison.Ordinal))
                {
                    var parts = from.Split(':', 2);
                    var producerState = stageStates[parts[0]];
                    sourceObject = stageIds[parts[0]];
                    sourceParameter = producerState.Outputs
                        .First(item => string.Equals(item.Name, parts[1], StringComparison.Ordinal)).ParameterId;
                }
                else
                {
                    continue;
                }
                wireOps.Add(await BuildWireOperationAsync(
                    session.Id,
                    $"split-wire-{wireIndex++}",
                    OperationKind.ConnectWire,
                    sourceObject,
                    sourceParameter,
                    stageIds[stage.BlockId],
                    targetParam,
                    cancellationToken).ConfigureAwait(false));
            }
        }
        if (wireOps.Count > 0)
        {
            var wireOutcome = await SubmitPhaseAsync(
                session, "wire stages", phases, wireOps, intent: null, cancellationToken).ConfigureAwait(false);
            if (!wireOutcome.Committed)
            {
                return SplitFailureReport("stage wiring did not commit", phases, wireOutcome);
            }
        }

        // Real block sources land only now, after the wires exist (one python ChangeSet per
        // stage); expireSolution false — the sink execute below re-solves the whole chain at once.
        foreach (var stage in meta.Stages)
        {
            var stageId = stageIds[stage.BlockId];
            var outcome = await SubmitPhaseAsync(
                session,
                $"set stage {stage.BlockId} source",
                phases,
                [
                    await BuildOperationAsync(
                        session.Id,
                        $"split-source-{stage.BlockId}",
                        OperationKind.UpdatePythonSource,
                        AdapterOwner.Script,
                        "python.setSource",
                        new
                        {
                            operationId = $"split-source-{stage.BlockId}",
                            componentId = stageId,
                            expectedSourceSha256 = ResourceExpectation.AutoFingerprint,
                            source = CSharpStageMerger.BuildStageSource(layout, stage.BlockId),
                            runtime = "csharp",
                            expireSolution = false,
                        },
                        writes: [new ResourceAddress(ResourceKind.GrasshopperComponentSource, stageId.ToString("D"))],
                        expectations:
                        [
                            new ResourceExpectation(
                                new ResourceAddress(ResourceKind.GrasshopperComponentSource, stageId.ToString("D")),
                                ResourceExpectation.AutoFingerprint)
                        ],
                        cancellationToken).ConfigureAwait(false),
                ],
                intent: null,
                cancellationToken).ConfigureAwait(false);
            if (!outcome.Committed)
            {
                return SplitFailureReport($"stage {stage.BlockId} source did not commit", phases, outcome);
            }
        }

        // Execute the recreated sink and verify against the merged component before touching it.
        // expireUpstream: the whole recreated chain still holds its stub solves — expire it so the
        // real sources compute before the equivalence read.
        var sinkMeta = meta.Stages[^1];
        var sinkId = stageIds[sinkMeta.BlockId];
        var executeOutcome = await SubmitPhaseAsync(
            session,
            "execute recreated sink",
            phases,
            [
                await BuildOperationAsync(
                    session.Id,
                    "split-execute",
                    OperationKind.ExecutePython,
                    AdapterOwner.Script,
                    "python.execute",
                    new
                    {
                        operationId = "split-execute",
                        componentId = sinkId,
                        expireUpstream = true,
                        recomputeDocument = false,
                    },
                    writes: [new ResourceAddress(ResourceKind.GrasshopperComponentValue, sinkId.ToString("D"))],
                    expectations:
                    [
                        new ResourceExpectation(
                            new ResourceAddress(ResourceKind.GrasshopperComponentValue, sinkId.ToString("D")),
                            ResourceExpectation.AutoFingerprint)
                    ],
                    cancellationToken).ConfigureAwait(false),
            ],
            intent: null,
            cancellationToken).ConfigureAwait(false);
        if (!executeOutcome.Committed)
        {
            return SplitFailureReport("sink execute did not commit", phases, executeOutcome);
        }
        var pairs = sinkMeta.Outputs.Select(output => (output.Name, output.Name)).ToArray();
        var equivalence = await CompareOutputsAsync(
            targetState, sinkId, mergedId, pairs, cancellationToken).ConfigureAwait(false);
        if (!equivalence.Matched)
        {
            return new
            {
                status = "verificationFailed",
                action = "split",
                phases,
                equivalence,
                stageComponentIds = stageIds.Values.ToArray(),
                message = "The recreated chain's sink does not match the merged component - the merged " +
                    "component is untouched; the recreated stages remain for inspection. See " +
                    "equivalence.diffs.",
            };
        }

        // Move the merged component's consumers onto the recreated sink, then delete it.
        var rewireOps = new List<PhaseOp>();
        var rewireIndex = 0;
        var sinkState = stageStates[sinkMeta.BlockId];
        foreach (var other in snapshot.Canvas.Objects)
        {
            if (other.ObjectId == mergedId || stageIds.ContainsValue(other.ObjectId))
            {
                continue;
            }
            foreach (var input in other.Inputs)
            {
                foreach (var source in input.CurrentSources)
                {
                    if (source.OwnerObjectId != mergedId)
                    {
                        continue;
                    }
                    var outputName = mergedState.Outputs.FirstOrDefault(
                        item => item.ParameterId == source.ParameterId)?.Name;
                    var sinkParam = outputName is null
                        ? null
                        : sinkState.Outputs.FirstOrDefault(
                            item => string.Equals(item.Name, outputName, StringComparison.Ordinal))?.ParameterId;
                    if (sinkParam is not { } replacement)
                    {
                        continue;
                    }
                    rewireOps.Add(await BuildWireOperationAsync(
                        session.Id, $"split-unwire-{rewireIndex}", OperationKind.DisconnectWire,
                        mergedId, source.ParameterId, other.ObjectId, input.ParameterId,
                        cancellationToken).ConfigureAwait(false));
                    rewireOps.Add(await BuildWireOperationAsync(
                        session.Id, $"split-rewire-{rewireIndex}", OperationKind.ConnectWire,
                        sinkId, replacement, other.ObjectId, input.ParameterId,
                        cancellationToken).ConfigureAwait(false));
                    rewireIndex++;
                }
            }
        }
        if (rewireOps.Count > 0)
        {
            var rewireOutcome = await SubmitPhaseAsync(
                session, "rewire merged consumers", phases, rewireOps, intent: null, cancellationToken)
                .ConfigureAwait(false);
            if (!rewireOutcome.Committed)
            {
                return SplitFailureReport(
                    "consumer rewire did not commit (recreated chain is live and verified; the merged " +
                    "component still carries the dataflow)", phases, rewireOutcome);
            }
        }
        var freshSnapshot = await CaptureFreshSnapshotAsync(targetState, cancellationToken).ConfigureAwait(false);
        var deleteOutcome = await SubmitPhaseAsync(
            session,
            "delete merged component",
            phases,
            [
                await BuildOperationAsync(
                    session.Id,
                    "split-delete",
                    OperationKind.DeleteComponent,
                    AdapterOwner.Canvas,
                    "canvas.delete",
                    new
                    {
                        operationId = "split-delete",
                        objectId = mergedId,
                        expectedFingerprint = StructureFingerprintOf(freshSnapshot, mergedId),
                    },
                    writes: [Component(mergedId)],
                    expectations:
                    [
                        new ResourceExpectation(Component(mergedId), StructureFingerprintOf(freshSnapshot, mergedId))
                    ],
                    cancellationToken).ConfigureAwait(false),
            ],
            intent: CleanupIntents.Destructive,
            cancellationToken).ConfigureAwait(false);

        return new
        {
            status = deleteOutcome.Committed ? "split" : "splitKeepingMerged",
            action = "split",
            stageComponentIds = meta.Stages.ToDictionary(stage => stage.BlockId, stage => stageIds[stage.BlockId]),
            phases,
            equivalence,
            message = deleteOutcome.Committed
                ? "Split and verified: the recreated chain matches the merged component, consumers were " +
                  "rewired, and the merged component was deleted."
                : "Recreated chain is live and verified, but the merged component's deletion did not " +
                  "commit (see phases). Delete it when possible.",
        };
    }

    // ----- shared helpers ------------------------------------------------------------------------

    private sealed record PhaseOutcome(bool Committed, string State, string? JobId, string? Message);

    private async Task<PhaseOutcome> SubmitPhaseAsync(
        SessionRecord session,
        string phase,
        List<object> phases,
        IReadOnlyList<PhaseOp> operations,
        string? intent,
        CancellationToken cancellationToken)
    {
        var targetState = ResolveSessionTargetState(session);
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            targetState.Target.ProjectId,
            session.Id,
            ResourceExpectation.AutoBaseRevision,
            null,
            Array.Empty<Guid>(),
            Array.Empty<ResourceExpectation>(),
            operations.SelectMany(item => item.Expectations).ToArray(),
            operations.Select(item => item.Operation).ToArray(),
            Array.Empty<VerificationPredicate>(),
            Array.Empty<RollbackBeforeImage>(),
            DateTimeOffset.UtcNow,
            Intent: intent);
        var submission = JsonSerializer.SerializeToElement(
            new
            {
                changeSet,
                expectedSnapshotId = ResourceExpectation.AutoFingerprint,
                idempotencyKey = FormattableString.Invariant($"consolidate-{Guid.NewGuid():N}"),
                summary = $"consolidate_stages: {phase}",
                wait = true,
            },
            BridgeProtocol.JsonOptions);
        JsonElement job;
        try
        {
            var outcome = await SubmitChangeAsync(session, submission, cancellationToken).ConfigureAwait(false);
            job = JsonSerializer.SerializeToElement(outcome);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            phases.Add(new { phase, state = "rejected", message = exception.Message });
            return new PhaseOutcome(false, "rejected", null, exception.Message);
        }
        var jobId = job.TryGetProperty("jobId", out var idElement) ? idElement.GetString() : null;
        var state = job.TryGetProperty("state", out var stateElement)
            ? stateElement.GetString() ?? "unknown"
            : "unknown";
        var watch = Stopwatch.StartNew();
        while (IsActiveJobStateName(state) &&
            watch.ElapsedMilliseconds < ConsolidationJobWaitMilliseconds &&
            jobId is not null)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            var read = await ReadJobAsync(
                JsonSerializer.SerializeToElement(new { jobId }),
                cancellationToken).ConfigureAwait(false);
            job = JsonSerializer.SerializeToElement(read);
            state = job.TryGetProperty("state", out stateElement)
                ? stateElement.GetString() ?? "unknown"
                : "unknown";
        }
        var message = job.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
        var diagnostics = job.TryGetProperty("diagnostics", out var diagElement) &&
            diagElement.ValueKind == JsonValueKind.Array
                ? diagElement.EnumerateArray()
                    .Select(item => item.TryGetProperty("message", out var text) ? text.GetString() : null)
                    .Where(text => !string.IsNullOrEmpty(text))
                    .Take(4)
                    .ToArray()
                : Array.Empty<string?>();
        phases.Add(new { phase, jobId, state, message, diagnostics });
        return new PhaseOutcome(
            string.Equals(state, "committed", StringComparison.OrdinalIgnoreCase),
            state,
            jobId,
            message);
    }

    private static bool IsActiveJobStateName(string state) =>
        state is "draft" or "queued" or "validating" or "executing" or "verifying";

    private async Task<PhaseOp> BuildOperationAsync(
        Guid sessionId,
        string operationId,
        OperationKind kind,
        AdapterOwner owner,
        string bridgeOperation,
        object arguments,
        IReadOnlyList<ResourceAddress> writes,
        IReadOnlyList<ResourceExpectation> expectations,
        CancellationToken cancellationToken)
    {
        var artifactName = FormattableString.Invariant($"consolidate-{operationId}-{Guid.NewGuid():N}.json");
        await WriteSessionArtifactAsync(
            sessionId,
            artifactName,
            new { bridgeOperation, arguments },
            cancellationToken).ConfigureAwait(false);
        return new PhaseOp(
            new TypedOperation(
                operationId,
                kind,
                owner,
                Array.Empty<ResourceAddress>(),
                writes,
                Reversible: kind != OperationKind.DeleteComponent,
                artifactName),
            expectations);
    }

    private async Task<PhaseOp> BuildWireOperationAsync(
        Guid sessionId,
        string operationId,
        OperationKind kind,
        Guid sourceObject,
        Guid sourceParameter,
        Guid targetObject,
        Guid targetParameter,
        CancellationToken cancellationToken)
    {
        var wireId = FormattableString.Invariant(
            $"{sourceObject:N}/{sourceParameter:N}>{targetObject:N}/{targetParameter:N}");
        var address = new ResourceAddress(ResourceKind.GrasshopperWire, wireId);
        return await BuildOperationAsync(
            sessionId,
            operationId,
            kind,
            AdapterOwner.Canvas,
            "canvas.setWire",
            new
            {
                operationId,
                wire = new
                {
                    sourceObjectId = sourceObject,
                    sourceParameterId = sourceParameter,
                    targetObjectId = targetObject,
                    targetParameterId = targetParameter,
                },
                action = kind == OperationKind.ConnectWire ? "connect" : "disconnect",
                rejectCycles = true,
            },
            writes: [address],
            expectations:
            [
                new ResourceExpectation(
                    address,
                    kind == OperationKind.ConnectWire
                        ? ResourceExpectation.AbsentFingerprint
                        : Sha256(wireId))
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<PhaseOp>> BuildMergedInputWiresAsync(
        SessionRecord session,
        MergePlan plan,
        Guid mergedId,
        PythonComponentState mergedState,
        CancellationToken cancellationToken)
    {
        var operations = new List<PhaseOp>();
        var index = 0;
        foreach (var socket in plan.Merged.Inputs)
        {
            var stage = plan.Stages.First(item =>
                string.Equals(item.BlockId, socket.StageBlockId, StringComparison.Ordinal));
            var stageInput = stage.State.Inputs.First(item =>
                string.Equals(item.Name, socket.OriginalName, StringComparison.Ordinal));
            var canvasInput = stage.Canvas.Inputs.FirstOrDefault(
                item => item.ParameterId == stageInput.ParameterId);
            if (canvasInput?.CurrentSources is not { Count: > 0 } sources)
            {
                continue; // unwired optional external
            }
            var mergedParam = mergedState.Inputs.First(item =>
                string.Equals(item.Name, socket.Socket.Name, StringComparison.Ordinal)).ParameterId;
            operations.Add(await BuildWireOperationAsync(
                session.Id,
                $"consolidate-wire-{index++}",
                OperationKind.ConnectWire,
                sources[0].OwnerObjectId,
                sources[0].ParameterId,
                mergedId,
                mergedParam,
                cancellationToken).ConfigureAwait(false));
        }
        return operations;
    }

    private async Task<List<PhaseOp>> BuildConsumerRewireAsync(
        SessionRecord session,
        MergePlan plan,
        Guid mergedId,
        PythonComponentState mergedState,
        CancellationToken cancellationToken)
    {
        var operations = new List<PhaseOp>();
        var index = 0;
        foreach (var consumer in plan.Consumers)
        {
            var mergedSocket = plan.Merged.Outputs.First(socket =>
                string.Equals(socket.OriginalName, consumer.SinkOutputName, StringComparison.Ordinal));
            var mergedParam = mergedState.Outputs.First(item =>
                string.Equals(item.Name, mergedSocket.Socket.Name, StringComparison.Ordinal)).ParameterId;
            operations.Add(await BuildWireOperationAsync(
                session.Id, $"consolidate-unwire-{index}", OperationKind.DisconnectWire,
                plan.Sink.ComponentId, consumer.SinkOutputParameterId,
                consumer.ConsumerObjectId, consumer.ConsumerParameterId,
                cancellationToken).ConfigureAwait(false));
            operations.Add(await BuildWireOperationAsync(
                session.Id, $"consolidate-rewire-{index}", OperationKind.ConnectWire,
                mergedId, mergedParam,
                consumer.ConsumerObjectId, consumer.ConsumerParameterId,
                cancellationToken).ConfigureAwait(false));
            index++;
        }
        return operations;
    }

    private async Task<List<PhaseOp>> BuildStageDeletesAsync(
        SessionRecord session,
        MergePlan plan,
        CancellationToken cancellationToken)
    {
        var fresh = await CaptureFreshSnapshotAsync(plan.TargetState, cancellationToken).ConfigureAwait(false);
        var operations = new List<PhaseOp>();
        foreach (var stage in plan.Stages)
        {
            var operationId = $"consolidate-delete-{stage.BlockId}";
            var fingerprint = StructureFingerprintOf(fresh, stage.ComponentId);
            operations.Add(await BuildOperationAsync(
                session.Id,
                operationId,
                OperationKind.DeleteComponent,
                AdapterOwner.Canvas,
                "canvas.delete",
                new { operationId, objectId = stage.ComponentId, expectedFingerprint = fingerprint },
                writes: [Component(stage.ComponentId)],
                expectations: [new ResourceExpectation(Component(stage.ComponentId), fingerprint)],
                cancellationToken).ConfigureAwait(false));
        }
        return operations;
    }

    private async Task TryDiscardMergedAsync(
        SessionRecord session,
        MergePlan plan,
        Guid mergedId,
        List<object> phases,
        CancellationToken cancellationToken)
    {
        try
        {
            var fresh = await CaptureFreshSnapshotAsync(plan.TargetState, cancellationToken).ConfigureAwait(false);
            if (fresh.Canvas.Objects.All(item => item.ObjectId != mergedId))
            {
                return; // the failed phase already rolled the create back
            }
            await SubmitPhaseAsync(
                session,
                "discard merged component",
                phases,
                [
                    await BuildOperationAsync(
                        session.Id,
                        "consolidate-discard",
                        OperationKind.DeleteComponent,
                        AdapterOwner.Canvas,
                        "canvas.delete",
                        new
                        {
                            operationId = "consolidate-discard",
                            objectId = mergedId,
                            expectedFingerprint = StructureFingerprintOf(fresh, mergedId),
                        },
                        writes: [Component(mergedId)],
                        expectations:
                        [
                            new ResourceExpectation(Component(mergedId), StructureFingerprintOf(fresh, mergedId))
                        ],
                        cancellationToken).ConfigureAwait(false),
                ],
                intent: CleanupIntents.Destructive,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not discard merged component {MergedId}.", mergedId);
            phases.Add(new
            {
                phase = "discard merged component",
                state = "failed",
                message = exception.Message,
            });
        }
    }

    private async Task SeedMergedMeasurementAsync(
        MergePlan plan,
        Guid mergedId,
        CancellationToken cancellationToken)
    {
        try
        {
            var fresh = await CaptureFreshSnapshotAsync(plan.TargetState, cancellationToken).ConfigureAwait(false);
            await RefreshUpstreamOutputCountsAsync(plan.TargetState, fresh, mergedId, cancellationToken)
                .ConfigureAwait(false);
            var estimate = EstimateComponentInputItems(
                fresh.Canvas, mergedId, BuildOutputCountLookup(plan.TargetState.DocKey));
            var record = new ComponentMeasurementRecord(
                mergedId,
                (long)Math.Ceiling(plan.PredictedTotalMilliseconds),
                estimate.KnownSources > 0 ? estimate.Total : null,
                new Dictionary<Guid, long>(),
                fresh.State.Revision,
                DateTimeOffset.UtcNow);
            _componentMeasurements[MeasurementKey(plan.TargetState.DocKey, mergedId)] = record;
            await _componentMeasurementStore.UpsertAsync(
                plan.TargetState.DocKey, [record], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Advisory: without the seed the W2 gate simply has no measurement and does not block.
            _logger.LogDebug(exception, "Could not seed measurement for merged component {MergedId}.", mergedId);
        }
    }

    private async Task<SnapshotEnvelope> CaptureFreshSnapshotAsync(
        TargetState targetState,
        CancellationToken cancellationToken)
    {
        using (await _documentGate.EnterReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return await CaptureSnapshotAsync(targetState, force: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PythonComponentState> ReadScriptStateAsync(
        TargetState targetState,
        Guid componentId,
        CancellationToken cancellationToken)
    {
        var element = await ReadScriptComponentJsonAsync(targetState, componentId, revision: 0, cancellationToken)
            .ConfigureAwait(false);
        return element.Deserialize<PythonComponentState>(BridgeProtocol.JsonOptions)
            ?? throw new InvalidOperationException($"Component {componentId:D} state is unreadable.");
    }

    // ----- equivalence ---------------------------------------------------------------------------

    private async Task<EquivalenceReport> CompareOutputsAsync(
        TargetState targetState,
        Guid candidateId,
        Guid referenceId,
        IReadOnlyList<(string CandidateName, string ReferenceName)> pairs,
        CancellationToken cancellationToken)
    {
        var candidate = await InspectOutputsDirectAsync(targetState, candidateId, cancellationToken)
            .ConfigureAwait(false);
        var reference = await InspectOutputsDirectAsync(targetState, referenceId, cancellationToken)
            .ConfigureAwait(false);
        var diffs = new List<string>();
        var outputs = new List<object>();
        foreach (var (candidateName, referenceName) in pairs)
        {
            var left = candidate.FirstOrDefault(item =>
                string.Equals(item.Name, candidateName, StringComparison.Ordinal));
            var right = reference.FirstOrDefault(item =>
                string.Equals(item.Name, referenceName, StringComparison.Ordinal));
            if (left is null || right is null)
            {
                diffs.Add($"output '{candidateName}' missing from {(left is null ? "candidate" : "reference")}");
                continue;
            }
            CompareOutputPair(candidateName, left, right, diffs);
            outputs.Add(new
            {
                name = candidateName,
                dataCount = left.DataCount,
                branchCount = left.BranchCount,
                typeNames = left.TypeNames,
                referenceDataCount = right.DataCount,
            });
        }
        return new EquivalenceReport(diffs.Count == 0, outputs, diffs);
    }

    internal sealed record EquivalenceReport(
        bool Matched,
        IReadOnlyList<object> Outputs,
        IReadOnlyList<string> Diffs);

    internal static void CompareOutputPair(
        string name,
        CanvasOutputParameterInspection candidate,
        CanvasOutputParameterInspection reference,
        List<string> diffs)
    {
        if (candidate.DataCount != reference.DataCount)
        {
            diffs.Add($"'{name}' dataCount {candidate.DataCount} != {reference.DataCount}");
        }
        if (candidate.BranchCount != reference.BranchCount)
        {
            diffs.Add($"'{name}' branchCount {candidate.BranchCount} != {reference.BranchCount}");
        }
        if (!candidate.TypeNames.OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(reference.TypeNames.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            diffs.Add($"'{name}' types [{string.Join(",", candidate.TypeNames)}] != " +
                $"[{string.Join(",", reference.TypeNames)}]");
        }
        if ((candidate.GeometryBounds is null) != (reference.GeometryBounds is null))
        {
            diffs.Add($"'{name}' geometry bounds present on only one side");
        }
        else if (candidate.GeometryBounds is { } leftBounds && reference.GeometryBounds is { } rightBounds &&
            (!NearlyEqual(leftBounds.Minimum, rightBounds.Minimum) ||
             !NearlyEqual(leftBounds.Maximum, rightBounds.Maximum)))
        {
            diffs.Add($"'{name}' geometry bounds differ beyond tolerance");
        }
        if (candidate.Closed != reference.Closed)
        {
            diffs.Add($"'{name}' closed {candidate.Closed?.ToString() ?? "null"} != " +
                $"{reference.Closed?.ToString() ?? "null"}");
        }
        var samples = Math.Min(candidate.SampleValues.Count, reference.SampleValues.Count);
        for (var index = 0; index < samples; index++)
        {
            if (!SampleValuesEquivalent(candidate.SampleValues[index], reference.SampleValues[index]))
            {
                diffs.Add($"'{name}' sample[{index}] '{Shorten(candidate.SampleValues[index])}' != " +
                    $"'{Shorten(reference.SampleValues[index])}'");
                break;
            }
        }
    }

    private static bool NearlyEqual(CanvasPoint3d left, CanvasPoint3d right) =>
        NearlyEqual(left.X, right.X) && NearlyEqual(left.Y, right.Y) && NearlyEqual(left.Z, right.Z);

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 1e-6 + 1e-9 * Math.Max(Math.Abs(left), Math.Abs(right));

    internal static bool SampleValuesEquivalent(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }
        return double.TryParse(left, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var leftValue) &&
            double.TryParse(right, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var rightValue) &&
            NearlyEqual(leftValue, rightValue);
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..37] + "...";

    private async Task<IReadOnlyList<CanvasOutputParameterInspection>> InspectOutputsDirectAsync(
        TargetState targetState,
        Guid objectId,
        CancellationToken cancellationToken)
    {
        var request = new BridgeOperationRequest(
            $"read-{Guid.NewGuid():N}",
            BridgeAdapterOwner.Canvas,
            "canvas.inspectOutputs",
            BridgeOperationAccess.Read,
            BaseSnapshotRevision: 0,
            ExpectedFingerprint: null,
            WriterLeaseToken: null,
            JsonSerializer.SerializeToElement(
                new { objectId, includeMassProperties = false },
                BridgeProtocol.JsonOptions));
        var response = await SendOperationAsync(targetState.Target, request, cancellationToken)
            .ConfigureAwait(false);
        var inspection = response.Result.Deserialize<CanvasOutputInspection>(BridgeProtocol.JsonOptions)
            ?? throw new InvalidOperationException($"Output inspection of {objectId:D} is unreadable.");
        return inspection.Outputs;
    }

    // ----- small shared bits ---------------------------------------------------------------------

    private static ResourceAddress Component(Guid id) =>
        new(ResourceKind.GrasshopperComponent, id.ToString("D"));

    private static string StructureFingerprintOf(SnapshotEnvelope snapshot, Guid objectId)
    {
        var component = snapshot.Canvas.Objects.FirstOrDefault(item => item.ObjectId == objectId)
            ?? throw new InvalidOperationException($"Component {objectId:D} is not on the canvas.");
        return string.IsNullOrEmpty(component.StructureFingerprint)
            ? component.Fingerprint
            : component.StructureFingerprint;
    }

    private static object SchemaSocket(CSharpStageMerger.MergedSocket socket) => new
    {
        name = socket.Socket.Name,
        typeHint = socket.Socket.TypeHint,
        access = socket.Socket.Access,
        optional = socket.Socket.Optional,
    };

    private static object SchemaSocketFromMeta(CSharpStageMerger.MetaSocket socket) => new
    {
        name = socket.Name,
        typeHint = socket.TypeHint,
        access = socket.Access,
        optional = socket.Optional,
    };

    private static CSharpStageMerger.StageSocketSpec ToMergerSocket(PythonParameter parameter) =>
        new(
            parameter.Name,
            parameter.TypeHint,
            parameter.Access.ToString().ToLowerInvariant(),
            parameter.Optional);

    private static IReadOnlyList<Guid> ReadStageComponentIds(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("stageComponentIds", out var idsElement) ||
            idsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "consolidate_stages action:merge requires stageComponentIds (the staged script " +
                "components to merge, at least two).");
        }
        var ids = new List<Guid>();
        foreach (var element in idsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || !element.TryGetGuid(out var id))
            {
                throw new InvalidOperationException("stageComponentIds must be UUID strings.");
            }
            ids.Add(id);
        }
        if (ids.Count < 2 || ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidOperationException(
                "stageComponentIds needs at least two DISTINCT component ids.");
        }
        return ids;
    }

    private static IReadOnlyList<Guid> TopologicalOrder(
        IReadOnlyList<Guid> nodes,
        IReadOnlyList<(Guid From, Guid To)> edges)
    {
        var incoming = nodes.ToDictionary(node => node, _ => 0);
        foreach (var (_, to) in edges)
        {
            incoming[to]++;
        }
        var queue = new Queue<Guid>(nodes.Where(node => incoming[node] == 0).OrderBy(node => node));
        var ordered = new List<Guid>();
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            ordered.Add(node);
            foreach (var (from, to) in edges)
            {
                if (from != node)
                {
                    continue;
                }
                if (--incoming[to] == 0)
                {
                    queue.Enqueue(to);
                }
            }
        }
        if (ordered.Count != nodes.Count)
        {
            throw new InvalidOperationException("The stage group contains a wire cycle.");
        }
        return ordered;
    }

    private static void RequireWeaklyConnected(
        IReadOnlyList<Guid> nodes,
        IReadOnlyList<(Guid From, Guid To)> edges,
        IReadOnlyDictionary<Guid, CanvasObjectState> canvasById)
    {
        if (nodes.Count == 0)
        {
            return;
        }
        var visited = new HashSet<Guid> { nodes[0] };
        var frontier = new Queue<Guid>(visited);
        while (frontier.Count > 0)
        {
            var node = frontier.Dequeue();
            foreach (var (from, to) in edges)
            {
                var next = from == node ? to : to == node ? from : Guid.Empty;
                if (next != Guid.Empty && visited.Add(next))
                {
                    frontier.Enqueue(next);
                }
            }
        }
        var disconnected = nodes.Where(node => !visited.Contains(node)).ToArray();
        if (disconnected.Length > 0)
        {
            throw new InvalidOperationException(
                "The stage group is not connected by wires: " +
                string.Join(", ", disconnected.Select(id => canvasById[id].Name)) +
                " share no seam with the rest. Consolidate connected chains only.");
        }
    }

    private static object MergeFailureReport(
        string reason,
        object plan,
        List<object> phases,
        PhaseOutcome outcome) => new
    {
        status = "failed",
        action = "merge",
        plan,
        phases,
        message = $"Consolidation stopped: {reason}. {outcome.Message}".Trim(),
    };

    private static object SplitFailureReport(string reason, List<object> phases, PhaseOutcome outcome) => new
    {
        status = "failed",
        action = "split",
        phases,
        message = $"Split stopped: {reason}. {outcome.Message}".Trim(),
    };
}
