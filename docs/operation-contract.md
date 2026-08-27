# Typed operation contract

Codex sessions can reason, draft, and consume cached state in parallel, but they
cannot write the bridge directly. AgentHost read leases may overlap; actual
Rhino/Grasshopper UI-context bridge work is currently processed sequentially.
Codex first saves one JSON payload per operation with `artifact_write`, then
submits an immutable `ChangeSet`. JSON properties and enum values are camelCase.

## ChangeSet rules

- `projectId`, `sessionId`, `baseSnapshotRevision`, and the separately supplied
  `expectedSnapshotId` must match the bound runtime and calling thread.
- `readSet` and `writeSet` contain exact resource addresses and fingerprints
  returned by `snapshot_read` or a scoped inspection.
- Every `operations[].reads` entry is covered by an actual fingerprint in
  `readSet`; every `operations[].writes` entry is covered by `writeSet`.
- A supported exact create target uses the shared optimistic sentinel
  `gptino:absent`. It passes only while that resource is still absent. This is
  supported for `createComponent`, `referenceRhinoObjects`,
  `createRhinoPrimitive`, `createRhinoObject`, `bakeGeometry`, `connectWire`, a
  new `setGroup`, and a new layer via `ensureRhinoLayer`; all other resources
  require actual fingerprints.
- Every operation has a unique `operationId`, an owning adapter, and a
  session-relative `payloadArtifact` path.
- Resource fields are `*`. Object-like IDs use canonical lowercase D-format
  UUIDs; wire IDs use canonical lowercase N-format endpoint UUIDs.
- Operation write domains are exact: layout, component, wire, group, Python
  source/I/O/value, and whole Rhino object. Payload targets must equal all
  declared writes; unused expectations, extra writes, and overlapping writes by
  two operations in one ChangeSet are rejected. Python source/I/O/value writes
  on one component share a whole-component fingerprint conflict domain; a
  contiguous sequence is the sole sibling-domain exception and rolls each
  verified after-fingerprint into the next operation. A ChangeSet containing
  that sequence may not write a second Python component or any non-Script
  resource because solver activity can change runtime-sensitive fingerprints.
- Grasshopper component and Rhino object parent resources conflict with their
  child domains, so deletion or whole-object mutation cannot evade a
  source/layout/geometry/attributes dependency.
- Distinct Rhino object IDs cannot claim the same case-sensitive
  `logicalEntityId` in one ChangeSet; the broker rejects the batch before it is
  queued or any bridge write runs.
- Before durable acceptance, the broker validates every payload and copies its
  original JSON bytes into job-owned `.vino-reserved` storage with a SHA-256
  digest. Validation, freezing, and bridge execution all use those unmodified
  bytes, so integer syntax and floating-point negative zero are preserved. User
  artifact writes cannot access the reserved namespace.
- An idempotency key is bound to a separate semantic canonical hash of the
  accepted request, including payload content. Object property order and
  equivalent number spellings compare equally, while negative zero remains
  distinct from positive zero. Reusing the key for different accepted content
  is rejected.
- Every live write is verified by at least one `acceptancePredicate`. Predicates
  are optional in the submission: when omitted, the broker attaches the standard
  set per write kind (creates/bakes → `objectExists`, deletes → `objectAbsent`,
  wires → `wireExists`/`wireAbsent`, everything else → `runtimeErrorAbsent`)
  before the request hash is computed. Explicit predicates are used as declared.
- `change_submit` returns a job ID (with `wait=true`, fast jobs return their
  terminal state in the same response). Only `job_status=committed` means
  success. A `failed` job carrying an `applied` block landed its writes without
  committing — deterministic script compile/runtime errors report this way; the
  session iterates by fixing the source and resubmitting with `gptino:auto`.

Resource kinds are `document`, `grasshopperComponent`,
`grasshopperComponentSource`, `grasshopperComponentIo`,
`grasshopperComponentValue`, `grasshopperComponentLayout`, `grasshopperWire`,
`grasshopperGroup`, `grasshopperSolver`, `rhinoObject`, `rhinoObjectGeometry`,
`rhinoObjectAttributes`, `rhinoLayer`, `rhinoGroup`, `rhinoMaterial`,
`rhinoLinetype`, `rhinoLayerTable` (the layer table as a whole — one CAS domain
covering presence and absence of layers, used by `saveRhinoLayerState`),
`rhinoBlockDefinition`, and `rhinoDimensionStyle`. A resource field of `*`
addresses its whole conflict domain.

Bounded discovery tools are read-only and do not enter the writer queue:

- `component_catalog` searches installed Grasshopper component metadata with
  `{query?,limit?:1..100,includeObsolete?:boolean}`.
- `rhino_list` lists at most 500 objects using optional object/layer/name/type,
  logical-entity, and selection filters. It returns deterministic GUID order,
  per-object fingerprints and bounds, a union bound, and a truncation flag.
- `rhino_layers` lists the layer table with per-layer fingerprints plus the
  whole-table fingerprint (the CAS base for layer writes and layer states).
- `rhino_audit` runs the read-only document audits (duplicates, near-miss
  endpoints, invalid geometry, unused table entries, …) whose findings carry the
  exact fingerprints later hygiene ChangeSets declare. The `layerSemantics` kind
  is the layer-curation fact scan: one finding per still-unlabeled layer, with
  structured `layerFacts` for the server-side proposal table.
- `structural_extract` and `structural_solve` are the read-only structural
  pipeline (geometry extraction and PyNite solve); they never mutate the
  document. Curves are exploded into segments and classified by role
  (column/beam/brace); the solve takes the user's answers — sections per role,
  fixed/pinned supports and support points, G/Q-tagged line and point loads —
  and reports SLS deflection, an ULS elastic utilization screen, and warnings.

Use those results to choose exact component type IDs, object IDs, and base
fingerprints before drafting a ChangeSet.

Both tools acquire the shared document-read gate. Independent reads can overlap,
but once a writer is waiting, new reads wait and never overlap its exclusive
validation/mutation/verification epoch.

## Supported operations and payloads

Every payload file must contain exactly
`{"bridgeOperation":"...","arguments":{...}}`. The explicit bridge operation,
owner, and payload `operationId` must match the typed operation. The entire
batch receives local schema/shape/resource preflight before acceptance. At job
execution, every frozen `rhino.upsert` receives an additional read-only Rhino
bridge preflight before the first write in the batch. `geometryJson` must decode
to `GeometryBase`, its RhinoCommon object type must match `geometryType`, and
`IsValidWithLog` must succeed. A non-empty `attributesJson` must decode to
`ObjectAttributes`; the preflight checks its RhinoCommon type but does not
simulate applying it to the document tables. The same pass also checks the exact
live object fingerprint, requested identity, and logical-entity constraints.
This prevents invalid or type-mismatched later geometry, and a non-attribute
attributes payload, from partially applying an earlier operation. Rhino's
eventual `ObjectTable.Add`, `Replace`, or `ModifyAttributes` call can still fail
at execution time—for example because of document-table constraints—and the job
can then enter `recoveryRequired`.

Bridge application frames use the current `protocolVersion`
(`BridgeProtocol.Version`), carry the exact bound
document target, and validate request/response correlation. Reads carry
`BridgeOperationAccess.Read` and no lease. Writes carry `Write` plus a non-empty
host-generated writer lease, and each adapter rechecks its expected access. The
model and panel receive neither the bridge secret nor a writer lease.

The typed `read` row below is an operation inside a brokered ChangeSet and thus
runs within the exclusive job epoch. It is distinct from `snapshot_read`, scoped
inspection, `component_catalog`, and `rhino_list`.
Every typed read operation must declare an empty `writes` list. A read-only
ChangeSet must also have an empty `writeSet`; in a mixed ChangeSet, `writeSet`
may cover only its write operations.

| Typed kind | Owner / bridge operation | Payload |
|---|---|---|
| `read` | owner-specific inspect | Canvas/Rhino: `{objectId}`; Script: `{componentId}` |
| `moveComponent`, `setLayout` | Canvas / `canvas.move` | `{operationId,pivots:{guid:{x,y}},expectedFingerprints:{guid:sha256}}` |
| `setValue` | Canvas / `canvas.setNumberSlider` | `{operationId,objectId,expectedFingerprint,value,minimum,maximum,decimalPlaces}`; only Number Slider is supported |
| `setInputValue` | Canvas / `canvas.setInputValue` | `{operationId,objectId,expectedFingerprint,kind:"valueList"\|"booleanToggle"\|"panel"}` plus the field for that kind: `items:[{name,expression,selected?}]` and/or `selectedIndex` (valueList), `toggle` (booleanToggle), `text` (panel). A Button is readable (its state is in the snapshot) but NOT writable: assigning its expressions opens Grasshopper's breakpoint modal and blocks the bridge past its budget (measured live 2026-08-26). `kind` is checked against the live object type before any write, so a payload aimed at the wrong component is a pre-write refusal. `expectedFingerprint` is the component's **value** fingerprint, like `setValue` — a layout move never conflicts with a value write. Exactly one Value List item ends up selected (`selectedIndex` wins, else the first `selected:true`, else index 0); an empty item list is rejected because a list with no items emits nothing |

> **Managed-history restore.** `rewind_layout` replays a past snapshot as an ordinary ChangeSet: `scope:"positions"` emits `moveComponent`, `scope:"canvas"` adds `connectWire`/`disconnectWire`, `setValue`/`setInputValue`, and `updatePythonSource`. It is not a privileged path — every operation carries the same write expectations as if a model had authored it, taken from a snapshot captured at restore time (a layout fingerprint contains the pivot, so a past one would be stale by construction). Components created after the restore point are reported, never deleted.
>
> **Where the source text comes from.** A snapshot stores a source *fingerprint*, never its text, so the history commit stores the text separately: `sources/<componentId>.txt` is what a component held after that job, and `sources-baseline/<componentId>.txt` is the text that existed before Vino first touched it — written once and never rewritten, which is what makes the *first* edit to a hand-authored script undoable. A commit writes only the paths it lists and inherits the rest of the parent tree, so a source captured once stays readable at every later revision and git stores an unchanged file as one blob. A script Vino has never written has no stored text; its id is reported in `sourceNotRestored` rather than being silently skipped.
>
> **A restore returns the code, not the bytes.** Every restore goes through an ordinary `python.setSource`, so the adapter stamps the language directive (`#! python 3`, `// #! csharp`) if it is missing and normalises line endings — a script restored from a pre-directive original comes back one line longer than it left. Restored scripts are marked dirty and recompute with the rest of the restore; a restore never runs code on its own. One limit is worth knowing: a **C# component that has never been authored** holds Rhino's default `Script_Instance : GH_ScriptInstance` template, and Vino refuses to write that shape into a script component — so its pre-Vino text cannot be put back, and it is reported in `sourceNotRestored`. Python components have no such problem; their default is ordinary script-mode source.
>
> Source writes ride their **own** ChangeSets, one per component, submitted before the canvas one. A Python component's source/I/O/value writes form a fingerprint chain that `RejectInterleavedPythonFingerprintSequences` refuses to interleave with another component's or with canvas writes. Going first also means the canvas ChangeSet's solve runs the restored code.
| `connectWire`, `disconnectWire` | Canvas / `canvas.setWire` | `{operationId,wire:{sourceObjectId,sourceParameterId,targetObjectId,targetParameterId},action:"connect"|"disconnect",rejectCycles:true}` |
| `createComponent` | Canvas / `canvas.create` | `{operationId,objectId,componentTypeId,pivot:{x,y},nickName,resultOutput}`; `resultOutput` is required-but-nullable — a non-null output name makes the server auto-attach `outputCountInRange ">=1"` on it, null means scaffolding; the model-facing contract mandates `pivot:"gptino:auto"` with optional `autoUpstream:[objectId,...]` — the broker resolves it to a concrete non-overlapping pivot before dispatch |
| `referenceRhinoObjects` | Canvas / `canvas.referenceRhinoObjects` | `{operationId,objectId,rhinoObjectIds:[guid,...],paramType:"curve"\|"brep"\|"mesh"\|"surface"\|"point"\|"geometry",pivot,nickName}`; creates a typed GH parameter that persistently references existing Rhino objects (a live reference, not a baked copy); writeSet is `grasshopperComponent` + `gptino:absent`, like `createComponent` |
| `deleteComponent` | Canvas / `canvas.delete` | `{operationId,objectId,expectedFingerprint}` |
| `setGroup` | Canvas / `canvas.setGroup` | `{operationId,groupId,name,objectIds,argbColor}` |
| `updatePythonSource` | Script / `python.setSource` | `{operationId,componentId,expectedSourceSha256,source,runtime:"csharp"|"cpython3"|"ironPython2",expireSolution}` — the `python.*` operations drive every Rhino 8 script component regardless of language |
| `setComponentIo` | Script / `python.setSchema` | `{operationId,componentId,inputs,outputs,preserveIncidentWires}` — append-only; for removal use `replaceComponentIo` |
| `replaceComponentIo` | Script / `python.replaceSchema` | `{operationId,componentId,newComponentId,inputs,outputs,source?,socketMap?,resultOutput}` — atomic socket removal by replacement: fresh component of the same type, declared schema rebuilt from scratch, source copied (null) or set, original wires re-attached by (mapped) socket name, original deleted, ONE solve. Must be the ChangeSet's only operation; writeSet declares just the replaced component (`grasshopperComponentIo`, concrete or `gptino:auto`). Live-foreign targets take the delete approval path. Protocol v18. |
| `replaceSourceBlock` | Script / `python.replaceBlock` | `{operationId,componentId,expectedSourceSha256,blockId,source,expireSolution}` — ID-addressed block edit on a consolidated (merged) component. A server-side MACRO: never crosses the bridge as itself; at dispatch the server reads the component's current stored source (under the job's exclusive write hold), watchdog-strips it, splices the block via the stage merger (block must exist, meta header intact, declared outputs still assigned, seam inputs not re-declared), and rewrites the operation into an ordinary `python.setSource` carrying the recomposed text with that read's concrete sha as CAS. writeSet declares `grasshopperComponentSource` exactly like `updatePythonSource`. No protocol change. |
| `convertSocket` | Script / `python.setTyping` | `{operationId,componentId,inputParameterId,typeHint,access:"item"|"list"|"tree"}` |
| `executePython` | Script / `python.execute` | `{operationId,componentId,expireUpstream,recomputeDocument}` |
| `readRuntimeMessages` | Script / `python.runtimeMessages` | `{componentId}` |
| `createRhinoPrimitive` | Rhino / `rhino.createPrimitive` | `{operationId,objectId,logicalEntityId,kind:"point"|"line"|"polyline"|"circle"|"box"|"sphere",point?,line?,polyline?,circle?,box?,sphere?,attributes?}`; exactly one definition must match `kind` |
| `transformRhinoObject` | Rhino / `rhino.transform` | `{operationId,objectId,expectedFingerprint,matrix:{m00,m01,m02,m03,m10,m11,m12,m13,m20,m21,m22,m23,m30,m31,m32,m33}}` |
| `createRhinoObject`, `modifyRhinoObject`, `bakeGeometry`, `updateRhinoAttributes` | Rhino / `rhino.upsert` | `{operationId,objectId,logicalEntityId,geometryType,geometryJson,attributesJson,expectedFingerprint}`; `createRhinoObject`/`bakeGeometry` require payload `null` plus writeSet `gptino:absent`, while modification/attribute updates require the same inspected fingerprint in both places |
| `deleteRhinoObject` | Rhino / `rhino.delete` | `{operationId,objectId,expectedFingerprint}` |
| `fixRhinoEndpointPair` | Rhino / `rhino.fixEndpointPair` | `{operationId,anchorObjectId,anchorEnd,moveObjectId,moveEnd,expectedAnchorFingerprint,expectedFingerprint,tolerance}`; heals one audited near-miss pair — the anchor is a declared read, the moved object the single write; ends are 0=start/1=end |
| `ensureRhinoLayer` | Rhino / `rhino.ensureLayer` | `{operationId,layerId,fullPath,parentLayerId?,argbColor?}`; creates a layer by full path (`Parent::Child` nesting) or updates the one already there; an omitted `argbColor` keeps an existing layer's colour (a new layer takes Rhino's default); a new layer declares writeSet kind `rhinoLayer` + `gptino:absent` |
| `purgeTableEntries` | Rhino / `rhino.purgeTableEntries` | `{operationId,entries:[{table:"block"\|"dimStyle"\|"linetype"\|"material",id}]}`; deletes unused document-table entries — "unused" is re-verified live at execution |
| `moveObjectsToLayer` | Rhino / `rhino.moveObjectsToLayer` | `{operationId,items:[{objectId,expectedFingerprint}],targetLayerId}`; attribute-only batch (geometry untouched), also the quarantine vehicle for invalid objects; every item declares its own exact `rhinoObject` expectation |
| `updateRhinoLayerProperties` | Rhino / `rhino.updateLayer` | `{operationId,layerId,expectedFingerprint,argbColor?,visible?,locked?,userText?,renderMaterial?}`; presentation only — rename/re-parent are not available (they rewrite descendant paths); `userText` writes `gptino.`-namespaced semantic labels only (other namespaces refused; empty/whitespace value deletes; labels sit outside the layer fingerprint AND outside Rhino Undo/layer-state snapshots — the revert is writing an empty value); `renderMaterial` accepts only `plaster` and is fill-empty-only (an existing material is kept and the skip returns as a diagnostic); writeSet kind `rhinoLayer` |
| `deleteRhinoLayer` | Rhino / `rhino.deleteLayer` | `{operationId,layerId,expectedFingerprint}`; only an empty leaf layer, with emptiness re-proved at execution; writeSet kind `rhinoLayer` |
| `saveRhinoLayerState` | Rhino / `rhino.layerState` | `{operationId,action:"save"\|"restore"\|"delete",name}`; named layer states — declares one write of kind `rhinoLayerTable` whose id is the document's projectId and whose fingerprint is the whole-table fingerprint from `rhino_layers` |

`Rename`, `SetSolverState`, `DocumentGlobal`, and `UpdateRhinoLayer` are
reserved backend enum values: they are not advertised in the model-facing tool
schema and any ChangeSet that reaches the broker with one of them fails closed
at submit. `UpdateRhinoLayer` (which bundled rename and re-parent, whose
descendant-path rewrites remain out of scope) is superseded by the narrow,
provable layer operations above — `ensureRhinoLayer`,
`updateRhinoLayerProperties`, `deleteRhinoLayer`, and `saveRhinoLayerState`.
Destructive operations on objects without Vino provenance stamps additionally
require a user-minted approval grant (`changeSet.approvalGrantId`, issued via
the panel's audit card); the server injects the per-operation approval flags
and rejects model-authored ones.
`geometryJson` must be RhinoCommon native JSON whose actual object type matches
`geometryType`, and the decoded geometry must pass `IsValidWithLog`.
`attributesJson` is RhinoCommon `ObjectAttributes` JSON and is type-checked; an
empty string requests default/new attributes (or a duplicate of current
attributes on modify). This does not pre-validate every layer, material, group,
or other document-table constraint. `{}` is not a valid substitute for either
RhinoCommon payload.

## Verification

Supported acceptance kinds are `fingerprintEquals`, `runtimeErrorAbsent`,
`wireExists`, `wireAbsent`, `objectExists`, `objectAbsent`,
`outputCountInRange`, and the semantic output checks `geometryClosed`,
`areaInRange`, `dataTreeBranchCountInRange`, `volumeInRange`, and
`boundingBoxInRange` (twelve in total). The remaining enum values
(`outputEquals`, `boundingBoxEquals`, `custom`) are reserved and fail closed. Canvas predicates are evaluated against a
fresh post-write snapshot. Python and Rhino predicates additionally use the
adapter's correlated post-operation fingerprint, including an explicit absence
observation after deletion. Any error diagnostic fails verification.

Verification failure semantics are deterministic: script-content errors
(`updatePythonSource` compile failures, `executePython` runtime errors) do not
abort the operation loop — every operation completes, the post-state snapshot is
captured, and the job ends `failed` with the full `diagnostics[]`, an `applied`
block carrying the actual post-write fingerprints, and the session resource
ledger updated to live state so the corrective resubmission is not
stale-blocked. No history revision is committed for a red state.
`recoveryRequired` is reserved for genuinely unknown outcomes: mid-write
exceptions on non-script operations, cancellation after a write, fingerprint
chain violations, history-commit failures, and restart recovery.
