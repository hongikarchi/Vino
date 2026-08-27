using Vino.BridgeContract;

namespace Vino.CanvasSceneAdapter;

public sealed class RhinoSceneBridgeOperationHandler : IBridgeOperationHandler
{
    private readonly IRhinoSceneAdapter _adapter;

    public RhinoSceneBridgeOperationHandler(IRhinoSceneAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public BridgeAdapterOwner Owner => BridgeAdapterOwner.RhinoScene;

    public async Task<BridgeOperationResponse> HandleAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Owner != Owner)
        {
            throw new BridgeProtocolException("adapter_owner", "Rhino handler received another owner's request.");
        }

        request.Validate();
        return request.Operation switch
        {
            "rhino.list" => await ListAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.listStampedObjects" => await ListStampedObjectsAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.audit" => await AuditAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.structuralExtract" => await StructuralExtractAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.structuralLoadSample" => await StructuralLoadSampleAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.inspect" => await InspectAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.validateUpsert" => await ValidateUpsertAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.createPrimitive" => await MutationAsync<CreateRhinoPrimitiveRequest>(target, request, _adapter.CreatePrimitiveAsync, cancellationToken).ConfigureAwait(false),
            "rhino.upsert" => await MutationAsync<UpsertRhinoObjectRequest>(target, request, _adapter.UpsertObjectAsync, cancellationToken).ConfigureAwait(false),
            "rhino.delete" => await MutationAsync<DeleteRhinoObjectRequest>(target, request, _adapter.DeleteObjectAsync, cancellationToken).ConfigureAwait(false),
            "rhino.ensureLayer" => await MutationAsync<EnsureRhinoLayerRequest>(target, request, _adapter.EnsureLayerAsync, cancellationToken).ConfigureAwait(false),
            "rhino.transform" => await TransformAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.fixEndpointPair" => await MutationAsync<FixEndpointPairRequest>(target, request, _adapter.FixEndpointPairAsync, cancellationToken).ConfigureAwait(false),
            "rhino.listLayers" => await ListLayersAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.focusObjects" => await FocusObjectsAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.updateLayer" => await MutationAsync<UpdateRhinoLayerRequest>(target, request, _adapter.UpdateLayerAsync, cancellationToken).ConfigureAwait(false),
            "rhino.deleteLayer" => await MutationAsync<DeleteRhinoLayerRequest>(target, request, _adapter.DeleteLayerAsync, cancellationToken).ConfigureAwait(false),
            "rhino.layerState" => await LayerStateAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.purgeTableEntries" => await PurgeTableEntriesAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.moveObjectsToLayer" => await MoveObjectsToLayerAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.captureView" => await CaptureViewAsync(target, request, cancellationToken).ConfigureAwait(false),
            _ => throw new BridgeProtocolException(
                "unknown_rhino_scene_operation",
                $"Unknown Rhino scene operation '{request.Operation}'."),
        };
    }

    private async Task<BridgeOperationResponse> ListAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.ListObjectsAsync(
            target,
            request.DeserializeArguments<RhinoListObjectsRequest>(),
            cancellationToken).ConfigureAwait(false);
        var diagnostics = result.Truncated
            ? new[]
            {
                new BridgeDiagnostic(
                    BridgeDiagnosticSeverity.Warning,
                    "rhino_list_truncated",
                    $"Rhino list result was truncated at the requested limit {result.Limit}.")
            }
            : Array.Empty<BridgeDiagnostic>();
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result,
            afterFingerprint: result.Fingerprint,
            diagnostics: diagnostics);
    }

    private async Task<BridgeOperationResponse> CaptureViewAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.CaptureViewAsync(
            target,
            request.DeserializeArguments<RhinoViewCaptureRequest>(),
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result,
            afterFingerprint: result.Fingerprint);
    }

    private async Task<BridgeOperationResponse> ListStampedObjectsAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.ListStampedObjectsAsync(target, cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result,
            afterFingerprint: result.Fingerprint);
    }

    private async Task<BridgeOperationResponse> ListLayersAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.ListLayersAsync(target, cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result,
            afterFingerprint: result.Fingerprint);
    }

    private async Task<BridgeOperationResponse> FocusObjectsAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        var arguments = request.DeserializeArguments<FocusObjectsRequest>();
        var mode = (arguments.Mode ?? "select").Trim().ToLowerInvariant();
        // Access follows what the mode actually does. select is a pure look (hidden/locked targets
        // are reported, never force-shown) and runs as a concurrent read. isolate/lock/restore
        // change visibility attributes — and therefore object fingerprints — so they declare Write
        // and ride the document write gate like every other mutation. The op stays panel-only
        // (absent from the agent's tool schema) and never enters a ChangeSet.
        RequireAccess(
            request,
            mode == "select" ? BridgeOperationAccess.Read : BridgeOperationAccess.Write);
        var result = await _adapter.FocusObjectsAsync(target, arguments, cancellationToken)
            .ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: mode != "select" &&
                (result.HiddenCount > 0 || result.LockedCount > 0 || result.Restored),
            result,
            afterFingerprint: result.Fingerprint);
    }

    private async Task<BridgeOperationResponse> LayerStateAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        // Saving/restoring/deleting a named state changes no geometry, but it does mutate the
        // document's layer state table — a write, so it takes the writer lease like any mutation.
        RequireAccess(request, BridgeOperationAccess.Write);
        var result = await _adapter.LayerStateAsync(
            target,
            request.DeserializeArguments<RhinoLayerStateRequest>(),
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: true,
            result,
            afterFingerprint: result.Fingerprint);
    }

    private async Task<BridgeOperationResponse> PurgeTableEntriesAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var result = await _adapter.PurgeTableEntriesAsync(
            target,
            request.DeserializeArguments<PurgeTableEntriesRequest>(),
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: result.Purged.Count > 0,
            result,
            afterFingerprint: result.Fingerprint,
            diagnostics: result.Diagnostics);
    }

    private async Task<BridgeOperationResponse> MoveObjectsToLayerAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var result = await _adapter.MoveObjectsToLayerAsync(
            target,
            request.DeserializeArguments<MoveObjectsToLayerRequest>(),
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: result.Changed,
            result,
            afterFingerprint: result.Fingerprint,
            diagnostics: result.Diagnostics);
    }

    private async Task<BridgeOperationResponse> StructuralLoadSampleAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.SampleStructuralLoadsAsync(
            target,
            request.DeserializeArguments<StructuralLoadSampleRequest>(),
            cancellationToken).ConfigureAwait(false);
        var diagnostics = result.Sources.Any(source => source.Truncated)
            ? new[]
            {
                new BridgeDiagnostic(
                    BridgeDiagnosticSeverity.Warning,
                    "structural_loads_truncated",
                    "A load source hit its sample cap; its total under-counts the real load."),
            }
            : Array.Empty<BridgeDiagnostic>();
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result,
            afterFingerprint: result.Fingerprint,
            diagnostics: diagnostics);
    }

    private async Task<BridgeOperationResponse> StructuralExtractAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.ExtractStructuralAxesAsync(
            target,
            request.DeserializeArguments<StructuralExtractRequest>(),
            cancellationToken).ConfigureAwait(false);
        var diagnostics = result.Truncated
            ? new[]
            {
                new BridgeDiagnostic(
                    BridgeDiagnosticSeverity.Warning,
                    "structural_extract_truncated",
                    "The extraction hit its member cap; axes cover part of the document."),
            }
            : Array.Empty<BridgeDiagnostic>();
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result,
            afterFingerprint: result.Fingerprint,
            diagnostics: diagnostics);
    }

    private async Task<BridgeOperationResponse> AuditAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.AuditAsync(
            target,
            request.DeserializeArguments<RhinoAuditRequest>(),
            cancellationToken).ConfigureAwait(false);
        var diagnostics = result.Truncated
            ? new[]
            {
                new BridgeDiagnostic(
                    BridgeDiagnosticSeverity.Warning,
                    "rhino_audit_truncated",
                    "The audit hit its scan or finding cap; results cover part of the document."),
            }
            : Array.Empty<BridgeDiagnostic>();
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result,
            afterFingerprint: result.Fingerprint,
            diagnostics: diagnostics);
    }

    private async Task<BridgeOperationResponse> InspectAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var arguments = request.DeserializeArguments<ObjectIdArguments>();
        var state = await _adapter.InspectObjectAsync(target, arguments.ObjectId, cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            state,
            afterFingerprint: state.Fingerprint);
    }

    private static async Task<BridgeOperationResponse> MutationAsync<TRequest>(
        DocumentTarget target,
        BridgeOperationRequest request,
        Func<DocumentTarget, TRequest, CancellationToken, Task<RhinoSceneMutationResult>> action,
        CancellationToken cancellationToken)
        where TRequest : IRhinoSceneMutationRequest
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var arguments = request.DeserializeArguments<TRequest>();
        RequireMatchingOperationId(request, arguments);
        var result = await action(
            target,
            arguments,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            result.Changed,
            result,
            result.BeforeFingerprint,
            result.AfterFingerprint,
            result.Diagnostics);
    }

    private async Task<BridgeOperationResponse> ValidateUpsertAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var arguments = request.DeserializeArguments<UpsertRhinoObjectRequest>();
        RequireMatchingOperationId(request, arguments);
        var result = await _adapter.ValidateUpsertObjectAsync(
            target,
            arguments,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result);
    }

    private async Task<BridgeOperationResponse> TransformAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var arguments = request.DeserializeArguments<TransformRhinoObjectRequest>();
        RequireMatchingOperationId(request, arguments);
        if (string.IsNullOrWhiteSpace(request.ExpectedFingerprint) ||
            !string.Equals(
                request.ExpectedFingerprint,
                arguments.ExpectedFingerprint,
                StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                "expected_fingerprint",
                "rhino.transform requires matching envelope and argument fingerprints.");
        }

        var result = await _adapter.TransformObjectAsync(
            target,
            arguments,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            result.Changed,
            result,
            result.BeforeFingerprint,
            result.AfterFingerprint,
            result.Diagnostics);
    }

    private static void RequireMatchingOperationId(
        BridgeOperationRequest request,
        IRhinoSceneMutationRequest arguments)
    {
        if (!string.Equals(request.OperationId, arguments.OperationId, StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                "operation_id",
                $"Operation envelope '{request.OperationId}' does not match payload " +
                $"'{arguments.OperationId}'.");
        }
    }

    private static void RequireAccess(BridgeOperationRequest request, BridgeOperationAccess expected)
    {
        if (request.Access != expected)
        {
            throw new BridgeProtocolException(
                "operation_access",
                $"Operation '{request.Operation}' requires {expected} access.");
        }
    }

    private sealed record ObjectIdArguments(Guid ObjectId);
}
