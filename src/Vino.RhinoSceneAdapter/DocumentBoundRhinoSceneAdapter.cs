using Vino.BridgeContract;

namespace Vino.CanvasSceneAdapter;

/// <summary>
/// Forces every Rhino scene call through an explicit target resolver. No active-doc fallback exists.
/// </summary>
public abstract class DocumentBoundRhinoSceneAdapter<TRhinoDocument> : IRhinoSceneAdapter
    where TRhinoDocument : class
{
    private readonly ICanvasSceneDocumentResolver<TRhinoDocument> _resolver;

    protected DocumentBoundRhinoSceneAdapter(ICanvasSceneDocumentResolver<TRhinoDocument> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public Task<RhinoSceneListResult> ListObjectsAsync(DocumentTarget target, RhinoListObjectsRequest request, CancellationToken cancellationToken = default) =>
        ListObjectsCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneObjectState> InspectObjectAsync(DocumentTarget target, Guid objectId, CancellationToken cancellationToken = default) =>
        InspectObjectCoreAsync(Resolve(target), objectId, cancellationToken);

    public Task<StampedObjectsResult> ListStampedObjectsAsync(DocumentTarget target, CancellationToken cancellationToken = default) =>
        ListStampedObjectsCoreAsync(Resolve(target), cancellationToken);

    public Task<RhinoAuditResult> AuditAsync(DocumentTarget target, RhinoAuditRequest request, CancellationToken cancellationToken = default) =>
        AuditCoreAsync(Resolve(target), request, cancellationToken);

    public Task<StructuralExtractResult> ExtractStructuralAxesAsync(DocumentTarget target, StructuralExtractRequest request, CancellationToken cancellationToken = default) =>
        ExtractStructuralAxesCoreAsync(Resolve(target), request, cancellationToken);

    public Task<StructuralLoadSampleResult> SampleStructuralLoadsAsync(DocumentTarget target, StructuralLoadSampleRequest request, CancellationToken cancellationToken = default) =>
        SampleStructuralLoadsCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoViewCaptureResult> CaptureViewAsync(DocumentTarget target, RhinoViewCaptureRequest request, CancellationToken cancellationToken = default) =>
        CaptureViewCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> CreatePrimitiveAsync(DocumentTarget target, CreateRhinoPrimitiveRequest request, CancellationToken cancellationToken = default) =>
        CreatePrimitiveCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> UpsertObjectAsync(DocumentTarget target, UpsertRhinoObjectRequest request, CancellationToken cancellationToken = default) =>
        UpsertObjectCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoUpsertValidationResult> ValidateUpsertObjectAsync(DocumentTarget target, UpsertRhinoObjectRequest request, CancellationToken cancellationToken = default) =>
        ValidateUpsertObjectCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> DeleteObjectAsync(DocumentTarget target, DeleteRhinoObjectRequest request, CancellationToken cancellationToken = default) =>
        DeleteObjectCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> EnsureLayerAsync(DocumentTarget target, EnsureRhinoLayerRequest request, CancellationToken cancellationToken = default) =>
        EnsureLayerCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> TransformObjectAsync(DocumentTarget target, TransformRhinoObjectRequest request, CancellationToken cancellationToken = default) =>
        TransformObjectCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> FixEndpointPairAsync(DocumentTarget target, FixEndpointPairRequest request, CancellationToken cancellationToken = default) =>
        FixEndpointPairCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoLayerTableResult> ListLayersAsync(DocumentTarget target, CancellationToken cancellationToken = default) =>
        ListLayersCoreAsync(Resolve(target), cancellationToken);

    public Task<FocusObjectsResult> FocusObjectsAsync(DocumentTarget target, FocusObjectsRequest request, CancellationToken cancellationToken = default) =>
        FocusObjectsCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> UpdateLayerAsync(DocumentTarget target, UpdateRhinoLayerRequest request, CancellationToken cancellationToken = default) =>
        UpdateLayerCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> DeleteLayerAsync(DocumentTarget target, DeleteRhinoLayerRequest request, CancellationToken cancellationToken = default) =>
        DeleteLayerCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoLayerStateResult> LayerStateAsync(DocumentTarget target, RhinoLayerStateRequest request, CancellationToken cancellationToken = default) =>
        LayerStateCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoPurgeResult> PurgeTableEntriesAsync(DocumentTarget target, PurgeTableEntriesRequest request, CancellationToken cancellationToken = default) =>
        PurgeTableEntriesCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoBatchMutationResult> MoveObjectsToLayerAsync(DocumentTarget target, MoveObjectsToLayerRequest request, CancellationToken cancellationToken = default) =>
        MoveObjectsToLayerCoreAsync(Resolve(target), request, cancellationToken);

    protected abstract Task<RhinoSceneListResult> ListObjectsCoreAsync(TRhinoDocument document, RhinoListObjectsRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneObjectState> InspectObjectCoreAsync(TRhinoDocument document, Guid objectId, CancellationToken cancellationToken);
    protected abstract Task<StampedObjectsResult> ListStampedObjectsCoreAsync(TRhinoDocument document, CancellationToken cancellationToken);
    protected abstract Task<RhinoAuditResult> AuditCoreAsync(TRhinoDocument document, RhinoAuditRequest request, CancellationToken cancellationToken);
    protected abstract Task<StructuralExtractResult> ExtractStructuralAxesCoreAsync(TRhinoDocument document, StructuralExtractRequest request, CancellationToken cancellationToken);

    protected abstract Task<StructuralLoadSampleResult> SampleStructuralLoadsCoreAsync(TRhinoDocument document, StructuralLoadSampleRequest request, CancellationToken cancellationToken);
    // Virtual (not abstract): viewport capture needs a real display pipeline, which only the
    // Rhino-hosted adapter has — other subclasses keep compiling and fail loudly if asked.
    protected virtual Task<RhinoViewCaptureResult> CaptureViewCoreAsync(TRhinoDocument document, RhinoViewCaptureRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Viewport capture is only available on the Rhino-hosted scene adapter.");
    protected abstract Task<RhinoSceneMutationResult> CreatePrimitiveCoreAsync(TRhinoDocument document, CreateRhinoPrimitiveRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> UpsertObjectCoreAsync(TRhinoDocument document, UpsertRhinoObjectRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoUpsertValidationResult> ValidateUpsertObjectCoreAsync(TRhinoDocument document, UpsertRhinoObjectRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> DeleteObjectCoreAsync(TRhinoDocument document, DeleteRhinoObjectRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> EnsureLayerCoreAsync(TRhinoDocument document, EnsureRhinoLayerRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> TransformObjectCoreAsync(TRhinoDocument document, TransformRhinoObjectRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> FixEndpointPairCoreAsync(TRhinoDocument document, FixEndpointPairRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoLayerTableResult> ListLayersCoreAsync(TRhinoDocument document, CancellationToken cancellationToken);

    protected abstract Task<FocusObjectsResult> FocusObjectsCoreAsync(TRhinoDocument document, FocusObjectsRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> UpdateLayerCoreAsync(TRhinoDocument document, UpdateRhinoLayerRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> DeleteLayerCoreAsync(TRhinoDocument document, DeleteRhinoLayerRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoLayerStateResult> LayerStateCoreAsync(TRhinoDocument document, RhinoLayerStateRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoPurgeResult> PurgeTableEntriesCoreAsync(TRhinoDocument document, PurgeTableEntriesRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoBatchMutationResult> MoveObjectsToLayerCoreAsync(TRhinoDocument document, MoveObjectsToLayerRequest request, CancellationToken cancellationToken);

    private TRhinoDocument Resolve(DocumentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        return _resolver.Resolve(target);
    }
}
