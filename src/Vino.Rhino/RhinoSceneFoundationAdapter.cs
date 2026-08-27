// SPDX-License-Identifier: Apache-2.0
// Behavioral reimplementation informed by Cordyceps; see THIRD_PARTY_NOTICES.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vino.BridgeContract;
using Vino.CanvasSceneAdapter;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Runtime;

using System.Diagnostics;

namespace Vino.Rhino;

/// <summary>
/// Undo-aware Rhino scene adapter using RhinoCommon's native CommonObject JSON format.
/// It never falls back to ActiveDoc and preserves object IDs on replacement.
/// </summary>
public sealed class RhinoSceneFoundationAdapter : DocumentBoundRhinoSceneAdapter<global::Rhino.RhinoDoc>
{
    private const string LogicalEntityKey = "GPTino.LogicalEntityId";
    // Provenance stamp: the durable GH docKey whose job produced this object. Server-injected
    // (never model-supplied); legacy objects without it stay honestly unattributed.
    private const string SourceDocKeyKey = "GPTino.SourceDocKey";
    // Stamped by the bake_manager skill (family identity for replace/append re-bakes).
    private const string BakeFamilyKey = "gptino_bake_family";
    // Stamped by the bake_manager skill: the InstanceGuid of the canvas component that baked the
    // object, so the data-flow panel can frame the bake's source on the GH canvas.
    private const string BakeComponentKey = "gptino_bake_component";
    // Layer-curation semantic labels live in layer user text under this namespace. The adapter
    // only ever reads and writes "gptino." keys (lowercase, dotted — the external-facing label
    // convention, distinct from the object-level PascalCase keys above): reads filter to the
    // prefix, and updateLayer refuses any other namespace.
    private const string LayerUserTextPrefix = "gptino.";
    // The two keys whose PRESENCE means "this layer is labeled" — layerSemantics reports layers
    // missing either one, so re-running the audit after an apply is the clean-state observation.
    private const string LayerCanonicalKey = "gptino.canonical";
    private const string LayerMaterialKey = "gptino.material";
    /// <summary>The only render-material template layer curation defines: matte, colour-only.</summary>
    private const string PlasterMaterialTemplate = "plaster";
    /// <summary>Bridge failure code for the human-wins refusal; see RequireProvenanceOrApproval.</summary>
    public const string ApprovalRequiredCode = "approval_required";
    /// <summary>
    /// Bridge failure code for a deterministic PRE-WRITE refusal (a proof the adapter runs before
    /// touching the document: layer not empty, block still referenced, style still current…).
    /// Nothing changed, so the executor reports a plain failure instead of "outcome unknown".
    /// </summary>
    public const string PreconditionRefusedCode = "precondition_refused";

    /// <summary>Refuses an operation before any document change, with the no-write guarantee.</summary>
    private static Exception Refuse(string message) =>
        new BridgeProtocolException(PreconditionRefusedCode, $"{message} No change was applied.");

    public RhinoSceneFoundationAdapter(ExplicitRhinoDocumentResolver resolver)
        : base(resolver)
    {
    }

    protected override Task<RhinoViewCaptureResult> CaptureViewCoreAsync(
        global::Rhino.RhinoDoc document,
        RhinoViewCaptureRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Clamped, not rejected: the capture is model feedback, not print output, and the 8 MiB
        // bridge frame is the real ceiling (base64 inflates ~1.37x). 1920x1200 PNG stays far under.
        var width = Math.Clamp(request.Width, 64, 1920);
        var height = Math.Clamp(request.Height, 64, 1200);
        var view = string.IsNullOrWhiteSpace(request.ViewName)
            ? document.Views.ActiveView
            : document.Views.Find(request.ViewName, compareCase: false);
        if (view is null)
        {
            throw new InvalidOperationException(
                $"Viewport '{request.ViewName}' was not found in the document.");
        }
        // Frame the document geometry first so an unattended capture photographs the work, not
        // whatever corner the viewport was last left at — but NEVER keep that framing: the
        // viewport may be the one the user is navigating right now, and a live ZoomExtents
        // yanked a working camera mid-session (observed 08-21). Snapshot the projection, zoom,
        // capture, put the camera back, and only then let the screen repaint.
        global::Rhino.DocObjects.ViewportInfo? savedProjection = null;
        if (request.ZoomExtents)
        {
            savedProjection = new global::Rhino.DocObjects.ViewportInfo(view.ActiveViewport);
            view.ActiveViewport.ZoomExtents();
        }
        try
        {
            using var bitmap = view.CaptureToBitmap(new System.Drawing.Size(width, height))
                ?? throw new InvalidOperationException("Rhino returned no bitmap for the viewport capture.");
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            var bytes = stream.ToArray();
            var result = new RhinoViewCaptureResult(
                view.ActiveViewport.Name,
                width,
                height,
                Convert.ToBase64String(bytes),
                Hash($"viewCapture|{view.ActiveViewport.Name}|{width}x{height}|{Convert.ToHexString(SHA256.HashData(bytes))}"));
            return Task.FromResult(result);
        }
        finally
        {
            if (savedProjection is not null)
            {
                view.ActiveViewport.SetViewProjection(savedProjection, updateTargetLocation: true);
                view.Redraw();
            }
        }
    }

    protected override Task<RhinoSceneListResult> ListObjectsCoreAsync(
        global::Rhino.RhinoDoc document,
        RhinoListObjectsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateListRequest(request);

        var matches = new List<RhinoSceneObjectSummary>(request.Limit + 1);
        // The SAME enumerator the audits use, and for the same reason: this answers "what is in the
        // document", which must mean one thing. Iterating `document.Objects` bare took RhinoCommon's
        // default settings and listed DELETED objects — a live gate caught rhino_list still
        // reporting an object the broker had just deleted and verified gone, which is a ghost the
        // model would then reference, re-audit, and reason about. Hidden and locked objects DO
        // exist and stay listed; deleted ones do not.
        foreach (var rhinoObject in document.Objects.GetObjectList(AuditEnumerator())
                     .OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ObjectId.HasValue && rhinoObject.Id != request.ObjectId.Value)
            {
                continue;
            }

            var attributes = rhinoObject.Attributes;
            var layer = attributes.LayerIndex >= 0 && attributes.LayerIndex < document.Layers.Count
                ? document.Layers[attributes.LayerIndex]
                : null;
            var logicalEntityId = attributes.GetUserString(LogicalEntityKey) ?? string.Empty;
            var name = attributes.Name ?? string.Empty;
            var geometryType = rhinoObject.Geometry.ObjectType.ToString();
            var selected = rhinoObject.IsSelected(checkSubObjects: false) != 0;

            if (request.LayerId.HasValue && layer?.Id != request.LayerId.Value ||
                request.LayerFullPath is not null &&
                !string.Equals(layer?.FullPath, request.LayerFullPath, StringComparison.OrdinalIgnoreCase) ||
                request.Name is not null &&
                !string.Equals(name, request.Name, StringComparison.OrdinalIgnoreCase) ||
                request.NameContains is not null &&
                !name.Contains(request.NameContains, StringComparison.OrdinalIgnoreCase) ||
                request.GeometryType is not null &&
                !string.Equals(geometryType, request.GeometryType, StringComparison.OrdinalIgnoreCase) ||
                request.LogicalEntityId is not null &&
                !string.Equals(logicalEntityId, request.LogicalEntityId, StringComparison.Ordinal) ||
                request.Selected.HasValue && selected != request.Selected.Value)
            {
                continue;
            }

            var state = ToState(rhinoObject);
            matches.Add(new RhinoSceneObjectSummary(
                rhinoObject.Id,
                logicalEntityId,
                name,
                geometryType,
                layer?.Id ?? Guid.Empty,
                layer?.FullPath ?? string.Empty,
                selected,
                ToBounds(rhinoObject.Geometry.GetBoundingBox(accurate: false)),
                state.Fingerprint));
            if (matches.Count > request.Limit)
            {
                break;
            }
        }

        var truncated = matches.Count > request.Limit;
        if (truncated)
        {
            matches.RemoveAt(matches.Count - 1);
        }

        var bounds = UnionBounds(matches.Select(item => item.Bounds));
        var fingerprint = Hash(
            $"{CanonicalQuery(request)}\n{truncated}\n" +
            string.Join("\n", matches.Select(item => $"{item.ObjectId:D}:{item.Fingerprint}")));
        return Task.FromResult(new RhinoSceneListResult(
            request.Limit,
            matches.Count,
            truncated,
            bounds,
            matches,
            fingerprint));
    }

    protected override Task<StampedObjectsResult> ListStampedObjectsCoreAsync(
        global::Rhino.RhinoDoc document,
        CancellationToken cancellationToken)
    {
        // Bake-ledger census: every object carrying a Vino stamp, grouped by
        // (source docKey, bake family). Deterministic ordering (group key, then object id) so the
        // fingerprint is stable across identical documents. Object id lists are capped per group —
        // the ledger needs counts and samples, not a full dump of a 10k-object bake.
        const int MaxIdsPerGroup = 50;
        // A family is normally baked by exactly one component; the cap only guards a pathological
        // document where many components stamped the same family.
        const int MaxComponentsPerGroup = 8;
        var groups = new Dictionary<(string? SourceDocKey, string? Family), List<Guid>>();
        var counts = new Dictionary<(string? SourceDocKey, string? Family), int>();
        var components = new Dictionary<(string? SourceDocKey, string? Family), List<Guid>>();
        var totalStamped = 0;
        // The census counts what EXISTS, not what is visible: the default enumerator skips hidden
        // objects, which would shrink bake counts (and churn the fingerprint) the moment a user
        // hides a baked family while iterating. Block-definition members stay excluded — instance
        // containers are the countable objects.
        var enumeratorSettings = new global::Rhino.DocObjects.ObjectEnumeratorSettings
        {
            ActiveObjects = true,
            HiddenObjects = true,
            LockedObjects = true,
            DeletedObjects = false,
        };
        foreach (var rhinoObject in document.Objects.GetObjectList(enumeratorSettings)
                     .OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = rhinoObject.Attributes;
            var logicalId = attributes.GetUserString(LogicalEntityKey);
            var family = attributes.GetUserString(BakeFamilyKey);
            if (string.IsNullOrEmpty(logicalId) && string.IsNullOrEmpty(family))
            {
                continue;
            }
            totalStamped++;
            var sourceDocKey = attributes.GetUserString(SourceDocKeyKey);
            var key = (
                string.IsNullOrEmpty(sourceDocKey) ? null : sourceDocKey.ToLowerInvariant(),
                string.IsNullOrEmpty(family) ? null : family);
            counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
            if (!groups.TryGetValue(key, out var ids))
            {
                groups[key] = ids = new List<Guid>();
            }
            if (ids.Count < MaxIdsPerGroup)
            {
                ids.Add(rhinoObject.Id);
            }
            if (Guid.TryParse(attributes.GetUserString(BakeComponentKey), out var componentId) &&
                componentId != Guid.Empty)
            {
                if (!components.TryGetValue(key, out var componentIds))
                {
                    components[key] = componentIds = new List<Guid>();
                }
                if (!componentIds.Contains(componentId) && componentIds.Count < MaxComponentsPerGroup)
                {
                    componentIds.Add(componentId);
                }
            }
        }

        var ordered = groups
            .OrderBy(pair => pair.Key.SourceDocKey ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Family ?? string.Empty, StringComparer.Ordinal)
            .Select(pair => new StampedObjectGroup(
                pair.Key.SourceDocKey,
                pair.Key.Family,
                counts[pair.Key],
                pair.Value,
                components.TryGetValue(pair.Key, out var componentIds)
                    ? componentIds.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray()
                    : Array.Empty<Guid>()))
            .ToArray();
        var fingerprint = Hash(
            "stampedObjects\n" + string.Join(
                "\n",
                ordered.Select(group =>
                    $"{group.SourceDocKey}|{group.BakeFamily}|{group.Count}|{string.Join(",", group.ObjectIds.Select(id => id.ToString("D")))}|{string.Join(",", group.SourceComponentIds.Select(id => id.ToString("D")))}")));
        return Task.FromResult(new StampedObjectsResult(totalStamped, ordered, fingerprint));
    }

    protected override Task<RhinoAuditResult> AuditCoreAsync(
        global::Rhino.RhinoDoc document,
        RhinoAuditRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limit = Math.Clamp(request.Limit, 1, 100);
        var docTolerance = document.ModelAbsoluteTolerance;
        // The SAME tolerance value flows into every measure and any later fix predicate — a
        // mm-doc heuristic silently becoming absurd in a meters doc is the audit's failure mode.
        var tolerance = request.Tolerance is > 0 ? request.Tolerance.Value : docTolerance;
        var units = document.ModelUnitSystem.ToString();
        var kind = (request.Kind ?? string.Empty).Trim();
        double? bandUsed = null;
        (List<RhinoAuditFinding> Findings, int Scanned, bool Truncated) outcome;
        switch (kind)
        {
            case "nearMissEndpoints":
            {
                // Clamped: an unbounded band degenerates the RTree search into all-pairs
                // enumeration on the UI thread.
                var bandFactor = request.BandFactor is > 1 ? Math.Min(request.BandFactor.Value, 100.0) : 10.0;
                bandUsed = tolerance * bandFactor;
                outcome = AuditNearMissEndpoints(document, tolerance, bandUsed.Value, limit, cancellationToken);
                break;
            }
            case "nearDuplicates":
                outcome = AuditNearDuplicates(document, tolerance, limit, cancellationToken);
                break;
            case "openBrepEdges":
            {
                var bandFactor = request.BandFactor is > 1 ? Math.Min(request.BandFactor.Value, 100.0) : 10.0;
                bandUsed = tolerance * bandFactor;
                outcome = AuditOpenBrepEdges(document, tolerance, bandUsed.Value, limit, cancellationToken);
                break;
            }
            case "geometryIntegrity":
            {
                var bandFactor = request.BandFactor is > 1 ? Math.Min(request.BandFactor.Value, 100.0) : 10.0;
                bandUsed = tolerance * bandFactor;
                outcome = AuditGeometryIntegrity(document, tolerance, bandUsed.Value, limit, cancellationToken);
                break;
            }
            case "layerIntegrity":
                outcome = AuditLayerIntegrity(document, limit, cancellationToken);
                break;
            case "blockIntegrity":
                outcome = AuditBlockIntegrity(document, limit, cancellationToken);
                break;
            case "purgeCandidates":
                outcome = AuditPurgeCandidates(document, limit, cancellationToken);
                break;
            case "layerSemantics":
                outcome = AuditLayerSemantics(document, limit, cancellationToken);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown audit kind '{request.Kind}'. Use {string.Join("|", RhinoAuditKinds.All)}.");
        }
        var fingerprint = Hash(
            $"audit|{kind}|{tolerance:R}|" +
            string.Join("\n", outcome.Findings.Select(finding => $"{finding.FindingId}|{finding.Measure}")));
        return Task.FromResult(new RhinoAuditResult(
            kind,
            docTolerance,
            units,
            tolerance,
            bandUsed,
            outcome.Scanned,
            outcome.Findings,
            outcome.Truncated,
            fingerprint));
    }

    /// <summary>
    /// Load-source sampling (rhino.structuralLoadSample). For each source scope the in-scope
    /// solids/surfaces/meshes are meshed and shot with VERTICAL rays on a plan grid; a sample
    /// records the total thickness the ray crossed. Thickness x density = pressure happens
    /// downstream (densities are AgentHost data, not geometry facts), tributary assignment
    /// happens downstream too — this op only answers "how much material stands over this point",
    /// which makes voids (no material -> no sample) and variable soil depth automatic.
    /// </summary>
    protected override Task<StructuralLoadSampleResult> SampleStructuralLoadsCoreAsync(
        global::Rhino.RhinoDoc document,
        StructuralLoadSampleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var grid = request.GridSpacing > 0 ? request.GridSpacing : 250.0;
        var limit = Math.Clamp(request.SampleLimit, 1_000, 200_000);
        var hitTolerance = Math.Max(document.ModelAbsoluteTolerance * 10.0, 0.01);
        var units = document.ModelUnitSystem.ToString();
        var sources = new List<StructuralLoadSourceSamples>();
        foreach (var source in request.Sources ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var mesh = new Mesh();
            var objectCount = 0;
            foreach (var candidate in document.Objects.GetObjectList(AuditEnumerator()))
            {
                var layer = candidate.Attributes.LayerIndex >= 0 && candidate.Attributes.LayerIndex < document.Layers.Count
                    ? document.Layers[candidate.Attributes.LayerIndex]
                    : null;
                if (layer is null ||
                    !layer.FullPath.Contains(source.LayerFilter ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                switch (candidate.Geometry)
                {
                    case Mesh sourceMesh:
                        mesh.Append(sourceMesh);
                        objectCount++;
                        break;
                    case Brep or Extrusion:
                    {
                        var brep = candidate.Geometry as Brep ?? (candidate.Geometry as Extrusion)?.ToBrep();
                        var pieces = brep is null ? null : Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
                        if (pieces is null || pieces.Length == 0)
                        {
                            skipped["unmeshable:" + candidate.Geometry.ObjectType] =
                                skipped.GetValueOrDefault("unmeshable:" + candidate.Geometry.ObjectType) + 1;
                            break;
                        }
                        foreach (var piece in pieces)
                        {
                            mesh.Append(piece);
                        }
                        objectCount++;
                        break;
                    }
                    default:
                        var reason = "skipped:" + (candidate.Geometry?.ObjectType.ToString() ?? "null");
                        skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
                        break;
                }
            }
            if (mesh.Faces.Count == 0)
            {
                sources.Add(new StructuralLoadSourceSamples(
                    source.Name, objectCount, 0, grid * grid, [], skipped, Truncated: false));
                continue;
            }
            var box = mesh.GetBoundingBox(accurate: true);
            var samples = new List<StructuralLoadSample>();
            var truncated = false;
            for (var x = box.Min.X + grid * 0.5; x < box.Max.X && !truncated; x += grid)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var y = box.Min.Y + grid * 0.5; y < box.Max.Y; y += grid)
                {
                    if (samples.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }
                    var ray = new Line(
                        new Point3d(x, y, box.Min.Z - 10.0),
                        new Point3d(x, y, box.Max.Z + 10.0));
                    var hits = global::Rhino.Geometry.Intersect.Intersection.MeshLine(mesh, ray);
                    if (hits is null || hits.Length == 0)
                    {
                        continue;
                    }
                    var heights = hits.Select(hit => hit.Z).OrderBy(z => z).ToList();
                    var merged = new List<double> { heights[0] };
                    foreach (var z in heights.Skip(1))
                    {
                        if (z - merged[^1] > hitTolerance)
                        {
                            merged.Add(z);
                        }
                    }
                    var thickness = 0.0;
                    for (var pair = 0; pair + 1 < merged.Count; pair += 2)
                    {
                        thickness += merged[pair + 1] - merged[pair];
                    }
                    samples.Add(new StructuralLoadSample(
                        Math.Round(x, 1),
                        Math.Round(y, 1),
                        Math.Round(thickness, 1),
                        merged.Count,
                        Math.Round(merged[^1], 1),
                        Math.Round(merged[0], 1)));
                }
            }
            sources.Add(new StructuralLoadSourceSamples(
                source.Name, objectCount, samples.Count, grid * grid, samples, skipped, truncated));
        }
        var fingerprint = Hash(
            $"structuralLoadSample|{units}|{grid}|" +
            string.Join("|", sources.Select(entry =>
                $"{entry.Name}:{entry.ObjectCount}:{entry.SampleCount}:{entry.Samples.Sum(sample => sample.Thickness):F1}")));
        return Task.FromResult(new StructuralLoadSampleResult(units, grid, sources, fingerprint));
    }

    /// <summary>
    /// Structural axis extraction (rhino.structuralExtract). Three source kinds, in honesty order:
    /// curves ARE axes; InstanceReferences of unit-prototype blocks yield EXACT axes (prototype
    /// axis pushed through the instance transform — no skeletonization, validated on a 1,199-member
    /// production model); loose slender solids get a PCA axis flagged "pca". Meshes and stocky
    /// solids are counted in SkippedByReason, never guessed at. The quality report (free ends,
    /// oblique exact axes, dedupe count) ships in the result because "the lines are where the
    /// members are" is a safety claim — it must be graded by server code, not by the model.
    /// </summary>
    protected override Task<StructuralExtractResult> ExtractStructuralAxesCoreAsync(
        global::Rhino.RhinoDoc document,
        StructuralExtractRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limit = Math.Clamp(request.Limit, 1, 10_000);
        var prototypeHeight = request.PrototypeHeight > 0 ? request.PrototypeHeight : 1000.0;
        var units = document.ModelUnitSystem.ToString();

        bool InScope(RhinoObject candidate)
        {
            if (request.SelectedOnly && candidate.IsSelected(checkSubObjects: false) == 0)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(request.LayerFilter))
            {
                return true;
            }
            var layer = candidate.Attributes.LayerIndex >= 0 && candidate.Attributes.LayerIndex < document.Layers.Count
                ? document.Layers[candidate.Attributes.LayerIndex]
                : null;
            return layer is not null &&
                layer.FullPath.Contains(request.LayerFilter, StringComparison.OrdinalIgnoreCase);
        }

        string LayerPath(RhinoObject candidate) =>
            candidate.Attributes.LayerIndex >= 0 && candidate.Attributes.LayerIndex < document.Layers.Count
                ? document.Layers[candidate.Attributes.LayerIndex].FullPath
                : string.Empty;

        var scoped = document.Objects.GetObjectList(AuditEnumerator())
            .Where(InScope)
            .OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal)
            .ToList();
        var scanned = scoped.Count;
        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Skip(string reason)
        {
            skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
        }

        // Pass 1 — unit prototypes: one solid per section-mark layer, parked at the origin at
        // exactly the prototype height. Its outer dims are the section identity (nominal × 1.02
        // in the validated real model); its axis (origin → +Z·height) is what instances transform.
        var prototypes = new Dictionary<string, StructuralPrototype>(StringComparer.Ordinal);
        foreach (var candidate in scoped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Geometry is not (Brep or Extrusion))
            {
                continue;
            }
            var box = candidate.Geometry.GetBoundingBox(accurate: true);
            var nearOrigin = Math.Abs(box.Min.X) < 5000 && Math.Abs(box.Min.Y) < 5000 && Math.Abs(box.Min.Z) < 100;
            var height = box.Max.Z - box.Min.Z;
            if (nearOrigin && Math.Abs(height - prototypeHeight) <= prototypeHeight * 0.01)
            {
                var layerPath = LayerPath(candidate);
                prototypes[layerPath] = new StructuralPrototype(
                    layerPath,
                    StructuralAxisMath.MarkPrefix(LayerLeaf(layerPath)),
                    Math.Round(box.Max.X - box.Min.X, 1),
                    Math.Round(box.Max.Y - box.Min.Y, 1));
            }
        }

        // Pass 1b — definition-parked prototypes. In the production convention the prototype solid
        // usually lives INSIDE the block definition (delete-original AddBlock leaves no top-level
        // copy), and document.Objects' enumerator never yields definition members — the real-model
        // live gate caught pass 1 finding 0 of the 29 prototypes the file-table scan had proven.
        // So the authoritative source is each in-scope instance's own definition: measure its
        // members' combined box, and accept it as this mark's prototype when it matches the unit
        // height. Keyed by the INSTANCE's layer (the mark layer); pass 1's top-level copies win on
        // conflict since they are the visibly-parked originals.
        var measuredDefinitions = new Dictionary<Guid, (double OuterX, double OuterY, bool UnitHeight)>();
        foreach (var candidate in scoped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Geometry is not InstanceReferenceGeometry instanceGeometry)
            {
                continue;
            }
            var layerPath = LayerPath(candidate);
            if (prototypes.ContainsKey(layerPath))
            {
                continue;
            }
            if (!measuredDefinitions.TryGetValue(instanceGeometry.ParentIdefId, out var measured))
            {
                var definition = document.InstanceDefinitions.FindId(instanceGeometry.ParentIdefId);
                var union = BoundingBox.Empty;
                foreach (var member in definition?.GetObjects() ?? [])
                {
                    if (member?.Geometry is Brep or Extrusion)
                    {
                        union.Union(member.Geometry.GetBoundingBox(accurate: true));
                    }
                }
                measured = union.IsValid
                    ? (Math.Round(union.Max.X - union.Min.X, 1),
                       Math.Round(union.Max.Y - union.Min.Y, 1),
                       Math.Abs(union.Max.Z - union.Min.Z - prototypeHeight) <= prototypeHeight * 0.01)
                    : (0, 0, false);
                measuredDefinitions[instanceGeometry.ParentIdefId] = measured;
            }
            if (measured.UnitHeight)
            {
                prototypes[layerPath] = new StructuralPrototype(
                    layerPath,
                    StructuralAxisMath.MarkPrefix(LayerLeaf(layerPath)),
                    measured.OuterX,
                    measured.OuterY);
            }
        }

        // Pass 2 — axes.
        var axes = new List<StructuralAxisMath.Axis>();
        var raw = new List<(string Mark, string Layer, StructuralAxisMath.Vec3 A, StructuralAxisMath.Vec3 B,
            string Kind, Guid ObjectId, string Fingerprint)>();
        var points = new List<StructuralPointObject>();
        var truncated = false;
        foreach (var candidate in scoped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (raw.Count >= limit)
            {
                truncated = true;
                break;
            }
            var layerPath = LayerPath(candidate);
            var mark = LayerLeaf(layerPath);
            switch (candidate.Geometry)
            {
                case Curve curve:
                {
                    // A curve is the user's own axis drawing, but one CURVE is not one MEMBER: a
                    // frame drawn as a single polyline, a rectangle ring beam, or an arch must
                    // become its segments — reading only the endpoints turned a whole polyline
                    // frame into one skewed line. Kinks split exactly; curved pieces become
                    // chords of about CurveSegmentLength (kind 'curve-discretized').
                    var pieces = CurveAxisSegments(curve, request.CurveSegmentLength, document.ModelAbsoluteTolerance);
                    if (pieces.Count == 0)
                    {
                        Skip("curve:degenerate");
                        break;
                    }
                    var sourceFingerprint = ToState(candidate).Fingerprint;
                    foreach (var (a, b, kind) in pieces)
                    {
                        raw.Add((mark, layerPath, a, b, kind, candidate.Id, sourceFingerprint));
                    }
                    break;
                }
                case global::Rhino.Geometry.Point point:
                {
                    // Not a member — a support or load marker candidate for the ask-back.
                    points.Add(new StructuralPointObject(candidate.Id, layerPath, ToPoint(ToVec(point.Location))));
                    break;
                }
                case InstanceReferenceGeometry instance:
                {
                    var transform = instance.Xform;
                    var matrix = new double[]
                    {
                        transform.M00, transform.M01, transform.M02, transform.M03,
                        transform.M10, transform.M11, transform.M12, transform.M13,
                        transform.M20, transform.M21, transform.M22, transform.M23,
                        transform.M30, transform.M31, transform.M32, transform.M33,
                    };
                    var a = StructuralAxisMath.TransformPoint(matrix, new StructuralAxisMath.Vec3(0, 0, 0));
                    var b = StructuralAxisMath.TransformPoint(matrix, new StructuralAxisMath.Vec3(0, 0, prototypeHeight));
                    raw.Add((mark, layerPath, a, b, "instance", candidate.Id, ToState(candidate).Fingerprint));
                    break;
                }
                case Brep or Extrusion:
                {
                    var box = candidate.Geometry.GetBoundingBox(accurate: true);
                    if (Math.Abs(box.Min.X) < 5000 && Math.Abs(box.Min.Y) < 5000)
                    {
                        Skip("prototype:" + candidate.Geometry.ObjectType);
                        break;
                    }
                    var brep = candidate.Geometry as Brep ?? (candidate.Geometry as Extrusion)?.ToBrep();
                    var vertices = brep?.Vertices
                        .Select(vertex => ToVec(vertex.Location))
                        .ToArray() ?? [];
                    var principal = StructuralAxisMath.PrincipalAxisEndpoints(vertices, minimumSpan: 300.0);
                    if (principal is not { } axis)
                    {
                        Skip(vertices.Length < 4 ? "loose:no-vertices" : "loose:too-short");
                        break;
                    }
                    raw.Add((mark, layerPath, axis.A, axis.B, "pca", candidate.Id, ToState(candidate).Fingerprint));
                    break;
                }
                default:
                    Skip("skipped:" + (candidate.Geometry?.ObjectType.ToString() ?? "null"));
                    break;
            }
        }

        foreach (var item in raw)
        {
            axes.Add(new StructuralAxisMath.Axis(
                StructuralAxisMath.MarkPrefix(item.Mark),
                item.A,
                item.B,
                (item.B - item.A).Length,
                Approximate: item.Kind is "pca" or "curve-discretized"));
        }
        var (keptIndices, mergedAway) = StructuralAxisMath.DedupeAxes(
            axes,
            request.DedupeAngleDegrees,
            request.DedupeMidpointDistance);

        var members = new List<StructuralMember>(keptIndices.Count);
        foreach (var index in keptIndices)
        {
            var item = raw[index];
            members.Add(new StructuralMember(
                item.Mark,
                item.Layer,
                ToPoint(item.A),
                ToPoint(item.B),
                Math.Round((item.B - item.A).Length, 1),
                item.Kind,
                StructuralAxisMath.ClassifyRole(item.A, item.B),
                [item.ObjectId],
                [item.Fingerprint]));
        }

        var segments = members
            .Select(member => (ToVecFromRecord(member.A), ToVecFromRecord(member.B)))
            .ToArray();
        var freeEnds = StructuralAxisMath.FindFreeEnds(segments, request.JoinSnapDistance)
            .Select(free => new StructuralFreeEnd(
                free.MemberIndex,
                free.End,
                ToPoint(free.Point),
                members[free.MemberIndex].SourceObjectIds))
            .ToArray();
        var exactSegments = members
            .Where(member => member.Kind is not ("pca" or "curve-discretized"))
            .Select(member => (ToVecFromRecord(member.A), ToVecFromRecord(member.B)))
            .ToArray();
        var oblique = StructuralAxisMath.CountObliqueAxes(exactSegments);

        var fingerprint = Hash(
            $"structuralExtract|{units}|{request.LayerFilter}|{request.SelectedOnly}|" +
            string.Join("\n", members.Select(member =>
                $"{member.SourceObjectIds[0]:D}|{member.Kind}|{member.A.X:R},{member.A.Y:R},{member.A.Z:R}|" +
                $"{member.B.X:R},{member.B.Y:R},{member.B.Z:R}")));
        return Task.FromResult(new StructuralExtractResult(
            units,
            scanned,
            members,
            prototypes.Values.OrderBy(prototype => prototype.Layer, StringComparer.Ordinal).ToArray(),
            freeEnds,
            points,
            mergedAway,
            oblique,
            skipped,
            truncated,
            fingerprint));

        static StructuralAxisMath.Vec3 ToVec(Point3d point) => new(point.X, point.Y, point.Z);
        static StructuralAxisMath.Vec3 ToVecFromRecord(RhinoPoint3d point) => new(point.X, point.Y, point.Z);
        static List<(StructuralAxisMath.Vec3 A, StructuralAxisMath.Vec3 B, string Kind)> CurveAxisSegments(
            Curve curve,
            double targetSegmentLength,
            double tolerance)
        {
            var result = new List<(StructuralAxisMath.Vec3, StructuralAxisMath.Vec3, string)>();
            // Lines, polylines, and degree-1 NURBS all answer TryGetPolyline: their kinks are the
            // member joints, exactly.
            if (curve.TryGetPolyline(out var polyline) && polyline.Count >= 2)
            {
                foreach (var (a, b) in StructuralAxisMath.PolylineSegments(polyline.Select(ToVec).ToList()))
                {
                    result.Add((a, b, "curve"));
                }
                return result;
            }
            // Polycurves: each piece is a line (exact) or a curved piece (chords).
            var pieces = curve.DuplicateSegments();
            if (pieces is null || pieces.Length == 0)
            {
                pieces = [curve];
            }
            foreach (var piece in pieces)
            {
                if (piece.TryGetPolyline(out var piecePolyline) && piecePolyline.Count >= 2)
                {
                    foreach (var (a, b) in StructuralAxisMath.PolylineSegments(piecePolyline.Select(ToVec).ToList()))
                    {
                        result.Add((a, b, "curve"));
                    }
                    continue;
                }
                if (piece.IsLinear(tolerance))
                {
                    foreach (var (a, b) in StructuralAxisMath.PolylineSegments([ToVec(piece.PointAtStart), ToVec(piece.PointAtEnd)]))
                    {
                        result.Add((a, b, "curve"));
                    }
                    continue;
                }
                var count = StructuralAxisMath.ChordCount(piece.GetLength(), targetSegmentLength);
                var parameters = piece.DivideByCount(count, includeEnds: true);
                if (parameters is null || parameters.Length < 2)
                {
                    continue;
                }
                var vertices = parameters.Select(t => ToVec(piece.PointAt(t))).ToList();
                foreach (var (a, b) in StructuralAxisMath.PolylineSegments(vertices))
                {
                    result.Add((a, b, "curve-discretized"));
                }
            }
            return result;
        }
        static RhinoPoint3d ToPoint(StructuralAxisMath.Vec3 vec) =>
            new(Math.Round(vec.X, 1), Math.Round(vec.Y, 1), Math.Round(vec.Z, 1));
        static string LayerLeaf(string path)
        {
            var separator = path.LastIndexOf("::", StringComparison.Ordinal);
            return separator < 0 ? path : path[(separator + 2)..];
        }
    }

    private static ObjectEnumeratorSettings AuditEnumerator(ObjectType? typeFilter = null) => new()
    {
        // Audits count what exists, not what is visible — hidden and locked included. Block
        // definition members are NOT reachable through this enumerator (see
        // EnumerateLayerOccupants); geometry analyses stay top-level by design anyway.
        NormalObjects = true,
        ActiveObjects = true,
        HiddenObjects = true,
        LockedObjects = true,
        DeletedObjects = false,
        // Lights, page/phantom objects and linked-block references are still objects ON a layer:
        // omitting them would report an occupied layer as an empty leaf and offer it for deletion.
        IncludeLights = true,
        IncludePhantoms = true,
        ReferenceObjects = true,
        ObjectTypeFilter = typeFilter ?? ObjectType.AnyObject,
    };

    /// <summary>
    /// Every object that OCCUPIES a layer: top-level objects plus block-definition members.
    /// Members come from the instance-definition table, NOT from an ObjectEnumeratorSettings flag:
    /// the live gate proved that a document with a real definition whose member sits on a layer
    /// still enumerated zero members through <c>IdefObjects</c>, whichever way it was combined with
    /// the mode flags. A missed member makes an occupied layer look like an empty leaf, which the
    /// deleteLayer proof would then happily approve.
    /// </summary>
    private static IReadOnlyList<RhinoObject> EnumerateLayerOccupants(global::Rhino.RhinoDoc document)
    {
        // The top-level pass is MATERIALIZED before the definitions are walked: a lazy walk would
        // keep the native object iterator open across the definition reads.
        var occupants = document.Objects.GetObjectList(AuditEnumerator()).ToList();
        if (document.InstanceDefinitions.Count == 0)
        {
            return occupants;
        }
        var seen = occupants.Select(item => item.Id).ToHashSet();
        foreach (var definition in document.InstanceDefinitions)
        {
            if (definition is null || definition.IsDeleted)
            {
                continue;
            }
            // Nested definitions are covered without recursion: every definition in the table is
            // visited, so a member of a nested one is reached when that definition comes up.
            foreach (var member in definition.GetObjects())
            {
                if (member is not null && seen.Add(member.Id))
                {
                    occupants.Add(member);
                }
            }
        }
        return occupants;
    }

    // Open-curve endpoints that ALMOST meet: gap in (tolerance, band]. Detection is endpoint-to-
    // endpoint via RTree; T-junctions (endpoint near a curve's interior) are a separate future
    // kind. Same-object pairs are skipped — an almost-closed curve is a different defect class.
    private (List<RhinoAuditFinding> Findings, int Scanned, bool Truncated) AuditNearMissEndpoints(
        global::Rhino.RhinoDoc document,
        double tolerance,
        double band,
        int limit,
        CancellationToken cancellationToken)
    {
        const int MaxCurves = 8000;
        var endpoints = new List<(Guid Id, int End, Point3d Point)>();
        var objectsById = new Dictionary<Guid, RhinoObject>();
        var scanned = 0;
        var truncated = false;
        foreach (var rhinoObject in document.Objects.GetObjectList(AuditEnumerator(ObjectType.Curve))
                     .OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rhinoObject.Geometry is not Curve curve || curve.IsClosed)
            {
                continue;
            }
            if (++scanned > MaxCurves)
            {
                truncated = true;
                break;
            }
            objectsById[rhinoObject.Id] = rhinoObject;
            endpoints.Add((rhinoObject.Id, 0, curve.PointAtStart));
            endpoints.Add((rhinoObject.Id, 1, curve.PointAtEnd));
        }

        var tree = new RTree();
        for (var index = 0; index < endpoints.Count; index++)
        {
            tree.Insert(endpoints[index].Point, index);
        }
        // Dense endpoint clusters (or a wide band) can still explode the hit count; the pair
        // budget keeps the UI-thread cost bounded and reports Truncated instead of freezing.
        const int MaxPairChecks = 20000;
        var pairChecks = 0;
        var pairs = new Dictionary<string, (Guid A, int EndA, Guid B, int EndB, double Gap)>(StringComparer.Ordinal);
        for (var index = 0; index < endpoints.Count && !truncated; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (idA, endA, pointA) = endpoints[index];
            var hits = new List<int>();
            tree.Search(new Sphere(pointA, band), (_, args) => hits.Add(args.Id));
            foreach (var hit in hits)
            {
                if (++pairChecks > MaxPairChecks)
                {
                    truncated = true;
                    break;
                }
                if (hit <= index)
                {
                    continue;
                }
                var (idB, endB, pointB) = endpoints[hit];
                if (idA == idB)
                {
                    continue;
                }
                var gap = pointA.DistanceTo(pointB);
                if (gap <= tolerance || gap > band)
                {
                    continue;
                }
                var key = string.CompareOrdinal(idA.ToString("D"), idB.ToString("D")) <= 0
                    ? $"{idA:D}|{endA}|{idB:D}|{endB}"
                    : $"{idB:D}|{endB}|{idA:D}|{endA}";
                if (!pairs.ContainsKey(key))
                {
                    pairs[key] = (idA, endA, idB, endB, gap);
                }
            }
        }

        var findings = pairs.Values
            .OrderBy(pair => pair.Gap)
            .ThenBy(pair => pair.A)
            .ThenBy(pair => pair.B)
            .Take(limit + 1)
            .Select(pair => new RhinoAuditFinding(
                Hash($"nearMiss|{pair.A:D}|{pair.EndA}|{pair.B:D}|{pair.EndB}")[..16],
                "nearMissEndpoints",
                new[] { pair.A, pair.B },
                new[] { ToState(objectsById[pair.A]).Fingerprint, ToState(objectsById[pair.B]).Fingerprint },
                pair.Gap,
                $"Curve endpoints {pair.Gap:G4} apart (doc tolerance {tolerance:G4}): " +
                $"end {pair.EndA} of {pair.A:D} vs end {pair.EndB} of {pair.B:D}.",
                new[] { "setEndPoint" },
                new[] { pair.EndA, pair.EndB }))
            .ToList();
        if (findings.Count > limit)
        {
            findings.RemoveAt(findings.Count - 1);
            truncated = true;
        }
        return (findings, scanned, truncated);
    }

    private static bool IsSolidGeometry(GeometryBase geometry) =>
        geometry is Brep or Extrusion;

    /// <summary>
    /// A Brep view of solid geometry. Extrusions are a compact representation of a Brep, so this is
    /// how an extruded box and the same box as a Brep become comparable at all.
    /// </summary>
    private static Brep? AsBrep(GeometryBase geometry) => geometry switch
    {
        Brep brep => brep,
        Extrusion extrusion => extrusion.ToBrep(),
        _ => null
    };

    /// <summary>
    /// Max distance between the two solids' vertex sets, or null when they are not the same solid.
    /// Vertices are sorted canonically (x, then y, then z) and compared pairwise: the same corners
    /// in the same places means the same occupied space, whichever representation each side uses.
    /// A differing vertex COUNT is a definitive no — this analyzer reports occupied-space copies,
    /// and it does not try to decide whether two differently-built shells are "the same".
    /// </summary>
    private static double? SolidVertexDeviation(GeometryBase a, GeometryBase b, double tolerance)
    {
        var brepA = AsBrep(a);
        var brepB = AsBrep(b);
        if (brepA is null || brepB is null || brepA.Vertices.Count != brepB.Vertices.Count ||
            brepA.Vertices.Count == 0)
        {
            return null;
        }
        static List<Point3d> SortedVertices(Brep brep) => brep.Vertices
            .Select(vertex => vertex.Location)
            .OrderBy(point => point.X)
            .ThenBy(point => point.Y)
            .ThenBy(point => point.Z)
            .ToList();
        var pointsA = SortedVertices(brepA);
        var pointsB = SortedVertices(brepB);
        var deviation = 0.0;
        for (var index = 0; index < pointsA.Count; index++)
        {
            var distance = pointsA[index].DistanceTo(pointsB[index]);
            if (distance > tolerance)
            {
                return null;
            }
            deviation = Math.Max(deviation, distance);
        }
        return deviation;
    }

    /// <summary>
    /// Solids that are NOT closed, ranked by how close they are to closing. A Brep with naked edges
    /// is the solid-modelling analogue of a curve gap: it looks like a solid, reports no volume, and
    /// fails boolean operations for a reason nothing on screen shows. The measure is the largest gap
    /// between naked-edge endpoints that ALMOST meet (gap in (tolerance, band]) — that is the number
    /// a user needs, because joining at a slightly larger tolerance closes exactly those.
    ///
    /// Deliberately reports NO fix. Rebuilding a shell is a modelling decision with many valid
    /// answers, and offering a one-click "close it" here would be the kind of confident guess this
    /// project refuses to make. Quarantine stays available for the ones that are genuinely broken.
    /// </summary>
    private (List<RhinoAuditFinding> Findings, int Scanned, bool Truncated) AuditOpenBrepEdges(
        global::Rhino.RhinoDoc document,
        double tolerance,
        double band,
        int limit,
        CancellationToken cancellationToken)
    {
        const int MaxSolids = 4000;
        const int MaxNakedEdgesPerSolid = 512;
        var scanned = 0;
        var truncated = false;
        // Same wall clock as the QC sweeps: a UI-thread analyzer that outlives the bridge budget
        // returns nothing AND leaves Rhino wedged for the next call.
        var budget = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(12);
        var open = new List<(RhinoObject Object, int NakedEdges, double? ClosableGap)>();
        foreach (var rhinoObject in document.Objects
                     .GetObjectList(AuditEnumerator(ObjectType.Brep | ObjectType.Extrusion))
                     .OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rhinoObject.Geometry is null || AsBrep(rhinoObject.Geometry) is not { } brep)
            {
                continue;
            }
            if (++scanned > MaxSolids || budget.Elapsed > deadline)
            {
                truncated = true;
                break;
            }
            // A surface (single face, open by nature) is not a failed solid — only shells that
            // enclose nothing while looking like they should are worth a user's attention.
            if (brep.IsSolid || brep.Faces.Count < 2)
            {
                continue;
            }
            var nakedEdges = brep.Edges
                .Where(edge => edge.Valence == EdgeAdjacency.Naked)
                .Take(MaxNakedEdgesPerSolid)
                .ToList();
            if (nakedEdges.Count == 0)
            {
                continue;
            }
            open.Add((rhinoObject, nakedEdges.Count, ClosableGap(nakedEdges, tolerance, band)));
        }

        // Closable first (an actionable number), then the widest-open shells; both ascending by id
        // so repeated scans of an unchanged document return an identical list.
        var findings = open
            .OrderBy(item => item.ClosableGap is null)
            .ThenBy(item => item.ClosableGap ?? double.MaxValue)
            .ThenBy(item => item.Object.Id.ToString("D"), StringComparer.Ordinal)
            .Take(limit + 1)
            .Select(item => new RhinoAuditFinding(
                Hash($"openBrep|{item.Object.Id:D}")[..16],
                "openBrepEdges",
                new[] { item.Object.Id },
                new[] { ToState(item.Object).Fingerprint },
                item.ClosableGap,
                item.ClosableGap is { } gap
                    ? $"Open solid with {item.NakedEdges} naked edge(s); the widest gap that would " +
                      $"close is {gap:G4} (doc tolerance {tolerance:G4}). Joining at a tolerance " +
                      $"above that gap would close it — verify the shape first."
                    : $"Open solid with {item.NakedEdges} naked edge(s), none within {band:G4} of " +
                      $"closing. This shell is missing geometry, not just tolerance.",
                Array.Empty<string>(),
                null))
            .ToList();
        if (findings.Count > limit)
        {
            findings.RemoveAt(findings.Count - 1);
            truncated = true;
        }
        return (findings, scanned, truncated);
    }

    /// <summary>
    /// The largest naked-edge endpoint gap in (tolerance, band], or null when no two naked ends are
    /// that close. "Largest" on purpose: it is the join tolerance that would close every one of
    /// them, so it is the number the user would actually type.
    /// </summary>
    private static double? ClosableGap(
        IReadOnlyList<BrepEdge> nakedEdges,
        double tolerance,
        double band)
    {
        var ends = new List<Point3d>(nakedEdges.Count * 2);
        foreach (var edge in nakedEdges)
        {
            ends.Add(edge.PointAtStart);
            ends.Add(edge.PointAtEnd);
        }
        // RTree, not a double loop: a shell with the 512-edge cap has 1024 ends, and the all-pairs
        // version is half a million distance checks PER SOLID — on a few hundred solids that alone
        // exhausted the bridge budget. Only ends within the band can matter, which is what the tree
        // answers directly.
        var tree = new RTree();
        for (var index = 0; index < ends.Count; index++)
        {
            tree.Insert(ends[index], index);
        }
        double? widest = null;
        for (var index = 0; index < ends.Count; index++)
        {
            var hits = new List<int>();
            tree.Search(new Sphere(ends[index], band), (_, args) => hits.Add(args.Id));
            foreach (var hit in hits)
            {
                if (hit <= index)
                {
                    continue;
                }
                var gap = ends[index].DistanceTo(ends[hit]);
                if (gap > tolerance && gap <= band)
                {
                    widest = Math.Max(widest ?? 0, gap);
                }
            }
        }
        return widest;
    }

    // Position-coincident near-duplicates SelDup cannot catch (SelDup requires exact matches).
    // Scope: points, curves, and SOLIDS (Brep + Extrusion). The solid scope was added after a real
    // production model turned out to be Brep/Extrusion/block geometry with two top-level curves —
    // a curve-only analyzer scanned one object out of 2484 and reported nothing, which is
    // indistinguishable from "the document is clean".
    //
    // Solids compare across representations on purpose: an extruded box and the same box as a Brep
    // are the same occupied space, and that is exactly the copy a user cannot see. The predicate is
    // the vertex set (same count, same positions within tolerance after a canonical sort) — cheap,
    // deterministic, and it does NOT claim to catch different-topology coincidences (a box built
    // from six trimmed surfaces vs one solid), which stay out of scope rather than being guessed at.
    // Transform-invariant (rotated/mirrored) duplicate detection is also explicitly out of scope.
    // Deletion is ALWAYS a human triage: bake_manager's append mode stacks design options on purpose.
    private (List<RhinoAuditFinding> Findings, int Scanned, bool Truncated) AuditNearDuplicates(
        global::Rhino.RhinoDoc document,
        double tolerance,
        int limit,
        CancellationToken cancellationToken)
    {
        const int MaxObjects = 6000;
        const int MaxPairChecks = 4000;
        var items = new List<(Guid Id, RhinoObject Obj, Point3d Center, double Diagonal)>();
        var scanned = 0;
        var truncated = false;
        foreach (var rhinoObject in document.Objects
                     .GetObjectList(AuditEnumerator(
                         ObjectType.Curve | ObjectType.Point | ObjectType.Brep | ObjectType.Extrusion))
                     .OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rhinoObject.Geometry is null)
            {
                continue;
            }
            if (++scanned > MaxObjects)
            {
                truncated = true;
                break;
            }
            // Accurate boxes are mandatory here: the estimated (control-hull) box of a NURBS
            // rebuild overshoots by the control-polygon sagitta — orders of magnitude beyond the
            // tolerance-scale gates below — silently filtering out exactly the
            // different-representation coincidences this analyzer exists to catch.
            var box = rhinoObject.Geometry.GetBoundingBox(accurate: true);
            items.Add((rhinoObject.Id, rhinoObject, box.Center, box.Diagonal.Length));
        }

        var tree = new RTree();
        for (var index = 0; index < items.Count; index++)
        {
            tree.Insert(items[index].Center, index);
        }
        var pairChecks = 0;
        var duplicates = new Dictionary<string, (Guid A, Guid B, double Measure)>(StringComparer.Ordinal);
        for (var index = 0; index < items.Count && !truncated; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (idA, objectA, centerA, diagonalA) = items[index];
            var hits = new List<int>();
            tree.Search(new Sphere(centerA, Math.Max(tolerance * 2, 1e-9)), (_, args) => hits.Add(args.Id));
            foreach (var hit in hits)
            {
                if (hit <= index)
                {
                    continue;
                }
                var (idB, objectB, _, diagonalB) = items[hit];
                // Solids compare across Brep/Extrusion (same space, different representation); every
                // other kind must match exactly, so a point never pairs with a curve.
                var sameKind = objectA.Geometry.ObjectType == objectB.Geometry.ObjectType ||
                    (IsSolidGeometry(objectA.Geometry) && IsSolidGeometry(objectB.Geometry));
                if (!sameKind || Math.Abs(diagonalA - diagonalB) > tolerance * 4)
                {
                    continue;
                }
                if (++pairChecks > MaxPairChecks)
                {
                    truncated = true;
                    break;
                }
                double? measure = null;
                if (objectA.Geometry is global::Rhino.Geometry.Point pointA &&
                    objectB.Geometry is global::Rhino.Geometry.Point pointB)
                {
                    var distance = pointA.Location.DistanceTo(pointB.Location);
                    if (distance <= tolerance)
                    {
                        measure = distance;
                    }
                }
                else if (objectA.Geometry is Curve curveA && objectB.Geometry is Curve curveB &&
                    Curve.GetDistancesBetweenCurves(
                        curveA, curveB, tolerance,
                        out var maxDistance, out _, out _, out _, out _, out _) &&
                    maxDistance <= tolerance)
                {
                    measure = maxDistance;
                }
                else if (IsSolidGeometry(objectA.Geometry) && IsSolidGeometry(objectB.Geometry))
                {
                    measure = SolidVertexDeviation(objectA.Geometry, objectB.Geometry, tolerance);
                }
                if (measure is null)
                {
                    continue;
                }
                var key = string.CompareOrdinal(idA.ToString("D"), idB.ToString("D")) <= 0
                    ? $"{idA:D}|{idB:D}"
                    : $"{idB:D}|{idA:D}";
                if (!duplicates.ContainsKey(key))
                {
                    duplicates[key] = (idA, idB, measure.Value);
                }
            }
        }

        var objectsById = items.ToDictionary(item => item.Id, item => item.Obj);
        var findings = duplicates.Values
            .OrderBy(pair => pair.Measure)
            .ThenBy(pair => pair.A)
            .ThenBy(pair => pair.B)
            .Take(limit + 1)
            .Select(pair => new RhinoAuditFinding(
                Hash($"nearDup|{pair.A:D}|{pair.B:D}")[..16],
                "nearDuplicates",
                new[] { pair.A, pair.B },
                new[] { ToState(objectsById[pair.A]).Fingerprint, ToState(objectsById[pair.B]).Fingerprint },
                pair.Measure,
                $"Position-coincident duplicates (max deviation {pair.Measure:G4} ≤ tolerance {tolerance:G4}): " +
                $"{pair.A:D} and {pair.B:D}. Which copy to keep is a human decision.",
                new[] { "deleteOneDuplicate" },
                null))
            .ToList();
        if (findings.Count > limit)
        {
            findings.RemoveAt(findings.Count - 1);
            truncated = true;
        }
        return (findings, scanned, truncated);
    }

    // ── QC sweeps ────────────────────────────────────────────────────────────────────────────
    // Three grouped checks the user asked for by name. Every threshold is DERIVED FROM THE
    // DOCUMENT — its absolute tolerance, or the model's own spread — never a number invented here:
    // a millimetre threshold hard-coded for one project is a trap in the next one. Each subkind
    // carries its own budget so a noisy category cannot starve the others, exactly as
    // purgeCandidates does. Everything is report-only triage; nothing proposes a destructive fix.

    /// <summary>Geometry integrity: the defects that survive SelDup and a visual pass.</summary>
    private (List<RhinoAuditFinding> Findings, int Scanned, bool Truncated) AuditGeometryIntegrity(
        global::Rhino.RhinoDoc document,
        double tolerance,
        double band,
        int limit,
        CancellationToken cancellationToken)
    {
        const int MaxObjects = 6000;
        var perKind = Math.Max(1, limit / 5);
        var scanned = 0;
        var truncated = false;
        // The bridge gives a UI-thread operation 45 seconds. A sweep that blows through it freezes
        // Rhino and returns nothing at all, which is strictly worse than partial results — so the
        // whole sweep runs against a wall clock and reports Truncated when it stops early.
        // Checked INSIDE every pass, not merely between them: the first version only tested at
        // pass boundaries, so one slow pass still ran past the bridge budget and left the UI thread
        // wedged for the next call. 12s leaves the bridge's 45s room to answer.
        var budget = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(8);
        bool Expired() => budget.Elapsed > deadline;

        // ESTIMATED boxes here on purpose. An accurate box evaluates every surface, and 2484 of
        // them alone exhausted the bridge budget on a real model. The estimate is a superset of the
        // accurate box, so it is sound for positioning and for the "is this small?" gate below,
        // which confirms its candidates with an accurate box before reporting them.
        var items = new List<(RhinoObject Object, BoundingBox Box)>();
        foreach (var rhinoObject in document.Objects.GetObjectList(AuditEnumerator())
                     .OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rhinoObject.Geometry is null)
            {
                continue;
            }
            if (++scanned > MaxObjects || Expired())
            {
                truncated = true;
                break;
            }
            var box = rhinoObject.Geometry.GetBoundingBox(accurate: false);
            if (box.IsValid)
            {
                items.Add((rhinoObject, box));
            }
        }

        var findings = new List<RhinoAuditFinding>();
        // Cheapest and most universally useful first, so an exhausted budget still returns the
        // findings most likely to matter rather than whichever ran fastest by accident.
        foreach (var pass in new Func<IEnumerable<RhinoAuditFinding>>[]
        {
            () => FindTinyAndSliverObjects(items, tolerance, perKind, Expired, cancellationToken),
            () => FindStrayObjects(items, perKind, cancellationToken),
            () => FindMappingHazards(items, perKind, Expired, cancellationToken),
            () => FindPartialDuplicates(items, tolerance, perKind, Expired, cancellationToken),
            () => FindAdjacentFaceGaps(items, tolerance, band, perKind, Expired, cancellationToken),
        })
        {
            if (Expired())
            {
                truncated = true;
                break;
            }
            findings.AddRange(pass());
        }
        truncated = truncated || Expired();
        return (Bounded(findings, limit, ref truncated), scanned, truncated);
    }

    /// <summary>
    /// Fragments and slivers. "Too small" is a bbox diagonal within an order of magnitude of the
    /// document tolerance — below that a shape cannot be modelled meaningfully anyway. "Too thin"
    /// is a shape whose SHORTEST extent is in that same range while its longest is a thousand times
    /// bigger: the signature of a failed offset, trim, or boolean rather than an intended detail.
    /// </summary>
    private static IEnumerable<RhinoAuditFinding> FindTinyAndSliverObjects(
        IReadOnlyList<(RhinoObject Object, BoundingBox Box)> items,
        double tolerance,
        int limit,
        Func<bool> expired,
        CancellationToken cancellationToken)
    {
        var tiny = new List<RhinoAuditFinding>();
        var slivers = new List<RhinoAuditFinding>();
        var degenerate = tolerance * 10.0;
        foreach (var (rhinoObject, box) in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expired() || (tiny.Count >= limit && slivers.Count >= limit))
            {
                break;
            }
            // The estimated box is a SUPERSET, so anything it calls big really is big: only
            // candidates that already look degenerate pay for an accurate box.
            if (box.Diagonal.Length > degenerate * 4 &&
                new[] { Math.Abs(box.Diagonal.X), Math.Abs(box.Diagonal.Y), Math.Abs(box.Diagonal.Z) }.Min()
                    > degenerate * 4)
            {
                continue;
            }
            var accurate = rhinoObject.Geometry?.GetBoundingBox(accurate: true) ?? box;
            var size = accurate.IsValid ? accurate.Diagonal : box.Diagonal;
            var extents = new[] { Math.Abs(size.X), Math.Abs(size.Y), Math.Abs(size.Z) };
            var longest = extents.Max();
            var diagonal = size.Length;
            if (diagonal <= degenerate)
            {
                if (tiny.Count < limit)
                {
                    tiny.Add(new RhinoAuditFinding(
                        Hash($"tiny|{rhinoObject.Id:D}")[..16],
                        "tinyObject",
                        new[] { rhinoObject.Id },
                        new[] { ToState(rhinoObject).Fingerprint },
                        diagonal,
                        $"Object is {diagonal:G4} across — within 10x the document tolerance " +
                        $"({tolerance:G4}). Almost always a leftover fragment rather than geometry.",
                        Array.Empty<string>(),
                        null));
                }
                continue;
            }
            // Only shapes that CLAIM VOLUME can be too thin. The first version tested "is a Brep",
            // and the live gate returned twelve flat planar surfaces measuring a nanometre thick —
            // which is simply what a surface is. A closed solid that thin is the real defect.
            var shortest = extents.Min();
            if (slivers.Count < limit && shortest <= degenerate && longest >= tolerance * 1000.0 &&
                rhinoObject.Geometry is { } geometry && AsBrep(geometry) is { IsSolid: true })
            {
                slivers.Add(new RhinoAuditFinding(
                    Hash($"sliver|{rhinoObject.Id:D}")[..16],
                    "sliverObject",
                    new[] { rhinoObject.Id },
                    new[] { ToState(rhinoObject).Fingerprint },
                    shortest,
                    $"Solid is {shortest:G4} thin across {longest:G4} — a ratio of 1:{longest / Math.Max(shortest, 1e-12):G3}. " +
                    "A failed offset, trim, or boolean looks like this.",
                    Array.Empty<string>(),
                    null));
            }
        }
        return tiny.Concat(slivers);
    }

    /// <summary>
    /// Objects sitting alone far from everything else — the classic accidental drag or a stray
    /// import. The threshold is the MODEL'S OWN spread, not a distance: an object more than ten
    /// times the median remove from the median centre is an outlier at any project scale.
    /// </summary>
    private static IEnumerable<RhinoAuditFinding> FindStrayObjects(
        IReadOnlyList<(RhinoObject Object, BoundingBox Box)> items,
        int limit,
        CancellationToken cancellationToken)
    {
        // Below this a "median" says nothing; a handful of objects are all equally alone.
        if (items.Count < 12)
        {
            return Array.Empty<RhinoAuditFinding>();
        }
        static double Median(List<double> values)
        {
            values.Sort();
            return values[values.Count / 2];
        }
        var centre = new Point3d(
            Median(items.Select(item => item.Box.Center.X).ToList()),
            Median(items.Select(item => item.Box.Center.Y).ToList()),
            Median(items.Select(item => item.Box.Center.Z).ToList()));
        var distances = items.Select(item => item.Box.Center.DistanceTo(centre)).ToList();
        var medianDistance = Median(new List<double>(distances));
        if (medianDistance <= 0)
        {
            return Array.Empty<RhinoAuditFinding>();
        }
        var threshold = medianDistance * 10.0;
        var strays = new List<RhinoAuditFinding>();
        for (var index = 0; index < items.Count && strays.Count < limit; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var distance = items[index].Box.Center.DistanceTo(centre);
            if (distance <= threshold)
            {
                continue;
            }
            var rhinoObject = items[index].Object;
            strays.Add(new RhinoAuditFinding(
                Hash($"stray|{rhinoObject.Id:D}")[..16],
                "strayObject",
                new[] { rhinoObject.Id },
                new[] { ToState(rhinoObject).Fingerprint },
                distance,
                $"Object sits {distance:G4} from the model centre, more than 10x the median " +
                $"{medianDistance:G4}. Check whether it was dragged or imported by accident.",
                Array.Empty<string>(),
                null));
        }
        return strays;
    }

    /// <summary>
    /// The same solid twice, slightly moved — what SelDup cannot see because nothing matches
    /// exactly. Accepted only when the two agree on VOLUME to within a percent while their centres
    /// differ by more than tolerance: same shape, different place. Vertex-identical copies are the
    /// nearDuplicates analyzer's job and are left to it.
    /// </summary>
    private static IEnumerable<RhinoAuditFinding> FindPartialDuplicates(
        IReadOnlyList<(RhinoObject Object, BoundingBox Box)> items,
        double tolerance,
        int limit,
        Func<bool> expired,
        CancellationToken cancellationToken)
    {
        const int MaxPairChecks = 4000;
        var solids = items
            .Where(item => item.Object.Geometry is Brep or Extrusion)
            .ToList();
        if (solids.Count < 2)
        {
            return Array.Empty<RhinoAuditFinding>();
        }
        var tree = new RTree();
        for (var index = 0; index < solids.Count; index++)
        {
            tree.Insert(solids[index].Box.Center, index);
        }
        // Mass properties on a heavy Brep cost whole seconds each, and the deadline can only stop
        // BETWEEN calls — so the count is capped too. Without this the pass ran 25 seconds past an
        // 8-second budget on a real model, because a handful of expensive solids is enough.
        const int MaxVolumeComputations = 120;
        var volumeComputations = 0;
        var volumes = new double?[solids.Count];
        double? VolumeOf(int index)
        {
            if (volumes[index] is { } cached)
            {
                return cached;
            }
            if (volumeComputations >= MaxVolumeComputations)
            {
                return null;
            }
            var brep = AsBrep(solids[index].Object.Geometry);
            if (brep is null)
            {
                return null;
            }
            volumeComputations++;
            var properties = VolumeMassProperties.Compute(brep);
            if (properties is null || properties.Volume <= 0)
            {
                return null;
            }
            volumes[index] = properties.Volume;
            return properties.Volume;
        }

        var pairChecks = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var findings = new List<RhinoAuditFinding>();
        for (var index = 0; index < solids.Count && findings.Count < limit && !expired(); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (objectA, boxA) = solids[index];
            // Search radius is a fraction of the object's own size, so the same rule reads the
            // same on a door handle and on a tower.
            var radius = boxA.Diagonal.Length * 0.05;
            if (radius <= tolerance)
            {
                continue;
            }
            var hits = new List<int>();
            tree.Search(new Sphere(boxA.Center, radius), (_, args) => hits.Add(args.Id));
            foreach (var hit in hits)
            {
                if (hit <= index || ++pairChecks > MaxPairChecks)
                {
                    continue;
                }
                var (objectB, boxB) = solids[hit];
                var offset = boxA.Center.DistanceTo(boxB.Center);
                if (offset <= tolerance)
                {
                    continue;
                }
                // Cheap gate first: mass properties are expensive, and two solids whose BOXES
                // disagree on volume can never agree on the real thing.
                var boxVolumeA = boxA.Volume;
                var boxVolumeB = boxB.Volume;
                if (boxVolumeA <= 0 || boxVolumeB <= 0 ||
                    Math.Abs(boxVolumeA - boxVolumeB) / Math.Max(boxVolumeA, boxVolumeB) > 0.05)
                {
                    continue;
                }
                var volumeA = VolumeOf(index);
                var volumeB = VolumeOf(hit);
                if (volumeA is not { } a || volumeB is not { } b)
                {
                    continue;
                }
                if (Math.Abs(a - b) / Math.Max(a, b) > 0.01)
                {
                    continue;
                }
                var key = string.CompareOrdinal(objectA.Id.ToString("D"), objectB.Id.ToString("D")) <= 0
                    ? $"{objectA.Id:D}|{objectB.Id:D}"
                    : $"{objectB.Id:D}|{objectA.Id:D}";
                if (!seen.Add(key))
                {
                    continue;
                }
                findings.Add(new RhinoAuditFinding(
                    Hash($"partialDup|{key}")[..16],
                    "partialDuplicate",
                    new[] { objectA.Id, objectB.Id },
                    new[] { ToState(objectA).Fingerprint, ToState(objectB).Fingerprint },
                    offset,
                    $"Two solids of the same volume ({a:G4}) sit {offset:G4} apart — the same shape " +
                    "copied and nudged. Which one is intended is a human decision.",
                    Array.Empty<string>(),
                    null));
                if (findings.Count >= limit)
                {
                    break;
                }
            }
        }
        return findings;
    }

    /// <summary>
    /// Faces of DIFFERENT solids that almost meet: naked edge endpoints a hair apart, in the same
    /// (tolerance, band] window the curve check uses. Two walls that look joined and are not fail
    /// booleans and leak in every downstream export.
    /// </summary>
    private static IEnumerable<RhinoAuditFinding> FindAdjacentFaceGaps(
        IReadOnlyList<(RhinoObject Object, BoundingBox Box)> items,
        double tolerance,
        double band,
        int limit,
        Func<bool> expired,
        CancellationToken cancellationToken)
    {
        const int MaxEnds = 40000;
        const int MaxPairChecks = 20000;
        var ends = new List<(Guid Id, Point3d Point)>();
        var objectsById = new Dictionary<Guid, RhinoObject>();
        foreach (var (rhinoObject, _) in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expired())
            {
                break;
            }
            if (ends.Count >= MaxEnds || AsBrep(rhinoObject.Geometry) is not { } brep || brep.IsSolid)
            {
                continue;
            }
            objectsById[rhinoObject.Id] = rhinoObject;
            foreach (var edge in brep.Edges.Where(edge => edge.Valence == EdgeAdjacency.Naked))
            {
                if (ends.Count >= MaxEnds)
                {
                    break;
                }
                ends.Add((rhinoObject.Id, edge.PointAtStart));
                ends.Add((rhinoObject.Id, edge.PointAtEnd));
            }
        }
        if (ends.Count < 2)
        {
            return Array.Empty<RhinoAuditFinding>();
        }

        var tree = new RTree();
        for (var index = 0; index < ends.Count; index++)
        {
            tree.Insert(ends[index].Point, index);
        }
        var pairChecks = 0;
        var pairs = new Dictionary<string, (Guid A, Guid B, double Gap)>(StringComparer.Ordinal);
        for (var index = 0; index < ends.Count && pairs.Count < limit && !expired(); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (idA, pointA) = ends[index];
            var hits = new List<int>();
            tree.Search(new Sphere(pointA, band), (_, args) => hits.Add(args.Id));
            foreach (var hit in hits)
            {
                if (hit <= index || ++pairChecks > MaxPairChecks)
                {
                    continue;
                }
                var (idB, pointB) = ends[hit];
                if (idA == idB)
                {
                    continue;
                }
                var gap = pointA.DistanceTo(pointB);
                if (gap <= tolerance || gap > band)
                {
                    continue;
                }
                var key = string.CompareOrdinal(idA.ToString("D"), idB.ToString("D")) <= 0
                    ? $"{idA:D}|{idB:D}"
                    : $"{idB:D}|{idA:D}";
                // Keep the WIDEST gap per object pair: it is the join tolerance that closes them.
                if (!pairs.TryGetValue(key, out var existing) || gap > existing.Gap)
                {
                    pairs[key] = (idA, idB, gap);
                }
            }
        }
        return pairs.Values
            .OrderByDescending(pair => pair.Gap)
            .Take(limit)
            .Select(pair => new RhinoAuditFinding(
                Hash($"faceGap|{pair.A:D}|{pair.B:D}")[..16],
                "adjacentFaceGap",
                new[] { pair.A, pair.B },
                new[] { ToState(objectsById[pair.A]).Fingerprint, ToState(objectsById[pair.B]).Fingerprint },
                pair.Gap,
                $"Two open solids have naked edges {pair.Gap:G4} apart (doc tolerance {tolerance:G4}) — " +
                "they look joined and are not.",
                Array.Empty<string>(),
                null))
            .ToList();
    }

    /// <summary>
    /// Texture-mapping hazards. More than one mapping channel is reported outright — it is
    /// ambiguous for every downstream renderer. Missing mapping is reported ONLY for objects that
    /// carry their own render material: without a material there is nothing to map, and flagging
    /// every untextured object would bury the real ones.
    /// </summary>
    private static IEnumerable<RhinoAuditFinding> FindMappingHazards(
        IReadOnlyList<(RhinoObject Object, BoundingBox Box)> items,
        int limit,
        Func<bool> expired,
        CancellationToken cancellationToken)
    {
        var multiple = new List<RhinoAuditFinding>();
        var missing = new List<RhinoAuditFinding>();
        foreach (var (rhinoObject, _) in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expired() || (multiple.Count >= limit && missing.Count >= limit))
            {
                break;
            }
            int[] channels;
            try
            {
                channels = rhinoObject.GetTextureChannels() ?? Array.Empty<int>();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Some object types have no mapping table at all; that is not a finding.
                continue;
            }
            if (channels.Length > 1 && multiple.Count < limit)
            {
                multiple.Add(new RhinoAuditFinding(
                    Hash($"mapChannels|{rhinoObject.Id:D}")[..16],
                    "multipleMappingChannels",
                    new[] { rhinoObject.Id },
                    new[] { ToState(rhinoObject).Fingerprint },
                    channels.Length,
                    $"Object carries {channels.Length} texture mapping channels " +
                    $"({string.Join(", ", channels)}). Which one renders is ambiguous downstream.",
                    Array.Empty<string>(),
                    null));
                continue;
            }
            if (channels.Length == 0 && missing.Count < limit &&
                rhinoObject.Attributes.MaterialSource == ObjectMaterialSource.MaterialFromObject &&
                rhinoObject.Attributes.MaterialIndex >= 0)
            {
                missing.Add(new RhinoAuditFinding(
                    Hash($"noMapping|{rhinoObject.Id:D}")[..16],
                    "noTextureMapping",
                    new[] { rhinoObject.Id },
                    new[] { ToState(rhinoObject).Fingerprint },
                    null,
                    "Object has its own render material but no texture mapping, so it falls back " +
                    "to surface parameters — usually not what a material with textures expects.",
                    Array.Empty<string>(),
                    null));
            }
        }
        return multiple.Concat(missing);
    }

    /// <summary>
    /// Layer integrity: emptiness, names that break name-based selection, missing materials, and
    /// layers that exist only to hold block geometry.
    /// </summary>
    private (List<RhinoAuditFinding> Findings, int Scanned, bool Truncated) AuditLayerIntegrity(
        global::Rhino.RhinoDoc document,
        int limit,
        CancellationToken cancellationToken)
    {
        var perKind = Math.Max(1, limit / 4);
        var truncated = false;
        var scanned = 0;

        var topLevelLayers = new HashSet<int>();
        foreach (var rhinoObject in document.Objects.GetObjectList(AuditEnumerator()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            topLevelLayers.Add(rhinoObject.Attributes.LayerIndex);
        }
        var occupiedLayers = new HashSet<int>();
        foreach (var rhinoObject in EnumerateLayerOccupants(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            occupiedLayers.Add(rhinoObject.Attributes.LayerIndex);
        }
        var parentIds = new HashSet<Guid>();
        foreach (var layer in document.Layers)
        {
            if (layer is not null && !layer.IsDeleted && layer.ParentLayerId != Guid.Empty)
            {
                parentIds.Add(layer.ParentLayerId);
            }
        }

        var empty = new List<RhinoAuditFinding>();
        var nameHazards = new List<RhinoAuditFinding>();
        var noMaterial = new List<RhinoAuditFinding>();
        var blockOnly = new List<RhinoAuditFinding>();
        // Sibling names that differ only by case make "select by layer name" ambiguous.
        var siblingNames = new Dictionary<string, List<Layer>>(StringComparer.Ordinal);
        foreach (var layer in document.Layers)
        {
            if (layer is null || layer.IsDeleted)
            {
                continue;
            }
            var key = $"{layer.ParentLayerId:D}|{layer.Name.Trim().ToUpperInvariant()}";
            if (!siblingNames.TryGetValue(key, out var bucket))
            {
                siblingNames[key] = bucket = [];
            }
            bucket.Add(layer);
        }

        foreach (var layer in document.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer is null || layer.IsDeleted)
            {
                continue;
            }
            scanned++;
            var fingerprint = LayerFingerprint(layer);
            if (empty.Count < perKind && !occupiedLayers.Contains(layer.Index) &&
                !parentIds.Contains(layer.Id) && layer.Index != document.Layers.CurrentLayerIndex)
            {
                empty.Add(new RhinoAuditFinding(
                    Hash($"emptyLayer|{layer.Id:D}")[..16],
                    "emptyLayer",
                    new[] { layer.Id },
                    new[] { fingerprint },
                    null,
                    $"Layer '{layer.FullPath}' is an empty leaf (no objects including hidden and " +
                    "block members, and no children).",
                    new[] { "deleteLayer" },
                    null));
            }
            if (blockOnly.Count < perKind && occupiedLayers.Contains(layer.Index) &&
                !topLevelLayers.Contains(layer.Index))
            {
                blockOnly.Add(new RhinoAuditFinding(
                    Hash($"blockOnlyLayer|{layer.Id:D}")[..16],
                    "blockOnlyLayer",
                    new[] { layer.Id },
                    new[] { fingerprint },
                    null,
                    $"Layer '{layer.FullPath}' holds only block-definition geometry — nothing is " +
                    "placed on it directly. Intentional for a block library, a leftover otherwise.",
                    Array.Empty<string>(),
                    null));
            }
            if (nameHazards.Count < perKind)
            {
                var name = layer.Name;
                var trimmed = name.Trim();
                var caseKey = $"{layer.ParentLayerId:D}|{trimmed.ToUpperInvariant()}";
                var caseTwins = siblingNames.TryGetValue(caseKey, out var bucket) ? bucket.Count : 1;
                string? hazard = null;
                if (trimmed.Length == 0)
                {
                    hazard = "the name is blank";
                }
                else if (!string.Equals(name, trimmed, StringComparison.Ordinal))
                {
                    hazard = "the name has leading or trailing whitespace, which name-based " +
                        "selection will not match";
                }
                else if (caseTwins > 1)
                {
                    hazard = $"{caseTwins} sibling layers differ only by letter case, so selecting " +
                        "by name is ambiguous";
                }
                if (hazard is not null)
                {
                    nameHazards.Add(new RhinoAuditFinding(
                        Hash($"layerName|{layer.Id:D}")[..16],
                        "layerNameHazard",
                        new[] { layer.Id },
                        new[] { fingerprint },
                        null,
                        $"Layer '{layer.FullPath}': {hazard}.",
                        Array.Empty<string>(),
                        null));
                }
            }
            if (noMaterial.Count < perKind && layer.RenderMaterialIndex < 0 &&
                occupiedLayers.Contains(layer.Index))
            {
                noMaterial.Add(new RhinoAuditFinding(
                    Hash($"layerMaterial|{layer.Id:D}")[..16],
                    "layerWithoutMaterial",
                    new[] { layer.Id },
                    new[] { fingerprint },
                    null,
                    $"Layer '{layer.FullPath}' holds geometry but has no render material assigned.",
                    Array.Empty<string>(),
                    null));
            }
        }

        var findings = empty.Concat(nameHazards).Concat(blockOnly).Concat(noMaterial).ToList();
        return (Bounded(findings, limit, ref truncated), scanned, truncated);
    }

    /// <summary>
    /// Layer-curation fact collection: one finding per layer that is NOT yet semantically labeled
    /// (missing gptino.canonical or gptino.material user text). The adapter reports FACTS — name,
    /// color, occupancy incl. block members, existing labels; matching names against alias tables
    /// and proposing colors is the AgentHost's job. Labeled layers drop out of the findings, so
    /// re-running this audit after an apply is the clean-state observation the live gate crosses
    /// against GET /layers.
    /// </summary>
    private (List<RhinoAuditFinding> Findings, int Scanned, bool Truncated) AuditLayerSemantics(
        global::Rhino.RhinoDoc document,
        int limit,
        CancellationToken cancellationToken)
    {
        var truncated = false;
        var scanned = 0;

        // Occupancy is split by provenance: top-level ids double as viewport-focusable samples
        // (a layer GUID cannot be selected), block members count separately so a block-only
        // layer reads occupied — the deleteLayer scope-gap lesson.
        var topLevelByLayer = new Dictionary<int, List<Guid>>();
        foreach (var rhinoObject in document.Objects.GetObjectList(AuditEnumerator()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!topLevelByLayer.TryGetValue(rhinoObject.Attributes.LayerIndex, out var bucket))
            {
                topLevelByLayer[rhinoObject.Attributes.LayerIndex] = bucket = [];
            }
            bucket.Add(rhinoObject.Id);
        }
        var occupantsByLayer = new Dictionary<int, int>();
        foreach (var rhinoObject in EnumerateLayerOccupants(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            occupantsByLayer[rhinoObject.Attributes.LayerIndex] =
                occupantsByLayer.GetValueOrDefault(rhinoObject.Attributes.LayerIndex) + 1;
        }
        var parentIds = new HashSet<Guid>();
        foreach (var layer in document.Layers)
        {
            if (layer is not null && !layer.IsDeleted && layer.ParentLayerId != Guid.Empty)
            {
                parentIds.Add(layer.ParentLayerId);
            }
        }

        var findings = new List<RhinoAuditFinding>();
        foreach (var layer in document.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer is null || layer.IsDeleted)
            {
                continue;
            }
            scanned++;
            // An empty LEAF holds nothing and organises nothing, so labelling it is noise — this
            // is how Rhino's own Default layer ended up proposed as concrete. An empty PARENT is
            // different: it scopes its children (철골 declares the material beneath it), so it
            // stays. Same "empty leaf" test layerIntegrity uses for its delete proof.
            if (occupantsByLayer.GetValueOrDefault(layer.Index) == 0 && !parentIds.Contains(layer.Id))
            {
                continue;
            }
            var userText = ReadVinoUserText(layer);
            var labeled = userText is not null &&
                userText.ContainsKey(LayerCanonicalKey) &&
                userText.ContainsKey(LayerMaterialKey);
            if (labeled)
            {
                continue;
            }
            var topLevel = topLevelByLayer.TryGetValue(layer.Index, out var ids) ? ids : [];
            var total = occupantsByLayer.GetValueOrDefault(layer.Index);
            findings.Add(new RhinoAuditFinding(
                Hash($"layerSemantics|{layer.Id:D}")[..16],
                "layerSemantics",
                new[] { layer.Id },
                new[] { LayerFingerprint(layer) },
                null,
                $"Layer '{layer.FullPath}' has no semantic label" +
                (userText is { Count: > 0 } ? " (a partial vino label set exists)" : string.Empty) + ".",
                new[] { "updateLayer" },
                null,
                new RhinoLayerFacts(
                    layer.FullPath,
                    layer.Name,
                    layer.Color.ToArgb(),
                    layer.RenderMaterialIndex >= 0 ? layer.RenderMaterial?.Name : null,
                    topLevel.Count,
                    Math.Max(0, total - topLevel.Count),
                    topLevel.Take(5).ToArray(),
                    userText)));
        }
        return (Bounded(findings, limit, ref truncated), scanned, truncated);
    }

    /// <summary>The layer's "gptino."-namespaced user text, or null when it has none.</summary>
    private static IReadOnlyDictionary<string, string>? ReadVinoUserText(Layer layer)
    {
        var strings = layer.GetUserStrings();
        if (strings is null || strings.Count == 0)
        {
            return null;
        }
        Dictionary<string, string>? result = null;
        foreach (var key in strings.AllKeys)
        {
            if (key is null || !key.StartsWith(LayerUserTextPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var value = strings[key];
            // Whitespace-only is as meaningless as absent — and a stray stored " " must never
            // satisfy the labeled-check, or the layer drops out of the audit with an unusable
            // label no re-run can surface again.
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            (result ??= new Dictionary<string, string>(StringComparer.Ordinal))[key] = value;
        }
        return result;
    }

    /// <summary>
    /// Block integrity: definitions holding nothing, one definition placed across several layers
    /// (usually a slip), and definitions whose members sit on layers nothing else uses — the
    /// signature of geometry pulled in from CAD.
    /// </summary>
    private (List<RhinoAuditFinding> Findings, int Scanned, bool Truncated) AuditBlockIntegrity(
        global::Rhino.RhinoDoc document,
        int limit,
        CancellationToken cancellationToken)
    {
        var perKind = Math.Max(1, limit / 3);
        var truncated = false;
        var scanned = 0;

        var topLevelLayers = new HashSet<int>();
        foreach (var rhinoObject in document.Objects.GetObjectList(AuditEnumerator()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            topLevelLayers.Add(rhinoObject.Attributes.LayerIndex);
        }
        // Instance placements per definition, so "one block on several layers" is answerable.
        var layersByDefinition = new Dictionary<int, HashSet<int>>();
        foreach (var rhinoObject in document.Objects
                     .GetObjectList(AuditEnumerator(ObjectType.InstanceReference)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rhinoObject is not InstanceObject instance || instance.InstanceDefinition is null)
            {
                continue;
            }
            var definitionIndex = instance.InstanceDefinition.Index;
            if (!layersByDefinition.TryGetValue(definitionIndex, out var layers))
            {
                layersByDefinition[definitionIndex] = layers = [];
            }
            layers.Add(rhinoObject.Attributes.LayerIndex);
        }

        // How many DEFINITIONS put members on each layer. A layer several blocks share is a
        // deliberate block-library convention, not import residue — the live gate flagged seven
        // healthy blocks before this counted. Only a layer used by exactly one definition, and by
        // nothing at top level, carries the CAD-import signature.
        var definitionsPerLayer = new Dictionary<int, int>();
        foreach (var definition in document.InstanceDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition is null || definition.IsDeleted)
            {
                continue;
            }
            foreach (var layerIndex in definition.GetObjects()
                         .Where(member => member is not null)
                         .Select(member => member.Attributes.LayerIndex)
                         .Distinct())
            {
                definitionsPerLayer[layerIndex] = definitionsPerLayer.GetValueOrDefault(layerIndex) + 1;
            }
        }

        var emptyDefinitions = new List<RhinoAuditFinding>();
        var splitPlacements = new List<RhinoAuditFinding>();
        var foreignLayers = new List<RhinoAuditFinding>();
        foreach (var definition in document.InstanceDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition is null || definition.IsDeleted)
            {
                continue;
            }
            scanned++;
            var fingerprint = Hash($"blockDefinition|{definition.Id:D}|{definition.Name}|{definition.ObjectCount}");
            if (emptyDefinitions.Count < perKind && definition.ObjectCount == 0)
            {
                emptyDefinitions.Add(new RhinoAuditFinding(
                    Hash($"emptyBlock|{definition.Id:D}")[..16],
                    "emptyBlockDefinition",
                    new[] { definition.Id },
                    new[] { fingerprint },
                    0,
                    $"Block definition '{definition.Name}' contains no objects. Every instance of " +
                    "it draws nothing.",
                    new[] { "purgeBlockDefinition" },
                    null));
                continue;
            }
            if (splitPlacements.Count < perKind &&
                layersByDefinition.TryGetValue(definition.Index, out var placementLayers) &&
                placementLayers.Count > 1)
            {
                var names = placementLayers
                    .Select(index => document.Layers.FindIndex(index)?.FullPath ?? $"#{index}")
                    .OrderBy(name => name, StringComparer.Ordinal);
                splitPlacements.Add(new RhinoAuditFinding(
                    Hash($"blockSplit|{definition.Id:D}")[..16],
                    "blockInstancesSplitAcrossLayers",
                    new[] { definition.Id },
                    new[] { fingerprint },
                    placementLayers.Count,
                    $"Block '{definition.Name}' is placed on {placementLayers.Count} different " +
                    $"layers ({string.Join(", ", names)}). Usually one of them is a slip.",
                    Array.Empty<string>(),
                    null));
            }
            if (foreignLayers.Count < perKind)
            {
                var stranger = definition.GetObjects()
                    .Where(member => member is not null)
                    .Select(member => member.Attributes.LayerIndex)
                    .Distinct()
                    .Where(index => !topLevelLayers.Contains(index) &&
                        definitionsPerLayer.GetValueOrDefault(index) <= 1)
                    .Select(index => document.Layers.FindIndex(index)?.FullPath ?? $"#{index}")
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
                if (stranger.Count > 0)
                {
                    foreignLayers.Add(new RhinoAuditFinding(
                        Hash($"blockForeignLayer|{definition.Id:D}")[..16],
                        "foreignLayerInBlock",
                        new[] { definition.Id },
                        new[] { fingerprint },
                        stranger.Count,
                        $"Block '{definition.Name}' has members on {stranger.Count} layer(s) that " +
                        $"nothing else uses — no top-level object and no other block " +
                        $"({string.Join(", ", stranger.Take(4))}" +
                        $"{(stranger.Count > 4 ? ", …" : string.Empty)}). Typical of CAD imports.",
                        Array.Empty<string>(),
                        null));
                }
            }
        }

        var findings = emptyDefinitions.Concat(splitPlacements).Concat(foreignLayers).ToList();
        return (Bounded(findings, limit, ref truncated), scanned, truncated);
    }

    /// <summary>Caps a grouped finding list at the caller's limit, reporting truncation honestly.</summary>
    private static List<RhinoAuditFinding> Bounded(
        List<RhinoAuditFinding> findings,
        int limit,
        ref bool truncated)
    {
        if (findings.Count <= limit)
        {
            return findings;
        }
        truncated = true;
        return findings.Take(limit).ToList();
    }

    // Junk census: unused block definitions (no references anywhere — not placed in the document
    // and not nested inside another definition), empty leaf layers (no objects including hidden
    // AND block-definition members, no children, not current), and invalid objects. Bad objects
    // propose QUARANTINE, never deletion — they are often repairable. Each subkind gets its own
    // finding budget so a junk-heavy category cannot starve the others.
    private (List<RhinoAuditFinding> Findings, int Scanned, bool Truncated) AuditPurgeCandidates(
        global::Rhino.RhinoDoc document,
        int limit,
        CancellationToken cancellationToken)
    {
        var scanned = 0;
        var truncated = false;
        var unusedBlocks = new List<RhinoAuditFinding>();
        var emptyLayers = new List<RhinoAuditFinding>();
        var badObjects = new List<RhinoAuditFinding>();

        foreach (var definition in document.InstanceDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition is null || definition.IsDeleted)
            {
                continue;
            }
            scanned++;
            // InUse(1) sees top-level + nested references IN THE DOCUMENT only; InUse(2) sees
            // references inside other definitions. Without the second check, an unplaced Root
            // nesting Child would flag BOTH in one pass, and purging Child first would corrupt
            // Root. With it, chains genuinely surface root-first.
            if (definition.InUse(1) || definition.InUse(2))
            {
                continue;
            }
            if (unusedBlocks.Count > limit)
            {
                truncated = true;
                break;
            }
            unusedBlocks.Add(new RhinoAuditFinding(
                Hash($"unusedBlock|{definition.Id:D}")[..16],
                "unusedBlockDefinition",
                new[] { definition.Id },
                Array.Empty<string>(),
                null,
                $"Block definition '{definition.Name}' has no references anywhere (not placed, not " +
                $"nested in another definition); {definition.ObjectCount} member object(s).",
                new[] { "purgeBlockDefinition" }));
        }

        // Layer census must include block-definition members: a block-library layer holding only
        // member geometry is IN USE, not empty.
        var layersWithObjects = new HashSet<int>();
        foreach (var rhinoObject in EnumerateLayerOccupants(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            layersWithObjects.Add(rhinoObject.Attributes.LayerIndex);
        }
        var parentIds = new HashSet<Guid>();
        foreach (var layer in document.Layers)
        {
            if (layer is not null && !layer.IsDeleted && layer.ParentLayerId != Guid.Empty)
            {
                parentIds.Add(layer.ParentLayerId);
            }
        }
        foreach (var layer in document.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer is null || layer.IsDeleted)
            {
                continue;
            }
            scanned++;
            if (layersWithObjects.Contains(layer.Index) ||
                parentIds.Contains(layer.Id) ||
                layer.Index == document.Layers.CurrentLayerIndex)
            {
                continue;
            }
            if (emptyLayers.Count > limit)
            {
                truncated = true;
                break;
            }
            emptyLayers.Add(new RhinoAuditFinding(
                Hash($"emptyLayer|{layer.Id:D}")[..16],
                "emptyLayer",
                new[] { layer.Id },
                new[] { LayerFingerprint(layer) },
                null,
                $"Layer '{layer.FullPath}' is an empty leaf (no objects — including hidden and " +
                "block members — and no children).",
                new[] { "deleteLayer" }));
        }

        const int MaxValidityChecks = 8000;
        var validityChecks = 0;
        foreach (var rhinoObject in document.Objects.GetObjectList(AuditEnumerator())
                     .OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            if (++validityChecks > MaxValidityChecks)
            {
                truncated = true;
                break;
            }
            if (rhinoObject.Geometry is null || rhinoObject.Geometry.IsValidWithLog(out var log))
            {
                continue;
            }
            if (badObjects.Count > limit)
            {
                truncated = true;
                break;
            }
            var reason = (log ?? string.Empty).Split('\n').FirstOrDefault()?.Trim();
            badObjects.Add(new RhinoAuditFinding(
                Hash($"badObject|{rhinoObject.Id:D}")[..16],
                "badObject",
                new[] { rhinoObject.Id },
                new[] { BadObjectFingerprint(rhinoObject) },
                null,
                $"Invalid geometry ({rhinoObject.Geometry.ObjectType}): " +
                $"{(string.IsNullOrEmpty(reason) ? "IsValidWithLog failed" : reason)} " +
                "— quarantine, do not delete (often repairable).",
                new[] { "quarantineToLayer" }));
        }

        var ordered = badObjects.Concat(emptyLayers).Concat(unusedBlocks)
            .OrderBy(finding => finding.Kind, StringComparer.Ordinal)
            .ThenBy(finding => finding.FindingId, StringComparer.Ordinal)
            .Take(limit + 1)
            .ToList();
        if (ordered.Count > limit)
        {
            ordered.RemoveAt(ordered.Count - 1);
            truncated = true;
        }
        return (ordered, scanned, truncated);
    }

    // ToState serializes geometry via ToJSON, which can throw on the very invalid geometry this
    // subkind reports; the bad-object fingerprint therefore hashes identity + attributes only.
    private static string BadObjectFingerprint(RhinoObject rhinoObject)
    {
        try
        {
            return ToState(rhinoObject).Fingerprint;
        }
        catch
        {
            var attributesJson = rhinoObject.Attributes.ToJSON(new SerializationOptions());
            return Hash($"badObject|{rhinoObject.Id:D}\n{attributesJson}");
        }
    }

    protected override Task<RhinoSceneObjectState> InspectObjectCoreAsync(
        global::Rhino.RhinoDoc document,
        Guid objectId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rhinoObject = document.Objects.FindId(objectId)
            ?? throw new KeyNotFoundException($"Rhino object {objectId:D} was not found.");
        return Task.FromResult(ToState(rhinoObject));
    }

    protected override Task<RhinoSceneMutationResult> CreatePrimitiveCoreAsync(
        global::Rhino.RhinoDoc document,
        CreateRhinoPrimitiveRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireOperationId(request.OperationId);
        if (request.ObjectId == Guid.Empty)
        {
            throw new InvalidOperationException("ObjectId is required for primitive creation.");
        }
        if (string.IsNullOrWhiteSpace(request.LogicalEntityId))
        {
            throw new InvalidOperationException("LogicalEntityId is required for primitive creation.");
        }
        if (document.Objects.FindId(request.ObjectId) is not null)
        {
            throw new InvalidOperationException($"Rhino object {request.ObjectId:D} already exists.");
        }
        EnsureLogicalEntityAvailable(document, request.LogicalEntityId, exceptObjectId: null);

        using var geometry = CreatePrimitiveGeometry(request);
        if (!geometry.IsValid)
        {
            throw new InvalidOperationException($"The {request.Kind} primitive is not valid Rhino geometry.");
        }
        var attributes = CreatePrimitiveAttributes(document, request);

        var undo = document.BeginUndoRecord($"Vino: {request.OperationId}");
        if (undo == 0)
        {
            throw new InvalidOperationException("Rhino could not start an undo record for primitive creation.");
        }
        var addedId = Guid.Empty;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            addedId = document.Objects.Add(geometry, attributes);
            if (addedId == Guid.Empty)
            {
                throw new InvalidOperationException("Rhino rejected the primitive geometry.");
            }
            if (addedId != request.ObjectId)
            {
                throw new InvalidOperationException(
                    $"Rhino returned object {addedId:D} instead of requested identity {request.ObjectId:D}.");
            }

            var afterObject = document.Objects.FindId(request.ObjectId)
                ?? throw new InvalidOperationException("Rhino object disappeared after primitive creation.");
            var after = ToState(afterObject);
            document.Views.Redraw();
            var diagnostics = new[]
            {
                new BridgeDiagnostic(
                    BridgeDiagnosticSeverity.Information,
                    "rhino_primitive_created",
                    $"Created {request.Kind} primitive as object {request.ObjectId:D}.",
                    request.ObjectId),
            };
            return Task.FromResult(new RhinoSceneMutationResult(
                request.OperationId,
                Changed: true,
                BeforeFingerprint: null,
                after.Fingerprint,
                request.ObjectId,
                after,
                diagnostics));
        }
        catch (Exception mutationFailure) when (addedId != Guid.Empty)
        {
            var rolledBack = document.Objects.FindId(addedId) is null ||
                document.Objects.Delete(addedId, quiet: true);
            if (!rolledBack || document.Objects.FindId(addedId) is not null)
            {
                throw new AggregateException(
                    $"Primitive creation failed and object {addedId:D} could not be rolled back; use Rhino Undo.",
                    mutationFailure);
            }
            throw;
        }
        finally
        {
            if (undo != 0)
            {
                document.EndUndoRecord(undo);
            }
        }
    }

    protected override Task<RhinoUpsertValidationResult> ValidateUpsertObjectCoreAsync(
        global::Rhino.RhinoDoc document,
        UpsertRhinoObjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var prepared = PrepareUpsert(document, request);
        return Task.FromResult(new RhinoUpsertValidationResult(
            request.OperationId,
            request.ObjectId,
            prepared.Geometry.ObjectType.ToString(),
            prepared.Existing is not null,
            prepared.Before?.Fingerprint,
            IsValid: true));
    }

    protected override Task<RhinoSceneMutationResult> UpsertObjectCoreAsync(
        global::Rhino.RhinoDoc document,
        UpsertRhinoObjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var prepared = PrepareUpsert(document, request);
        var geometry = prepared.Geometry;
        var existing = prepared.Existing;
        var before = prepared.Before;
        var attributes = prepared.Attributes;
        var undo = document.BeginUndoRecord($"Vino: {request.OperationId}");
        if (undo == 0)
        {
            throw new InvalidOperationException("Rhino could not start an undo record for object upsert.");
        }
        try
        {
            Guid objectId;
            if (existing is null)
            {
                objectId = document.Objects.Add(geometry, attributes);
                if (objectId == Guid.Empty)
                {
                    throw new InvalidOperationException("Rhino rejected the new geometry object.");
                }
                if (objectId != request.ObjectId)
                {
                    document.Objects.Delete(objectId, quiet: true);
                    throw new InvalidOperationException(
                        "Rhino could not preserve the requested ObjectId; the unexpected object was removed.");
                }
            }
            else
            {
                objectId = existing.Id;
                using var oldGeometry = existing.Geometry.Duplicate();
                using var oldAttributes = existing.Attributes.Duplicate();
                var geometryReplaced = false;
                try
                {
                    if (!document.Objects.Replace(objectId, geometry, ignoreModes: false))
                    {
                        throw new InvalidOperationException($"Rhino could not replace object {objectId:D}.");
                    }
                    geometryReplaced = true;
                    if (!document.Objects.ModifyAttributes(objectId, attributes, quiet: true))
                    {
                        throw new InvalidOperationException(
                            $"Rhino could not update attributes for {objectId:D}.");
                    }
                }
                catch (Exception mutationFailure) when (geometryReplaced)
                {
                    var restoredGeometry = document.Objects.Replace(
                        objectId,
                        oldGeometry,
                        ignoreModes: true);
                    var restoredAttributes = document.Objects.ModifyAttributes(
                        objectId,
                        oldAttributes,
                        quiet: true);
                    if (!restoredGeometry || !restoredAttributes)
                    {
                        throw new AggregateException(
                            $"Rhino object {objectId:D} update failed and rollback was incomplete; use Rhino Undo.",
                            mutationFailure);
                    }
                    throw;
                }
            }

            var afterObject = document.Objects.FindId(objectId)
                ?? throw new InvalidOperationException("Rhino object disappeared after upsert.");
            if (afterObject.Id != objectId || afterObject.Id != request.ObjectId)
            {
                throw new InvalidOperationException("Rhino object identity changed during upsert.");
            }
            var after = ToState(afterObject);
            document.Views.Redraw();
            return Task.FromResult(new RhinoSceneMutationResult(
                request.OperationId,
                before is null || !string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal),
                before?.Fingerprint,
                after.Fingerprint,
                objectId,
                after));
        }
        finally
        {
            document.EndUndoRecord(undo);
        }
    }

    /// <summary>
    /// The human-wins default-deny: CAS fingerprints prove "unchanged since inspected", never
    /// "user consents". Objects without a Vino provenance stamp are the user's own geometry —
    /// destroying or mutating them requires a server-injected approval (minted when the user
    /// approves the change on the panel), not just a fingerprint.
    /// </summary>
    private static void RequireProvenanceOrApproval(RhinoObject existing, bool approved, string verb)
    {
        if (approved)
        {
            return;
        }
        var attributes = existing.Attributes;
        var hasProvenance =
            !string.IsNullOrEmpty(attributes.GetUserString(LogicalEntityKey)) ||
            !string.IsNullOrEmpty(attributes.GetUserString(BakeFamilyKey));
        if (!hasProvenance)
        {
            // Typed code, not a bare exception: the refusal happens BEFORE any document change, so
            // the executor can classify it as a deterministic failure instead of the
            // "outcome unknown -> recoveryRequired" bucket every mid-write fault lands in.
            throw new BridgeProtocolException(
                ApprovalRequiredCode,
                $"Rhino object {existing.Id:D} was not created by Vino; {verb} it requires the " +
                "user's explicit approval. Present the change (naming this object) and resubmit " +
                "with the approval grant the panel issues. No change was applied.");
        }
    }

    protected override Task<RhinoSceneMutationResult> DeleteObjectCoreAsync(
        global::Rhino.RhinoDoc document,
        DeleteRhinoObjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
        {
            throw new InvalidOperationException("ObjectId and ExpectedFingerprint are required for deletion.");
        }
        var existing = document.Objects.FindId(request.ObjectId)
            ?? throw new KeyNotFoundException($"Rhino object {request.ObjectId:D} was not found.");
        var before = ToState(existing);
        if (!string.Equals(before.Fingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rhino object changed after the request snapshot.");
        }
        RequireProvenanceOrApproval(existing, request.Approved, "deleting");

        var undo = document.BeginUndoRecord($"Vino: {request.OperationId}");
        try
        {
            if (!document.Objects.Delete(request.ObjectId, quiet: true))
            {
                throw new InvalidOperationException($"Rhino could not delete object {request.ObjectId:D}.");
            }
            document.Views.Redraw();
            return Task.FromResult(new RhinoSceneMutationResult(
                request.OperationId,
                Changed: true,
                before.Fingerprint,
                AfterFingerprint: null,
                request.ObjectId));
        }
        finally
        {
            if (undo != 0)
            {
                document.EndUndoRecord(undo);
            }
        }
    }

    protected override Task<RhinoSceneMutationResult> EnsureLayerCoreAsync(
        global::Rhino.RhinoDoc document,
        EnsureRhinoLayerRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        if (string.IsNullOrWhiteSpace(request.FullPath))
        {
            throw new InvalidOperationException("Layer full path is required.");
        }

        var normalizedPath = request.FullPath.Trim();
        var byPath = document.Layers.FindByFullPath(normalizedPath, -1);
        var byId = request.LayerId == Guid.Empty
            ? -1
            : document.Layers.Find(request.LayerId, ignoreDeletedLayers: false, notFoundReturnValue: -1);
        if (byId >= 0 && byPath >= 0 && byId != byPath)
        {
            throw new InvalidOperationException(
                $"LayerId {request.LayerId:D} and path '{normalizedPath}' identify different layers.");
        }
        if (request.LayerId != Guid.Empty && byId < 0 && byPath >= 0)
        {
            throw new InvalidOperationException(
                $"Layer path '{normalizedPath}' already exists with another identity.");
        }
        if (byId >= 0 &&
            !string.Equals(document.Layers[byId].FullPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "EnsureLayer does not rename or re-parent an existing layer without a fingerprinted operation.");
        }

        var existing = byId >= 0 ? byId : byPath;
        var before = existing >= 0 ? LayerFingerprint(document.Layers[existing]) : null;
        var leafName = normalizedPath.Split(new[] { "::" }, StringSplitOptions.None)[^1].Trim();
        if (string.IsNullOrWhiteSpace(leafName))
        {
            throw new InvalidOperationException("Layer leaf name is required.");
        }

        var parentLayerId = request.ParentLayerId.GetValueOrDefault();
        if (parentLayerId != Guid.Empty &&
            document.Layers.Find(parentLayerId, ignoreDeletedLayers: false, notFoundReturnValue: -1) < 0)
        {
            throw new KeyNotFoundException($"Parent layer {parentLayerId:D} was not found.");
        }
        // A nested path implies its ancestors. Without this, "Vino::Quarantine" silently created
        // a TOP-LEVEL layer named "Quarantine" and reported success — a different layer than the
        // caller asked for, which the live gate caught.
        if (existing < 0 && parentLayerId == Guid.Empty)
        {
            var segments = normalizedPath
                .Split(new[] { "::" }, StringSplitOptions.None)
                .Select(segment => segment.Trim())
                .ToArray();
            if (segments.Length > 1)
            {
                var ancestorPath = string.Empty;
                for (var depth = 0; depth < segments.Length - 1; depth++)
                {
                    ancestorPath = depth == 0 ? segments[0] : $"{ancestorPath}::{segments[depth]}";
                    var ancestorIndex = document.Layers.FindByFullPath(ancestorPath, -1);
                    if (ancestorIndex < 0)
                    {
                        var ancestor = new Layer { Name = segments[depth], ParentLayerId = parentLayerId };
                        ancestorIndex = document.Layers.Add(ancestor);
                        if (ancestorIndex < 0)
                        {
                            throw new InvalidOperationException(
                                $"Rhino could not create the parent layer '{ancestorPath}'.");
                        }
                    }
                    parentLayerId = document.Layers[ancestorIndex].Id;
                }
            }
        }
        if (existing >= 0 && document.Layers[existing].ParentLayerId != parentLayerId)
        {
            throw new InvalidOperationException(
                "EnsureLayer does not re-parent an existing layer without a fingerprinted operation.");
        }

        var layer = existing >= 0
            ? CommonObject.FromJSON(document.Layers[existing].ToJSON(new SerializationOptions())) as Layer
                ?? throw new InvalidOperationException("Could not clone the existing Rhino layer.")
            : new Layer();
        layer.Name = leafName;
        // A null argbColor leaves the colour alone: the existing layer keeps its colour and a new
        // one takes Rhino's default — instead of both being repainted ARGB 0 (transparent black).
        if (request.ArgbColor is { } argbColor)
        {
            layer.Color = System.Drawing.Color.FromArgb(argbColor);
        }
        layer.ParentLayerId = parentLayerId;
        if (existing < 0 && request.LayerId != Guid.Empty)
        {
            layer.Id = request.LayerId;
        }

        var undo = document.BeginUndoRecord($"Vino: {request.OperationId}");
        try
        {
            var index = existing >= 0
                ? document.Layers.Modify(layer, existing, quiet: true) ? existing : -1
                : document.Layers.Add(layer);
            if (index < 0)
            {
                throw new InvalidOperationException($"Rhino could not ensure layer '{normalizedPath}'.");
            }
            var actual = document.Layers[index];
            if (request.LayerId != Guid.Empty && actual.Id != request.LayerId)
            {
                if (existing < 0)
                {
                    document.Layers.Delete(actual.Id, quiet: true);
                }
                throw new InvalidOperationException(
                    "Rhino could not preserve the requested LayerId; the unexpected layer was removed.");
            }
            // Verify the layer actually landed at the REQUESTED path: reporting success for a
            // layer at a different path is the false-success class this project exists to prevent.
            if (!string.Equals(actual.FullPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                if (existing < 0)
                {
                    document.Layers.Delete(actual.Id, quiet: true);
                }
                throw new InvalidOperationException(
                    $"Rhino placed the layer at '{actual.FullPath}' instead of '{normalizedPath}'" +
                    (existing < 0 ? "; the unexpected layer was removed." : "."));
            }
            var after = LayerFingerprint(actual);
            return Task.FromResult(new RhinoSceneMutationResult(
                request.OperationId,
                !string.Equals(before, after, StringComparison.Ordinal),
                before,
                after,
                actual.Id));
        }
        finally
        {
            if (undo != 0)
            {
                document.EndUndoRecord(undo);
            }
        }
    }

    // Heals one audited near-miss pair: the ANCHOR curve is referenced (fingerprint-verified,
    // never modified); the MOVE curve's chosen endpoint is set onto the anchor's endpoint. The
    // fix is verified before any write — the modified duplicate must be valid and land within
    // Tolerance — so a failed strategy changes nothing. SetStartPoint/SetEndPoint is not
    // implemented for every curve type (and can silently NURBS-ify arcs), so unsupported types
    // fail loudly instead of approximating.
    protected override Task<RhinoSceneMutationResult> FixEndpointPairCoreAsync(
        global::Rhino.RhinoDoc document,
        FixEndpointPairRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireOperationId(request.OperationId);
        if (request.AnchorObjectId == Guid.Empty || request.MoveObjectId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ExpectedAnchorFingerprint) ||
            string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
        {
            throw new InvalidOperationException(
                "Anchor/move object ids and both expected fingerprints are required for an endpoint fix.");
        }
        if (request.AnchorObjectId == request.MoveObjectId)
        {
            throw new InvalidOperationException("Anchor and move objects must differ.");
        }
        if (request.AnchorEnd is not (0 or 1) || request.MoveEnd is not (0 or 1))
        {
            throw new InvalidOperationException("Endpoint indices must be 0 (start) or 1 (end).");
        }
        var tolerance = request.Tolerance > 0 ? request.Tolerance : document.ModelAbsoluteTolerance;

        var anchorObject = document.Objects.FindId(request.AnchorObjectId)
            ?? throw new KeyNotFoundException($"Rhino object {request.AnchorObjectId:D} was not found.");
        var moveObject = document.Objects.FindId(request.MoveObjectId)
            ?? throw new KeyNotFoundException($"Rhino object {request.MoveObjectId:D} was not found.");
        var anchorState = ToState(anchorObject);
        if (!string.Equals(anchorState.Fingerprint, request.ExpectedAnchorFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Anchor Rhino object changed after the request snapshot.");
        }
        var before = ToState(moveObject);
        if (!string.Equals(before.Fingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rhino object changed after the request snapshot.");
        }
        RequireProvenanceOrApproval(moveObject, request.Approved, "editing");

        if (anchorObject.Geometry is not Curve anchorCurve || moveObject.Geometry is not Curve moveCurve)
        {
            throw new InvalidOperationException("Endpoint fixes require two curve objects.");
        }
        var anchorPoint = request.AnchorEnd == 0 ? anchorCurve.PointAtStart : anchorCurve.PointAtEnd;

        var healed = moveCurve.DuplicateCurve()
            ?? throw new InvalidOperationException("Rhino could not duplicate the curve to heal.");
        try
        {
            var moved = request.MoveEnd == 0
                ? healed.SetStartPoint(anchorPoint)
                : healed.SetEndPoint(anchorPoint);
            if (!moved)
            {
                throw new InvalidOperationException(
                    $"This curve type ({moveCurve.GetType().Name}) does not support endpoint editing; " +
                    "rebuild it as a NURBS curve first or choose the other curve as the move target.");
            }
            var resultingPoint = request.MoveEnd == 0 ? healed.PointAtStart : healed.PointAtEnd;
            var resultingGap = resultingPoint.DistanceTo(anchorPoint);
            if (!healed.IsValid || resultingGap > tolerance)
            {
                throw new InvalidOperationException(
                    $"Endpoint edit did not converge (resulting gap {resultingGap:G4} > tolerance {tolerance:G4}); " +
                    "no change was applied.");
            }

            var undo = document.BeginUndoRecord($"Vino: {request.OperationId}");
            if (undo == 0)
            {
                throw new InvalidOperationException("Rhino could not start an undo record for the endpoint fix.");
            }
            try
            {
                // Guid-based Replace overload (like TransformObjectCoreAsync) — the ObjRef
                // overload would leave a native CRhinoObjRef to the finalizer.
                if (!document.Objects.Replace(moveObject.Id, healed))
                {
                    throw new InvalidOperationException(
                        $"Rhino could not replace curve {request.MoveObjectId:D} with the healed geometry.");
                }
                var afterObject = document.Objects.FindId(request.MoveObjectId)
                    ?? throw new InvalidOperationException("Rhino object disappeared after the endpoint fix.");
                var after = ToState(afterObject);
                document.Views.Redraw();
                return Task.FromResult(new RhinoSceneMutationResult(
                    request.OperationId,
                    Changed: true,
                    before.Fingerprint,
                    after.Fingerprint,
                    request.MoveObjectId,
                    after,
                    new[]
                    {
                        new BridgeDiagnostic(
                            BridgeDiagnosticSeverity.Information,
                            "endpoint_fix_verified",
                            $"Endpoint gap closed to {resultingGap:G4} (tolerance {tolerance:G4}).")
                    }));
            }
            finally
            {
                // An orphaned open record would swallow the user's later edits into this undo step
                // AND make every subsequent Vino mutation hard-fail on BeginUndoRecord == 0.
                document.EndUndoRecord(undo);
            }
        }
        finally
        {
            healed.Dispose();
        }
    }

    protected override Task<RhinoSceneMutationResult> TransformObjectCoreAsync(
        global::Rhino.RhinoDoc document,
        TransformRhinoObjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireOperationId(request.OperationId);
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
        {
            throw new InvalidOperationException(
                "ObjectId and ExpectedFingerprint are required for a Rhino transform.");
        }

        var existing = document.Objects.FindId(request.ObjectId)
            ?? throw new KeyNotFoundException($"Rhino object {request.ObjectId:D} was not found.");
        var before = ToState(existing);
        if (!string.Equals(before.Fingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rhino object changed after the request snapshot.");
        }
        RequireProvenanceOrApproval(existing, request.Approved, "transforming");

        var transform = CreateTransform(request.Matrix);
        using var originalGeometry = existing.Geometry.Duplicate();
        using var transformedGeometry = existing.Geometry.Duplicate();
        if (!transformedGeometry.Transform(transform) || !transformedGeometry.IsValid)
        {
            throw new InvalidOperationException(
                $"Rhino could not apply the requested transform to object {request.ObjectId:D}.");
        }

        var undo = document.BeginUndoRecord($"Vino: {request.OperationId}");
        if (undo == 0)
        {
            throw new InvalidOperationException("Rhino could not start an undo record for transform.");
        }
        var replaced = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!document.Objects.Replace(request.ObjectId, transformedGeometry, ignoreModes: false))
            {
                throw new InvalidOperationException($"Rhino could not transform object {request.ObjectId:D}.");
            }
            replaced = true;

            var afterObject = document.Objects.FindId(request.ObjectId)
                ?? throw new InvalidOperationException("Rhino object disappeared after transform.");
            if (afterObject.Id != request.ObjectId)
            {
                throw new InvalidOperationException("Rhino object identity changed during transform.");
            }
            var after = ToState(afterObject);
            document.Views.Redraw();
            var changed = !string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal);
            var diagnostics = new[]
            {
                new BridgeDiagnostic(
                    BridgeDiagnosticSeverity.Information,
                    changed ? "rhino_object_transformed" : "rhino_transform_no_change",
                    changed
                        ? $"Transformed Rhino object {request.ObjectId:D}."
                        : $"Transform left Rhino object {request.ObjectId:D} unchanged.",
                    request.ObjectId),
            };
            return Task.FromResult(new RhinoSceneMutationResult(
                request.OperationId,
                changed,
                before.Fingerprint,
                after.Fingerprint,
                request.ObjectId,
                after,
                diagnostics));
        }
        catch (Exception mutationFailure) when (replaced)
        {
            var geometryRestored = document.Objects.Replace(
                request.ObjectId,
                originalGeometry,
                ignoreModes: true);
            var restored = document.Objects.FindId(request.ObjectId);
            var fingerprintRestored = restored is not null &&
                string.Equals(ToState(restored).Fingerprint, before.Fingerprint, StringComparison.Ordinal);
            if (!geometryRestored || !fingerprintRestored)
            {
                throw new AggregateException(
                    $"Transform failed and object {request.ObjectId:D} rollback was incomplete; use Rhino Undo.",
                    mutationFailure);
            }
            throw;
        }
        finally
        {
            if (undo != 0)
            {
                document.EndUndoRecord(undo);
            }
        }
    }

    private static GeometryBase CreatePrimitiveGeometry(CreateRhinoPrimitiveRequest request)
    {
        var suppliedDefinitionCount = new object?[]
        {
            request.Point,
            request.Line,
            request.Polyline,
            request.Circle,
            request.Box,
            request.Sphere,
        }.Count(item => item is not null);
        if (suppliedDefinitionCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one primitive definition matching Kind must be supplied.");
        }

        return request.Kind switch
        {
            RhinoPrimitiveKind.Point when request.Point is not null =>
                new Point(ToPoint3d(request.Point.Location, "point.location")),
            RhinoPrimitiveKind.Line when request.Line is not null =>
                CreateLine(request.Line),
            RhinoPrimitiveKind.Polyline when request.Polyline is not null =>
                CreatePolyline(request.Polyline),
            RhinoPrimitiveKind.Circle when request.Circle is not null =>
                CreateCircle(request.Circle),
            RhinoPrimitiveKind.Box when request.Box is not null =>
                CreateBox(request.Box),
            RhinoPrimitiveKind.Sphere when request.Sphere is not null =>
                CreateSphere(request.Sphere),
            _ => throw new InvalidOperationException(
                $"Primitive definition does not match Kind '{request.Kind}'."),
        };
    }

    private static LineCurve CreateLine(RhinoLinePrimitive definition)
    {
        var from = ToPoint3d(definition.From, "line.from");
        var to = ToPoint3d(definition.To, "line.to");
        if (from.DistanceToSquared(to) <=
            global::Rhino.RhinoMath.ZeroTolerance * global::Rhino.RhinoMath.ZeroTolerance)
        {
            throw new InvalidOperationException("Line endpoints must be distinct.");
        }
        return new LineCurve(from, to);
    }

    private static PolylineCurve CreatePolyline(RhinoPolylinePrimitive definition)
    {
        ArgumentNullException.ThrowIfNull(definition.Vertices);
        var minimumCount = definition.Closed ? 3 : 2;
        if (definition.Vertices.Count < minimumCount || definition.Vertices.Count > 10_000)
        {
            throw new InvalidOperationException(
                $"Polyline requires {minimumCount} to 10000 input vertices.");
        }

        var vertices = definition.Vertices
            .Select((point, index) => ToPoint3d(point, $"polyline.vertices[{index}]"))
            .ToList();
        if (definition.Closed && vertices[0].DistanceToSquared(vertices[^1]) >
            global::Rhino.RhinoMath.ZeroTolerance * global::Rhino.RhinoMath.ZeroTolerance)
        {
            vertices.Add(vertices[0]);
        }
        return new PolylineCurve(vertices);
    }

    private static NurbsCurve CreateCircle(RhinoCirclePrimitive definition)
    {
        var center = ToPoint3d(definition.Center, "circle.center");
        var normal = ToVector3d(definition.Normal, "circle.normal");
        RequirePositiveFinite(definition.Radius, "circle.radius");
        if (!normal.Unitize())
        {
            throw new InvalidOperationException("Circle normal must be non-zero.");
        }
        var plane = new Plane(center, normal);
        if (!plane.IsValid)
        {
            throw new InvalidOperationException("Circle plane is invalid.");
        }
        return new Circle(plane, definition.Radius).ToNurbsCurve();
    }

    private static Brep CreateBox(RhinoBoxPrimitive definition)
    {
        var minimum = ToPoint3d(definition.Minimum, "box.minimum");
        var maximum = ToPoint3d(definition.Maximum, "box.maximum");
        if (maximum.X <= minimum.X || maximum.Y <= minimum.Y || maximum.Z <= minimum.Z)
        {
            throw new InvalidOperationException(
                "Box maximum components must each be greater than minimum components.");
        }
        var box = new Box(new BoundingBox(minimum, maximum));
        return box.ToBrep();
    }

    private static Brep CreateSphere(RhinoSpherePrimitive definition)
    {
        var center = ToPoint3d(definition.Center, "sphere.center");
        RequirePositiveFinite(definition.Radius, "sphere.radius");
        return new Sphere(center, definition.Radius).ToBrep();
    }

    private static ObjectAttributes CreatePrimitiveAttributes(
        global::Rhino.RhinoDoc document,
        CreateRhinoPrimitiveRequest request)
    {
        var requestedAttributes = request.Attributes;
        var attributes = new ObjectAttributes
        {
            ObjectId = request.ObjectId,
            Name = requestedAttributes?.Name ?? string.Empty,
        };
        if (requestedAttributes?.Name is { Length: > 1024 })
        {
            throw new InvalidOperationException("Primitive object name must be at most 1024 characters.");
        }

        if (requestedAttributes?.LayerId is Guid layerId)
        {
            if (layerId == Guid.Empty)
            {
                throw new InvalidOperationException("Primitive LayerId cannot be empty.");
            }
            var layerIndex = document.Layers.Find(
                layerId,
                ignoreDeletedLayers: false,
                notFoundReturnValue: -1);
            if (layerIndex < 0)
            {
                throw new KeyNotFoundException($"Rhino layer {layerId:D} was not found.");
            }
            attributes.LayerIndex = layerIndex;
        }
        else
        {
            attributes.LayerIndex = document.Layers.CurrentLayerIndex;
        }

        if (requestedAttributes?.ArgbColor is int argbColor)
        {
            attributes.ObjectColor = System.Drawing.Color.FromArgb(argbColor);
            attributes.ColorSource = ObjectColorSource.ColorFromObject;
        }
        attributes.SetUserString(LogicalEntityKey, request.LogicalEntityId);
        if (!string.IsNullOrWhiteSpace(request.SourceDocKey))
        {
            attributes.SetUserString(SourceDocKeyKey, request.SourceDocKey);
        }
        return attributes;
    }

    private static void EnsureLogicalEntityAvailable(
        global::Rhino.RhinoDoc document,
        string logicalEntityId,
        Guid? exceptObjectId)
    {
        var collision = document.Objects.FirstOrDefault(candidate =>
            candidate.Id != exceptObjectId &&
            string.Equals(
                candidate.Attributes.GetUserString(LogicalEntityKey),
                logicalEntityId,
                StringComparison.Ordinal));
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"Logical entity '{logicalEntityId}' is already bound to Rhino object {collision.Id:D}.");
        }
    }

    private static Transform CreateTransform(RhinoTransformMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        var values = new[]
        {
            matrix.M00, matrix.M01, matrix.M02, matrix.M03,
            matrix.M10, matrix.M11, matrix.M12, matrix.M13,
            matrix.M20, matrix.M21, matrix.M22, matrix.M23,
            matrix.M30, matrix.M31, matrix.M32, matrix.M33,
        };
        if (values.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidOperationException("Transform matrix components must be finite.");
        }
        const double affineTolerance = 1e-12;
        if (Math.Abs(matrix.M30) > affineTolerance ||
            Math.Abs(matrix.M31) > affineTolerance ||
            Math.Abs(matrix.M32) > affineTolerance ||
            Math.Abs(matrix.M33 - 1.0) > affineTolerance)
        {
            throw new InvalidOperationException(
                "Transform matrix must be affine with final row [0, 0, 0, 1].");
        }

        var linearDeterminant =
            matrix.M00 * (matrix.M11 * matrix.M22 - matrix.M12 * matrix.M21) -
            matrix.M01 * (matrix.M10 * matrix.M22 - matrix.M12 * matrix.M20) +
            matrix.M02 * (matrix.M10 * matrix.M21 - matrix.M11 * matrix.M20);
        if (Math.Abs(linearDeterminant) <= 1e-12)
        {
            throw new InvalidOperationException("Transform matrix must be non-singular.");
        }

        var transform = Transform.Identity;
        transform.M00 = matrix.M00;
        transform.M01 = matrix.M01;
        transform.M02 = matrix.M02;
        transform.M03 = matrix.M03;
        transform.M10 = matrix.M10;
        transform.M11 = matrix.M11;
        transform.M12 = matrix.M12;
        transform.M13 = matrix.M13;
        transform.M20 = matrix.M20;
        transform.M21 = matrix.M21;
        transform.M22 = matrix.M22;
        transform.M23 = matrix.M23;
        transform.M30 = matrix.M30;
        transform.M31 = matrix.M31;
        transform.M32 = matrix.M32;
        transform.M33 = matrix.M33;
        if (!transform.IsValid)
        {
            throw new InvalidOperationException("Transform matrix is not valid in RhinoCommon.");
        }
        return transform;
    }

    private static global::Rhino.Geometry.Point3d ToPoint3d(RhinoPoint3d point, string field)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
        {
            throw new InvalidOperationException($"{field} coordinates must be finite.");
        }
        return new global::Rhino.Geometry.Point3d(point.X, point.Y, point.Z);
    }

    private static Vector3d ToVector3d(RhinoVector3d vector, string field)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (!double.IsFinite(vector.X) || !double.IsFinite(vector.Y) || !double.IsFinite(vector.Z))
        {
            throw new InvalidOperationException($"{field} components must be finite.");
        }
        return new Vector3d(vector.X, vector.Y, vector.Z);
    }

    private static void RequirePositiveFinite(double value, string field)
    {
        if (!double.IsFinite(value) || value <= global::Rhino.RhinoMath.ZeroTolerance)
        {
            throw new InvalidOperationException($"{field} must be finite and positive.");
        }
    }

    private static void ValidateListRequest(RhinoListObjectsRequest request)
    {
        if (request.Limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Rhino list Limit must be between 1 and 500.");
        }
        if (request.ObjectId == Guid.Empty || request.LayerId == Guid.Empty)
        {
            throw new InvalidOperationException("Rhino list ID filters cannot be empty GUIDs.");
        }
        if (request.LayerFullPath is not null && string.IsNullOrWhiteSpace(request.LayerFullPath) ||
            request.NameContains is not null && string.IsNullOrEmpty(request.NameContains) ||
            request.GeometryType is not null && string.IsNullOrWhiteSpace(request.GeometryType))
        {
            throw new InvalidOperationException("Rhino list text filters cannot be blank.");
        }
    }

    private static string CanonicalQuery(RhinoListObjectsRequest request) =>
        JsonSerializer.Serialize(request, BridgeProtocol.JsonOptions);

    private static RhinoBoundingBoxSummary? ToBounds(BoundingBox bounds)
    {
        if (!bounds.IsValid)
        {
            return null;
        }
        return new RhinoBoundingBoxSummary(
            new RhinoPoint3d(bounds.Min.X, bounds.Min.Y, bounds.Min.Z),
            new RhinoPoint3d(bounds.Max.X, bounds.Max.Y, bounds.Max.Z),
            new RhinoPoint3d(bounds.Center.X, bounds.Center.Y, bounds.Center.Z),
            new RhinoVector3d(
                bounds.Max.X - bounds.Min.X,
                bounds.Max.Y - bounds.Min.Y,
                bounds.Max.Z - bounds.Min.Z));
    }

    private static RhinoBoundingBoxSummary? UnionBounds(
        IEnumerable<RhinoBoundingBoxSummary?> bounds)
    {
        var valid = bounds.Where(item => item is not null).Select(item => item!).ToArray();
        if (valid.Length == 0)
        {
            return null;
        }
        var minimum = new RhinoPoint3d(
            valid.Min(item => item.Minimum.X),
            valid.Min(item => item.Minimum.Y),
            valid.Min(item => item.Minimum.Z));
        var maximum = new RhinoPoint3d(
            valid.Max(item => item.Maximum.X),
            valid.Max(item => item.Maximum.Y),
            valid.Max(item => item.Maximum.Z));
        return new RhinoBoundingBoxSummary(
            minimum,
            maximum,
            new RhinoPoint3d(
                (minimum.X + maximum.X) / 2.0,
                (minimum.Y + maximum.Y) / 2.0,
                (minimum.Z + maximum.Z) / 2.0),
            new RhinoVector3d(
                maximum.X - minimum.X,
                maximum.Y - minimum.Y,
                maximum.Z - minimum.Z));
    }

    private static PreparedRhinoUpsert PrepareUpsert(
        global::Rhino.RhinoDoc document,
        UpsertRhinoObjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOperationId(request.OperationId);
        if (request.ObjectId == Guid.Empty)
        {
            throw new InvalidOperationException("ObjectId is required for a managed Rhino object.");
        }
        if (string.IsNullOrWhiteSpace(request.LogicalEntityId))
        {
            throw new InvalidOperationException("LogicalEntityId is required for a managed Rhino object.");
        }
        if (string.IsNullOrWhiteSpace(request.GeometryType))
        {
            throw new InvalidOperationException("GeometryType is required.");
        }

        var decodedGeometry = CommonObject.FromJSON(request.GeometryJson);
        if (decodedGeometry is not GeometryBase geometry)
        {
            decodedGeometry?.Dispose();
            throw new InvalidOperationException("GeometryJson is not a Rhino GeometryBase JSON payload.");
        }
        try
        {
            if (!geometry.IsValidWithLog(out var geometryLog))
            {
                throw new InvalidOperationException(
                    "GeometryJson decoded to invalid Rhino geometry" +
                    (string.IsNullOrWhiteSpace(geometryLog) ? "." : $": {geometryLog}"));
            }
            var actualGeometryType = geometry.ObjectType.ToString();
            if (!string.Equals(
                    actualGeometryType,
                    request.GeometryType,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"GeometryType '{request.GeometryType}' does not match payload type '{actualGeometryType}'.");
            }

            var existing = document.Objects.FindId(request.ObjectId);
            var before = existing is null ? null : ToState(existing);
            if (before is not null && !string.IsNullOrWhiteSpace(before.LogicalEntityId) &&
                !string.Equals(before.LogicalEntityId, request.LogicalEntityId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Upsert cannot reassign an existing Rhino object to another logical entity.");
            }
            var logicalCollision = document.Objects.FirstOrDefault(candidate =>
                candidate.Id != existing?.Id &&
                string.Equals(
                    candidate.Attributes.GetUserString(LogicalEntityKey),
                    request.LogicalEntityId,
                    StringComparison.Ordinal));
            if (logicalCollision is not null)
            {
                throw new InvalidOperationException(
                    $"Logical entity '{request.LogicalEntityId}' is already bound to Rhino object " +
                    $"{logicalCollision.Id:D}.");
            }
            if (before is null && !string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
            {
                throw new InvalidOperationException(
                    "ExpectedFingerprint was supplied, but the requested Rhino object does not exist.");
            }
            if (before is not null &&
                (string.IsNullOrWhiteSpace(request.ExpectedFingerprint) ||
                 !string.Equals(before.Fingerprint, request.ExpectedFingerprint, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Rhino object changed after the request snapshot.");
            }
            if (existing is not null)
            {
                // Creates are always allowed; REPLACING an existing object destroys what the user
                // may have made — same default-deny as delete/transform.
                RequireProvenanceOrApproval(existing, request.Approved, "modifying");
            }

            var attributes = ParseAttributes(request.AttributesJson, existing?.Attributes);
            try
            {
                attributes.SetUserString(LogicalEntityKey, request.LogicalEntityId);
                if (!string.IsNullOrWhiteSpace(request.SourceDocKey))
                {
                    attributes.SetUserString(SourceDocKeyKey, request.SourceDocKey);
                }
                attributes.ObjectId = existing?.Id ?? request.ObjectId;
                return new PreparedRhinoUpsert(existing, before, geometry, attributes);
            }
            catch
            {
                attributes.Dispose();
                throw;
            }
        }
        catch
        {
            geometry.Dispose();
            throw;
        }
    }

    private static ObjectAttributes ParseAttributes(
        string json,
        ObjectAttributes? fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback?.Duplicate() ?? new ObjectAttributes();
        }
        var decoded = CommonObject.FromJSON(json);
        if (decoded is ObjectAttributes attributes)
        {
            return attributes;
        }
        decoded?.Dispose();
        throw new InvalidOperationException(
            "AttributesJson is not a Rhino ObjectAttributes JSON payload.");
    }

    private static RhinoSceneObjectState ToState(RhinoObject rhinoObject)
    {
        var options = new SerializationOptions();
        var geometryJson = rhinoObject.Geometry.ToJSON(options);
        var attributesJson = rhinoObject.Attributes.ToJSON(options);
        var logicalId = rhinoObject.Attributes.GetUserString(LogicalEntityKey) ?? string.Empty;
        var fingerprint = Hash($"{rhinoObject.Id:D}\n{logicalId}\n{geometryJson}\n{attributesJson}");
        return new RhinoSceneObjectState(
            rhinoObject.Id,
            logicalId,
            rhinoObject.Geometry.ObjectType.ToString(),
            geometryJson,
            attributesJson,
            fingerprint);
    }

    // What the last isolate/lock touched, per document runtime serial, so "restore" can put exactly
    // that back — INCLUDING the targets' own prior hidden/locked state (a target that was hidden
    // gets re-hidden on restore instead of staying shown forever). In-memory and ephemeral by
    // design: this is view state, and a Rhino restart or a reopened document legitimately forgets
    // it — the same way Rhino's own Isolate does. OwnerToken names the panel surface that created
    // the isolation; a token-carrying restore from any other surface is refused.
    private sealed record FocusRestoreState(
        List<Guid> Hidden,
        List<Guid> Locked,
        List<Guid> ShownTargets,
        List<Guid> UnlockedTargets,
        string? OwnerToken);

    private readonly Dictionary<uint, FocusRestoreState> _focusStack = [];

    protected override Task<FocusObjectsResult> FocusObjectsCoreAsync(
        global::Rhino.RhinoDoc document,
        FocusObjectsRequest request,
        CancellationToken cancellationToken)
    {
        var mode = (request.Mode ?? "select").Trim().ToLowerInvariant();
        if (mode is not ("select" or "isolate" or "lock" or "restore"))
        {
            throw new InvalidOperationException(
                $"Unknown focus mode '{request.Mode}'. Use select|isolate|lock|restore.");
        }

        if (mode == "restore")
        {
            // A token-carrying restore is a surface's AUTOMATIC cleanup: it may only clear an
            // isolation it still owns. Refusing a stale token (restored:false, nothing touched) is
            // what keeps one card's unmount from clearing the isolation another card has since
            // taken over. A tokenless restore is the user's explicit "Restore view": always clears.
            if (request.OwnerToken is { } token &&
                _focusStack.TryGetValue(document.RuntimeSerialNumber, out var owned) &&
                !string.Equals(owned.OwnerToken, token, StringComparison.Ordinal))
            {
                return Task.FromResult(new FocusObjectsResult(
                    0, 0, 0, 0, false, FocusFingerprint(document)));
            }
            var undoRestore = document.BeginUndoRecord("Vino: restore view");
            bool restored;
            try
            {
                restored = RestoreFocusState(document);
                document.Objects.UnselectAll();
            }
            finally
            {
                if (undoRestore != 0)
                {
                    document.EndUndoRecord(undoRestore);
                }
            }
            document.Views.Redraw();
            return Task.FromResult(new FocusObjectsResult(0, 0, 0, 0, restored, FocusFingerprint(document)));
        }

        var wanted = new HashSet<Guid>(request.ObjectIds ?? Array.Empty<Guid>());
        var targets = new List<RhinoObject>();
        foreach (var id in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.Objects.FindId(id) is { } found)
            {
                targets.Add(found);
            }
        }
        var missing = wanted.Count - targets.Count;

        if (mode == "select")
        {
            // A pure look: select + zoom what is selectable and REPORT what is not, instead of
            // force-showing it. Select used to Show/Unlock a hidden or locked target and never put
            // it back — one zoom click permanently undoing the user's own tidying. This branch
            // mutates nothing (and deliberately leaves a standing isolation alone), which is what
            // lets it run as a plain concurrent read while isolate/lock/restore take the writer
            // lease. HiddenCount/LockedCount here mean "targets LEFT hidden/locked".
            document.Objects.UnselectAll();
            var skippedHidden = 0;
            var skippedLocked = 0;
            var selected = 0;
            foreach (var rhinoObject in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!rhinoObject.IsNormal)
                {
                    if (rhinoObject.IsHidden)
                    {
                        skippedHidden++;
                    }
                    else
                    {
                        skippedLocked++;
                    }
                    continue;
                }
                rhinoObject.Select(true);
                selected++;
            }
            if (request.Zoom && selected > 0)
            {
                ZoomExtentsSelected(document);
            }
            document.Views.Redraw();
            return Task.FromResult(new FocusObjectsResult(
                selected, missing, skippedHidden, skippedLocked, false, FocusFingerprint(document)));
        }

        // isolate | lock — a real (view) mutation, wrapped in an undo record like Rhino's own
        // Isolate so Ctrl+Z can unwind it too. Replace any previous isolation first, so pressing
        // one finding after another does not accumulate hidden geometry.
        var undo = document.BeginUndoRecord($"Vino: focus {mode}");
        try
        {
            var restoredPrevious = RestoreFocusState(document);
            document.Objects.UnselectAll();
            var shownTargets = new List<Guid>();
            var unlockedTargets = new List<Guid>();
            foreach (var rhinoObject in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A locked or hidden target cannot be selected; make the thing the user asked to
                // see visible before selecting it — and RECORD what actually changed (the Show/
                // Unlock return values), so restore re-hides/re-locks exactly that.
                if (!rhinoObject.IsNormal)
                {
                    if (document.Objects.Show(rhinoObject, ignoreLayerMode: true))
                    {
                        shownTargets.Add(rhinoObject.Id);
                    }
                    if (document.Objects.Unlock(rhinoObject, ignoreLayerMode: true))
                    {
                        unlockedTargets.Add(rhinoObject.Id);
                    }
                }
                rhinoObject.Select(true);
            }

            var hidden = new List<Guid>();
            var locked = new List<Guid>();
            if (targets.Count > 0)
            {
                foreach (var rhinoObject in document.Objects.GetObjectList(AuditEnumerator()))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (wanted.Contains(rhinoObject.Id) || !rhinoObject.IsNormal)
                    {
                        continue;
                    }
                    if (mode == "isolate" && document.Objects.Hide(rhinoObject, ignoreLayerMode: false))
                    {
                        hidden.Add(rhinoObject.Id);
                    }
                    else if (mode == "lock" && document.Objects.Lock(rhinoObject, ignoreLayerMode: false))
                    {
                        locked.Add(rhinoObject.Id);
                    }
                }
            }
            if (hidden.Count > 0 || locked.Count > 0 ||
                shownTargets.Count > 0 || unlockedTargets.Count > 0)
            {
                _focusStack[document.RuntimeSerialNumber] =
                    new FocusRestoreState(hidden, locked, shownTargets, unlockedTargets, request.OwnerToken);
            }

            if (request.Zoom && targets.Count > 0)
            {
                ZoomExtentsSelected(document);
            }
            document.Views.Redraw();
            return Task.FromResult(new FocusObjectsResult(
                targets.Count,
                missing,
                hidden.Count,
                locked.Count,
                restoredPrevious,
                FocusFingerprint(document)));
        }
        finally
        {
            if (undo != 0)
            {
                document.EndUndoRecord(undo);
            }
        }
    }

    private static void ZoomExtentsSelected(global::Rhino.RhinoDoc document)
    {
        foreach (var view in document.Views)
        {
            view.ActiveViewport.ZoomExtentsSelected();
        }
    }

    /// <summary>
    /// Puts back what the last isolate/lock changed — re-shows/unlocks the bystanders AND
    /// re-hides/re-locks the targets it had forced visible. True when it touched anything.
    /// </summary>
    private bool RestoreFocusState(global::Rhino.RhinoDoc document)
    {
        if (!_focusStack.Remove(document.RuntimeSerialNumber, out var previous))
        {
            return false;
        }
        foreach (var id in previous.Hidden)
        {
            document.Objects.Show(id, ignoreLayerMode: false);
        }
        foreach (var id in previous.Locked)
        {
            document.Objects.Unlock(id, ignoreLayerMode: false);
        }
        foreach (var id in previous.ShownTargets)
        {
            document.Objects.Hide(id, ignoreLayerMode: false);
        }
        foreach (var id in previous.UnlockedTargets)
        {
            document.Objects.Lock(id, ignoreLayerMode: false);
        }
        return previous.Hidden.Count > 0 || previous.Locked.Count > 0 ||
            previous.ShownTargets.Count > 0 || previous.UnlockedTargets.Count > 0;
    }

    private static string FocusFingerprint(global::Rhino.RhinoDoc document) =>
        Hash($"focus|{document.RuntimeSerialNumber}|{document.Objects.Count}");

    protected override Task<RhinoLayerTableResult> ListLayersCoreAsync(
        global::Rhino.RhinoDoc document,
        CancellationToken cancellationToken)
    {
        // Object counts include hidden objects AND block-definition members: a layer holding only
        // block geometry is in use, and deleteLayer's emptiness proof depends on this listing.
        var objectCounts = new Dictionary<int, int>();
        foreach (var rhinoObject in EnumerateLayerOccupants(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = rhinoObject.Attributes.LayerIndex;
            objectCounts[index] = objectCounts.TryGetValue(index, out var count) ? count + 1 : 1;
        }
        var parentIds = new HashSet<Guid>();
        foreach (var layer in document.Layers)
        {
            if (layer is not null && !layer.IsDeleted && layer.ParentLayerId != Guid.Empty)
            {
                parentIds.Add(layer.ParentLayerId);
            }
        }

        var summaries = new List<RhinoLayerSummary>();
        foreach (var layer in document.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer is null || layer.IsDeleted)
            {
                continue;
            }
            summaries.Add(new RhinoLayerSummary(
                layer.Id,
                layer.FullPath,
                layer.ParentLayerId,
                layer.Index,
                layer.Color.ToArgb(),
                layer.IsVisible,
                layer.IsLocked,
                layer.Index == document.Layers.CurrentLayerIndex,
                objectCounts.TryGetValue(layer.Index, out var objectCount) ? objectCount : 0,
                parentIds.Contains(layer.Id),
                LayerFingerprint(layer),
                ReadVinoUserText(layer)));
        }
        var ordered = summaries
            .OrderBy(summary => summary.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var states = ReadLayerStateNames(document);
        // The table fingerprint covers presence AND absence: adding or removing any layer changes
        // it, which is what makes layerAbsent provable.
        var fingerprint = Hash(
            "layerTable\n" +
            string.Join("\n", ordered.Select(layer => $"{layer.LayerId:D}:{layer.Fingerprint}")));
        return Task.FromResult(new RhinoLayerTableResult(ordered, states, fingerprint));
    }

    private static IReadOnlyList<string> ReadLayerStateNames(global::Rhino.RhinoDoc document)
    {
        try
        {
            return document.NamedLayerStates.Names
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            // Named layer states are a convenience surface; never fail a layer listing over them.
            return Array.Empty<string>();
        }
    }

    protected override Task<RhinoSceneMutationResult> UpdateLayerCoreAsync(
        global::Rhino.RhinoDoc document,
        UpdateRhinoLayerRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireOperationId(request.OperationId);
        if (request.LayerId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
        {
            throw new InvalidOperationException("LayerId and ExpectedFingerprint are required for a layer update.");
        }
        if (request.ArgbColor is null && request.Visible is null && request.Locked is null &&
            request.UserText is not { Count: > 0 } && string.IsNullOrWhiteSpace(request.RenderMaterial) &&
            request.SetCurrent is not true)
        {
            throw new InvalidOperationException(
                "A layer update must change at least one of color, visible, locked, userText, " +
                "renderMaterial, setCurrent.");
        }
        // Mirrors the submit-time validator (defense in depth, like the user-text namespace guard):
        // setCurrent:false is meaningless — a document always has a current layer — and
        // setCurrent:true + visible:false can never succeed because Rhino requires the current
        // layer to be visible.
        if (request.SetCurrent is false)
        {
            throw new InvalidOperationException(
                "setCurrent accepts only true; to move current off a layer, set setCurrent:true " +
                "on the layer that should become current instead.");
        }
        if (request.SetCurrent is true && request.Visible is false)
        {
            throw new InvalidOperationException(
                "setCurrent:true cannot be combined with visible:false — Rhino requires the " +
                "current layer to be visible. Make another layer current, then hide this one in " +
                "a separate update.");
        }
        if (!string.IsNullOrWhiteSpace(request.RenderMaterial) &&
            !string.Equals(request.RenderMaterial, PlasterMaterialTemplate, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unknown render-material template '{request.RenderMaterial}'. Use '{PlasterMaterialTemplate}'.");
        }
        if (request.UserText is { Count: > 0 })
        {
            // Namespace guard BEFORE any write: a model payload must never touch user text that
            // other plugins or the user's own workflows own.
            var foreign = request.UserText.Keys.FirstOrDefault(
                key => !key.StartsWith(LayerUserTextPrefix, StringComparison.Ordinal));
            if (foreign is not null)
            {
                throw new InvalidOperationException(
                    $"Layer user-text keys must start with '{LayerUserTextPrefix}' (got '{foreign}').");
            }
        }
        var index = document.Layers.Find(request.LayerId, ignoreDeletedLayers: true, notFoundReturnValue: -1);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Rhino layer {request.LayerId:D} was not found.");
        }
        var layer = document.Layers[index];
        var beforeFingerprint = LayerFingerprint(layer);
        if (!string.Equals(beforeFingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rhino layer changed after the request snapshot.");
        }
        // PRE-CHECK, before any write: Rhino's rule is that the CURRENT layer cannot be hidden —
        // the write would be silently refused and surface only as a verification failure after
        // the fact (twice in production, RecoveryRequired). A single update cannot move current
        // elsewhere (setCurrent targets THIS layer, and setCurrent:true+visible:false was already
        // rejected above), so hiding the current layer is refused outright with the remedy named.
        if (request.Visible is false && index == document.Layers.CurrentLayerIndex)
        {
            throw Refuse(
                $"Layer '{layer.FullPath}' is the current layer, and Rhino's rule is that the " +
                "current layer cannot be hidden. First make another layer current " +
                "(updateRhinoLayerProperties with setCurrent:true on a layer that may stay " +
                "visible), then hide this one.");
        }

        var undo = document.BeginUndoRecord($"Vino: {request.OperationId}");
        if (undo == 0)
        {
            throw new InvalidOperationException("Rhino could not start an undo record for the layer update.");
        }
        try
        {
            var beforeOwnVisible = layer.GetPersistentVisibility();
            var beforeCurrentIndex = document.Layers.CurrentLayerIndex;
            if (request.ArgbColor is { } argb)
            {
                layer.Color = System.Drawing.Color.FromArgb(argb);
            }
            if (request.Visible is { } visible)
            {
                // SetPersistentVisibility writes the layer's OWN stored flag. IsVisible is
                // EFFECTIVE visibility — a child under a hidden parent reads false no matter what
                // its own flag says — so assigning IsVisible both mis-applied and mis-verified
                // child-layer writes on Rhino's real semantics.
                layer.SetPersistentVisibility(visible);
            }
            if (request.Locked is { } locked)
            {
                layer.IsLocked = locked;
            }
            if (request.SetCurrent is true && index != document.Layers.CurrentLayerIndex)
            {
                // Rhino refuses to make a hidden or locked layer current; the return value is
                // ignored on purpose — the verification below reports the honest outcome.
                // (second argument: quiet — no command-line chatter.)
                document.Layers.SetCurrentLayerIndex(index, true);
            }
            var userTextChanged = false;
            if (request.UserText is { Count: > 0 } userText)
            {
                foreach (var (key, value) in userText)
                {
                    var before = layer.GetUserString(key);
                    // Delete semantic: an empty OR whitespace-only value removes the key — a
                    // stored " " would satisfy the audit's labeled-check while matching nothing
                    // downstream, an unusable label no re-run could surface again.
                    var desired = string.IsNullOrWhiteSpace(value) ? null : value;
                    layer.SetUserString(key, desired);
                    userTextChanged |= !string.Equals(before, desired, StringComparison.Ordinal);
                }
                if (userTextChanged)
                {
                    // Layer user text bypasses the layer-table modify pipeline (the color/visible/
                    // locked setters go through it): no table event fires and the document-modified
                    // flag stays untouched, so a label-only session would close without a save
                    // prompt and silently lose every label. Set the flag explicitly. NOTE: for the
                    // same reason labels are NOT captured by Rhino Undo or layer-state snapshots —
                    // the documented revert is writing empty values (tool spec + payload guide).
                    document.Modified = true;
                }
            }
            string? materialSkip = null;
            if (!string.IsNullOrWhiteSpace(request.RenderMaterial))
            {
                if (layer.RenderMaterialIndex >= 0)
                {
                    // Fill-empty-only: an existing assignment is the user's, and replacing it would
                    // be exactly the kind of silent overwrite this feature refuses to do.
                    materialSkip = $"Layer '{layer.FullPath}' already has a render material; " +
                        "the plaster template was not applied.";
                }
                else
                {
                    layer.RenderMaterialIndex = CreatePlasterMaterial(document, layer);
                }
            }
            // Layer property changes are immediate in Rhino 8 (CommitChanges is obsolete), and the
            // setters return void — a rejected commit (Rhino refuses to hide or lock the CURRENT
            // layer, and refuses to make a hidden/locked layer current) is silent. So verify each
            // REQUESTED field against the re-read layer: a fingerprint that merely differs would
            // report "visible and unlocked" for a layer that is still hidden. Visibility verifies
            // the layer's OWN flag (GetPersistentVisibility) — a hidden parent no longer poisons
            // the verify; the parent's effect is reported as an informational diagnostic below.
            var after = document.Layers[index];
            var mismatches = new List<string>();
            if (request.ArgbColor is { } requestedArgb && after.Color.ToArgb() != requestedArgb)
            {
                mismatches.Add($"color (requested {requestedArgb:X8}, got {after.Color.ToArgb():X8})");
            }
            if (request.Visible is { } requestedVisible && after.GetPersistentVisibility() != requestedVisible)
            {
                mismatches.Add(
                    $"visible (requested {requestedVisible}, own flag {after.GetPersistentVisibility()})");
            }
            if (request.Locked is { } requestedLocked && after.IsLocked != requestedLocked)
            {
                mismatches.Add($"locked (requested {requestedLocked}, got {after.IsLocked})");
            }
            if (request.SetCurrent is true && document.Layers.CurrentLayerIndex != index)
            {
                mismatches.Add(
                    "setCurrent (Rhino kept another layer current — a hidden or locked layer " +
                    "cannot be made current)");
            }
            if (request.UserText is { Count: > 0 } requestedUserText)
            {
                foreach (var (key, value) in requestedUserText)
                {
                    var actual = after.GetUserString(key);
                    var expected = string.IsNullOrWhiteSpace(value) ? null : value;
                    if (!string.Equals(
                        string.IsNullOrEmpty(actual) ? null : actual,
                        expected,
                        StringComparison.Ordinal))
                    {
                        mismatches.Add($"userText '{key}' (requested '{expected}', got '{actual}')");
                    }
                }
            }
            if (materialSkip is null && !string.IsNullOrWhiteSpace(request.RenderMaterial) &&
                after.RenderMaterialIndex < 0)
            {
                mismatches.Add("renderMaterial (the layer still has none assigned)");
            }
            if (mismatches.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Rhino did not apply {string.Join(", ", mismatches)} to layer '{after.FullPath}'. " +
                    "The layer being current, or a parent layer's lock, can override the request.");
            }
            // Own-flag verify passed; when the layer STILL is not effectively visible, that is a
            // parent's doing — an honest, informational outcome, never a failure of this write.
            string? visibilityNote = null;
            if (request.Visible is true && after.GetPersistentVisibility() && !after.IsVisible)
            {
                visibilityNote =
                    $"Layer '{after.FullPath}': own visibility flag applied; effectively hidden " +
                    $"by parent '{FindHidingAncestorPath(document, after)}'.";
            }
            var afterFingerprint = LayerFingerprint(after);
            document.Views.Redraw();
            var diagnostics = DescribeCascadedLayerChanges(document, request.LayerId, index);
            if (visibilityNote is not null)
            {
                diagnostics = (diagnostics ?? Array.Empty<BridgeDiagnostic>())
                    .Append(new BridgeDiagnostic(
                        BridgeDiagnosticSeverity.Information,
                        "rhino_layer_hidden_by_parent",
                        visibilityNote))
                    .ToArray();
            }
            if (materialSkip is not null)
            {
                // A skip is an honest outcome, not a failure: report it so the agent tells the user
                // the layer kept its own material instead of claiming the plaster went on.
                diagnostics = (diagnostics ?? Array.Empty<BridgeDiagnostic>())
                    .Append(new BridgeDiagnostic(
                        BridgeDiagnosticSeverity.Information,
                        "rhino_layer_material_kept",
                        materialSkip))
                    .ToArray();
            }
            return Task.FromResult(new RhinoSceneMutationResult(
                request.OperationId,
                // An idempotent request (setting a field to the value it already has) is a
                // legitimate no-op, not a failure — the verification above already proved the
                // requested state holds. User text is OUTSIDE the fingerprint by design (labels
                // must not invalidate CAS pins), so a label-only update must OR its own changed
                // signal in — otherwise a whole labeling batch reports Changed:false. The same
                // goes for the OWN visibility flag of a child under a hidden parent (the
                // fingerprint tracks EFFECTIVE visibility, which a parent holds at false) and for
                // current-ness (a document property, outside the layer fingerprint entirely).
                Changed: !string.Equals(afterFingerprint, beforeFingerprint, StringComparison.Ordinal)
                    || userTextChanged
                    || (request.Visible is { } appliedVisible && appliedVisible != beforeOwnVisible)
                    || (request.SetCurrent is true && beforeCurrentIndex != index),
                beforeFingerprint,
                afterFingerprint,
                request.LayerId,
                Diagnostics: diagnostics));
        }
        finally
        {
            document.EndUndoRecord(undo);
        }
    }

    /// <summary>
    /// Adds a matte, colour-only material matching the layer's CURRENT display colour (the update
    /// applies colour before this runs, so the material and the layer always agree) and returns
    /// its table index. Deliberately plain — no PBR, no textures: the point is that a rendered
    /// view reads the same material families the viewport shows.
    /// </summary>
    private static int CreatePlasterMaterial(global::Rhino.RhinoDoc document, Layer layer)
    {
        using var material = new Material
        {
            Name = $"Vino {PlasterMaterialTemplate} — {layer.Name}",
            DiffuseColor = layer.Color,
            SpecularColor = System.Drawing.Color.Black,
            Shine = 0,
            Reflectivity = 0,
            Transparency = 0,
        };
        var index = document.Materials.Add(material);
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Rhino refused to add a {PlasterMaterialTemplate} material for layer '{layer.FullPath}'.");
        }
        return index;
    }

    /// <summary>
    /// Rhino cascades a parent layer's visibility/lock to its descendants, so one layer update can
    /// change several layers' fingerprints. The caller is told which ones, instead of discovering
    /// it as an unexplained CAS failure on the next layer operation.
    /// </summary>
    /// <summary>
    /// Names the nearest ancestor whose OWN visibility flag is off — the layer actually hiding a
    /// child whose own flag was just applied. Effective-only hiding higher up resolves to the
    /// first ancestor holding a false persistent flag.
    /// </summary>
    private static string FindHidingAncestorPath(global::Rhino.RhinoDoc document, Layer layer)
    {
        var parentId = layer.ParentLayerId;
        while (parentId != Guid.Empty)
        {
            var parentIndex = document.Layers.Find(parentId, ignoreDeletedLayers: true, notFoundReturnValue: -1);
            if (parentIndex < 0)
            {
                break;
            }
            var parent = document.Layers[parentIndex];
            if (!parent.GetPersistentVisibility())
            {
                return parent.FullPath;
            }
            parentId = parent.ParentLayerId;
        }
        return "(unknown)";
    }

    private static IReadOnlyList<BridgeDiagnostic>? DescribeCascadedLayerChanges(
        global::Rhino.RhinoDoc document,
        Guid layerId,
        int layerIndex)
    {
        var descendants = new List<string>();
        var frontier = new Queue<Guid>();
        frontier.Enqueue(layerId);
        while (frontier.Count > 0)
        {
            var parentId = frontier.Dequeue();
            foreach (var candidate in document.Layers)
            {
                if (candidate is not null && !candidate.IsDeleted &&
                    candidate.ParentLayerId == parentId && candidate.Index != layerIndex)
                {
                    descendants.Add(candidate.FullPath);
                    frontier.Enqueue(candidate.Id);
                }
            }
        }
        return descendants.Count == 0
            ? null
            : new[]
            {
                new BridgeDiagnostic(
                    BridgeDiagnosticSeverity.Warning,
                    "layer_update_cascade",
                    $"This layer has {descendants.Count} descendant layer(s) whose effective " +
                    $"visibility/lock follow it: {string.Join(", ", descendants.Take(10))}" +
                    (descendants.Count > 10 ? ", …" : "") +
                    ". Re-read the layer table before operating on them; their fingerprints may have changed."),
            };
    }

    protected override Task<RhinoSceneMutationResult> DeleteLayerCoreAsync(
        global::Rhino.RhinoDoc document,
        DeleteRhinoLayerRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireOperationId(request.OperationId);
        if (request.LayerId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
        {
            throw new InvalidOperationException("LayerId and ExpectedFingerprint are required for a layer delete.");
        }
        var index = document.Layers.Find(request.LayerId, ignoreDeletedLayers: true, notFoundReturnValue: -1);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Rhino layer {request.LayerId:D} was not found.");
        }
        var layer = document.Layers[index];
        var beforeFingerprint = LayerFingerprint(layer);
        if (!string.Equals(beforeFingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rhino layer changed after the request snapshot.");
        }
        // Emptiness is re-proved here, not taken from the audit: hidden objects and block members
        // count, children count, and the current layer is never deletable.
        if (index == document.Layers.CurrentLayerIndex)
        {
            throw Refuse($"Layer '{layer.FullPath}' is the current layer and cannot be deleted.");
        }
        foreach (var candidate in document.Layers)
        {
            if (candidate is not null && !candidate.IsDeleted && candidate.ParentLayerId == layer.Id)
            {
                throw Refuse($"Layer '{layer.FullPath}' has child layers; delete or re-parent them first.");
            }
        }
        foreach (var rhinoObject in EnumerateLayerOccupants(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rhinoObject.Attributes.LayerIndex == index)
            {
                throw Refuse($"Layer '{layer.FullPath}' still holds objects (including hidden or block members).");
            }
        }

        var undo = document.BeginUndoRecord($"Vino: {request.OperationId}");
        if (undo == 0)
        {
            throw new InvalidOperationException("Rhino could not start an undo record for the layer delete.");
        }
        try
        {
            if (!document.Layers.Delete(index, quiet: true))
            {
                throw new InvalidOperationException($"Rhino could not delete layer '{layer.FullPath}'.");
            }
            // Absence is verified, not assumed — the deleted-layer lookup must now fail.
            if (document.Layers.Find(request.LayerId, ignoreDeletedLayers: true, notFoundReturnValue: -1) >= 0)
            {
                throw new InvalidOperationException(
                    $"Rhino reported success but layer {request.LayerId:D} is still present.");
            }
            document.Views.Redraw();
            return Task.FromResult(new RhinoSceneMutationResult(
                request.OperationId,
                Changed: true,
                beforeFingerprint,
                null,
                request.LayerId));
        }
        finally
        {
            document.EndUndoRecord(undo);
        }
    }

    protected override Task<RhinoLayerStateResult> LayerStateCoreAsync(
        global::Rhino.RhinoDoc document,
        RhinoLayerStateRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireOperationId(request.OperationId);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("A layer state name is required.");
        }
        var name = request.Name.Trim();
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        var states = document.NamedLayerStates;

        var undo = document.BeginUndoRecord($"Vino: {request.OperationId}");
        if (undo == 0)
        {
            throw new InvalidOperationException("Rhino could not start an undo record for the layer state.");
        }
        try
        {
            switch (action)
            {
                case "save":
                    // Save overwrites an existing state of the same name — that is the intended
                    // "refresh my checkpoint" behavior.
                    if (states.Save(name) < 0)
                    {
                        throw new InvalidOperationException($"Rhino could not save layer state '{name}'.");
                    }
                    break;
                case "restore":
                    if (states.FindName(name) < 0)
                    {
                        throw new KeyNotFoundException($"Layer state '{name}' does not exist.");
                    }
                    // Restore everything the state captured: a partial restore would make the
                    // checkpoint a half-truth ("layers restored" while lock/visibility drifted).
                    if (!states.Restore(name, global::Rhino.DocObjects.Tables.RestoreLayerProperties.All))
                    {
                        throw new InvalidOperationException($"Rhino could not restore layer state '{name}'.");
                    }
                    break;
                case "delete":
                    if (states.FindName(name) < 0)
                    {
                        throw new KeyNotFoundException($"Layer state '{name}' does not exist.");
                    }
                    if (!states.Delete(name))
                    {
                        throw new InvalidOperationException($"Rhino could not delete layer state '{name}'.");
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown layer-state action '{request.Action}'. Use save|restore|delete.");
            }
            document.Views.Redraw();
            var remaining = ReadLayerStateNames(document);
            // The fingerprint must cover the LAYERS too: a restore rewrites every layer while the
            // state-name list is unchanged, so hashing only the names would report an identical
            // fingerprint for a document-wide change.
            var layerTableFingerprint = Hash(
                "layerTable\n" + string.Join(
                    "\n",
                    document.Layers
                        .Where(layer => layer is not null && !layer.IsDeleted)
                        .OrderBy(layer => layer.FullPath, StringComparer.OrdinalIgnoreCase)
                        .Select(layer => $"{layer.Id:D}:{LayerFingerprint(layer)}")));
            return Task.FromResult(new RhinoLayerStateResult(
                request.OperationId,
                action,
                name,
                remaining,
                Hash($"layerStates\n{string.Join("\n", remaining)}\n{layerTableFingerprint}")));
        }
        finally
        {
            document.EndUndoRecord(undo);
        }
    }

    protected override Task<RhinoPurgeResult> PurgeTableEntriesCoreAsync(
        global::Rhino.RhinoDoc document,
        PurgeTableEntriesRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireOperationId(request.OperationId);
        if (request.Entries is null || request.Entries.Count == 0)
        {
            throw new InvalidOperationException("At least one table entry is required for a purge.");
        }

        // Phase 1 — prove every entry is purgeable BEFORE deleting anything. Interleaving checks
        // and deletions would let a multi-entry purge half-apply on the first in-use entry.
        foreach (var entry in request.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = (entry.Table ?? string.Empty).Trim().ToLowerInvariant();
            switch (table)
            {
                case "block":
                {
                    var definition = document.InstanceDefinitions.FindId(entry.Id)
                        ?? throw new KeyNotFoundException($"Block definition {entry.Id:D} was not found.");
                    // Re-verified live: an entry that gained a reference since the audit is
                    // refused rather than purged on the audit's word. InUse(1) covers top-level
                    // and nested references in the document; InUse(2) covers references from
                    // other definitions.
                    if (definition.InUse(1) || definition.InUse(2))
                    {
                        throw Refuse($"Block definition '{definition.Name}' is in use and cannot be purged.");
                    }
                    break;
                }
                case "dimstyle":
                {
                    var style = document.DimStyles.FindId(entry.Id)
                        ?? throw new KeyNotFoundException($"Dimension style {entry.Id:D} was not found.");
                    if (document.DimStyles.CurrentIndex == style.Index)
                    {
                        throw Refuse($"Dimension style '{style.Name}' is the current style and cannot be purged.");
                    }
                    break;
                }
                case "linetype":
                {
                    var linetype = document.Linetypes.FindId(entry.Id)
                        ?? throw new KeyNotFoundException($"Linetype {entry.Id:D} was not found.");
                    // Delete() only refuses linetypes referenced by active geometry, so the
                    // layer-reference check has to be ours: a layer's linetype would otherwise be
                    // purged out from under it.
                    if (document.Layers.Any(layer =>
                            layer is not null && !layer.IsDeleted && layer.LinetypeIndex == linetype.Index))
                    {
                        throw Refuse($"Linetype '{linetype.Name}' is referenced by a layer and cannot be purged.");
                    }
                    break;
                }
                case "material":
                {
                    var material = document.Materials.FindId(entry.Id)
                        ?? throw new KeyNotFoundException($"Material {entry.Id:D} was not found.");
                    if (document.Layers.Any(layer =>
                            layer is not null && !layer.IsDeleted && layer.RenderMaterialIndex == material.Index))
                    {
                        throw Refuse($"Material '{material.Name}' is referenced by a layer and cannot be purged.");
                    }
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        $"Unknown purge table '{entry.Table}'. Use block|dimStyle|linetype|material.");
            }
        }

        var undo = document.BeginUndoRecord($"Vino: {request.OperationId}");
        if (undo == 0)
        {
            throw new InvalidOperationException("Rhino could not start an undo record for the purge.");
        }
        var purged = new List<PurgedTableEntry>();
        try
        {
            // Phase 2 — apply. Anything that still fails here reports what already went in the
            // exception message, so a partial purge is never silent.
            foreach (var entry in request.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var table = (entry.Table ?? string.Empty).Trim().ToLowerInvariant();
                switch (table)
                {
                    case "block":
                    {
                        var definition = document.InstanceDefinitions.FindId(entry.Id)
                            ?? throw new KeyNotFoundException($"Block definition {entry.Id:D} was not found.");
                        var name = definition.Name;
                        // Delete(index, deleteReferences, quiet): deleteReferences=false means
                        // "do not destroy geometry that references this definition" — passing true
                        // here would delete the user's instances along with the definition.
                        if (!document.InstanceDefinitions.Delete(definition.Index, false, true))
                        {
                            throw new InvalidOperationException($"Rhino could not purge block definition '{name}'.");
                        }
                        purged.Add(new PurgedTableEntry("block", entry.Id, name));
                        break;
                    }
                    case "dimstyle":
                    {
                        var style = document.DimStyles.FindId(entry.Id)
                            ?? throw new KeyNotFoundException($"Dimension style {entry.Id:D} was not found.");
                        var name = style.Name;
                        if (!document.DimStyles.Delete(style.Index, quiet: true))
                        {
                            throw new InvalidOperationException(
                                $"Rhino could not purge dimension style '{name}' (it may still be in use).");
                        }
                        purged.Add(new PurgedTableEntry("dimStyle", entry.Id, name));
                        break;
                    }
                    case "linetype":
                    {
                        var linetype = document.Linetypes.FindId(entry.Id)
                            ?? throw new KeyNotFoundException($"Linetype {entry.Id:D} was not found.");
                        var name = linetype.Name;
                        if (!document.Linetypes.Delete(linetype.Index, quiet: true))
                        {
                            throw new InvalidOperationException(
                                $"Rhino could not purge linetype '{name}' (it may still be in use).");
                        }
                        purged.Add(new PurgedTableEntry("linetype", entry.Id, name));
                        break;
                    }
                    case "material":
                    {
                        var material = document.Materials.FindId(entry.Id)
                            ?? throw new KeyNotFoundException($"Material {entry.Id:D} was not found.");
                        var name = material.Name ?? string.Empty;
                        if (!document.Materials.Delete(material))
                        {
                            throw new InvalidOperationException(
                                $"Rhino could not purge material '{name}' (it may still be in use).");
                        }
                        purged.Add(new PurgedTableEntry("material", entry.Id, name));
                        break;
                    }
                    default:
                        throw new InvalidOperationException(
                            $"Unknown purge table '{entry.Table}'. Use block|dimStyle|linetype|material.");
                }
            }
            document.Views.Redraw();
            return Task.FromResult(new RhinoPurgeResult(
                request.OperationId,
                purged,
                Hash($"purge\n{string.Join("\n", purged.Select(item => $"{item.Table}:{item.Id:D}"))}")));
        }
        catch (Exception exception) when (purged.Count > 0 && exception is not OperationCanceledException)
        {
            // Never let a partial purge be reported as a bare failure: the caller must know which
            // entries actually went before it re-audits.
            throw new InvalidOperationException(
                $"{exception.Message} Already purged before the failure: " +
                string.Join(", ", purged.Select(item => $"{item.Table} '{item.Name}'")) + ".",
                exception);
        }
        finally
        {
            document.EndUndoRecord(undo);
        }
    }

    protected override Task<RhinoBatchMutationResult> MoveObjectsToLayerCoreAsync(
        global::Rhino.RhinoDoc document,
        MoveObjectsToLayerRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireOperationId(request.OperationId);
        if (request.Items is null || request.Items.Count == 0)
        {
            throw new InvalidOperationException("At least one object is required for a layer move.");
        }
        if (request.TargetLayerId == Guid.Empty)
        {
            throw new InvalidOperationException("TargetLayerId is required for a layer move.");
        }
        var layerIndex = document.Layers.Find(request.TargetLayerId, ignoreDeletedLayers: true, notFoundReturnValue: -1);
        if (layerIndex < 0)
        {
            throw new KeyNotFoundException($"Target Rhino layer {request.TargetLayerId:D} was not found.");
        }

        // Everything is validated BEFORE the first write: a half-applied batch would leave the
        // ChangeSet's per-object expectations partially true with no honest way to report it.
        var prepared = new List<(RhinoObject Object, string BeforeFingerprint)>(request.Items.Count);
        foreach (var item in request.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(item.ExpectedFingerprint))
            {
                throw new InvalidOperationException("Each layer-move item needs an objectId and expectedFingerprint.");
            }
            var rhinoObject = document.Objects.FindId(item.ObjectId)
                ?? throw new KeyNotFoundException($"Rhino object {item.ObjectId:D} was not found.");
            var before = ToState(rhinoObject);
            if (!string.Equals(before.Fingerprint, item.ExpectedFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Rhino object {item.ObjectId:D} changed after the request snapshot.");
            }
            RequireProvenanceOrApproval(rhinoObject, request.Approved, "moving");
            prepared.Add((rhinoObject, before.Fingerprint));
        }

        var undo = document.BeginUndoRecord($"Vino: {request.OperationId}");
        if (undo == 0)
        {
            throw new InvalidOperationException("Rhino could not start an undo record for the layer move.");
        }
        try
        {
            var results = new List<BatchMutationItem>(prepared.Count);
            foreach (var (rhinoObject, beforeFingerprint) in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = rhinoObject.Attributes.Duplicate();
                attributes.LayerIndex = layerIndex;
                if (!document.Objects.ModifyAttributes(rhinoObject, attributes, quiet: true))
                {
                    // Pre-validation cannot prove ModifyAttributes will succeed (a locked layer or
                    // a locked object refuses at write time), so a mid-batch failure names what
                    // already moved instead of leaving the caller to guess.
                    var applied = results.Count == 0
                        ? "none"
                        : string.Join(", ", results.Select(item => item.ObjectId.ToString("D")));
                    throw new InvalidOperationException(
                        $"Rhino could not move object {rhinoObject.Id:D} to the target layer. " +
                        $"Already moved in this batch: {applied}.");
                }
                var afterObject = document.Objects.FindId(rhinoObject.Id)
                    ?? throw new InvalidOperationException(
                        $"Rhino object {rhinoObject.Id:D} disappeared after the layer move.");
                results.Add(new BatchMutationItem(
                    rhinoObject.Id,
                    beforeFingerprint,
                    ToState(afterObject).Fingerprint));
            }
            document.Views.Redraw();
            return Task.FromResult(new RhinoBatchMutationResult(
                request.OperationId,
                Changed: results.Count > 0,
                results,
                Hash($"moveToLayer\n{request.TargetLayerId:D}\n" +
                    string.Join("\n", results.Select(item => $"{item.ObjectId:D}:{item.AfterFingerprint}")))));
        }
        finally
        {
            document.EndUndoRecord(undo);
        }
    }

    // Widened beyond identity+color: visibility, lock, material and linetype are exactly the
    // fields layer updates change, and a fingerprint that ignored them could not prove a layer
    // was unchanged since it was inspected (the reason layer mutation stayed reserved).
    private static string LayerFingerprint(Layer layer) => Hash(
        $"{layer.Id:D}\n{layer.FullPath}\n{layer.ParentLayerId:D}\n{layer.Color.ToArgb()}\n" +
        $"{layer.IsVisible}\n{layer.IsLocked}\n{layer.RenderMaterialIndex}\n{layer.LinetypeIndex}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void RequireOperationId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new InvalidOperationException("OperationId is required.");
        }
    }

    private sealed class PreparedRhinoUpsert : IDisposable
    {
        public PreparedRhinoUpsert(
            RhinoObject? existing,
            RhinoSceneObjectState? before,
            GeometryBase geometry,
            ObjectAttributes attributes)
        {
            Existing = existing;
            Before = before;
            Geometry = geometry;
            Attributes = attributes;
        }

        public RhinoObject? Existing { get; }

        public RhinoSceneObjectState? Before { get; }

        public GeometryBase Geometry { get; }

        public ObjectAttributes Attributes { get; }

        public void Dispose()
        {
            Attributes.Dispose();
            Geometry.Dispose();
        }
    }
}
