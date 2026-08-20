using Vino.CanvasSceneAdapter;
using System.Text.Json;

namespace Vino.BridgeContract.Tests;

public sealed class RhinoSceneBridgeOperationHandlerTests
{
    [Fact]
    public void ListPayload_OmittedFiltersUseBoundedDefaults()
    {
        var arguments = JsonSerializer.Deserialize<RhinoListObjectsRequest>(
            "{}",
            BridgeProtocol.JsonOptions);

        Assert.NotNull(arguments);
        Assert.Equal(100, arguments.Limit);
        Assert.Null(arguments.ObjectId);
        Assert.Null(arguments.Selected);
    }

    [Fact]
    public async Task List_IsReadOnlyBoundedAndReturnsTruncationDiagnostic()
    {
        var adapter = new FakeRhinoSceneAdapter
        {
            ListResult = new RhinoSceneListResult(
                1,
                1,
                Truncated: true,
                Bounds: null,
                new[]
                {
                    new RhinoSceneObjectSummary(
                        Guid.Parse("2f927896-83f3-43c2-8a84-29b779547b7a"),
                        "wall-1",
                        "Wall",
                        "Brep",
                        Guid.Parse("80ace29e-9912-41de-88af-de9a7b6a57f0"),
                        "Building::Walls",
                        Selected: false,
                        Bounds: null,
                        "object-fingerprint"),
                },
                "list-fingerprint"),
        };
        var handler = new RhinoSceneBridgeOperationHandler(adapter);
        var arguments = new RhinoListObjectsRequest(
            Limit: 1,
            LayerFullPath: "Building::Walls",
            GeometryType: "Brep");
        var request = BridgeOperationRequest.Create(
            "list-1",
            BridgeAdapterOwner.RhinoScene,
            "rhino.list",
            BridgeOperationAccess.Read,
            2,
            arguments);

        var response = await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);

        Assert.False(response.Changed);
        Assert.Equal("list-fingerprint", response.AfterFingerprint);
        Assert.Equal("rhino_list_truncated", Assert.Single(response.Diagnostics).Code);
        Assert.Equal(arguments, adapter.LastListRequest);
    }

    [Fact]
    public async Task CreatePrimitive_UsesExplicitTypedPayload()
    {
        var objectId = Guid.Parse("660eb647-3699-4f8c-a9dc-bfeb010f5d0f");
        var adapter = new FakeRhinoSceneAdapter
        {
            MutationResult = Mutation("create-1", objectId, before: null, after: "created"),
        };
        var handler = new RhinoSceneBridgeOperationHandler(adapter);
        var arguments = new CreateRhinoPrimitiveRequest(
            "create-1",
            objectId,
            "control-point-1",
            RhinoPrimitiveKind.Point,
            Point: new RhinoPointPrimitive(new RhinoPoint3d(1, 2, 3)),
            Attributes: new RhinoPrimitiveAttributes(Name: "Control Point"));
        var request = BridgeOperationRequest.Create(
            "create-1",
            BridgeAdapterOwner.RhinoScene,
            "rhino.createPrimitive",
            BridgeOperationAccess.Write,
            2,
            arguments,
            writerLeaseToken: "broker-lease");

        var response = await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);

        Assert.True(response.Changed);
        Assert.Equal("created", response.AfterFingerprint);
        Assert.Equal(arguments, adapter.LastCreateRequest);
    }

    /// <summary>
    /// focus mode=select is a pure look — it travels as Read, reports changed:false, and routes
    /// the payload verbatim (hidden/locked targets come back as counts, never as mutations).
    /// </summary>
    [Fact]
    public async Task Focus_SelectRunsAsReadAndRoutesExactPayload()
    {
        var adapter = new FakeRhinoSceneAdapter
        {
            FocusResult = new FocusObjectsResult(2, 1, 1, 0, false, "focus-fp"),
        };
        var handler = new RhinoSceneBridgeOperationHandler(adapter);
        var arguments = new FocusObjectsRequest(
            new[] { Guid.Parse("2f927896-83f3-43c2-8a84-29b779547b7a") }, "select");
        var request = BridgeOperationRequest.Create(
            "focus-1",
            BridgeAdapterOwner.RhinoScene,
            "rhino.focusObjects",
            BridgeOperationAccess.Read,
            2,
            arguments);

        var response = await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);

        Assert.False(response.Changed);
        Assert.Equal("focus-fp", response.AfterFingerprint);
        // Field-wise: ObjectIds round-trips through JSON into a fresh list, so record equality
        // (reference-equal collections) cannot be used here.
        Assert.Equal(arguments.ObjectIds, adapter.LastFocusRequest!.ObjectIds);
        Assert.Equal("select", adapter.LastFocusRequest.Mode);
        Assert.True(adapter.LastFocusRequest.Zoom);
        Assert.Null(adapter.LastFocusRequest.OwnerToken);
    }

    /// <summary>
    /// isolate mutates visibility attributes (and therefore object fingerprints), so a Read-access
    /// request is refused — the honest classification this op used to dodge as a disguised read.
    /// </summary>
    [Fact]
    public async Task Focus_IsolateRefusesReadAccess()
    {
        var handler = new RhinoSceneBridgeOperationHandler(new FakeRhinoSceneAdapter());
        var request = BridgeOperationRequest.Create(
            "focus-2",
            BridgeAdapterOwner.RhinoScene,
            "rhino.focusObjects",
            BridgeOperationAccess.Read,
            2,
            new FocusObjectsRequest(new[] { Guid.NewGuid() }, "isolate"));

        await Assert.ThrowsAsync<BridgeProtocolException>(
            () => handler.HandleAsync(DocumentTargetTests.CreateTarget(), request));
    }

    [Fact]
    public async Task Focus_IsolateUnderWriteLeaseReportsChangedAndCarriesOwnerToken()
    {
        var adapter = new FakeRhinoSceneAdapter
        {
            FocusResult = new FocusObjectsResult(1, 0, 3, 0, false, "focus-fp"),
        };
        var handler = new RhinoSceneBridgeOperationHandler(adapter);
        var arguments = new FocusObjectsRequest(
            new[] { Guid.NewGuid() }, "isolate", OwnerToken: "surface-a");
        var request = BridgeOperationRequest.Create(
            "focus-3",
            BridgeAdapterOwner.RhinoScene,
            "rhino.focusObjects",
            BridgeOperationAccess.Write,
            2,
            arguments,
            writerLeaseToken: "view-lease");

        var response = await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);

        Assert.True(response.Changed);
        Assert.Equal("surface-a", adapter.LastFocusRequest!.OwnerToken);
    }

    /// <summary>
    /// A stale-token restore the adapter refused (restored:false, nothing re-shown) must come back
    /// changed:false — the stale surface's cleanup was a no-op, not a mutation.
    /// </summary>
    [Fact]
    public async Task Focus_RefusedStaleRestoreReportsUnchanged()
    {
        var adapter = new FakeRhinoSceneAdapter
        {
            FocusResult = new FocusObjectsResult(0, 0, 0, 0, false, "focus-fp"),
        };
        var handler = new RhinoSceneBridgeOperationHandler(adapter);
        var request = BridgeOperationRequest.Create(
            "focus-4",
            BridgeAdapterOwner.RhinoScene,
            "rhino.focusObjects",
            BridgeOperationAccess.Write,
            2,
            new FocusObjectsRequest(Array.Empty<Guid>(), "restore", OwnerToken: "stale-surface"),
            writerLeaseToken: "view-lease");

        var response = await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);

        Assert.False(response.Changed);
        Assert.Equal("stale-surface", adapter.LastFocusRequest!.OwnerToken);
    }

    [Fact]
    public async Task Audit_RoutesLayerSemanticsAndReportsTruncation()
    {
        var layerId = Guid.Parse("80ace29e-9912-41de-88af-de9a7b6a57f0");
        var occupantId = Guid.Parse("2f927896-83f3-43c2-8a84-29b779547b7a");
        var adapter = new FakeRhinoSceneAdapter
        {
            AuditResult = new RhinoAuditResult(
                "layerSemantics", 0.001, "Millimeters", 0.001, null, 12,
                new[]
                {
                    new RhinoAuditFinding(
                        "finding-1", "layerSemantics", new[] { layerId }, new[] { "layer-fp" },
                        null, "Layer 'Building::벽' has no semantic label.", new[] { "updateLayer" },
                        null,
                        new RhinoLayerFacts(
                            "Building::벽", "벽", -8355712, "Plaster", 3, 2,
                            new[] { occupantId },
                            new Dictionary<string, string> { ["gptino.labelSource"] = "seed" })),
                },
                Truncated: true,
                "audit-fingerprint"),
        };
        var handler = new RhinoSceneBridgeOperationHandler(adapter);
        var request = BridgeOperationRequest.Create(
            "audit-1",
            BridgeAdapterOwner.RhinoScene,
            "rhino.audit",
            BridgeOperationAccess.Read,
            2,
            new RhinoAuditRequest("layerSemantics", Limit: 100));

        var response = await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);

        Assert.Equal("layerSemantics", adapter.LastAuditRequest?.Kind);
        Assert.Equal(100, adapter.LastAuditRequest?.Limit);
        Assert.Equal("rhino_audit_truncated", Assert.Single(response.Diagnostics).Code);
    }

    /// <summary>
    /// The new curation fields must survive the ACTUAL wire options — BridgeProtocol.JsonOptions
    /// disallows unmapped members and camelCases property names, and a DictionaryKeyPolicy slip
    /// would silently mangle "gptino.material" into something no reader recognizes.
    /// </summary>
    [Fact]
    public void LayerCurationShapes_RoundTripThroughBridgeJsonVerbatim()
    {
        var facts = new RhinoLayerFacts(
            "Building::벽", "벽", -8355712, "Plaster", 3, 2,
            new[] { Guid.Parse("2f927896-83f3-43c2-8a84-29b779547b7a") },
            new Dictionary<string, string> { ["gptino.material"] = "plaster", ["gptino.canonical"] = "WALL" });
        var finding = new RhinoAuditFinding(
            "finding-1", "layerSemantics", new[] { Guid.NewGuid() }, new[] { "fp" },
            null, "detail", Array.Empty<string>(), null, facts);
        var roundTrippedFinding = JsonSerializer.Deserialize<RhinoAuditFinding>(
            JsonSerializer.Serialize(finding, BridgeProtocol.JsonOptions),
            BridgeProtocol.JsonOptions);
        Assert.NotNull(roundTrippedFinding?.LayerFacts);
        Assert.Equal("Building::벽", roundTrippedFinding.LayerFacts.FullPath);
        Assert.Equal("Plaster", roundTrippedFinding.LayerFacts.RenderMaterialName);
        Assert.Equal("plaster", roundTrippedFinding.LayerFacts.UserText?["gptino.material"]);
        Assert.Equal("WALL", roundTrippedFinding.LayerFacts.UserText?["gptino.canonical"]);

        var update = new UpdateRhinoLayerRequest(
            "op-1", Guid.NewGuid(), "fp",
            UserText: new Dictionary<string, string> { ["gptino.confidence"] = "high" },
            RenderMaterial: "plaster");
        var roundTrippedUpdate = JsonSerializer.Deserialize<UpdateRhinoLayerRequest>(
            JsonSerializer.Serialize(update, BridgeProtocol.JsonOptions),
            BridgeProtocol.JsonOptions);
        Assert.Equal("high", roundTrippedUpdate?.UserText?["gptino.confidence"]);
        Assert.Equal("plaster", roundTrippedUpdate?.RenderMaterial);

        var summary = new RhinoLayerSummary(
            Guid.NewGuid(), "Building::벽", Guid.Empty, 3, -8355712,
            Visible: true, Locked: false, IsCurrent: false, ObjectCount: 5, HasChildren: false,
            "fp", new Dictionary<string, string> { ["gptino.canonical"] = "WALL" });
        var roundTrippedSummary = JsonSerializer.Deserialize<RhinoLayerSummary>(
            JsonSerializer.Serialize(summary, BridgeProtocol.JsonOptions),
            BridgeProtocol.JsonOptions);
        Assert.Equal("WALL", roundTrippedSummary?.UserText?["gptino.canonical"]);
    }

    [Fact]
    public async Task UpdateLayer_PassesUserTextThroughTheTypedPayload()
    {
        var layerId = Guid.Parse("80ace29e-9912-41de-88af-de9a7b6a57f0");
        var adapter = new FakeRhinoSceneAdapter
        {
            MutationResult = Mutation("label-1", layerId, before: "fp-before", after: "fp-before"),
        };
        var handler = new RhinoSceneBridgeOperationHandler(adapter);
        var request = BridgeOperationRequest.Create(
            "label-1",
            BridgeAdapterOwner.RhinoScene,
            "rhino.updateLayer",
            BridgeOperationAccess.Write,
            2,
            new UpdateRhinoLayerRequest(
                "label-1", layerId, "fp-before",
                UserText: new Dictionary<string, string>
                {
                    ["gptino.material"] = "concrete",
                    ["gptino.canonical"] = "WALL",
                }),
            writerLeaseToken: "broker-lease");

        await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);

        var arrived = adapter.LastUpdateLayerRequest;
        Assert.NotNull(arrived?.UserText);
        Assert.Equal("concrete", arrived.UserText["gptino.material"]);
        Assert.Equal("WALL", arrived.UserText["gptino.canonical"]);
    }

    [Fact]
    public async Task Mutation_RejectsPayloadOperationIdMismatch()
    {
        var objectId = Guid.Parse("660eb647-3699-4f8c-a9dc-bfeb010f5d0f");
        var adapter = new FakeRhinoSceneAdapter();
        var handler = new RhinoSceneBridgeOperationHandler(adapter);
        var arguments = new CreateRhinoPrimitiveRequest(
            "payload-id",
            objectId,
            "control-point-1",
            RhinoPrimitiveKind.Point,
            Point: new RhinoPointPrimitive(new RhinoPoint3d(1, 2, 3)));
        var request = BridgeOperationRequest.Create(
            "envelope-id",
            BridgeAdapterOwner.RhinoScene,
            "rhino.createPrimitive",
            BridgeOperationAccess.Write,
            2,
            arguments,
            writerLeaseToken: "broker-lease");

        var exception = await Assert.ThrowsAsync<BridgeProtocolException>(
            () => handler.HandleAsync(DocumentTargetTests.CreateTarget(), request));

        Assert.Equal("operation_id", exception.Code);
        Assert.Null(adapter.LastCreateRequest);
    }

    [Fact]
    public async Task Transform_RequiresMatchingEnvelopeFingerprint()
    {
        var objectId = Guid.Parse("40dd3f09-678d-45cd-84c5-27de846b940d");
        var adapter = new FakeRhinoSceneAdapter
        {
            MutationResult = Mutation("transform-1", objectId, "before", "after"),
        };
        var handler = new RhinoSceneBridgeOperationHandler(adapter);
        var arguments = new TransformRhinoObjectRequest(
            "transform-1",
            objectId,
            "before",
            Translation(10, 20, 30));
        var request = BridgeOperationRequest.Create(
            "transform-1",
            BridgeAdapterOwner.RhinoScene,
            "rhino.transform",
            BridgeOperationAccess.Write,
            2,
            arguments,
            expectedFingerprint: "stale",
            writerLeaseToken: "broker-lease");

        var exception = await Assert.ThrowsAsync<BridgeProtocolException>(
            () => handler.HandleAsync(DocumentTargetTests.CreateTarget(), request));

        Assert.Equal("expected_fingerprint", exception.Code);
        Assert.Null(adapter.LastTransformRequest);
    }

    [Fact]
    public async Task Transform_RoutesExactObjectMatrixAndFingerprint()
    {
        var objectId = Guid.Parse("40dd3f09-678d-45cd-84c5-27de846b940d");
        var adapter = new FakeRhinoSceneAdapter
        {
            MutationResult = Mutation("transform-1", objectId, "before", "after"),
        };
        var handler = new RhinoSceneBridgeOperationHandler(adapter);
        var arguments = new TransformRhinoObjectRequest(
            "transform-1",
            objectId,
            "before",
            Translation(10, 20, 30));
        var request = BridgeOperationRequest.Create(
            "transform-1",
            BridgeAdapterOwner.RhinoScene,
            "rhino.transform",
            BridgeOperationAccess.Write,
            2,
            arguments,
            expectedFingerprint: "before",
            writerLeaseToken: "broker-lease");

        var response = await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);

        Assert.True(response.Changed);
        Assert.Equal("before", response.BeforeFingerprint);
        Assert.Equal("after", response.AfterFingerprint);
        Assert.Equal(arguments, adapter.LastTransformRequest);
    }

    [Fact]
    public async Task ValidateUpsert_IsReadOnlyAndRoutesTheExactPayload()
    {
        var objectId = Guid.Parse("1ca7b351-bc98-46c6-bb8c-eec5dff139d8");
        var arguments = new UpsertRhinoObjectRequest(
            "validate-1",
            objectId,
            "surface-1",
            "Brep",
            "{\"archive3dm\":1}",
            "{}",
            ExpectedFingerprint: null);
        var adapter = new FakeRhinoSceneAdapter
        {
            ValidationResult = new RhinoUpsertValidationResult(
                "validate-1",
                objectId,
                "Brep",
                ExistingObject: false,
                ExistingFingerprint: null,
                IsValid: true),
        };
        var handler = new RhinoSceneBridgeOperationHandler(adapter);
        var request = BridgeOperationRequest.Create(
            "validate-1",
            BridgeAdapterOwner.RhinoScene,
            "rhino.validateUpsert",
            BridgeOperationAccess.Read,
            2,
            arguments);

        var response = await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);

        Assert.False(response.Changed);
        Assert.Equal(arguments, adapter.LastValidationRequest);
        var result = response.Result.Deserialize<RhinoUpsertValidationResult>(BridgeProtocol.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.Equal("Brep", result.ActualGeometryType);
    }

    [Fact]
    public async Task ValidateUpsert_RejectsWriteAccess()
    {
        var objectId = Guid.NewGuid();
        var arguments = new UpsertRhinoObjectRequest(
            "validate-write",
            objectId,
            "surface-1",
            "Brep",
            "{}",
            "{}",
            ExpectedFingerprint: null);
        var adapter = new FakeRhinoSceneAdapter();
        var handler = new RhinoSceneBridgeOperationHandler(adapter);
        var request = BridgeOperationRequest.Create(
            "validate-write",
            BridgeAdapterOwner.RhinoScene,
            "rhino.validateUpsert",
            BridgeOperationAccess.Write,
            2,
            arguments,
            writerLeaseToken: "broker-lease");

        await Assert.ThrowsAsync<BridgeProtocolException>(
            () => handler.HandleAsync(DocumentTargetTests.CreateTarget(), request));
        Assert.Null(adapter.LastValidationRequest);
    }

    private static RhinoTransformMatrix Translation(double x, double y, double z) => new(
        1, 0, 0, x,
        0, 1, 0, y,
        0, 0, 1, z,
        0, 0, 0, 1);

    private static RhinoSceneMutationResult Mutation(
        string operationId,
        Guid objectId,
        string? before,
        string after) =>
        new(operationId, Changed: true, before, after, objectId);

    private sealed class FakeRhinoSceneAdapter : IRhinoSceneAdapter
    {
        public RhinoSceneListResult ListResult { get; set; } = new(
            100,
            0,
            Truncated: false,
            Bounds: null,
            Array.Empty<RhinoSceneObjectSummary>(),
            "empty");

        public RhinoSceneMutationResult MutationResult { get; set; } =
            Mutation("unused", Guid.NewGuid(), "before", "after");

        public RhinoUpsertValidationResult ValidationResult { get; set; } =
            new(
                "unused",
                Guid.NewGuid(),
                "Point",
                ExistingObject: false,
                ExistingFingerprint: null,
                IsValid: true);

        public RhinoListObjectsRequest? LastListRequest { get; private set; }
        public CreateRhinoPrimitiveRequest? LastCreateRequest { get; private set; }
        public TransformRhinoObjectRequest? LastTransformRequest { get; private set; }
        public UpsertRhinoObjectRequest? LastValidationRequest { get; private set; }

        public Task<StructuralExtractResult> ExtractStructuralAxesAsync(
            DocumentTarget target,
            StructuralExtractRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StructuralExtractResult(
                "Millimeters",
                0,
                Array.Empty<StructuralMember>(),
                Array.Empty<StructuralPrototype>(),
                Array.Empty<StructuralFreeEnd>(),
                0,
                0,
                new Dictionary<string, int>(),
                Truncated: false,
                "empty"));

        public Task<RhinoSceneListResult> ListObjectsAsync(
            DocumentTarget target,
            RhinoListObjectsRequest request,
            CancellationToken cancellationToken = default)
        {
            LastListRequest = request;
            return Task.FromResult(ListResult);
        }

        public Task<RhinoSceneObjectState> InspectObjectAsync(
            DocumentTarget target,
            Guid objectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(State(objectId));

        public RhinoViewCaptureRequest? LastCaptureRequest { get; private set; }

        public Task<RhinoViewCaptureResult> CaptureViewAsync(
            DocumentTarget target,
            RhinoViewCaptureRequest request,
            CancellationToken cancellationToken = default)
        {
            LastCaptureRequest = request;
            return Task.FromResult(new RhinoViewCaptureResult(
                request.ViewName ?? "Perspective",
                request.Width,
                request.Height,
                Convert.ToBase64String(new byte[] { 137, 80, 78, 71 }),
                "capture-fp"));
        }

        public Task<RhinoSceneMutationResult> CreatePrimitiveAsync(
            DocumentTarget target,
            CreateRhinoPrimitiveRequest request,
            CancellationToken cancellationToken = default)
        {
            LastCreateRequest = request;
            return Task.FromResult(MutationResult);
        }

        public Task<RhinoSceneMutationResult> UpsertObjectAsync(
            DocumentTarget target,
            UpsertRhinoObjectRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MutationResult);

        public Task<RhinoUpsertValidationResult> ValidateUpsertObjectAsync(
            DocumentTarget target,
            UpsertRhinoObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            LastValidationRequest = request;
            return Task.FromResult(ValidationResult);
        }

        public Task<RhinoSceneMutationResult> DeleteObjectAsync(
            DocumentTarget target,
            DeleteRhinoObjectRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MutationResult);

        public Task<RhinoSceneMutationResult> EnsureLayerAsync(
            DocumentTarget target,
            EnsureRhinoLayerRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MutationResult);

        public Task<RhinoSceneMutationResult> TransformObjectAsync(
            DocumentTarget target,
            TransformRhinoObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            LastTransformRequest = request;
            return Task.FromResult(MutationResult);
        }

        public Task<StampedObjectsResult> ListStampedObjectsAsync(DocumentTarget target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public RhinoAuditRequest? LastAuditRequest { get; private set; }

        public RhinoAuditResult AuditResult { get; set; } = new(
            "layerSemantics", 0.001, "Millimeters", 0.001, null, 0,
            Array.Empty<RhinoAuditFinding>(), Truncated: false, "audit-fingerprint");

        public Task<RhinoAuditResult> AuditAsync(DocumentTarget target, RhinoAuditRequest request, CancellationToken cancellationToken = default)
        {
            LastAuditRequest = request;
            return Task.FromResult(AuditResult);
        }

        public Task<RhinoSceneMutationResult> FixEndpointPairAsync(DocumentTarget target, FixEndpointPairRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RhinoLayerTableResult> ListLayersAsync(DocumentTarget target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public FocusObjectsRequest? LastFocusRequest { get; private set; }

        public FocusObjectsResult FocusResult { get; set; } = new(0, 0, 0, 0, false, "focus-fingerprint");

        public Task<FocusObjectsResult> FocusObjectsAsync(DocumentTarget target, FocusObjectsRequest request, CancellationToken cancellationToken = default)
        {
            LastFocusRequest = request;
            return Task.FromResult(FocusResult);
        }

        public UpdateRhinoLayerRequest? LastUpdateLayerRequest { get; private set; }

        public Task<RhinoSceneMutationResult> UpdateLayerAsync(DocumentTarget target, UpdateRhinoLayerRequest request, CancellationToken cancellationToken = default)
        {
            LastUpdateLayerRequest = request;
            return Task.FromResult(MutationResult);
        }

        public Task<RhinoSceneMutationResult> DeleteLayerAsync(DocumentTarget target, DeleteRhinoLayerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RhinoLayerStateResult> LayerStateAsync(DocumentTarget target, RhinoLayerStateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RhinoPurgeResult> PurgeTableEntriesAsync(DocumentTarget target, PurgeTableEntriesRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RhinoBatchMutationResult> MoveObjectsToLayerAsync(DocumentTarget target, MoveObjectsToLayerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static RhinoSceneObjectState State(Guid objectId) =>
            new(objectId, "logical", "Point", "{}", "{}", "fingerprint");
    }
}
