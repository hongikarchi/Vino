using Vino.BridgeContract;

namespace Vino.CanvasSceneAdapter;

public sealed class CanvasBridgeOperationHandler : IBridgeOperationHandler
{
    private readonly ICanvasAdapter _adapter;

    public CanvasBridgeOperationHandler(ICanvasAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public BridgeAdapterOwner Owner => BridgeAdapterOwner.Canvas;

    public async Task<BridgeOperationResponse> HandleAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireRequest(request);
        return request.Operation switch
        {
            "canvas.snapshot" => await SnapshotAsync(target, request, cancellationToken).ConfigureAwait(false),
            "canvas.inspect" => await InspectAsync(target, request, cancellationToken).ConfigureAwait(false),
            "canvas.inspectOutputs" => await InspectOutputsAsync(target, request, cancellationToken).ConfigureAwait(false),
            "canvas.catalog" => await CatalogAsync(target, request, cancellationToken).ConfigureAwait(false),
            "canvas.create" => await MutationAsync<CreateCanvasObjectRequest>(target, request, _adapter.CreateObjectAsync, cancellationToken).ConfigureAwait(false),
            "canvas.delete" => await MutationAsync<DeleteCanvasObjectRequest>(target, request, _adapter.DeleteObjectAsync, cancellationToken).ConfigureAwait(false),
            "canvas.move" => await MutationAsync<MoveCanvasObjectsRequest>(target, request, _adapter.MoveObjectsAsync, cancellationToken).ConfigureAwait(false),
            "canvas.setNumberSlider" => await MutationAsync<SetNumberSliderValueRequest>(target, request, _adapter.SetNumberSliderValueAsync, cancellationToken).ConfigureAwait(false),
            "canvas.setInputValue" => await MutationAsync<SetInputValueRequest>(target, request, _adapter.SetInputValueAsync, cancellationToken).ConfigureAwait(false),
            "canvas.setWire" => await MutationAsync<SetWireRequest>(target, request, _adapter.SetWireAsync, cancellationToken).ConfigureAwait(false),
            "canvas.setGroup" => await MutationAsync<SetGroupRequest>(target, request, _adapter.SetGroupAsync, cancellationToken).ConfigureAwait(false),
            "canvas.referenceRhinoObjects" => await MutationAsync<ReferenceRhinoObjectsRequest>(target, request, _adapter.ReferenceRhinoObjectsAsync, cancellationToken).ConfigureAwait(false),
            "canvas.listReferencedRhinoIds" => await ListReferencedRhinoIdsAsync(target, request, cancellationToken).ConfigureAwait(false),
            "canvas.focusObjects" => await FocusObjectsAsync(target, request, cancellationToken).ConfigureAwait(false),
            "canvas.capture" => await CaptureAsync(target, request, cancellationToken).ConfigureAwait(false),
            _ => throw new BridgeProtocolException(
                "unknown_canvas_operation",
                $"Unknown canvas operation '{request.Operation}'."),
        };
    }

    private async Task<BridgeOperationResponse> ListReferencedRhinoIdsAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.ListReferencedRhinoIdsAsync(target, cancellationToken).ConfigureAwait(false);
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
        // Read access on purpose: this only changes canvas selection and viewport (ephemeral UI
        // state), never document content. It is a human pressing a chip to go look at what Vino
        // built — routing it through the writer lease would let a running job block the user from
        // inspecting the very thing it is arguing about. Panel-only, self-contained, no undo.
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.FocusObjectsAsync(
            target,
            request.DeserializeArguments<CanvasFocusRequest>(),
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result,
            afterFingerprint: result.Fingerprint);
    }

    private async Task<BridgeOperationResponse> SnapshotAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var snapshot = await _adapter.CaptureSnapshotAsync(target, cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            snapshot,
            afterFingerprint: snapshot.DocumentFingerprint);
    }

    private async Task<BridgeOperationResponse> CaptureAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.CaptureCanvasImageAsync(
            target,
            request.DeserializeArguments<CanvasCaptureRequest>(),
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result,
            afterFingerprint: result.Fingerprint);
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

    private async Task<BridgeOperationResponse> InspectOutputsAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.InspectOutputsAsync(
            target,
            request.DeserializeArguments<InspectCanvasOutputsRequest>(),
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result,
            afterFingerprint: result.Fingerprint);
    }

    private async Task<BridgeOperationResponse> CatalogAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.SearchComponentCatalogAsync(
            target,
            request.DeserializeArguments<ComponentCatalogSearchRequest>(),
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result);
    }

    private static async Task<BridgeOperationResponse> MutationAsync<TRequest>(
        DocumentTarget target,
        BridgeOperationRequest request,
        Func<DocumentTarget, TRequest, CancellationToken, Task<CanvasMutationResult>> action,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var result = await action(
            target,
            request.DeserializeArguments<TRequest>(),
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            result.Changed,
            result,
            result.BeforeFingerprint,
            result.AfterFingerprint);
    }

    private void RequireRequest(BridgeOperationRequest request)
    {
        if (request.Owner != Owner)
        {
            throw new BridgeProtocolException("adapter_owner", "Canvas handler received another owner's request.");
        }

        request.Validate();
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
