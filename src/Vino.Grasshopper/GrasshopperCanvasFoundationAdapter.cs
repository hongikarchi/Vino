// SPDX-License-Identifier: Apache-2.0
// Behavioral reimplementation informed by Cordyceps; see THIRD_PARTY_NOTICES.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Vino.BridgeContract;
using Vino.CanvasSceneAdapter;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;

namespace Vino.Grasshopper;

/// <summary>
/// Document-bound adapter for deterministic canvas inspection, catalog search, creation,
/// deletion, movement, wiring, and group membership updates with Rhino/Grasshopper undo support.
/// </summary>
public sealed class GrasshopperCanvasFoundationAdapter : DocumentBoundCanvasAdapter<GH_Document>
{
    // Bridge failure code for a fingerprint CAS refusal raised BEFORE any document mutation (delete
    // structure, move layout, slider value). Must match the executor's recognized refusal codes
    // (LiveDocumentBackend.PreconditionRefusedFailureCode) so a clean "nothing changed, resubmit with
    // the current fingerprint" refusal classifies as a deterministic Failed, not RecoveryRequired.
    private const string PreconditionRefusedCode = "precondition_refused";

    public GrasshopperCanvasFoundationAdapter(ExplicitGrasshopperDocumentResolver resolver)
        : base(resolver)
    {
    }

    protected override Task<CanvasSnapshot> CaptureSnapshotCoreAsync(
        GH_Document document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parameterOwners = BuildParameterOwners(document);
        var objects = document.Objects
            .Select(documentObject => ToObjectState(documentObject, parameterOwners))
            .OrderBy(state => state.ObjectId)
            .ToArray();
        var wires = parameterOwners.Keys
            .SelectMany(target => target.Sources.Select(source => new WireState(
                parameterOwners.TryGetValue(source, out var sourceOwner) ? sourceOwner : source.InstanceGuid,
                source.InstanceGuid,
                parameterOwners[target],
                target.InstanceGuid)))
            .OrderBy(wire => wire.SourceObjectId)
            .ThenBy(wire => wire.SourceParameterId)
            .ThenBy(wire => wire.TargetObjectId)
            .ThenBy(wire => wire.TargetParameterId)
            .ToArray();
        var groups = document.Objects
            .OfType<GH_Group>()
            .Select(group => new GroupState(
                group.InstanceGuid,
                group.NickName,
                group.ObjectIDs.OrderBy(id => id).ToArray(),
                group.Colour.ToArgb()))
            .OrderBy(group => group.GroupId)
            .ToArray();
        var fingerprint = ComputeDocumentFingerprint(objects, wires, groups);
        return Task.FromResult(new CanvasSnapshot(
            document.DocumentID,
            fingerprint,
            objects,
            wires,
            groups));
    }

    protected override Task<CanvasObjectState> InspectObjectCoreAsync(
        GH_Document document,
        Guid objectId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var documentObject = document.FindObject(objectId, true)
            ?? throw new KeyNotFoundException($"Grasshopper object {objectId:D} was not found.");
        return Task.FromResult(ToObjectState(documentObject, BuildParameterOwners(document)));
    }

    protected override Task<CanvasOutputInspection> InspectOutputsCoreAsync(
        GH_Document document,
        InspectCanvasOutputsRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.ObjectId == Guid.Empty)
        {
            throw new InvalidOperationException("ObjectId is required for output inspection.");
        }

        var documentObject = document.FindObject(request.ObjectId, true)
            ?? throw new KeyNotFoundException(
                $"Grasshopper object {request.ObjectId:D} was not found.");
        // A component exposes its Output parameters; a STANDALONE parameter — a Rhino reference
        // parameter created by referenceRhinoObjects, or a Number Slider — IS its own single output
        // and holds the data directly. Inspect whichever applies. Only objects with neither (a
        // Scribble, a group) are genuinely un-inspectable. Previously a standalone param threw
        // NotSupportedException, which the Verify path swallowed but logged as a bridge failure and
        // which blocked semantic predicates on the parameter's own data.
        IReadOnlyList<IGH_Param> outputParameters = documentObject switch
        {
            IGH_Component component => component.Params.Output,
            IGH_Param parameter => new[] { parameter },
            _ => throw new NotSupportedException(
                $"Grasshopper object {request.ObjectId:D} exposes no inspectable outputs.")
        };

        var outputs = outputParameters
            .Select(parameter => InspectOutputParameter(parameter, request.IncludeMassProperties, cancellationToken))
            .ToArray();
        var canonical = JsonSerializer.Serialize(outputs);
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{document.DocumentID:N}|{request.ObjectId:N}|{canonical}")))
            .ToLowerInvariant();
        return Task.FromResult(new CanvasOutputInspection(
            document.DocumentID,
            request.ObjectId,
            outputs,
            fingerprint));
    }

    protected override Task<ComponentCatalogSearchResult> SearchComponentCatalogCoreAsync(
        GH_Document document,
        ComponentCatalogSearchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Limit,
                "Component catalog limit must be between 1 and 100.");
        }

        var query = request.Query?.Trim() ?? string.Empty;
        var matches = global::Grasshopper.Instances.ComponentServer.ObjectProxies
            .Where(proxy => proxy.Guid != Guid.Empty && (request.IncludeObsolete || !proxy.Obsolete))
            .Select(proxy => new CatalogCandidate(proxy, CatalogScore(proxy, query)))
            .Where(candidate => candidate.Score is not null)
            .GroupBy(candidate => candidate.Proxy.Guid)
            .Select(group => group
                .OrderBy(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Proxy.Desc.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Proxy.Desc.Category, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Proxy.Obsolete)
            .ThenBy(candidate => candidate.Proxy.Desc.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Proxy.Desc.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Proxy.Desc.SubCategory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Proxy.Guid)
            .Take(request.Limit)
            .Select(candidate => new CanvasComponentCatalogItem(
                candidate.Proxy.Guid,
                candidate.Proxy.Desc.Name ?? string.Empty,
                candidate.Proxy.Desc.NickName ?? string.Empty,
                candidate.Proxy.Desc.Category ?? string.Empty,
                candidate.Proxy.Desc.SubCategory ?? string.Empty,
                candidate.Proxy.Desc.Description ?? string.Empty,
                candidate.Proxy.Exposure.ToString(),
                candidate.Proxy.Obsolete))
            .ToArray();

        return Task.FromResult(new ComponentCatalogSearchResult(
            document.DocumentID,
            query,
            request.Limit,
            matches));
    }

    protected override Task<CanvasMutationResult> CreateObjectCoreAsync(
        GH_Document document,
        CreateCanvasObjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        if (request.ComponentTypeId == Guid.Empty)
        {
            throw new InvalidOperationException("ComponentTypeId is required.");
        }
        RequireFinite(request.Pivot, "Pivot");
        if (request.ObjectId != Guid.Empty && document.FindObject(request.ObjectId, true) is { } existing)
        {
            var state = ToObjectState(existing, BuildParameterOwners(document));
            if (state.ComponentTypeId != request.ComponentTypeId)
            {
                // Deterministic PRE-WRITE refusal (nothing touched yet): classify as a clean
                // Failed via precondition_refused, not a RecoveryRequired review.
                throw new BridgeProtocolException(
                    PreconditionRefusedCode,
                    $"Object {request.ObjectId:D} already exists with another component type " +
                    $"({state.ComponentTypeId:D}).");
            }
            return Task.FromResult(new CanvasMutationResult(
                request.OperationId,
                Changed: false,
                state.Fingerprint,
                state.Fingerprint,
                new[] { request.ObjectId }));
        }

        // PRE-WRITE refusal (backstop for the executor's canvas.create catalog preflight): a type
        // GUID the component server cannot emit means nothing was touched — precondition_refused,
        // with the same lookup recipe the preflight gives.
        var documentObject = global::Grasshopper.Instances.ComponentServer.EmitObject(request.ComponentTypeId)
            ?? throw new BridgeProtocolException(
                PreconditionRefusedCode,
                $"Grasshopper component type {request.ComponentTypeId:D} is not installed. Look the " +
                "GUID up with a component_catalog name search (or use the well-known GUID table in " +
                "the gh-authoring skill) and resubmit — never write a type GUID from memory.");
        if (request.ObjectId != Guid.Empty)
        {
            documentObject.NewInstanceGuid(request.ObjectId);
            if (documentObject.InstanceGuid != request.ObjectId)
            {
                throw new InvalidOperationException("Grasshopper did not accept the requested object identity.");
            }
        }
        if (!string.IsNullOrWhiteSpace(request.NickName))
        {
            documentObject.NickName = request.NickName.Trim();
        }
        // Freshly emitted objects (Number Slider among them) have null Attributes until
        // CreateAttributes runs; setting Pivot first would throw for those types.
        if (documentObject.Attributes is null)
        {
            documentObject.CreateAttributes();
        }
        var attributes = documentObject.Attributes
            ?? throw new InvalidOperationException(
                $"Grasshopper did not create attributes for component type {request.ComponentTypeId:D}.");
        attributes.Pivot = new System.Drawing.PointF(request.Pivot.X, request.Pivot.Y);
        document.UndoUtil.RecordAddObjectEvent($"Vino: {request.OperationId}", documentObject);
        if (!document.AddObject(documentObject, update: true))
        {
            throw new InvalidOperationException("Grasshopper rejected the new canvas object.");
        }
        // update:true solves, a solve pumps, and a pump can let Grasshopper retire this document
        // underneath us — every line below touches it again.
        GrasshopperDocumentLiveness.ThrowIfDetached(document, "canvas.create");
        if (request.ObjectId != Guid.Empty && documentObject.InstanceGuid != request.ObjectId)
        {
            document.RemoveObject(documentObject, update: false);
            throw new InvalidOperationException(
                "Grasshopper changed the requested object identity while adding it; the object was removed.");
        }
        var after = ToObjectState(documentObject, BuildParameterOwners(document));
        return Task.FromResult(new CanvasMutationResult(
            request.OperationId,
            Changed: true,
            string.Empty,
            after.Fingerprint,
            new[] { documentObject.InstanceGuid }));
    }

    protected override Task<CanvasMutationResult> ReferenceRhinoObjectsCoreAsync(
        GH_Document document,
        uint rhinoDocumentSerial,
        ReferenceRhinoObjectsRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        RequireFinite(request.Pivot, "Pivot");
        if (request.RhinoObjectIds is null || request.RhinoObjectIds.Count == 0)
        {
            throw new InvalidOperationException("At least one Rhino object id is required.");
        }
        // Resolve the session's PAIRED Rhino document by serial — the same document the Rhino scene
        // adapter uses for rhino_list — never RhinoDoc.ActiveDoc. Referencing by GUID against the active
        // doc would silently validate against the wrong model if the user tabbed to another document.
        var rhinoDoc = global::Rhino.RhinoDoc.FromRuntimeSerialNumber(rhinoDocumentSerial)
            ?? throw new InvalidOperationException(
                $"Paired Rhino document {rhinoDocumentSerial} is not open; cannot reference its objects.");

        // Idempotent re-create: an existing object with this id is returned unchanged.
        if (request.ObjectId != Guid.Empty && document.FindObject(request.ObjectId, true) is { } existing)
        {
            var existingState = ToObjectState(existing, BuildParameterOwners(document));
            return Task.FromResult(new CanvasMutationResult(
                request.OperationId,
                Changed: false,
                existingState.Fingerprint,
                existingState.Fingerprint,
                new[] { request.ObjectId }));
        }

        var (parameter, loaded) = BuildReferenceParameter(request.ParamType, request.RhinoObjectIds, rhinoDoc);
        if (loaded == 0)
        {
            throw new InvalidOperationException(
                "None of the requested Rhino objects could be referenced — check the object ids exist and " +
                $"match the '{request.ParamType}' parameter type.");
        }
        if (request.ObjectId != Guid.Empty)
        {
            parameter.NewInstanceGuid(request.ObjectId);
            if (parameter.InstanceGuid != request.ObjectId)
            {
                throw new InvalidOperationException("Grasshopper did not accept the requested object identity.");
            }
        }
        if (!string.IsNullOrWhiteSpace(request.NickName))
        {
            parameter.NickName = request.NickName.Trim();
        }
        if (parameter.Attributes is null)
        {
            parameter.CreateAttributes();
        }
        var attributes = parameter.Attributes
            ?? throw new InvalidOperationException("Grasshopper did not create attributes for the reference parameter.");
        attributes.Pivot = new System.Drawing.PointF(request.Pivot.X, request.Pivot.Y);
        document.UndoUtil.RecordAddObjectEvent($"Vino: {request.OperationId}", parameter);
        if (!document.AddObject(parameter, update: true))
        {
            throw new InvalidOperationException("Grasshopper rejected the new reference parameter.");
        }
        if (request.ObjectId != Guid.Empty && parameter.InstanceGuid != request.ObjectId)
        {
            document.RemoveObject(parameter, update: false);
            throw new InvalidOperationException(
                "Grasshopper changed the requested object identity while adding it; the object was removed.");
        }
        parameter.ExpireSolution(recompute: false);
        document.NewSolution(expireAllObjects: false);
        GrasshopperDocumentLiveness.ThrowIfDetached(document, "canvas.referenceRhinoObjects");
        global::Grasshopper.Instances.ActiveCanvas?.Invalidate();

        var after = ToObjectState(parameter, BuildParameterOwners(document));
        return Task.FromResult(new CanvasMutationResult(
            request.OperationId,
            Changed: true,
            string.Empty,
            after.Fingerprint,
            new[] { parameter.InstanceGuid }));
    }

    // Creates the typed parameter and appends referenced Rhino geometry as persistent data. Each goo
    // carries the Rhino object's GUID as its ReferenceID and hydrates from the live document via
    // LoadGeometry, so the parameter stays a LIVE reference (edit the Rhino object -> the definition
    // updates) rather than a baked copy. Returns the parameter plus how many objects were referenced.
    private static (IGH_Param Parameter, int Loaded) BuildReferenceParameter(
        string paramType,
        IReadOnlyList<Guid> ids,
        global::Rhino.RhinoDoc rhinoDoc)
    {
        switch ((paramType ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "curve":
            {
                var parameter = new Param_Curve();
                return (parameter, AppendReferences(parameter.PersistentData, ids, rhinoDoc, id => new GH_Curve { ReferenceID = id }));
            }
            case "brep":
            {
                var parameter = new Param_Brep();
                return (parameter, AppendReferences(parameter.PersistentData, ids, rhinoDoc, id => new GH_Brep { ReferenceID = id }));
            }
            case "mesh":
            {
                var parameter = new Param_Mesh();
                return (parameter, AppendReferences(parameter.PersistentData, ids, rhinoDoc, id => new GH_Mesh { ReferenceID = id }));
            }
            case "surface":
            {
                var parameter = new Param_Surface();
                return (parameter, AppendReferences(parameter.PersistentData, ids, rhinoDoc, id => new GH_Surface { ReferenceID = id }));
            }
            case "point":
            {
                var parameter = new Param_Point();
                return (parameter, AppendReferences(parameter.PersistentData, ids, rhinoDoc, id => new GH_Point { ReferenceID = id }));
            }
            case "":
            case "geometry":
            {
                var parameter = new Param_Geometry();
                return (parameter, AppendGenericReferences(parameter, ids, rhinoDoc));
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported reference parameter type '{paramType}'. Use curve|brep|mesh|surface|point|geometry.");
        }
    }

    private static int AppendReferences<T>(
        GH_Structure<T> data,
        IReadOnlyList<Guid> ids,
        global::Rhino.RhinoDoc rhinoDoc,
        Func<Guid, T> makeReference)
        where T : class, IGH_GeometricGoo
    {
        var loaded = 0;
        foreach (var id in ids)
        {
            if (id == Guid.Empty)
            {
                continue;
            }
            var goo = makeReference(id);
            if (goo.LoadGeometry(rhinoDoc))
            {
                data.Append(goo);
                loaded++;
            }
        }
        return loaded;
    }

    // For the generic Geometry parameter, pick the goo type from each Rhino object's actual geometry.
    private static int AppendGenericReferences(
        Param_Geometry parameter,
        IReadOnlyList<Guid> ids,
        global::Rhino.RhinoDoc rhinoDoc)
    {
        var loaded = 0;
        foreach (var id in ids)
        {
            if (id == Guid.Empty)
            {
                continue;
            }
            var rhinoObject = rhinoDoc.Objects.FindId(id);
            IGH_GeometricGoo? goo = rhinoObject?.Geometry switch
            {
                global::Rhino.Geometry.Curve => new GH_Curve { ReferenceID = id },
                global::Rhino.Geometry.Brep => new GH_Brep { ReferenceID = id },
                global::Rhino.Geometry.Mesh => new GH_Mesh { ReferenceID = id },
                global::Rhino.Geometry.Surface => new GH_Surface { ReferenceID = id },
                global::Rhino.Geometry.Point => new GH_Point { ReferenceID = id },
                _ => null
            };
            if (goo is not null && goo.LoadGeometry(rhinoDoc))
            {
                parameter.PersistentData.Append(goo);
                loaded++;
            }
        }
        return loaded;
    }

    protected override Task<ReferencedRhinoIdsResult> ListReferencedRhinoIdsCoreAsync(
        GH_Document document,
        uint rhinoDocumentSerial,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Same pairing rule as referenceRhinoObjects: existence resolves against the session's
        // paired Rhino document serial, never RhinoDoc.ActiveDoc.
        var rhinoDoc = global::Rhino.RhinoDoc.FromRuntimeSerialNumber(rhinoDocumentSerial)
            ?? throw new InvalidOperationException(
                $"Paired Rhino document {rhinoDocumentSerial} is not open; cannot resolve references.");

        var parameters = new List<ReferencedParameterState>();
        var referenceCount = 0;
        var missingCount = 0;
        // Clusters carry their own inner GH_Document that document.Objects does not enumerate —
        // and users routinely wrap internalized references into clusters. The purge guard built on
        // this ledger ("never delete what GH references") cannot afford that blind spot, so the
        // scan recurses (depth-capped; a visited set breaks self-referencing cluster files).
        const int MaxClusterDepth = 8;
        var visitedDocuments = new HashSet<Guid>();
        void ScanDocument(GH_Document scanned, int depth)
        {
            if (!visitedDocuments.Add(scanned.DocumentID))
            {
                return;
            }
            foreach (var documentObject in scanned.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (documentObject is global::Grasshopper.Kernel.Special.GH_Cluster cluster &&
                    depth < MaxClusterDepth &&
                    cluster.Document("") is { } innerDocument)
                {
                    ScanDocument(innerDocument, depth + 1);
                }
                IEnumerable<IGH_Param> candidates = documentObject switch
                {
                    IGH_Component component => component.Params.Input,
                    IGH_Param standalone => new[] { standalone },
                    _ => Array.Empty<IGH_Param>()
                };
                foreach (var parameter in candidates)
                {
                    var ids = CollectPersistentReferenceIds(parameter);
                    if (ids.Count == 0)
                    {
                        continue;
                    }
                    var objects = new List<ReferencedRhinoObjectState>(ids.Count);
                    foreach (var id in ids)
                    {
                        var rhinoObject = rhinoDoc.Objects.FindId(id);
                        string? layer = null;
                        if (rhinoObject is not null)
                        {
                            var layerIndex = rhinoObject.Attributes.LayerIndex;
                            layer = layerIndex >= 0 && layerIndex < rhinoDoc.Layers.Count
                                ? rhinoDoc.Layers[layerIndex].FullPath
                                : null;
                        }
                        else
                        {
                            missingCount++;
                        }
                        objects.Add(new ReferencedRhinoObjectState(id, rhinoObject is not null, layer));
                    }
                    referenceCount += ids.Count;
                    parameters.Add(new ReferencedParameterState(
                        parameter.InstanceGuid,
                        parameter.NickName ?? string.Empty,
                        parameter.GetType().Name,
                        objects));
                }
            }
        }

        ScanDocument(document, depth: 0);

        var canonical = JsonSerializer.Serialize(parameters);
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{document.DocumentID:N}|referencedRhinoIds|{canonical}")))
            .ToLowerInvariant();
        return Task.FromResult(new ReferencedRhinoIdsResult(
            document.DocumentID,
            referenceCount,
            missingCount,
            parameters,
            fingerprint));
    }

    // Persistent data is the source of truth for references: volatile data would double-count goos
    // flowing through wires from upstream reference parameters. IGH_Param exposes no non-generic
    // persistent accessor, so read GH_PersistentParam<T>.PersistentData reflectively and walk it as
    // IGH_Structure. Covers both agent-made reference parameters and user-internalized ("Set One
    // Curve") references, on standalone parameters and component inputs alike.
    private static IReadOnlyList<Guid> CollectPersistentReferenceIds(IGH_Param parameter)
    {
        var property = parameter.GetType().GetProperty("PersistentData");
        if (property?.GetValue(parameter) is not IGH_Structure structure)
        {
            return Array.Empty<Guid>();
        }
        List<Guid>? ids = null;
        foreach (var item in structure.AllData(true))
        {
            if (item is IGH_GeometricGoo { IsReferencedGeometry: true } goo &&
                goo.ReferenceID != Guid.Empty)
            {
                (ids ??= new List<Guid>()).Add(goo.ReferenceID);
            }
        }
        return (IReadOnlyList<Guid>?)ids ?? Array.Empty<Guid>();
    }

    // Panel-only viewport primitive (mirrors the Rhino-scene FocusObjects): selects exactly the
    // requested components — clearing any prior selection — and frames them so the user can see what
    // Vino built. Changes no document content, records no undo, and is absent from the agent's tool
    // schema; a human clicked a chip. Ids the chat referenced that no longer exist are counted as
    // missing rather than failing the whole call.
    protected override Task<CanvasFocusResult> FocusObjectsCoreAsync(
        GH_Document document,
        CanvasFocusRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var requestedIds = (request.ObjectIds ?? Array.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        var targets = new List<IGH_DocumentObject>(requestedIds.Length);
        foreach (var id in requestedIds)
        {
            if (document.FindObject(id, true) is { } found)
            {
                targets.Add(found);
            }
        }

        // Set selection on EVERY object (select the targets, deselect the rest) so the result is a
        // clean "these and only these are highlighted". Attribute-less objects cannot draw selected,
        // so they are simply skipped.
        foreach (var documentObject in document.Objects)
        {
            if (documentObject.Attributes is { } attributes)
            {
                attributes.Selected = targets.Contains(documentObject);
            }
        }

        var skipReason = FrameCanvasOnObjects(document, targets, request.Zoom);

        var fingerprint = HashHex(
            $"canvasFocus|{document.DocumentID:N}|{request.Zoom}|" +
            string.Join(',', targets.Select(item => item.InstanceGuid.ToString("N")).OrderBy(value => value)));
        return Task.FromResult(new CanvasFocusResult(
            targets.Count,
            requestedIds.Length - targets.Count,
            fingerprint,
            Framed: skipReason is null,
            SkipReason: skipReason));
    }

    // Frames the given objects in the live canvas viewport. Best-effort and non-fatal: if the
    // Grasshopper editor is not up, or is showing a different document, the selection set above still
    // stands and simply becomes visible when the user opens/returns to this definition. Viewport and
    // control mutation must run on the canvas UI thread, so it is marshaled when required.
    //
    // Returns null when framing was dispatched, or a stable reason code when it was refused. The
    // preconditions are evaluated HERE, synchronously, precisely so the caller can report them —
    // silently skipping was the whole reason "the chip does nothing" was unexplainable. The apply
    // itself stays asynchronous (BeginInvoke), so a document switch racing in after this check is
    // the one case the reason code cannot cover; every deterministic refusal is named.
    private static string? FrameCanvasOnObjects(
        GH_Document document,
        IReadOnlyList<IGH_DocumentObject> targets,
        bool zoom)
    {
        if (targets.Count == 0)
        {
            return "nothingFound";
        }
        var canvas = global::Grasshopper.Instances.ActiveCanvas;
        if (canvas is null)
        {
            return "editorClosed";
        }
        if (canvas.Document is null || canvas.Document.DocumentID != document.DocumentID)
        {
            return "otherDocumentShown";
        }

        System.Drawing.RectangleF box = System.Drawing.RectangleF.Empty;
        var haveBox = false;
        foreach (var target in targets)
        {
            if (target.Attributes is not { } attributes)
            {
                continue;
            }
            box = haveBox ? System.Drawing.RectangleF.Union(box, attributes.Bounds) : attributes.Bounds;
            haveBox = true;
        }
        if (zoom && !haveBox)
        {
            return "noBounds";
        }

        void Apply()
        {
            try
            {
                // Only frame when the canvas is actually showing THIS document; reframing a view the
                // user is not looking at (a different open GH definition) would be disorienting.
                if (canvas.Document is null || canvas.Document.DocumentID != document.DocumentID)
                {
                    canvas.Refresh();
                    return;
                }
                global::Grasshopper.Instances.DocumentEditor?.Show();
                if (zoom && haveBox)
                {
                    var viewport = canvas.Viewport;
                    var padX = box.Width * 0.15f + 40f;
                    var padY = box.Height * 0.15f + 40f;
                    var frameWidth = box.Width + padX * 2f;
                    var frameHeight = box.Height + padY * 2f;
                    var clientWidth = Math.Max(1, canvas.Width);
                    var clientHeight = Math.Max(1, canvas.Height);
                    // Zoom is drawn pixels per document unit; take the axis-limiting fit and clamp to
                    // the viewport's own bounds so one tiny or huge component cannot blow past them.
                    var fit = Math.Min(clientWidth / frameWidth, clientHeight / frameHeight);
                    viewport.Zoom = Math.Max(
                        global::Grasshopper.GUI.Canvas.GH_Viewport.ZoomMinimum,
                        Math.Min(global::Grasshopper.GUI.Canvas.GH_Viewport.ZoomMaximum, fit));
                    viewport.MidPoint = new System.Drawing.PointF(
                        box.X + box.Width / 2f,
                        box.Y + box.Height / 2f);
                }
                canvas.Refresh();
            }
            catch
            {
                // A view nudge must never surface as an operation failure.
            }
        }

        if (canvas.InvokeRequired)
        {
            canvas.BeginInvoke((Action)Apply);
        }
        else
        {
            Apply();
        }
        return zoom ? null : "zoomNotRequested";
    }

    /// <summary>The output raster's longer side never exceeds this; a flat-colour canvas PNG at
    /// this size stays far under the 8 MiB bridge frame with base64 inflation (~1.37x).</summary>
    private const int CaptureMaxSide = 2400;

    /// <summary>Canvas units of breathing room around the definition's union bounds.</summary>
    private const float CaptureMargin = 40f;

    protected override Task<CanvasCaptureResult> CaptureCanvasImageCoreAsync(
        GH_Document document,
        CanvasCaptureRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        // GenerateHiResImageTile is the renderer Grasshopper's own "Export Hi-Res Image" drives
        // tile by tile, and it draws whatever definition the canvas control currently shows — so
        // the capture must refuse a canvas showing another document: a picture of the wrong
        // definition presented as this one would be worse than no picture. Bridge operations
        // already run on the Rhino UI thread (same threading as the viewport capture).
        var canvas = global::Grasshopper.Instances.ActiveCanvas
            ?? throw new InvalidOperationException(
                "The Grasshopper editor is not open; there is no canvas to render.");
        // PRE-CHECKS ahead of the native renderer: during the only live failure so far the bridge
        // connection died MID-FRAME (EndOfStreamException host-side) instead of returning an
        // error — suspected native crash in GenerateHiResImageTile on a degenerate editor state.
        // Everything checkable is refused cleanly here so a bad state becomes a normal bridge
        // error response, not a dead pipe.
        if (canvas.IsDisposed)
        {
            throw new InvalidOperationException(
                "The Grasshopper canvas control has been disposed (the editor is closing); " +
                "reopen the editor to capture.");
        }
        if (canvas.Document is null || canvas.Document.DocumentID != document.DocumentID)
        {
            throw new InvalidOperationException(
                "The Grasshopper canvas is showing a different document; open this definition to capture it.");
        }

        // Frame the WHOLE definition — every object's canvas bounds plus a margin — so an
        // unattended capture photographs the work, not whatever corner the user last panned to.
        var box = System.Drawing.RectangleF.Empty;
        var haveBox = false;
        var componentCount = 0;
        foreach (var documentObject in document.Objects)
        {
            if (documentObject.Attributes is not { } attributes)
            {
                continue;
            }
            componentCount++;
            box = haveBox ? System.Drawing.RectangleF.Union(box, attributes.Bounds) : attributes.Bounds;
            haveBox = true;
        }
        if (!haveBox)
        {
            // An empty definition still captures honestly: a small blank region around the origin.
            box = new System.Drawing.RectangleF(-200f, -150f, 400f, 300f);
        }
        box.Inflate(CaptureMargin, CaptureMargin);
        // Degenerate union bounds (a NaN/Infinity coordinate from a corrupt component's
        // attributes) would flow into the zoom and the viewport projection as NaN — exactly the
        // kind of degenerate state suspected of killing the native renderer. Refuse cleanly. A
        // zero-size-but-located union is fine: the margin above already gave it real area.
        if (!float.IsFinite(box.X) || !float.IsFinite(box.Y) ||
            !(box.Width > 0f) || !(box.Height > 0f) ||
            !float.IsFinite(box.Width) || !float.IsFinite(box.Height))
        {
            throw new InvalidOperationException(
                $"The definition's union bounds are empty or degenerate ({box.Width}x{box.Height} " +
                $"at {box.X},{box.Y}); there is nothing renderable to capture.");
        }

        // Clamped, not rejected: the capture is layout feedback, not print output. Zoom never
        // exceeds 1:1 (upscaling adds pixels, not information); the viewport's own ZoomMinimum
        // floor means a definition too large to fit at 5% zoom yields a centered partial frame
        // rather than failing.
        var maxWidth = Math.Clamp(request.Width ?? CaptureMaxSide, 64, CaptureMaxSide);
        var maxHeight = Math.Clamp(request.Height ?? CaptureMaxSide, 64, CaptureMaxSide);
        var fit = Math.Min(1f, Math.Min(maxWidth / box.Width, maxHeight / box.Height));
        var zoom = Math.Max(
            global::Grasshopper.GUI.Canvas.GH_Viewport.ZoomMinimum,
            Math.Min(global::Grasshopper.GUI.Canvas.GH_Viewport.ZoomMaximum, fit));
        // A sub-pixel raster is refused, not silently floored to 1px: a 1x1 "capture" of a real
        // definition would be a false success, and a zero/negative computed size means the inputs
        // upstream were degenerate in a way the bounds check did not model.
        var rasterWidth = (int)MathF.Ceiling(box.Width * zoom);
        var rasterHeight = (int)MathF.Ceiling(box.Height * zoom);
        if (rasterWidth < 1 || rasterHeight < 1)
        {
            throw new InvalidOperationException(
                $"The computed capture raster is {rasterWidth}x{rasterHeight} (zoom {zoom} over " +
                $"bounds {box.Width}x{box.Height}); there is nothing renderable to capture.");
        }
        var viewport = new global::Grasshopper.GUI.Canvas.GH_Viewport
        {
            Width = Math.Min(rasterWidth, maxWidth),
            Height = Math.Min(rasterHeight, maxHeight),
            Zoom = zoom,
            MidPoint = new System.Drawing.PointF(box.X + box.Width / 2f, box.Y + box.Height / 2f),
        };
        viewport.ComputeProjection();

        // Any MANAGED failure inside the renderer or the PNG encoder becomes a normal bridge
        // error response instead of whatever killed the pipe during the live gate. A NATIVE
        // access violation cannot be caught here (it tears the process down regardless) — if the
        // live repro shows the crash is native, the fix moves upstream of this call; this wrapper
        // is for everything else.
        System.Drawing.Bitmap? bitmap;
        try
        {
            bitmap = canvas.GenerateHiResImageTile(
                viewport,
                global::Grasshopper.GUI.Canvas.GH_Skin.canvas_back);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Grasshopper's canvas renderer (GenerateHiResImageTile) failed: " +
                $"{exception.Message}", exception);
        }
        if (bitmap is null)
        {
            throw new InvalidOperationException("Grasshopper returned no bitmap for the canvas capture.");
        }
        byte[] bytes;
        int bitmapWidth;
        int bitmapHeight;
        using (bitmap)
        {
            bitmapWidth = bitmap.Width;
            bitmapHeight = bitmap.Height;
            using var stream = new MemoryStream();
            try
            {
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"PNG encoding of the canvas capture failed: {exception.Message}", exception);
            }
            bytes = stream.ToArray();
        }
        return Task.FromResult(new CanvasCaptureResult(
            Convert.ToBase64String(bytes),
            bitmapWidth,
            bitmapHeight,
            componentCount,
            HashHex(
                $"canvasCapture|{document.DocumentID:N}|{bitmapWidth}x{bitmapHeight}|" +
                Convert.ToHexString(SHA256.HashData(bytes)))));
    }

    protected override Task<CanvasMutationResult> DeleteObjectCoreAsync(
        GH_Document document,
        DeleteCanvasObjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
        {
            throw new InvalidOperationException("ObjectId and ExpectedFingerprint are required for deletion.");
        }
        // PRE-WRITE refusal: the target does not exist, nothing was touched — precondition_refused.
        var documentObject = document.FindObject(request.ObjectId, true)
            ?? throw new BridgeProtocolException(
                PreconditionRefusedCode,
                $"Grasshopper object {request.ObjectId:D} was not found.");
        // Delete guards on the STRUCTURE fingerprint: deleting a component the user merely moved
        // or value-tweaked is still their intent; rewiring or renaming it is a real conflict.
        var beforeState = ToObjectState(documentObject, BuildParameterOwners(document));
        var before = beforeState.StructureFingerprint;
        if (!string.Equals(before, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                PreconditionRefusedCode,
                "Canvas object structure changed after the request snapshot. Current structure " +
                $"fingerprint: {before}. Resubmit with this value.");
        }

        document.UndoUtil.RecordRemoveObjectEvent($"Vino: {request.OperationId}", documentObject);
        if (!document.RemoveObject(documentObject, true))
        {
            throw new InvalidOperationException($"Grasshopper could not remove object {request.ObjectId:D}.");
        }
        // Same hazard as canvas.create, on the cleanup path: the update flag solves. This is the
        // op a batch delete runs dozens of times in a row.
        GrasshopperDocumentLiveness.ThrowIfDetached(document, "canvas.delete");
        return Task.FromResult(new CanvasMutationResult(
            request.OperationId,
            Changed: true,
            before,
            string.Empty,
            new[] { request.ObjectId }));
    }

    protected override async Task<CanvasMutationResult> MoveObjectsCoreAsync(
        GH_Document document,
        MoveCanvasObjectsRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        ArgumentNullException.ThrowIfNull(request.Pivots);
        ArgumentNullException.ThrowIfNull(request.ExpectedFingerprints);
        if (request.Pivots.Count == 0)
        {
            throw new InvalidOperationException("At least one object pivot is required.");
        }
        if (request.Pivots.Keys.Any(id => id == Guid.Empty) ||
            request.Pivots.Count != request.ExpectedFingerprints.Count ||
            request.Pivots.Keys.Any(id => !request.ExpectedFingerprints.ContainsKey(id)) ||
            request.ExpectedFingerprints.Keys.Any(id => !request.Pivots.ContainsKey(id)))
        {
            throw new InvalidOperationException(
                "Pivots and ExpectedFingerprints must contain the same non-empty object IDs.");
        }

        var beforeSnapshot = await CaptureSnapshotCoreAsync(document, cancellationToken).ConfigureAwait(false);
        // Resolve and fingerprint the complete batch before recording undo or changing a pivot.
        // A stale final item therefore cannot leave the earlier items partially moved.
        var prepared = request.Pivots
            .Select(pair =>
            {
                RequireFinite(pair.Value, $"Pivot for {pair.Key:D}");
                // PRE-WRITE refusal: the whole batch is resolved before any pivot changes, so a
                // missing object means nothing was touched — precondition_refused.
                var documentObject = document.FindObject(pair.Key, true)
                    ?? throw new BridgeProtocolException(
                        PreconditionRefusedCode,
                        $"Grasshopper object {pair.Key:D} was not found.");
                var state = ToObjectState(documentObject, BuildParameterOwners(document));
                var expected = request.ExpectedFingerprints[pair.Key];
                // Moves guard on the LAYOUT fingerprint only: a concurrent value or wiring edit
                // does not conflict with repositioning the component.
                if (string.IsNullOrWhiteSpace(expected) ||
                    !string.Equals(state.LayoutFingerprint, expected, StringComparison.Ordinal))
                {
                    throw new BridgeProtocolException(
                        PreconditionRefusedCode,
                        $"Canvas object {pair.Key:D} layout changed after the request snapshot. " +
                        $"Current layout fingerprint: {state.LayoutFingerprint}. Resubmit with this value.");
                }
                return new PreparedMove(
                    pair.Key,
                    documentObject,
                    documentObject.Attributes.Pivot,
                    new System.Drawing.PointF(pair.Value.X, pair.Value.Y));
            })
            .ToArray();
        var changes = prepared
            .Where(item => item.DocumentObject.Attributes.Pivot != item.Pivot)
            .ToArray();
        if (changes.Length > 0)
        {
            document.UndoUtil.RecordPivotEvent(
                $"Vino: {request.OperationId}",
                changes.Select(item => item.DocumentObject).ToArray());
            try
            {
                foreach (var change in changes)
                {
                    change.DocumentObject.Attributes.Pivot = change.Pivot;
                    // Setting Pivot alone moves the attribute origin but does not re-flow the
                    // object's layout — the canvas keeps drawing it at the old spot until something
                    // forces a re-layout (which is why a manual nudge "fixed" it before). Expire the
                    // layout now so the new position takes effect without user intervention.
                    change.DocumentObject.Attributes.ExpireLayout();
                }
            }
            catch (Exception mutationFailure)
            {
                try
                {
                    foreach (var change in changes)
                    {
                        change.DocumentObject.Attributes.Pivot = change.OriginalPivot;
                    }
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(
                        "Canvas move failed and its in-place rollback also failed; use Grasshopper Undo.",
                        mutationFailure,
                        rollbackFailure);
                }
                throw;
            }
            // Force the visible canvas to repaint the moved objects at their new pivots. Control
            // .Invalidate() is safe to call off the UI thread (it just marshals a repaint request),
            // and a null active canvas (headless) simply means there is nothing on screen to redraw.
            global::Grasshopper.Instances.ActiveCanvas?.Invalidate();
        }

        var afterSnapshot = await CaptureSnapshotCoreAsync(document, cancellationToken).ConfigureAwait(false);
        return new CanvasMutationResult(
            request.OperationId,
            changes.Length > 0,
            beforeSnapshot.DocumentFingerprint,
            afterSnapshot.DocumentFingerprint,
            changes.Select(item => item.ObjectId).ToArray());
    }

    protected override Task<CanvasMutationResult> SetNumberSliderValueCoreAsync(
        GH_Document document,
        SetNumberSliderValueRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedFingerprint) ||
            request.Minimum >= request.Maximum || request.Value < request.Minimum ||
            request.Value > request.Maximum || request.DecimalPlaces is < 0 or > 12 ||
            !IsRepresentableAtPrecision(request.Value, request.DecimalPlaces) ||
            !IsRepresentableAtPrecision(request.Minimum, request.DecimalPlaces) ||
            !IsRepresentableAtPrecision(request.Maximum, request.DecimalPlaces))
        {
            throw new InvalidOperationException("The Number Slider value request is invalid.");
        }

        // PRE-WRITE refusal: wrong target type, nothing was touched — precondition_refused.
        var slider = document.FindObject(request.ObjectId, true) as GH_NumberSlider
            ?? throw new BridgeProtocolException(
                PreconditionRefusedCode,
                $"Grasshopper object {request.ObjectId:D} is not a Number Slider.");
        var beforeState = ToObjectState(slider, BuildParameterOwners(document));
        // Value writes guard on the VALUE fingerprint only: moving the slider around the canvas
        // does not conflict with setting its value.
        if (!string.Equals(beforeState.ValueFingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                PreconditionRefusedCode,
                "The Number Slider value changed after the request snapshot. Current value " +
                $"fingerprint: {beforeState.ValueFingerprint}. Resubmit with this value.");
        }

        var oldMinimum = slider.Slider.Minimum;
        var oldMaximum = slider.Slider.Maximum;
        var oldValue = slider.CurrentValue;
        var oldDecimalPlaces = slider.Slider.DecimalPlaces;
        if (oldMinimum == request.Minimum && oldMaximum == request.Maximum &&
            oldValue == request.Value && oldDecimalPlaces == request.DecimalPlaces)
        {
            return Task.FromResult(new CanvasMutationResult(
                request.OperationId,
                Changed: false,
                beforeState.Fingerprint,
                beforeState.Fingerprint,
                [request.ObjectId]));
        }

        document.UndoUtil.RecordGenericObjectEvent($"Vino: {request.OperationId}", slider);
        try
        {
            SetSliderRangeAndValue(
                slider,
                request.Minimum,
                request.Maximum,
                request.Value,
                request.DecimalPlaces);
            slider.ExpireSolution(true);
            // recompute:true schedules a solve, which pumps — and the next four lines read the
            // slider back out of the document. The 5 guards added by the document-open crash fix
            // covered NewSolution only, so this path stayed open.
            GrasshopperDocumentLiveness.ThrowIfDetached(document, "canvas.setSliderState");
            if (slider.Slider.Minimum != request.Minimum ||
                slider.Slider.Maximum != request.Maximum ||
                slider.CurrentValue != request.Value ||
                slider.Slider.DecimalPlaces != request.DecimalPlaces)
            {
                throw new InvalidOperationException(
                    "Grasshopper did not apply the Number Slider state exactly as requested.");
            }
        }
        catch (Exception mutationFailure)
        {
            try
            {
                SetSliderRangeAndValue(slider, oldMinimum, oldMaximum, oldValue, oldDecimalPlaces);
                slider.ExpireSolution(true);
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    "Number Slider mutation failed and its in-place rollback also failed; use Grasshopper Undo.",
                    mutationFailure,
                    rollbackFailure);
            }
            throw;
        }

        var afterState = ToObjectState(slider, BuildParameterOwners(document));
        return Task.FromResult(new CanvasMutationResult(
            request.OperationId,
            Changed: true,
            beforeState.Fingerprint,
            afterState.Fingerprint,
            [request.ObjectId]));
    }

    protected override async Task<CanvasMutationResult> SetWireCoreAsync(
        GH_Document document,
        SetWireRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        if (request.Wire.SourceObjectId == Guid.Empty ||
            request.Wire.SourceParameterId == Guid.Empty ||
            request.Wire.TargetObjectId == Guid.Empty ||
            request.Wire.TargetParameterId == Guid.Empty)
        {
            throw new InvalidOperationException("Wire object and parameter IDs are required.");
        }
        var beforeSnapshot = await CaptureSnapshotCoreAsync(document, cancellationToken).ConfigureAwait(false);
        var source = ResolveParameter(
            document,
            request.Wire.SourceObjectId,
            request.Wire.SourceParameterId,
            source: true);
        var target = ResolveParameter(
            document,
            request.Wire.TargetObjectId,
            request.Wire.TargetParameterId,
            source: false);
        var connected = target.Sources.Any(candidate => candidate.InstanceGuid == source.InstanceGuid);
        var changed = false;

        if (request.Action == WireAction.Connect && !connected)
        {
            if (request.RejectCycles && WouldCreateCycle(
                    document,
                    request.Wire.SourceObjectId,
                    request.Wire.TargetObjectId))
            {
                throw new InvalidOperationException("Wire would create a Grasshopper dependency cycle.");
            }

            document.UndoUtil.RecordWireEvent($"Vino: {request.OperationId}", target);
            target.AddSource(source);
            changed = true;
        }
        else if (request.Action == WireAction.Disconnect && connected)
        {
            document.UndoUtil.RecordWireEvent($"Vino: {request.OperationId}", target);
            target.RemoveSource(source);
            changed = true;
        }

        if (changed)
        {
            if (request.DeferSolve)
            {
                // Batched rewire (server-injected): expire the consumer so nothing ships stale, and
                // let the batch's LAST solve-carrying op run the single document solve. The executor
                // guarantees one follows — deferSolve is never true on the batch's final wire.
                target.ExpireSolution(recompute: false);
            }
            else
            {
                // A wire edit with the global solver off expires the target and recomputes nothing,
                // so the downstream reads back empty — the same silent-data-loss the python paths hit.
                // The user asked for this connection to take effect; that needs a live solver.
                GH_Document.EnableSolutions = true;
                document.NewSolution(false);
                GrasshopperDocumentLiveness.ThrowIfDetached(document, "canvas.setWire");
            }
        }

        var afterSnapshot = await CaptureSnapshotCoreAsync(document, cancellationToken).ConfigureAwait(false);
        return new CanvasMutationResult(
            request.OperationId,
            changed,
            beforeSnapshot.DocumentFingerprint,
            afterSnapshot.DocumentFingerprint,
            new[] { request.Wire.SourceObjectId, request.Wire.TargetObjectId });
    }

    protected override Task<CanvasMutationResult> SetGroupCoreAsync(
        GH_Document document,
        SetGroupRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        ArgumentNullException.ThrowIfNull(request.ObjectIds);
        if (request.GroupId == Guid.Empty)
        {
            throw new InvalidOperationException("GroupId is required so group identity can be verified.");
        }
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Group name is required.");
        }
        var objectIds = request.ObjectIds.Distinct().ToArray();
        if (objectIds.Any(id => id == Guid.Empty || id == request.GroupId))
        {
            throw new InvalidOperationException("A group cannot contain an empty ID or itself.");
        }
        foreach (var objectId in objectIds)
        {
            _ = document.FindObject(objectId, true)
                ?? throw new KeyNotFoundException($"Grasshopper object {objectId:D} was not found.");
        }

        var objectAtGroupId = document.FindObject(request.GroupId, true);
        if (objectAtGroupId is not null && objectAtGroupId is not GH_Group)
        {
            throw new InvalidOperationException(
                $"Canvas object {request.GroupId:D} already exists and is not a group.");
        }
        var group = objectAtGroupId as GH_Group;
        var before = group is null ? string.Empty : GroupFingerprint(group);
        if (group is null)
        {
            group = new GH_Group();
            group.NewInstanceGuid(request.GroupId);
            if (group.InstanceGuid != request.GroupId)
            {
                throw new InvalidOperationException("Grasshopper did not accept the requested group identity.");
            }
            document.UndoUtil.RecordAddObjectEvent($"Vino: {request.OperationId}", group);
            if (!document.AddObject(group, update: false) || group.InstanceGuid != request.GroupId)
            {
                if (document.FindObject(group.InstanceGuid, true) is not null)
                {
                    document.RemoveObject(group, update: false);
                }
                throw new InvalidOperationException("Grasshopper could not create the requested group safely.");
            }
        }
        else
        {
            document.UndoUtil.RecordGenericObjectEvent($"Vino: {request.OperationId}", group);
        }

        group.NickName = request.Name;
        group.Colour = System.Drawing.Color.FromArgb(request.ArgbColor);
        foreach (var objectId in group.ObjectIDs.ToArray())
        {
            group.RemoveObject(objectId);
        }
        foreach (var objectId in objectIds)
        {
            group.AddObject(objectId);
        }
        group.Attributes.ExpireLayout();
        var after = GroupFingerprint(group);
        return Task.FromResult(new CanvasMutationResult(
            request.OperationId,
            !string.Equals(before, after, StringComparison.Ordinal),
            before,
            after,
            objectIds.Append(group.InstanceGuid).ToArray()));
    }

    private static CanvasObjectState ToObjectState(
        IGH_DocumentObject documentObject,
        IReadOnlyDictionary<IGH_Param, Guid> parameterOwners)
    {
        var pivot = documentObject.Attributes.Pivot;
        var bounds = documentObject.Attributes.Bounds;
        // LIVE order — as Grasshopper presents the sockets, top to bottom. This is the order the
        // socket index means something in, the order committed.outputs already uses, and the order
        // python.setSchema matches declarations against by POSITION. Sorting the DTO by ParameterId
        // (a random GUID) made snapshot/committed.sockets disagree with all of those, so a model
        // that read committed.sockets and re-declared in that order silently swapped socket
        // names/types. The fingerprint below is computed from a GUID-sorted COPY, so its value is
        // unchanged and no CAS pin churns.
        var inputs = ParametersFor(documentObject, CanvasParameterDirection.Input)
            .Select(parameter => ToParameterState(
                documentObject.InstanceGuid,
                parameter,
                CanvasParameterDirection.Input,
                parameterOwners))
            .ToArray();
        var outputs = ParametersFor(documentObject, CanvasParameterDirection.Output)
            .Select(parameter => ToParameterState(
                documentObject.InstanceGuid,
                parameter,
                CanvasParameterDirection.Output,
                parameterOwners))
            .ToArray();
        // Order-independent: sort by ParameterId ONLY for the hash so re-emission order cannot
        // change the structure fingerprint. Matches the value this produced before the DTO order
        // changed, so existing CAS pins keep matching.
        var socketsForHash = inputs.OrderBy(parameter => parameter.ParameterId)
            .Concat(outputs.OrderBy(parameter => parameter.ParameterId));
        var sockets = string.Join('|', socketsForHash.Select(parameter =>
            $"{parameter.Direction}:{parameter.ParameterId:N}:{parameter.Name}:{parameter.NickName}:" +
            $"{parameter.TypeName}:{parameter.TypeHint}:{parameter.Access}:{parameter.Optional}:" +
            string.Join(',', parameter.CurrentSources.Select(source =>
                $"{source.OwnerObjectId:N}/{source.ParameterId:N}"))));
        var valueJson = DescribeInputValue(documentObject);
        // Per-domain hashes: layout (position/size), value (slider state), structure (identity,
        // nickname, sockets, incoming wires). Independent user edits must not invalidate each
        // other's optimistic-concurrency expectations — moving a component cannot block a value
        // write. The whole-object fingerprint combines all three and keeps driving the document
        // revision, so any edit still bumps the snapshot.
        var structureSource = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{documentObject.InstanceGuid:N}|{documentObject.ComponentGuid:N}|{documentObject.NickName}|{sockets}");
        var layoutSource = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{documentObject.InstanceGuid:N}|{pivot.X:R}|{pivot.Y:R}|{bounds.Width:R}|{bounds.Height:R}");
        var structureFingerprint = HashHex(structureSource);
        var layoutFingerprint = HashHex(layoutSource);
        var valueFingerprint = valueJson is null
            ? null
            : HashHex($"{documentObject.InstanceGuid:N}|{valueJson}");
        var fingerprint = HashHex($"{structureFingerprint}|{layoutFingerprint}|{valueFingerprint}");
        return new CanvasObjectState(
            documentObject.InstanceGuid,
            documentObject.ComponentGuid,
            documentObject.NickName,
            new CanvasPoint(pivot.X, pivot.Y),
            new CanvasSize(bounds.Width, bounds.Height),
            fingerprint)
        {
            Inputs = inputs,
            Outputs = outputs,
            ValueJson = valueJson,
            StructureFingerprint = structureFingerprint,
            LayoutFingerprint = layoutFingerprint,
            ValueFingerprint = valueFingerprint,
            // The bounds top-left, reported so the broker's deterministic auto-placement can build a
            // true collision rectangle for panels and other pivot-off-center types. Intentionally kept
            // OUT of every fingerprint above (layoutSource hashes size, never origin), so pure GH
            // re-layout jitter of X/Y never bumps the document revision.
            BoundsOrigin = new CanvasPoint(bounds.X, bounds.Y),
        };
    }

    /// <summary>
    /// Sets a non-slider input primitive's user-settable state. Same discipline as the slider path:
    /// a wrong target type or a stale value fingerprint refuses BEFORE any mutation, the change is
    /// recorded for Grasshopper Undo, and the write is read back and rolled back in place if it did
    /// not take.
    /// </summary>
    protected override Task<CanvasMutationResult> SetInputValueCoreAsync(
        GH_Document document,
        SetInputValueRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
        {
            throw new InvalidOperationException("The input value request is invalid.");
        }

        var documentObject = document.FindObject(request.ObjectId, true)
            ?? throw new BridgeProtocolException(
                PreconditionRefusedCode,
                $"Grasshopper object {request.ObjectId:D} was not found.");
        // PRE-WRITE refusal: the declared kind must match the live object, so a payload aimed at the
        // wrong component can never half-apply.
        var kindMatches = request.Kind switch
        {
            InputValueKind.ValueList => documentObject is GH_ValueList,
            InputValueKind.BooleanToggle => documentObject is GH_BooleanToggle,
            InputValueKind.Panel => documentObject is GH_Panel,
            InputValueKind.Button => documentObject is GH_ButtonObject,
            _ => false,
        };
        if (!kindMatches)
        {
            throw new BridgeProtocolException(
                PreconditionRefusedCode,
                $"Grasshopper object {request.ObjectId:D} is not a {request.Kind}; it is a " +
                $"{documentObject.GetType().Name}.");
        }

        var owners = BuildParameterOwners(document);
        var beforeState = ToObjectState(documentObject, owners);
        // Value writes guard on the VALUE fingerprint only: moving the object around the canvas does
        // not conflict with setting its value.
        if (!string.Equals(beforeState.ValueFingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                PreconditionRefusedCode,
                $"The {request.Kind} value changed after the request snapshot. Current value " +
                $"fingerprint: {beforeState.ValueFingerprint}. Resubmit with this value.");
        }

        var before = DescribeInputValue(documentObject);
        if (string.Equals(before, ProjectRequestedValue(documentObject, request), StringComparison.Ordinal))
        {
            return Task.FromResult(new CanvasMutationResult(
                request.OperationId,
                Changed: false,
                beforeState.Fingerprint,
                beforeState.Fingerprint,
                [request.ObjectId]));
        }

        document.UndoUtil.RecordGenericObjectEvent($"Vino: {request.OperationId}", documentObject);
        var restore = CaptureInputValue(documentObject);
        try
        {
            ApplyInputValue(documentObject, request);
            documentObject.ExpireSolution(true);
            // recompute:true pumps; the read-back below touches the document again.
            GrasshopperDocumentLiveness.ThrowIfDetached(document, "canvas.setInputValue");
            var applied = DescribeInputValue(documentObject);
            var expected = ProjectRequestedValue(documentObject, request);
            if (!string.Equals(applied, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Grasshopper did not apply the {request.Kind} value exactly as requested.");
            }
        }
        catch (Exception mutationFailure)
        {
            try
            {
                restore();
                documentObject.ExpireSolution(true);
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    $"{request.Kind} mutation failed and its in-place rollback also failed; use Grasshopper Undo.",
                    mutationFailure,
                    rollbackFailure);
            }
            throw;
        }

        var afterState = ToObjectState(documentObject, BuildParameterOwners(document));
        return Task.FromResult(new CanvasMutationResult(
            request.OperationId,
            Changed: true,
            beforeState.Fingerprint,
            afterState.Fingerprint,
            [request.ObjectId]));
    }

    /// <summary>Captures enough state to put the object back exactly as it was.</summary>
    private static Action CaptureInputValue(IGH_DocumentObject documentObject)
    {
        switch (documentObject)
        {
            case GH_ValueList valueList:
            {
                var mode = valueList.ListMode;
                var items = valueList.ListItems
                    .Select(item => (item.Name, item.Expression, item.Selected))
                    .ToArray();
                return () =>
                {
                    valueList.ListMode = mode;
                    valueList.ListItems.Clear();
                    foreach (var (name, expression, selected) in items)
                    {
                        valueList.ListItems.Add(new GH_ValueListItem(name, expression) { Selected = selected });
                    }
                };
            }
            case GH_BooleanToggle toggle:
            {
                var value = toggle.Value;
                return () => toggle.Value = value;
            }
            case GH_Panel panel:
            {
                var text = panel.UserText;
                return () => panel.UserText = text;
            }
            case GH_ButtonObject button:
            {
                var normal = button.ExpressionNormal;
                var pressed = button.ExpressionPressed;
                return () =>
                {
                    button.ExpressionNormal = normal;
                    button.ExpressionPressed = pressed;
                };
            }
            default:
                return static () => { };
        }
    }

    private static void ApplyInputValue(IGH_DocumentObject documentObject, SetInputValueRequest request)
    {
        switch (documentObject)
        {
            case GH_ValueList valueList:
                if (request.Items is { Count: > 0 })
                {
                    valueList.ListItems.Clear();
                    foreach (var entry in request.Items)
                    {
                        valueList.ListItems.Add(new GH_ValueListItem(entry.Name, entry.Expression));
                    }
                }
                if (valueList.ListItems.Count > 0)
                {
                    // Exactly one selection, always: an index picks it, otherwise the declared
                    // Selected flags do, otherwise the first item. A Value List with nothing selected
                    // emits nothing, which is the silent-empty state this change exists to end.
                    valueList.SelectItem(ResolveSelectedIndex(request, valueList.ListItems.Count));
                }
                break;
            case GH_BooleanToggle toggle:
                toggle.Value = request.Toggle ?? false;
                break;
            case GH_Panel panel:
                panel.UserText = request.Text ?? string.Empty;
                break;
            case GH_ButtonObject button:
                if (request.ExpressionNormal is not null)
                {
                    button.ExpressionNormal = request.ExpressionNormal;
                }
                if (request.ExpressionPressed is not null)
                {
                    button.ExpressionPressed = request.ExpressionPressed;
                }
                break;
        }
    }

    /// <summary>
    /// The single selected index a Value List write resolves to: an explicit SelectedIndex, else the
    /// first declared Selected entry, else 0. Always clamped into range.
    /// </summary>
    internal static int ResolveSelectedIndex(SetInputValueRequest request, int count)
    {
        if (count <= 0)
        {
            return 0;
        }
        var index = request.SelectedIndex ?? -1;
        if (index < 0 && request.Items is { Count: > 0 })
        {
            for (var position = 0; position < request.Items.Count; position++)
            {
                if (request.Items[position].Selected)
                {
                    index = position;
                    break;
                }
            }
        }
        return Math.Clamp(index < 0 ? 0 : index, 0, count - 1);
    }

    /// <summary>
    /// What <see cref="DescribeInputValue"/> would report if the request applied cleanly — the
    /// read-back target, so the verification compares exactly the fields the request controls.
    /// </summary>
    private static string? ProjectRequestedValue(
        IGH_DocumentObject documentObject,
        SetInputValueRequest request) => documentObject switch
    {
        GH_ValueList valueList => JsonSerializer.Serialize(new
        {
            kind = "valueList",
            listMode = valueList.ListMode.ToString(),
            items = ProjectValueListItems(valueList, request),
        }),
        GH_BooleanToggle => JsonSerializer.Serialize(new
        {
            kind = "booleanToggle",
            value = request.Toggle ?? false,
        }),
        GH_ButtonObject button => JsonSerializer.Serialize(new
        {
            kind = "button",
            expressionNormal = request.ExpressionNormal ?? button.ExpressionNormal,
            expressionPressed = request.ExpressionPressed ?? button.ExpressionPressed,
        }),
        GH_Panel panel => JsonSerializer.Serialize(new
        {
            kind = "panel",
            text = request.Text ?? string.Empty,
            streamed = panel.SourceCount > 0,
        }),
        _ => null,
    };

    private static object[] ProjectValueListItems(GH_ValueList valueList, SetInputValueRequest request)
    {
        var entries = request.Items is { Count: > 0 }
            ? request.Items.Select(entry => (entry.Name, entry.Expression)).ToArray()
            : valueList.ListItems.Select(item => (item.Name, item.Expression)).ToArray();
        if (entries.Length == 0)
        {
            return Array.Empty<object>();
        }
        var index = ResolveSelectedIndex(request, entries.Length);
        return entries
            .Select((entry, position) => (object)new
            {
                name = entry.Name,
                expression = entry.Expression,
                selected = position == index,
            })
            .ToArray();
    }

    /// <summary>
    /// The user-settable state of an input primitive, as JSON, or null for anything else. This is
    /// what gives a component a <c>valueFingerprint</c> — and therefore an optimistic-concurrency
    /// guard on value writes and a way for the model to READ the value at all.
    ///
    /// <para>
    /// Only the Number Slider used to be described here. Everything else on the canvas that a person
    /// sets by hand — a Value List's items, a Boolean Toggle, a Panel's text — was invisible: the
    /// model could not read it and had no way to write it, so the user did it by hand. Measured in
    /// the 07-21..08-26 corpus as the largest tool gap of its class.
    /// </para>
    /// </summary>
    internal static string? DescribeInputValue(IGH_DocumentObject documentObject) => documentObject switch
    {
        GH_NumberSlider slider => JsonSerializer.Serialize(new
        {
            kind = "numberSlider",
            value = slider.CurrentValue,
            minimum = slider.Slider.Minimum,
            maximum = slider.Slider.Maximum,
            decimalPlaces = slider.Slider.DecimalPlaces,
        }),
        GH_ValueList valueList => JsonSerializer.Serialize(new
        {
            kind = "valueList",
            listMode = valueList.ListMode.ToString(),
            items = valueList.ListItems.Select(item => new
            {
                name = item.Name,
                expression = item.Expression,
                selected = item.Selected,
            }).ToArray(),
        }),
        GH_BooleanToggle toggle => JsonSerializer.Serialize(new
        {
            kind = "booleanToggle",
            value = toggle.Value,
        }),
        // A Button is momentary: its persistent state is what it reports when NOT pressed, so the
        // fingerprint stays stable across presses and a press never looks like a value conflict.
        GH_ButtonObject button => JsonSerializer.Serialize(new
        {
            kind = "button",
            expressionNormal = button.ExpressionNormal,
            expressionPressed = button.ExpressionPressed,
        }),
        GH_Panel panel => JsonSerializer.Serialize(new
        {
            kind = "panel",
            // A panel wired to an upstream source displays that data instead of its own text; only
            // the user-typed text is settable, so only that is reported as the value.
            text = panel.UserText,
            streamed = panel.SourceCount > 0,
        }),
        _ => null,
    };

    private static string HashHex(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    private static void SetSliderRangeAndValue(
        GH_NumberSlider slider,
        decimal minimum,
        decimal maximum,
        decimal value,
        int decimalPlaces)
    {
        slider.Slider.Minimum = Math.Min(slider.Slider.Minimum, Math.Min(minimum, value));
        slider.Slider.Maximum = Math.Max(slider.Slider.Maximum, Math.Max(maximum, value));
        slider.Slider.Minimum = minimum;
        slider.Slider.Maximum = maximum;
        slider.Slider.DecimalPlaces = decimalPlaces;
        slider.SetSliderValue(value);
    }

    private static bool IsRepresentableAtPrecision(decimal value, int decimalPlaces) =>
        decimal.Round(value, decimalPlaces) == value;

    private const int MaximumSampleValuesPerParameter = 5;
    private const int MaximumSampleValueCharacters = 200;

    private static CanvasOutputParameterInspection InspectOutputParameter(
        IGH_Param parameter,
        bool includeMassProperties,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        var samples = new List<string>(MaximumSampleValuesPerParameter);
        Rhino.Geometry.BoundingBox? bounds = null;
        var branchCount = parameter.VolatileData.PathCount;
        bool? closedAll = null;
        double? areaSum = null;
        double? volumeSum = null;
        foreach (var goo in parameter.VolatileData.AllData(true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            if (goo is null)
            {
                typeNames.Add("null");
                continue;
            }

            typeNames.Add(goo.GetType().FullName ?? goo.GetType().Name);
            if (samples.Count < MaximumSampleValuesPerParameter)
            {
                var text = goo.ToString() ?? string.Empty;
                samples.Add(text.Length <= MaximumSampleValueCharacters
                    ? text
                    : text[..MaximumSampleValueCharacters]);
            }
            if (goo.ScriptVariable() is not Rhino.Geometry.GeometryBase geometry)
            {
                continue;
            }
            var candidate = geometry.GetBoundingBox(accurate: true);
            if (!candidate.IsValid)
            {
                continue;
            }
            if (bounds is null)
            {
                bounds = candidate;
            }
            else
            {
                var union = bounds.Value;
                union.Union(candidate);
                bounds = union;
            }

            // Closed-ness (curve/Brep/mesh): all-closed across the output, null when nothing has a
            // closed notion. Cheap (a property/flag read), so always computed. Area/volume below are
            // AreaMassProperties/VolumeMassProperties integrations — expensive on dense Breps/meshes —
            // so they are gated behind includeMassProperties and stay null on the common Verify path.
            var isClosed = geometry switch
            {
                Rhino.Geometry.Curve curve => (bool?)curve.IsClosed,
                Rhino.Geometry.Brep brep => brep.IsSolid,
                Rhino.Geometry.Extrusion extrusion => extrusion.IsSolid,
                Rhino.Geometry.Mesh mesh => mesh.IsClosed,
                _ => null
            };
            if (isClosed is { } closedValue)
            {
                closedAll = (closedAll ?? true) && closedValue;
            }
            if (!includeMassProperties)
            {
                continue;
            }
            var area = geometry switch
            {
                Rhino.Geometry.Brep brep => Rhino.Geometry.AreaMassProperties.Compute(brep)?.Area,
                Rhino.Geometry.Surface surface => Rhino.Geometry.AreaMassProperties.Compute(surface)?.Area,
                Rhino.Geometry.Mesh mesh => Rhino.Geometry.AreaMassProperties.Compute(mesh)?.Area,
                Rhino.Geometry.Curve curve when curve.IsClosed && curve.IsPlanar() =>
                    Rhino.Geometry.AreaMassProperties.Compute(curve)?.Area,
                _ => null
            };
            if (area is { } areaValue)
            {
                areaSum = (areaSum ?? 0) + areaValue;
            }
            var volume = geometry switch
            {
                Rhino.Geometry.Brep { IsSolid: true } brep => Rhino.Geometry.VolumeMassProperties.Compute(brep)?.Volume,
                Rhino.Geometry.Extrusion { IsSolid: true } extrusion => Rhino.Geometry.VolumeMassProperties.Compute(extrusion.ToBrep())?.Volume,
                Rhino.Geometry.Mesh { IsClosed: true } mesh => Rhino.Geometry.VolumeMassProperties.Compute(mesh)?.Volume,
                _ => null
            };
            if (volume is { } volumeValue)
            {
                volumeSum = (volumeSum ?? 0) + volumeValue;
            }
        }

        return new CanvasOutputParameterInspection(
            parameter.InstanceGuid,
            parameter.Name ?? string.Empty,
            parameter.NickName ?? string.Empty,
            count,
            typeNames.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            bounds is { } value ? ToCanvasBounds(value) : null,
            samples,
            branchCount,
            closedAll,
            areaSum,
            volumeSum);
    }

    private static CanvasBoundingBox3d ToCanvasBounds(Rhino.Geometry.BoundingBox bounds) =>
        new(
            new CanvasPoint3d(bounds.Min.X, bounds.Min.Y, bounds.Min.Z),
            new CanvasPoint3d(bounds.Max.X, bounds.Max.Y, bounds.Max.Z),
            new CanvasPoint3d(
                bounds.Max.X - bounds.Min.X,
                bounds.Max.Y - bounds.Min.Y,
                bounds.Max.Z - bounds.Min.Z));

    private static IReadOnlyDictionary<IGH_Param, Guid> BuildParameterOwners(GH_Document document) =>
        EnumerateParameters(document)
            .ToDictionary(
                pair => pair.Parameter,
                pair => pair.OwnerId,
                ParameterReferenceComparer.Instance);

    private static IEnumerable<IGH_Param> ParametersFor(
        IGH_DocumentObject documentObject,
        CanvasParameterDirection direction) => documentObject switch
        {
            IGH_Component component when direction == CanvasParameterDirection.Input =>
                component.Params.Input,
            IGH_Component component => component.Params.Output,
            IGH_Param standalone => new[] { standalone },
            _ => Enumerable.Empty<IGH_Param>(),
        };

    private static CanvasParameterState ToParameterState(
        Guid ownerObjectId,
        IGH_Param parameter,
        CanvasParameterDirection direction,
        IReadOnlyDictionary<IGH_Param, Guid> parameterOwners)
    {
        var sources = parameter.Sources
            .Select(source => new CanvasParameterEndpoint(
                parameterOwners.TryGetValue(source, out var sourceOwner)
                    ? sourceOwner
                    : source.InstanceGuid,
                source.InstanceGuid))
            .OrderBy(source => source.OwnerObjectId)
            .ThenBy(source => source.ParameterId)
            .ToArray();
        return new CanvasParameterState(
            ownerObjectId,
            parameter.InstanceGuid,
            parameter.Name ?? string.Empty,
            parameter.NickName ?? string.Empty,
            direction,
            parameter.TypeName ?? parameter.GetType().FullName ?? parameter.GetType().Name,
            ReadTypeHint(parameter),
            parameter.Access switch
            {
                GH_ParamAccess.list => CanvasParameterAccess.List,
                GH_ParamAccess.tree => CanvasParameterAccess.Tree,
                _ => CanvasParameterAccess.Item,
            },
            parameter.Optional,
            sources);
    }

    private static string? ReadTypeHint(IGH_Param parameter)
    {
        try
        {
            var scriptParameter = parameter.GetType().GetInterfaces().FirstOrDefault(type =>
                string.Equals(
                    type.FullName,
                    "RhinoCodePlatform.GH.IScriptParameter",
                    StringComparison.Ordinal));
            if (scriptParameter is not null)
            {
                var converter = scriptParameter.GetProperty("Converter")?.GetValue(parameter);
                if (converter is null)
                {
                    return "object";
                }

                var converterTypeName = converter.GetType().GetProperty("TypeName")
                    ?.GetValue(converter)?.ToString();
                if (!string.IsNullOrWhiteSpace(converterTypeName))
                {
                    return converterTypeName;
                }

                var target = converter.GetType().GetProperty("TargetType")?.GetValue(converter);
                if (target is Type targetType)
                {
                    return targetType.FullName ?? targetType.Name;
                }
            }

            var legacyHint = parameter.GetType().GetProperty("TypeHint")?.GetValue(parameter);
            if (legacyHint is null)
            {
                return null;
            }

            return legacyHint.GetType().GetProperty("TypeName")?.GetValue(legacyHint)?.ToString()
                ?? legacyHint.ToString();
        }
        catch
        {
            // Third-party parameter metadata must not make canvas inspection unavailable.
            return null;
        }
    }

    private static int? CatalogScore(IGH_ObjectProxy proxy, string query)
    {
        if (query.Length == 0)
        {
            return 5;
        }

        // A GUID query is an exact component-TYPE lookup (used by the executor's canvas.create
        // preflight to verify a type id is actually installed before any write, and by sessions
        // pasting a type id): only the proxy whose Guid equals it matches — never a fuzzy text
        // match. Obsolete filtering stays in the caller (includeObsolete), unchanged.
        if (Guid.TryParse(query, out var guidQuery))
        {
            return proxy.Guid == guidQuery ? 0 : null;
        }

        var description = proxy.Desc;
        if (EqualsQuery(description.Name, query))
        {
            return 0;
        }
        if (EqualsQuery(description.NickName, query))
        {
            return 1;
        }
        if (StartsWithQuery(description.Name, query) || StartsWithQuery(description.NickName, query))
        {
            return 2;
        }
        if (ContainsQuery(description.Name, query) || ContainsQuery(description.NickName, query))
        {
            return 3;
        }
        if (EqualsQuery(description.Category, query) ||
            EqualsQuery(description.SubCategory, query))
        {
            return 4;
        }

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var searchable = string.Join(' ', new[]
        {
            description.Name,
            description.NickName,
            description.Category,
            description.SubCategory,
            description.Description,
        });
        return tokens.All(token => ContainsQuery(searchable, token)) ? 5 : null;
    }

    private static bool EqualsQuery(string? value, string query) =>
        string.Equals(value, query, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithQuery(string? value, string query) =>
        value?.StartsWith(query, StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsQuery(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static IEnumerable<(Guid OwnerId, IGH_Param Parameter)> EnumerateParameters(
        GH_Document document)
    {
        foreach (var documentObject in document.Objects)
        {
            if (documentObject is IGH_Param standaloneParameter)
            {
                yield return (documentObject.InstanceGuid, standaloneParameter);
            }

            if (documentObject is not IGH_Component component)
            {
                continue;
            }

            foreach (var parameter in component.Params.Input)
            {
                yield return (documentObject.InstanceGuid, parameter);
            }

            foreach (var parameter in component.Params.Output)
            {
                yield return (documentObject.InstanceGuid, parameter);
            }
        }
    }

    private static IGH_Param ResolveParameter(
        GH_Document document,
        Guid ownerId,
        Guid parameterId,
        bool source)
    {
        var owner = document.FindObject(ownerId, true)
            ?? throw new KeyNotFoundException($"Grasshopper object {ownerId:D} was not found.");
        IGH_Param? parameter = owner switch
        {
            IGH_Component component when source => component.Params.Output
                .FirstOrDefault(item => item.InstanceGuid == parameterId),
            IGH_Component component => component.Params.Input
                .FirstOrDefault(item => item.InstanceGuid == parameterId),
            IGH_Param standalone when standalone.InstanceGuid == parameterId => standalone,
            _ => null,
        };
        if (parameter is not null)
        {
            return parameter;
        }
        // List the object's available parameters on the required side so the model can correct a
        // wrong socket id in one retry instead of guessing (socket ids are Grasshopper-assigned).
        var available = owner is IGH_Component comp
            ? string.Join(", ", (source ? comp.Params.Output : comp.Params.Input)
                .Select(item => $"{item.Name}={item.InstanceGuid:D}"))
            : owner is IGH_Param p ? $"{p.Name}={p.InstanceGuid:D}" : "none";
        throw new KeyNotFoundException(
            $"Grasshopper {(source ? "source" : "target")} parameter {parameterId:D} " +
            $"on object {ownerId:D} was not found. Available {(source ? "output" : "input")} " +
            $"sockets: {available}.");
    }

    private static bool WouldCreateCycle(
        GH_Document document,
        Guid sourceObjectId,
        Guid targetObjectId)
    {
        if (sourceObjectId == targetObjectId)
        {
            return true;
        }

        var ownerByParameter = EnumerateParameters(document)
            .ToDictionary(item => item.Parameter.InstanceGuid, item => item.OwnerId);
        var visited = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(sourceObjectId);
        while (pending.Count > 0)
        {
            var currentId = pending.Pop();
            if (!visited.Add(currentId))
            {
                continue;
            }

            if (currentId == targetObjectId)
            {
                return true;
            }

            var currentObject = document.FindObject(currentId, true);
            var inputs = currentObject switch
            {
                IGH_Component component => component.Params.Input.AsEnumerable(),
                IGH_Param parameter => new[] { parameter },
                _ => Enumerable.Empty<IGH_Param>()
            };
            foreach (var upstream in inputs.SelectMany(input => input.Sources))
            {
                if (ownerByParameter.TryGetValue(upstream.InstanceGuid, out var upstreamOwner))
                {
                    pending.Push(upstreamOwner);
                }
            }
        }

        return false;
    }

    private static string GroupFingerprint(GH_Group group)
    {
        var value = $"{group.InstanceGuid:N}|{group.NickName}|{group.Colour.ToArgb()}|" +
            string.Join(',', group.ObjectIDs.OrderBy(id => id));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string ComputeDocumentFingerprint(
        IReadOnlyList<CanvasObjectState> objects,
        IReadOnlyList<WireState> wires,
        IReadOnlyList<GroupState> groups)
    {
        var builder = new StringBuilder();
        foreach (var item in objects)
        {
            builder.Append(item.ObjectId.ToString("N")).Append(':').AppendLine(item.Fingerprint);
        }

        foreach (var wire in wires)
        {
            builder.AppendLine(FormattableString.Invariant(
                $"{wire.SourceObjectId:N}/{wire.SourceParameterId:N}>{wire.TargetObjectId:N}/{wire.TargetParameterId:N}"));
        }

        foreach (var group in groups)
        {
            builder.Append(group.GroupId.ToString("N")).Append(':').Append(group.Name).Append(':')
                .Append(group.ArgbColor).Append(':')
                .AppendLine(string.Join(',', group.ObjectIds.OrderBy(id => id)));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private sealed class ParameterReferenceComparer : IEqualityComparer<IGH_Param>
    {
        public static ParameterReferenceComparer Instance { get; } = new();

        public bool Equals(IGH_Param? x, IGH_Param? y) => ReferenceEquals(x, y);

        public int GetHashCode(IGH_Param obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private static void RequireOperationId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new InvalidOperationException("OperationId is required.");
        }
    }

    private static void RequireFinite(CanvasPoint point, string name)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            throw new InvalidOperationException($"{name} coordinates must be finite.");
        }
    }

    private sealed record PreparedMove(
        Guid ObjectId,
        IGH_DocumentObject DocumentObject,
        System.Drawing.PointF OriginalPivot,
        System.Drawing.PointF Pivot);

    private sealed record CatalogCandidate(
        IGH_ObjectProxy Proxy,
        int? Score);
}
