using Vino.CanvasSceneAdapter;
using Vino.Contracts;

namespace Vino.AgentHost.Codex;

internal static class DynamicToolSpecs
{
    private static readonly string PayloadGuide =
        Hosting.InstructionAssets.LoadOrFallback("payload-guide.md");

    public static object[] Create() =>
    [
        new
        {
            type = "namespace",
            name = "vino_v1",
            description = "Read the bound Rhino/Grasshopper pair and submit centrally serialized, conflict-checked, verified changes.",
            tools = new object[]
            {
                Function(
                    "snapshot_read",
                    "Read an immutable snapshot. Parallel-safe; never acquires the writer lease. The response always includes the exact sessionId and target projectId required by ChangeSet. Omit scopes for a cheap meta orientation read (ids + counts + groups). Then \"index\" for one-line rows of every component, components:<id,...> for full detail of the ones you will touch, \"wires\"/\"groups\" for topology. \"canvas\" returns the whole document and is capped at 256KB with explicit continuation — prefer targeted scopes on large documents.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            scopes = new
                            {
                                type = "array",
                                description = "Optional reads. \"meta\" (= omitted/empty) -> counts + group membership only. \"index\" -> one compact row per component: {id,name,typeId,groupIds}. components:<guid>,<guid>,... -> the full detail of exactly those components (unknown ids return in missingComponents, never an error). \"wires\"/\"groups\" -> the full topology lists. \"canvas\" -> the whole-document dump, byte-capped at 256KB; a cut sets truncated plus nextOffset/remainingComponentIds so nothing is dropped silently. Targeted inspections: script:<component-guid>, script-messages:<component-guid>, rhino:<object-guid>. A script source longer than 24,000 characters comes back windowed: the response carries sourceTotal/sourceOffset/sourceTruncated and, when there is more, nextSourceOffset plus a ready-made continueWith scope (script:<guid>:<offset>) — read it again with that scope to get the next window. Never assume you have the whole source unless sourceTruncated is absent or false.",
                                items = new { type = "string" }
                            },
                            knownSnapshotId = NullableString("Return unchanged=true when this still identifies the current snapshot (bodies are then omitted — envelope only).")
                        },
                        additionalProperties = false
                    }),
                Function(
                    "component_catalog",
                    "Look up a component's type GUID in the installed Grasshopper catalog. Use the well-known GUID table (gh-authoring skill) for the types it lists; for ANY other type a component_catalog lookup is MANDATORY before canvas.create — creates with unverified GUIDs are refused. Never write a type GUID from memory. Parallel-safe and read-only.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "Name, nickname, category, subcategory, or description text." },
                            limit = new { type = "integer", minimum = 1, maximum = 100, description = "Maximum deterministic matches; default 25." },
                            includeObsolete = new { type = "boolean", description = "Include obsolete components; default false." }
                        },
                        additionalProperties = false
                    }),
                Function(
                    "rhino_list",
                    "List or filter objects in the exact bound Rhino document. Parallel-safe and read-only; use returned IDs and fingerprints for changes.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            limit = new { type = "integer", minimum = 1, maximum = 500, description = "Maximum objects; default 100." },
                            objectId = Uuid(),
                            layerId = Uuid(),
                            layerFullPath = new { type = "string" },
                            name = new { type = "string" },
                            nameContains = new { type = "string" },
                            geometryType = new { type = "string" },
                            logicalEntityId = new { type = "string" },
                            selected = new { type = "boolean" }
                        },
                        additionalProperties = false
                    }),
                Function(
                    "rhino_view_capture",
                    "Capture a Rhino viewport render as a PNG for VISUAL verification of your work " +
                    "(does the result actually look right — coverage, gradients, proportions?). " +
                    "Read-only; by default it ZoomExtents first so the document geometry is framed. " +
                    "IMPORTANT: the image cannot appear inside this turn. It is saved and attached " +
                    "as an image to your NEXT turn's input automatically. Call it when you want " +
                    "eyes on the result (typically after your final geometry lands), finish what " +
                    "does not depend on seeing it, then end the turn; inspect the image when it " +
                    "arrives and fix what looks wrong. Prefer once-per-milestone over every step.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            viewName = NullableString("Viewport name (e.g. Perspective, Top). Default: the active view."),
                            width = new { type = "integer", minimum = 64, maximum = 1920, description = "Pixel width; default 1024." },
                            height = new { type = "integer", minimum = 64, maximum = 1200, description = "Pixel height; default 640." },
                            zoomExtents = new { type = "boolean", description = "Frame the document geometry first; default true." }
                        },
                        additionalProperties = false
                    }),
                Function(
                    "rhino_audit",
                    "Deterministic document-hygiene audit of the bound Rhino document. Detection is server " +
                    "code — never eyeball geometry yourself; call this and TRIAGE the findings. Kinds: " +
                    "nearMissEndpoints (open-curve endpoints almost meeting, gap in (tolerance, " +
                    "tolerance*bandFactor]), nearDuplicates (position-coincident points, curves and SOLIDS — " +
                    "Brep and Extrusion compare across representations, so an extruded box and the same box " +
                    "as a Brep pair up; which copy to keep is always the user's call, design-option stacks " +
                    "are intentional), openBrepEdges (solids that are not closed, ranked by the gap that " +
                    "would close them — REPORT ONLY, rebuilding a shell is the user's modelling decision), " +
                    "purgeCandidates (unused block definitions, empty leaf layers, invalid objects — " +
                    "quarantine bad objects, never delete them), and three grouped QC sweeps: " +
                    "geometryIntegrity (fragments, slivers, strays far from the model, partial duplicates, " +
                    "gaps between adjacent solids, texture-mapping hazards), layerIntegrity (empty layers, " +
                    "names that break name-based selection, layers without a material, layers holding only " +
                    "block geometry), blockIntegrity (definitions with no objects, one block placed across " +
                    "several layers, definitions whose members sit on layers nothing else uses), and " +
                    "layerSemantics (layer-curation fact scan: one finding per layer still missing its " +
                    "vino semantic label, carrying layerFacts — name, color, occupancy incl. block " +
                    "members, existing labels — for the server-side proposal table; labeled layers drop " +
                    "out, so re-running it verifies an apply). QC sweeps " +
                    "are REPORT ONLY triage — they propose no destructive fix. scannedObjects tells you how many objects " +
                    "were IN SCOPE: zero scanned means this document holds nothing this kind looks at, which " +
                    "is NOT the same as a clean document — say which it was. Every finding carries object " +
                    "fingerprints for CAS-pinned follow-up fixes; results name the tolerance and units used. " +
                    "Read-only and parallel-safe.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            kind = new
                            {
                                type = "string",
                                // The canonical list — shared with the adapter's unknown-kind
                                // error so the two bridge ends cannot drift.
                                @enum = RhinoAuditKinds.All.ToArray(),
                            },
                            tolerance = new { type = "number", description = "Override; default = document absolute tolerance." },
                            bandFactor = new { type = "number", description = "nearMissEndpoints/openBrepEdges band multiplier; default 10." },
                            limit = new { type = "integer", minimum = 1, maximum = 100, description = "Max findings; default 50." }
                        },
                        required = new[] { "kind" },
                        additionalProperties = false
                    }),
                Function(
                    "structural_extract",
                    "Extract structural member AXES from the Rhino document — server-computed, never " +
                    "eyeballed. Three source kinds: curves ARE axes (lines, polylines and polycurves are " +
                    "exploded at their kinks into one member per segment; arcs/NURBS become chords of " +
                    "about curveSegmentLength, kind 'curve-discretized'); unit-prototype block instances " +
                    "recover the EXACT axis from the instance transform; loose slender solids get a " +
                    "PCA-approximated axis (kind 'pca'). Meshes are skipped and counted, not guessed. " +
                    "Every member gets a GEOMETRIC role (column | beam | brace) — for curves drawn on " +
                    "ordinary layers that role, not the layer name, is what sections and supports key on. " +
                    "Point objects in scope are listed as pointObjects (support/load marker candidates). " +
                    "Returns a SUMMARY (counts by mark/kind/role, section guesses from the KS catalog, " +
                    "quality signals, docUnits) and writes the full member list to the session artifact " +
                    "named in membersArtifact — pass that to structural_solve; do not re-list members in " +
                    "chat. freeEnds are the ask-back items: each carries real objectIds, so point at them " +
                    "with [[focus:...]] chips and ask the user whether they are intended cantilevers BEFORE " +
                    "solving. A high obliqueExactAxes count means the extraction is skewed (buildings are " +
                    "orthogonal grids plus deliberate diagonals) — say so instead of analyzing bad axes. " +
                    "For 'analyze these curves' use selectedOnly or layerFilter to scope. Read-only and " +
                    "parallel-safe.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            layerFilter = new { type = "string", description = "Case-insensitive substring of layer FullPath (e.g. '철골'); omit for the whole document." },
                            selectedOnly = new { type = "boolean", description = "Only currently selected objects." },
                            prototypeHeight = new { type = "number", description = "Unit-prototype height in document units; default 1000." },
                            joinSnapDistance = new { type = "number", description = "Endpoint join tolerance for free-end detection; default 350." },
                            curveSegmentLength = new { type = "number", description = "Chord length for arcs/NURBS axes in document units; default 1000. Polylines split at kinks regardless." },
                            limit = new { type = "integer", minimum = 1, maximum = 10000, description = "Member cap; default 4000." }
                        },
                        additionalProperties = false
                    }),
                Function(
                    "structural_solve",
                    "Solve the extracted frame with the SHIPPED PyNite solver (out of process, " +
                    "deterministic — you call it, you never re-implement it in a script). Reads the " +
                    "structural_extract artifact, merges node grid, snaps drawn-to-face joints, splits " +
                    "T-junctions, detects supports (base band + column feet, plus answers.supportPoints), " +
                    "applies self-weight (case G) and the user's line/point loads tagged G or Q, solves " +
                    "SLS (1.0G+1.0Q) for the L/limit deflection check and ULS (1.35G+1.5Q by default; " +
                    "KDS uses 1.2/1.6 — set answers.loadFactors) for an ELASTIC stress utilization " +
                    "screen (N/A+M/S vs fy) and a slenderness sanity limit; every component that has a " +
                    "support is solved, unsupported members are reported as islands. Sections resolve " +
                    "mark → role (answers.roleSections, e.g. columns H-300x300, beams H-400x200) → " +
                    "defaultSection; curves drawn on ordinary layers have no mark, so ASK for sections by " +
                    "role. BEFORE calling: resolve the free ends structural_extract reported — ask the " +
                    "user (focus chips!) which are intended cantilevers (answers.cantileverPoints) and " +
                    "whether to snap-repair the rest (answers.repairFreeEnds); confirm supports (fixed vs " +
                    "pinned, which points) and loads the model cannot know. The summary returns verdicts " +
                    "and the WORST members with their source object ids — point at them, don't recite " +
                    "coordinates. Full per-member checks (deflection, axial, moments, stress, " +
                    "utilization, slenderness) and the displacement field land in the " +
                    "structural/results.json artifact; the summary's resultsPathAbsolute feeds the " +
                    "diagnosis viewer payload (structural_viewer.py) when the user asks to SEE the " +
                    "state in the viewport. Supports and the utilization screen are stated " +
                    "ASSUMPTIONS — name them in your report (the screen is not a code member design: no " +
                    "buckling, shear or connection checks); podium/boundary details need drawings the " +
                    "model does not carry. Coordinates in answers are in DOCUMENT units.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            membersArtifact = new { type = "string", description = "Extraction artifact path; default structural/members.json." },
                            answers = new
                            {
                                type = "object",
                                description = "The user's ask-back answers.",
                                properties = new
                                {
                                    repairFreeEnds = new { type = "boolean", description = "User approved wide-radius snap repair of unconfirmed free ends." },
                                    cantileverPoints = new
                                    {
                                        type = "array",
                                        description = "User-confirmed intended cantilever endpoints [x,y,z] (never repaired).",
                                        items = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                                    },
                                    extraDistributedKnPerM = new
                                    {
                                        type = "object",
                                        description = "Additional line load per mark in kN/m (user-supplied dead+live, lumped).",
                                        additionalProperties = new { type = "number" },
                                    },
                                    markSections = new
                                    {
                                        type = "object",
                                        description = "Section override per mark when the user knows the schedule (e.g. {\"SG1\":\"H-400x200x8x13\"}).",
                                        additionalProperties = new { type = "string" },
                                    },
                                    deflectionLimitRatio = new { type = "number", description = "Member check limit L/ratio; default 250." },
                                    roleSections = new
                                    {
                                        type = "object",
                                        description = "Section per geometric role for members whose mark has none: {\"column\":\"H-300x300x10x15\",\"beam\":\"H-400x200x8x13\",\"brace\":\"H-150x150x7x10\"}. Names are KS catalog rows (data_read structural/sections-ks.json).",
                                        additionalProperties = new { type = "string" },
                                    },
                                    defaultSection = new { type = "string", description = "Catalog section for anything still unresolved; default H-300x300x10x15." },
                                    supportType = new { type = "string", @enum = new[] { "fixed", "pinned" }, description = "All supports fixed (6 DOF, default) or pinned (translations only)." },
                                    supportPoints = new
                                    {
                                        type = "array",
                                        description = "User-named support locations [x,y,z] in document units (snapped to the nearest node); added to the detected ones.",
                                        items = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                                    },
                                    autoSupports = new { type = "boolean", description = "Detect supports from geometry (base band + column feet); default true. false = only supportPoints." },
                                    lineLoads = new
                                    {
                                        type = "array",
                                        description = "Distributed loads by role or mark, tagged G (permanent) or Q (variable): [{\"role\":\"beam\",\"kNPerM\":5,\"case\":\"Q\"}]. Area loads × tributary width become kN/m here.",
                                        items = new
                                        {
                                            type = "object",
                                            properties = new
                                            {
                                                role = new { type = "string", @enum = new[] { "column", "beam", "brace" } },
                                                mark = new { type = "string" },
                                                kNPerM = new { type = "number" },
                                                @case = new { type = "string", @enum = new[] { "G", "Q" } },
                                            },
                                            required = new[] { "kNPerM" },
                                            additionalProperties = false,
                                        },
                                    },
                                    pointLoadsKn = new
                                    {
                                        type = "array",
                                        description = "Point loads at a location [x,y,z] (document units; snapped to a node or a member interior): [{\"point\":[4000,0,3000],\"fz\":-50,\"case\":\"Q\"}]. fz negative = downward.",
                                        items = new
                                        {
                                            type = "object",
                                            properties = new
                                            {
                                                point = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                                                fx = new { type = "number" },
                                                fy = new { type = "number" },
                                                fz = new { type = "number" },
                                                @case = new { type = "string", @enum = new[] { "G", "Q" } },
                                            },
                                            required = new[] { "point" },
                                            additionalProperties = false,
                                        },
                                    },
                                    loadFactors = new
                                    {
                                        type = "object",
                                        description = "ULS partial factors; default {\"G\":1.35,\"Q\":1.5} (EC0). KDS: {\"G\":1.2,\"Q\":1.6}.",
                                        properties = new { G = new { type = "number" }, Q = new { type = "number" } },
                                        additionalProperties = false,
                                    },
                                    fyMPa = new { type = "number", description = "Steel yield strength for the utilization screen; default 275 (SS275/SM275)." },
                                    maxUtilization = new { type = "number", description = "Utilization limit for the screen; default 1.0." },
                                    slendernessLimit = new { type = "number", description = "L/r_min limit for compression members; default 200." },
                                },
                                additionalProperties = false,
                            },
                        },
                        additionalProperties = false
                    }),
                Function(
                    "layer_scheme_draft",
                    "Read-only first step of layer curation: reports how THIS document's layer names " +
                    "actually group — shared parent layer, structural mark family (SC1/SC2/SC5 -> SC), " +
                    "shared token, or shared Korean substring (외벽 and 콘크리트 벽 share 벽 with no " +
                    "separator to split on). Use it BEFORE proposing any labelling, because naming " +
                    "conventions differ per office, project and designer: the scheme must come from the " +
                    "user's own file, not from Vino's shipped vocabulary, which only annotates a group " +
                    "it recognises (hintCanonical/hintMaterial) and never creates one. Groups are " +
                    "OBSERVED overlaps, not decisions — propose names and materials for them, show the " +
                    "user, and let them correct or reject. Layers under 'ungrouped' matched no rule: " +
                    "leave them unclassified rather than forcing them into the nearest group, and " +
                    "'alsoMatched' names the other keys a layer hit (usually a second axis, e.g. a " +
                    "material inside an element name). Writes nothing and raises no card.",
                    new { type = "object", properties = new { }, additionalProperties = false }),
                Function(
                    "rhino_layers",
                    "Read the bound Rhino document's full layer table (path, parent, color, visibility, lock, " +
                    "object count including hidden and block members, whether it has children, per-layer " +
                    "fingerprint, and any vino.* semantic labels as userText) plus the saved named layer " +
                    "states. Read-only. Use it before any layer work: " +
                    "the fingerprints are what layer updates and deletes must pin, and the object/children " +
                    "counts are what prove a layer is safely deletable. Save a named layer state before a " +
                    "layer sweep so the whole sweep can be reverted without touching geometry.",
                    new
                    {
                        type = "object",
                        properties = new { },
                        additionalProperties = false
                    }),
                Function(
                    "data_flow_read",
                    "Read the Rhino<->Grasshopper data-flow ledger for the session's bound GH document: every " +
                    "Rhino object its parameters reference (with per-object existence — a missing object means a " +
                    "broken reference silently emitting empty data) and every Vino-stamped bake grouped by " +
                    "source document and family. Read-only. Consult it before deleting or purging Rhino objects: " +
                    "never remove a referenced object SILENTLY — name the parameter that breaks and ask first. " +
                    "If the user then explicitly confirms despite the breakage, proceed: the human's informed " +
                    "decision wins over the guard. If a writer session is active this returns writerActive=true " +
                    "immediately instead of queueing.",
                    new
                    {
                        type = "object",
                        properties = new { },
                        additionalProperties = false
                    }),
                Function(
                    "inspect_outputs",
                    "Read a component's live output data: per-output DataCount, TypeNames, GeometryBounds, and capped " +
                    "sample values. Use it to ground input access (item/list/tree), type hints, and to verify a script " +
                    "produced sensible geometry — never guess the data when you can read it. Committed jobs already " +
                    "include the same report under committed.outputs; call this for ad-hoc inspection when idle. If a " +
                    "writer session is active this returns writerActive=true immediately instead of queueing.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            objectId = new { type = "string", format = "uuid", description = "Grasshopper component object id." },
                            includeMassProperties = new
                            {
                                type = "boolean",
                                description = "Also compute per-geometry Area/Volume (expensive integration on Rhino's " +
                                    "UI thread). Default false — set true only when you actually need area/volume numbers."
                            }
                        },
                        required = new[] { "objectId" },
                        additionalProperties = false
                    }),
                Function(
                    "artifact_read",
                    "Read a draft artifact belonging only to this chat session.",
                    new
                    {
                        type = "object",
                        properties = new { path = new { type = "string" } },
                        required = new[] { "path" },
                        additionalProperties = false
                    }),
                Function(
                    "artifact_write",
                    "Write code or a structured operation payload into this chat session's isolated draft storage. This " +
                    "never changes Rhino or Grasshopper. Operation payloads are exactly one JSON object " +
                    "{\"bridgeOperation\":\"...\",\"arguments\":{...}} — the full mapping is documented on change_submit.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Session-relative path such as operations/move-01.json; traversal and the broker-owned .vino-reserved namespace are rejected." },
                            content = new { type = "string", description = "UTF-8 text. Operation payloads must contain one JSON object." }
                        },
                        required = new[] { "path", "content" },
                        additionalProperties = false
                    }),
                Function(
                    "change_submit",
                    "Submit a typed ChangeSet to the central single-writer broker. Pass wait=true to receive the terminal " +
                    "result (state, diagnostics, committed view with sockets/outputs) in this same response for fast jobs. " +
                    "If the returned state is still queued/executing — normal when other sessions are ahead — fall back to " +
                    "polling job_status; the jobId is always returned. state=failed with an applied block means the writes " +
                    "landed but did not commit (e.g. script compile/runtime errors): read diagnostics[], fix, and resubmit " +
                    "with gptino:auto — the retry is not stale-blocked. CLEANUP TIERS: cleanup ChangeSets declare " +
                    "changeSet.intent — cleanupRelayout (moveComponent/setLayout only), cleanupRegroup (adds setGroup), " +
                    "cleanupDestructive (adds deleteComponent; deleting orphans or your own components needs no grant); " +
                    "omit intent for authoring. Regardless of intent, deleting a component that still has wires to " +
                    "surviving components is refused unless this session authored it (and it is unchanged) or the user " +
                    "approved that exact (objectId, current STRUCTURE fingerprint) — the same gate covers cutting " +
                    "dataflow INTO a live foreign component (a bare disconnectWire, or a setComponentIo dropping its " +
                    "wired inputs). Rebuilds run author → rewire → delete-orphans, and a live foreign delete cannot " +
                    "share a ChangeSet with create/wire/source/disconnect/schema/value/reference operations. " + PayloadGuide,
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            changeSet = ChangeSetSchema(),
                            expectedSnapshotId = new { type = "string", description = "gptino:auto to let the server anchor to the current snapshot, or the exact snapshotId returned by snapshot_read." },
                            idempotencyKey = new { type = "string", description = "Stable unique key for retrying this logically identical submission." },
                            summary = new { type = "string", description = "Short user-visible queue/history summary." },
                            wait = new { type = "boolean", description = "Block briefly (bounded well under the tool deadline) for the terminal result; default false. Timeout is normal, not an error — poll job_status then." }
                        },
                        required = new[] { "changeSet", "expectedSnapshotId", "idempotencyKey", "summary" },
                        additionalProperties = false
                    }),
                Function(
                    "layout_history",
                    "List this Grasshopper document's managed history, newest first. Every verified job " +
                    "already commits a full canvas snapshot, so this is the record of what the canvas " +
                    "looked like before each change. Each row is {sha, revision, summary, committedAt, " +
                    "movedLayout}; movedLayout marks the automatic tidy jobs, which are the ones a user " +
                    "most often wants undone. Read-only. Pair it with rewind_layout, which takes a sha " +
                    "from here.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            limit = new { type = "integer", minimum = 1, maximum = 200, description = "Newest N revisions; default 40." }
                        },
                        additionalProperties = false
                    }),
                Function(
                    "rewind_layout",
                    "Put the canvas back to what it was at a past revision. Use it when the user says " +
                    "their work was rearranged or undone and they want it back — 'undo the tidy', " +
                    "'canvas를 작업 전으로 돌려놔'. Pass a sha from layout_history, or that sha with " +
                    "restoreStateBefore:true to undo THAT job (restores its parent's state — the usual " +
                    "intent). scope:'positions' (default) moves components only; scope:'canvas' also " +
                    "reconnects wires that were cut, removes wires added since, and puts input-control " +
                    "values (slider, Value List, toggle, panel) back. It submits ONE ordinary ChangeSet " +
                    "through the same guarded path as any other write, so anything the user has since " +
                    "changed by hand blocks the restore instead of being silently overwritten. " +
                    "Script SOURCE is restored too, for every script Vino has written at least once: " +
                    "each provenance commit stores the text, so the past text is on disk and gets " +
                    "written back — the CODE, not the bytes: the write re-stamps the language " +
                    "directive and normalises line endings. A script Vino has NEVER edited has no " +
                    "stored text, and a C# component still holding Rhino's default GH_ScriptInstance " +
                    "template cannot have that template written back; either way the id comes " +
                    "back in `sourceNotRestored`, so tell the user that script was left as-is " +
                    "rather than reporting a complete undo. Restored scripts are marked dirty and " +
                    "recompute with the rest of the restore; a restore never runs code on its own. " +
                    "A component created since the restore point is left alone (restoring must " +
                    "never look like a deletion) and is counted in `componentsAddedSinceThen`. Reports " +
                    "{restoredFrom, moved, wiresReconnected, wiresRemoved, valuesRestored, " +
                    "sourcesRestored, componentsAddedSinceThen, componentsGoneSinceThen, " +
                    "sourceNotRestored}.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            sha = new { type = "string", description = "Managed-history revision from layout_history." },
                            restoreStateBefore = new { type = "boolean", description = "Restore the state this revision REPLACED (its parent) rather than the state it produced. Use true to undo that job; default false." },
                            scope = Enum("positions", "canvas"),
                            wait = new { type = "boolean", description = "Block briefly for the terminal result; default true." }
                        },
                        required = new[] { "sha" },
                        additionalProperties = false
                    }),
                Function(
                    "arrange_layout",
                    "Tidy the canvas: the server computes a clean left-to-right dataflow layout (inputs on the left, " +
                    "script stages flowing rightward, outputs on the right, stacked top-to-bottom, groups kept together) " +
                    "from the wire topology and real component sizes, then moves the components. You pass only the objectIds " +
                    "you authored (seedComponentIds); the whole connected dataflow cluster they belong to is arranged, and " +
                    "every coordinate is server-owned — you never compute positions or fingerprints. It is a single canvas.move " +
                    "under the hood (single-writer, rollback-safe) and a no-op when the cluster is already tidy. THIS CALL MOVES " +
                    "COMPONENTS YOU DID NOT NAME — everything wired to your seeds, including work the user placed by hand. Call it " +
                    "only when re-tidying the whole cluster is what was asked for. The host also runs a tidy itself after a " +
                    "component-creating turn, but that one moves ONLY what the turn created, so it never covers the cluster this " +
                    "call would. It is disabled entirely for projects whose own rules define a canvas standard or forbid the " +
                    "automatic tidy — the call then returns status 'disabled-by-project-rules' and moves nothing; place components " +
                    "yourself there. The result reports a `layout` audit (backward wires, " +
                    "column crowding, edge alignment) measured server-side from the committed arrangement.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            seedComponentIds = new
                            {
                                type = "array",
                                items = new { type = "string", format = "uuid" },
                                description = "objectIds of components you authored; the connected cluster around them is tidied."
                            },
                            wait = new { type = "boolean", description = "Block briefly for the terminal result; default true." }
                        },
                        required = new[] { "seedComponentIds" },
                        additionalProperties = false
                    }),
                Function(
                    "consolidate_stages",
                    "Mechanically merge a VERIFIED chain of staged C# script components into one block-structured " +
                    "component (action:merge), or split a merged component back into stages (action:split). Server-side " +
                    "and deterministic: no re-authoring — sources are concatenated with collision renames, wires become " +
                    "seam variables, and the merged component is EXECUTED and field-compared against the old chain's " +
                    "sink before any consumer is rewired or any stage deleted (mismatch discards the merged component; " +
                    "the chain is never touched first). Requirements for merge: every stage is C#, has a committed " +
                    "measured solve, the group is wire-connected with exactly one sink, no intermediate output leaves " +
                    "the group, and the measured solve sum fits the 2s consolidation cap. Use dryRun:true first to see " +
                    "the plan and the merged source without writes. After a merge, edit single blocks with the " +
                    "replaceSourceBlock operation (python.replaceBlock) instead of full-source rewrites. Do NOT merge " +
                    "across a slider the user is actively tuning or a checkpoint stage the user watches — those seams " +
                    "earn their cost.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            action = Enum("merge", "split"),
                            stageComponentIds = new
                            {
                                type = "array",
                                items = new { type = "string", format = "uuid" },
                                description = "merge only: the staged script components to merge (at least two, wire-connected)."
                            },
                            componentId = new { type = "string", format = "uuid", description = "split only: the merged component." },
                            dryRun = new { type = "boolean", description = "Report the plan (and merged source) without any writes; default false." },
                            nickName = new { type = "string", description = "merge only: nickname for the merged component." }
                        },
                        required = new[] { "action" },
                        additionalProperties = false
                    }),
                Function(
                    "job_status",
                    "Read queue, execution, verification, commit, recovery-required, or failure state for a submitted job. " +
                    "Terminal states include diagnostics[] (per-operation errors/warnings/remarks from the live solve; " +
                    "capped at 50 rows, errors kept first — diagnosticsOmitted reports per-severity counts when trimmed). " +
                    "A committed job includes committed { snapshotId, revision, resources[].fingerprint, sockets, outputs }: " +
                    "base the next ChangeSet on these fingerprints, wire using the Grasshopper-assigned socket ids in " +
                    "committed.sockets, and verify results from committed.outputs instead of calling snapshot_read again. " +
                    "A failed job with an applied block landed its writes without committing (script errors report this " +
                    "way): read diagnostics[], fix the source, resubmit with gptino:auto.",
                    new
                    {
                        type = "object",
                        properties = new { jobId = new { type = "string", format = "uuid" } },
                        required = new[] { "jobId" },
                        additionalProperties = false
                    }),
                Function(
                    "recovery_resume",
                    "Lift the host-enforced session halt after a job ended recoveryRequired. When that " +
                    "happens the host halts THIS session only: queued jobs are cancelled and new " +
                    "change_submit calls are refused until you resume. FIRST inspect job_status for the " +
                    "halting job and the live document, report the actual state to the user, THEN call " +
                    "this with the halting jobId. Never blind-resubmit: jobs cancelled by the halt need a " +
                    "NEW idempotencyKey, resubmitted only after resume. A wrong jobId does not resume — " +
                    "the response returns the current halt {jobId, message} so you can self-correct. " +
                    "Idempotent when the session is not halted.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            jobId = new { type = "string", format = "uuid", description = "The jobId that ended recoveryRequired and halted this session." }
                        },
                        required = new[] { "jobId" },
                        additionalProperties = false
                    }),
                Function(
                    "skill_read",
                    "Read a built-in Vino skill: vetted Python sources and reference notes shipped with the plugin. " +
                    "The available skills are indexed in your instructions. Use skill code verbatim for conventional " +
                    "plumbing such as baking; adapt reference notes freely.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Skill file name from the index, for example bake_manager.py." }
                        },
                        required = new[] { "name" },
                        additionalProperties = false
                    }),
                Function(
                    "goal_propose",
                    "Frame what the user asked for BEFORE doing the work, and stop for their confirmation. " +
                    "Use it when the request is ambiguous, large, destructive, or hard to reverse — not for " +
                    "small obvious edits. Write the objective in one sentence in the user's own terms; make " +
                    "each criterion something a tool result can decide (a predicate, a job outcome, a measured " +
                    "value), never a feeling; list the assumptions you had to invent and what you are " +
                    "deliberately leaving out. Options are the user's structured replies — give 2-4, put the " +
                    "one you recommend first, and attach objectIds to an option when choosing it should also " +
                    "show that geometry in the viewport. This tool does NOT run the work: after calling it, " +
                    "end your turn and wait. The confirmed card returns on every later turn, and you will be " +
                    "asked to score yourself against these exact criteria.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            objective = new { type = "string", description = "One sentence: what will be true when this is done." },
                            criteria = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "Checks that decide success, each answerable from a tool result."
                            },
                            assumptions = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "What you had to assume because the request did not say."
                            },
                            outOfScope = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "What you are deliberately NOT doing, so the user can object now."
                            },
                            options = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        id = new { type = "string", description = "Short stable id, e.g. approve / narrow-scope." },
                                        label = new { type = "string", description = "What the user is choosing, in their language." },
                                        detail = new { type = "string", description = "One line on what changes if they pick this." },
                                        objectIds = new
                                        {
                                            type = "array",
                                            items = new { type = "string", format = "uuid" },
                                            description = "Rhino objects this option is about; the panel can show them."
                                        }
                                    },
                                    required = new[] { "id", "label" },
                                    additionalProperties = false
                                }
                            }
                        },
                        required = new[] { "objective", "criteria" },
                        additionalProperties = false
                    }),
                Function(
                    "ask_user",
                    "Ask the user a question they can ANSWER WITH A CLICK, and end your turn. Use this " +
                    "for every decision you would otherwise have written as prose and stopped on: which " +
                    "of two approaches, whether to replace a component in parallel before removing the " +
                    "old one, whether a side effect is acceptable. Prose questions cannot be clicked, so " +
                    "they cost the user a typed reply every time — never end a turn with a question in " +
                    "prose when this tool fits. Give 2-4 concrete options, mark the one you recommend, " +
                    "and put WHY you must ask in `because`. This tool changes nothing and grants " +
                    "nothing: for permission to touch the user's own geometry you still need " +
                    "approval_request, which mints the grant the broker checks. The answer arrives as " +
                    "the next turn's message, so stop after calling this.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            question = new { type = "string", description = "One sentence, in the user's language. Ask exactly one thing." },
                            because = new { type = "string", description = "One line: what is at stake / why you cannot just decide." },
                            options = new
                            {
                                type = "array",
                                minItems = 2,
                                maxItems = 4,
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        id = new { type = "string", description = "Short stable id, e.g. 'parallel-replace'." },
                                        label = new { type = "string", description = "The button text, in the user's language." },
                                        detail = new { type = "string", description = "One line on what this choice actually does." },
                                        recommended = new { type = "boolean", description = "Set on AT MOST ONE option — it becomes the default the user can accept without reading every line." }
                                    },
                                    required = new[] { "id", "label" },
                                    additionalProperties = false
                                }
                            }
                        },
                        required = new[] { "question", "options" },
                        additionalProperties = false
                    }),
                Function(
                    "approval_request",
                    "Ask the user to approve destructive fixes to geometry THEY made — and destructive " +
                    "CLEANUP of live Grasshopper components you did not author. The broker refuses " +
                    "delete/modify/transform on objects without Vino provenance unless the ChangeSet " +
                    "carries an approvalGrantId, and this is how you get one. List exactly what you would " +
                    "touch, one item per finding, each with the objectIds AND the fingerprints rhino_audit " +
                    "returned — the grant binds to those fingerprints, so a stale one fails the fix instead " +
                    "of hitting a moved object. For destructive-cleanup approval on Grasshopper components, " +
                    "fill each target completely: objectId = the component INSTANCE id, fingerprint = the " +
                    "same fingerprint the delete CAS expects — the component's STRUCTURE fingerprint, i.e. " +
                    "the grasshopperComponent resource fingerprint from snapshot/job results, label = a " +
                    "short name, role = what the component does in the definition, impact = what changes if " +
                    "it is deleted (which wires get cut / what replaces it) — the user judges the deletion " +
                    "from those lines. Add choices when the machine must not decide (which of two " +
                    "near-duplicates to keep is always the user's call). Never bundle unrelated fixes into " +
                    "one item. This tool does NOT change anything: after calling it, end your turn. The " +
                    "granted items and the grantId arrive with the next turn. LAYER CURATION: pass " +
                    "kind=layerSemantics after a layerSemantics audit — each item targets one layer " +
                    "(objectId = layerId, fingerprint = the audit's layer fingerprint) and the SERVER fills " +
                    "the proposal row (canonical, material, confidence, colors) from its own scan; anything " +
                    "you author for those fields is ignored, and items whose layer the scan did not report " +
                    "are dropped. For unmatched (low) rows add choices listing candidate material families " +
                    "from the audit's familyColors keys.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            summary = new { type = "string", description = "One line: what you audited and what you propose." },
                            kind = new
                            {
                                type = "string",
                                @enum = new[] { "layerSemantics", "layerScheme" },
                                description = "Card kind. Omit for the classic destructive-fix card; " +
                                    "layerSemantics renders the server-filled layer proposal table; " +
                                    "layerScheme settles this PROJECT's naming rules (see the items' " +
                                    "scheme object) and writes them on approval.",
                            },
                            items = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        id = new { type = "string", description = "Finding id from the audit result." },
                                        label = new { type = "string", description = "What this fix does, in the user's language." },
                                        measure = new { type = "string", description = "The measured value verbatim (gap, distance), with units." },
                                        targets = new
                                        {
                                            type = "array",
                                            items = new
                                            {
                                                type = "object",
                                                properties = new
                                                {
                                                    objectId = new { type = "string", format = "uuid", description = "Rhino object id, or the Grasshopper component INSTANCE id for canvas cleanup." },
                                                    fingerprint = new { type = "string", description = "The fingerprint the audit reported — for a GH component, the same fingerprint the delete CAS expects: its CURRENT structure fingerprint (the grasshopperComponent resource fingerprint from snapshot/job results)." },
                                                    label = new { type = "string", description = "Short display name for this target, in the user's language." },
                                                    role = new { type = "string", description = "What this component does in the definition (fill for destructive cleanup)." },
                                                    impact = new { type = "string", description = "What changes if it is deleted — which wires get cut, what replaces it." },
                                                    domain = new
                                                    {
                                                        type = "string",
                                                        @enum = new[] { "rhino", "grasshopper" },
                                                        description = "Which viewport can show this id. Set \"grasshopper\" ONLY for canvas " +
                                                            "component instance ids; Rhino objects and layers are \"rhino\" (the default when " +
                                                            "omitted). The card's zoom control follows this — a Rhino target sent to the canvas " +
                                                            "cannot be shown at all when no definition is open.",
                                                    }
                                                },
                                                required = new[] { "objectId", "fingerprint" },
                                                additionalProperties = false
                                            }
                                        },
                                        choices = new
                                        {
                                            type = "array",
                                            items = new { type = "string" },
                                            description = "Options only a human should pick between, e.g. which copy to keep."
                                        },
                                        scheme = new
                                        {
                                            type = "object",
                                            description = "kind=layerScheme ONLY: one proposed rule for a group of "
                                                + "layers. Two INDEPENDENT axes — element is what the layers ARE, "
                                                + "material is what they are MADE OF — because the same mark can be "
                                                + "a steel column in one office and a concrete one in another, and "
                                                + "colour comes from material. Give at least one axis.",
                                            properties = new
                                            {
                                                groupKey = new { type = "string", description = "The draft's group key (e.g. SC, 벽, 철골)." },
                                                groupKind = new
                                                {
                                                    type = "string",
                                                    @enum = new[] { "markFamily", "parent", "token", "substring", "proposed" },
                                                    description = "From layer_scheme_draft. markFamily also earns a digit pattern, so SC7 matches later even if only SC1..SC5 were on screen.",
                                                },
                                                members = new
                                                {
                                                    type = "array",
                                                    items = new { type = "string" },
                                                    description = "Layer full paths from the latest layer_scheme_draft — anything else is rejected.",
                                                },
                                                element = new { type = "string", description = "What these layers ARE, in the USER's vocabulary (free text — do not force ours)." },
                                                material = new { type = "string", description = "Must be one of the palette families the draft reported; colour is derived from it." },
                                                underPath = new { type = "string", description = "Scope the MATERIAL to this layer branch (e.g. 철골). The strongest and most common form — a parent layer is how a file usually declares what its contents are made of." },
                                                evidence = new { type = "string", description = "Why you propose this, in the user's language." }
                                            },
                                            required = new[] { "members" },
                                            additionalProperties = false
                                        }
                                    },
                                    required = new[] { "id", "label" },
                                    additionalProperties = false
                                }
                            }
                        },
                        required = new[] { "summary", "items" },
                        additionalProperties = false
                    }),
                Function(
                    "goal_score",
                    "Close out a confirmed goal by answering ITS criteria one by one. Every verdict must quote " +
                    "the evidence that decided it — a job id, a predicate outcome, a measured output value. " +
                    "'It looks right' is not evidence; if nothing verified a criterion, mark it failed and say " +
                    "what is missing. Call this once the work is done, before your closing report.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            scores = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        criterion = new { type = "string", description = "The criterion verbatim from the confirmed card." },
                                        passed = new { type = "boolean" },
                                        evidence = new { type = "string", description = "Job id / predicate / measured value that decided it." }
                                    },
                                    required = new[] { "criterion", "passed", "evidence" },
                                    additionalProperties = false
                                }
                            }
                        },
                        required = new[] { "scores" },
                        additionalProperties = false
                    }),
                Function(
                    "memory_append",
                    "Append a durable note to this project's MEMORY.md (append-only, folded into every future session for " +
                    "this project). Use ONLY for a non-obvious, reusable lesson: a symptom -> cause -> fix, a hard project " +
                    "constraint, or a convention the user confirmed. One concise entry; never restate the obvious, the " +
                    "current task, or code the repo already records. Refused if MEMORY.md is near its size cap.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            entry = new { type = "string", description = "Markdown note to append, e.g. a short '## Title' with symptom/cause/fix lines." }
                        },
                        required = new[] { "entry" },
                        additionalProperties = false
                    }),
                Function(
                    "data_read",
                    "Read a shipped Vino reference data file, such as the structural catalogs " +
                    "structural/sections.json (steel profile properties) and structural/materials.json " +
                    "(material constants). Read-only. Extract the rows you need and inject the values into " +
                    "script payloads — Rhino-side scripts must never depend on these file paths.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Data file path relative to the data root, for example structural/sections.json." }
                        },
                        required = new[] { "name" },
                        additionalProperties = false
                    })
            }
        }
    ];

    private static object ChangeSetSchema() => new
    {
        type = "object",
        description = "Immutable optimistic-concurrency contract. IDs and fingerprints must come from the bound snapshot/inspections.",
        properties = new
        {
            changeSetId = Uuid(),
            projectId = Uuid(),
            sessionId = Uuid(),
            baseSnapshotRevision = new { type = "integer", minimum = -1, description = "-1 to let the server anchor to the current revision, or the exact revision from snapshot_read/job_status." },
            baseGitCommit = NullableString("Managed-history HEAD from the snapshot, or null before baseline."),
            dependencies = new { type = "array", items = Uuid() },
            readSet = new { type = "array", items = ResourceExpectationSchema() },
            writeSet = new { type = "array", items = ResourceExpectationSchema() },
            operations = new { type = "array", minItems = 1, items = TypedOperationSchema() },
            acceptancePredicates = new { type = "array", items = PredicateSchema() },
            rollbackBeforeImages = new { type = "array", items = RollbackSchema() },
            createdAt = new { type = "string", format = "date-time" },
            approvalGrantId = new
            {
                type = "string",
                description = "Panel-issued user approval id. Required ONLY when a destructive op " +
                    "(delete/modify/transform/fixEndpointPair) targets an object without Vino " +
                    "provenance stamps — i.e. the user's own geometry. The user mints it by " +
                    "approving on the audit card; never invent one. Never author approved or " +
                    "sourceDocKey fields yourself — the server injects them."
            },
            intent = new
            {
                type = "string",
                @enum = new[] { CleanupIntents.Relayout, CleanupIntents.Regroup, CleanupIntents.Destructive },
                description = "Declared cleanup tier; omit for authoring work. cleanupRelayout admits " +
                    "moveComponent/setLayout; cleanupRegroup adds setGroup; cleanupDestructive adds " +
                    "deleteComponent — no grant needed for orphans or components this session authored. " +
                    "Ops outside the declared tier are rejected at submit. Deleting live (survivor-wired) " +
                    "components you did not author still needs user approval (approvalGrantId) " +
                    "regardless of tier."
            }
        },
        required = new[]
        {
            "changeSetId", "projectId", "sessionId", "baseSnapshotRevision", "baseGitCommit",
            "dependencies", "readSet", "writeSet", "operations", "acceptancePredicates",
            "rollbackBeforeImages", "createdAt"
        },
        additionalProperties = false
    };

    private static object TypedOperationSchema() => new
    {
        type = "object",
        properties = new
        {
            operationId = new { type = "string", minLength = 1 },
            kind = Enum(
                "read", "moveComponent", "connectWire", "disconnectWire", "setValue",
                "setInputValue",
                "updatePythonSource", "setComponentIo", "replaceComponentIo", "replaceSourceBlock",
                "convertSocket",
                "createComponent", "deleteComponent",
                "setLayout", "createRhinoObject", "modifyRhinoObject", "deleteRhinoObject",
                "bakeGeometry", "updateRhinoAttributes", "setGroup",
                "executePython", "readRuntimeMessages", "createRhinoPrimitive", "transformRhinoObject",
                "referenceRhinoObjects", "fixRhinoEndpointPair", "purgeTableEntries",
                "moveObjectsToLayer", "updateRhinoLayerProperties", "deleteRhinoLayer",
                "saveRhinoLayerState", "ensureRhinoLayer"),
            owner = Enum("script", "canvas", "rhinoBridge"),
            reads = new { type = "array", items = ResourceAddressSchema() },
            writes = new { type = "array", items = ResourceAddressSchema() },
            reversible = new { type = "boolean" },
            payloadArtifact = new { type = "string", minLength = 1, description = "Path previously written with artifact_write in this same session." }
        },
        required = new[] { "operationId", "kind", "owner", "reads", "writes", "reversible", "payloadArtifact" },
        additionalProperties = false
    };

    private static object ResourceExpectationSchema() => new
    {
        type = "object",
        properties = new
        {
            resource = ResourceAddressSchema(),
            expectedFingerprint = new
            {
                type = "string",
                minLength = 1,
                description = "gptino:auto (server fills it from this session's own last commit), the actual snapshot fingerprint, or gptino:absent only for a supported exact create target."
            }
        },
        required = new[] { "resource", "expectedFingerprint" },
        additionalProperties = false
    };

    private static object ResourceAddressSchema() => new
    {
        type = "object",
        properties = new
        {
            kind = Enum(
                "document", "grasshopperComponent", "grasshopperComponentSource", "grasshopperComponentIo",
                "grasshopperComponentValue", "grasshopperComponentLayout", "grasshopperWire", "grasshopperGroup",
                "rhinoObject", "rhinoObjectGeometry", "rhinoObjectAttributes",
                "rhinoLayer", "rhinoLayerTable", "rhinoBlockDefinition", "rhinoDimensionStyle",
                "rhinoMaterial", "rhinoLinetype"),
            id = new { type = "string", minLength = 1 },
            field = new { type = "string", minLength = 1, description = "Use * for the whole conflict domain." }
        },
        required = new[] { "kind", "id", "field" },
        additionalProperties = false
    };

    private static object PredicateSchema() => new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string", minLength = 1 },
            kind = Enum(
                "fingerprintEquals", "runtimeErrorAbsent", "wireExists", "wireAbsent",
                "objectExists", "objectAbsent",
                "outputCountInRange", "geometryClosed", "areaInRange",
                "dataTreeBranchCountInRange", "volumeInRange", "boundingBoxInRange"),
            resource = new { oneOf = new object[] { ResourceAddressSchema(), new { type = "null" } } },
            expectedValue = NullableString("Expected fingerprint/value, or null for existence and runtime-error checks.")
        },
        required = new[] { "name", "kind", "resource", "expectedValue" },
        additionalProperties = false
    };

    private static object RollbackSchema() => new
    {
        type = "object",
        description = "Optional provenance-only before image in this alpha; failed writes that were verifiably rolled back (or refused before any write) are reported as failed with an explanatory message; recoveryRequired remains for genuinely unknown outcomes.",
        properties = new
        {
            resource = ResourceAddressSchema(),
            artifactReference = new { type = "string", minLength = 1 },
            fingerprint = new { type = "string", minLength = 1 }
        },
        required = new[] { "resource", "artifactReference", "fingerprint" },
        additionalProperties = false
    };

    private static object Uuid() => new { type = "string", format = "uuid" };

    private static object NullableString(string description) =>
        new { type = new[] { "string", "null" }, description };

    private static object Enum(params string[] values) => new { type = "string", @enum = values };

    private static object Function(string name, string description, object inputSchema) => new
    {
        type = "function",
        name,
        description,
        inputSchema
    };
}
