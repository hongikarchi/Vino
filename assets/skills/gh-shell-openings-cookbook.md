Through-openings in curved shells/breps — project/pull the boundary, split + face removal over booleans, planar-hole recipe, loud failures.

# Shell-openings cookbook (C# script components)

Vetted RhinoCommon recipes for cutting REAL through-voids into curved shells and breps — the
cases the paneling cookbook's planar extrusion boolean cannot handle. Script-mode scaffold and
the namespace/signature crib: gh-csharp-cookbook.md. Staging and the 45s solve budget: house
rules (openings are their own stage; low counts first). Every signature here is verified against
Rhino 8; anything else you reach for, verify via component_catalog/inspect first.

## Rule zero — null-check every Brep op and fail LOUD

Every call below returns null or an empty array on failure. The openings mandate means a failed
cut is REPORTED and the piece left OUT — never ship a covered opening as if it were fine.

```csharp
var report = new List<string>();
// ... after every op:
if (pieces == null || pieces.Length == 0) { report.Add($"split failed on panel {k}"); continue; }
// last line of the stage — always assign, even when empty:
status = report.Count == 0 ? "ok" : string.Join("; ", report);
```

## Getting the opening curve onto the shell

- **Directional projection** — `Curve.ProjectToBrep(curve, brep, direction, tol)` → `Curve[]`.
  A closed shell gets hit on BOTH sides: cull fragments by distance to the intended region, then
  `Curve.JoinCurves(fragments, tol)` and require ONE closed loop before cutting.
- **Closest-point pull** — `curve.PullToBrepFace(face, tol)` → `Curve[]`. No direction; better
  on steep curvature where a projection smears or misses. Same join-and-verify-closed rule.
- A boundary that fails to close on the shell is a failed opening — report it; do not cut with
  an open loop (the split will leak or no-op).

## Thin shell (single-skin brep): split + face removal, not solid boolean

Solid booleans need closed operands; a one-sided shell is cut by SPLITTING it with the on-shell
boundary and discarding the pieces inside the opening.

```csharp
// boundary = the closed on-shell loop from project/pull; shell = the (open) shell Brep.
Brep[] pieces = shell.Split(new[] { boundary }, tol);            // Brep.Split(IEnumerable<Curve>, tol)
if (pieces == null || pieces.Length < 2) { report.Add("split produced no cut"); }
else
{
  boundary.TryGetPlane(out Plane opPl, tol * 100);               // best-fit plane for classification
  var kept = new List<Brep>();
  foreach (var piece in pieces)
  {
    var amp = AreaMassProperties.Compute(piece);
    if (amp == null) { report.Add("piece area failed"); continue; }
    Point3d probe = opPl.ClosestPoint(amp.Centroid);
    bool inHole = boundary.Contains(probe, opPl, tol) == PointContainment.Inside;
    if (!inHole) kept.Add(piece);                                // drop the hole cap(s)
  }
  Brep[] joined = Brep.JoinBreps(kept, tol);
  result = (joined != null && joined.Length == 1) ? joined[0] : null;
  if (result == null) report.Add("join did not return one shell");
}
```

The centroid/plane classification is exact for near-planar openings; on a strongly curved
boundary region verify the kept/dropped decision from committed.outputs (piece count and areas)
before trusting it — and prefer the smaller-count check: a clean single-loop split yields exactly
one hole piece.

## Thick shell (closed solid): boolean first, split-face route as the fallback

1. **First choice**: `Brep.CreateBooleanDifference(new[]{ solid }, new[]{ cutter }, tol)` with a
   cutter obeying the sizing rules below. Null/empty → do NOT resubmit as-is.
2. **Fallback (split-face route)** when the boolean fails or the opening boundary is non-planar:
   - Project/pull the boundary onto the OUTER skin and the INNER skin separately (two loops).
   - `solid.Split(new[]{ outerLoop, innerLoop }, tol)` and discard the two hole caps (classify as
     above, one per skin).
   - Build the hole WALL by lofting the two loops:
     `Brep[] wall = Brep.CreateFromLoft(new[]{ outerLoop, innerLoop }, Point3d.Unset, Point3d.Unset, LoftType.Straight, closed: false);`
   - `Brep.JoinBreps(keptPieces.Concat(wall), tol)` and require ONE brep with `IsSolid` true.
   Each step null-checks and reports; a half-cut shell is never the committed product.

## Planar plate with many holes — one face with REAL inner loops

The classic failure: feeding hole curves to `Brep.CreatePlanarBreps` one at a time caps every
hole as a disk. The vetted sequence (all `Rhino.Geometry`, tolerances from the document):

```csharp
// 0) Work on duplicates; verify every loop closed + planar + coplanar at doc tolerance,
//    then flatten exactly onto the working plane:
Curve flat = loop.DuplicateCurve();
flat.Transform(Transform.PlanarProjection(plane));

// 1) Optional diagnostics of the arrangement (region/overlap count before committing):
var regions = Curve.CreateBooleanRegions(allLoops, plane, combineRegions: false, tolerance: tol);

// 2) Union overlapping cutters FIRST so overlaps never double-cut:
Curve[] cutters = Curve.CreateBooleanUnion(holeLoops, tol);

// 3) Subtract all cutters from the outer loop in ONE call (multi-subtractor overload):
Curve[] diff = Curve.CreateBooleanDifference(outerLoop, cutters, tol);

// 4) ALL difference loops into ONE CreatePlanarBreps call — outer + holes TOGETHER.
//    Per-curve calls would cap the holes; the single call classifies containment into inner loops.
Brep[] planar = Brep.CreatePlanarBreps(diff, tol);
if (planar == null || planar.Length != 1)
  report.Add($"expected 1 holed face, got {(planar?.Length ?? 0)} (islands = material split apart)");
```

`planar.Length != 1` means the cutters severed the plate into islands — that is a design-level
finding to report, not something to silently join. Thickness afterwards:
`Brep.CreateOffsetBrep(planar[0], thk, true, true, tol, out _, out _)` (solid:true → closed shell
with the holes carried through); when the offset fails, the robust route is mesh-prism — mesh the
holed face (`Mesh.CreateFromBrep(brep, meshingParameters)`), duplicate + reverse the cap, wall the
naked topology edges, then `Brep.CreateFromMesh(mesh, trimmedTriangles: true)` — and it must pass
the full audit below before you call it a solid.

## Cutter sizing / direction / overlap rules

- **Direction**: extrude the cutter along the LOCAL surface normal at the opening centroid
  (`srf.ClosestPoint(c, out u, out v)` then `srf.NormalAt(u, v)`), never world Z on a curved shell.
- **Full pierce**: back-offset the cutter seat by more than the wall thickness and extrude at
  least `2*thk` past the far skin (the paneling cookbook's `-thk*2` seat / `thk*4` length rule).
  A cutter that ends inside the wall makes a pocket, not a through-void.
- **No grazing**: a cutter wall tangent to the shell or riding a panel edge makes the boolean
  flaky — keep the boundary clear of edges (the paneling stage-2 clamp ≤0.95 exists for this).
- **Overlaps**: union overlapping cutters BEFORE subtracting (curves: `Curve.CreateBooleanUnion`;
  solids: `Brep.CreateBooleanUnion(cutters, tol)`) — one clean subtraction instead of stacked ones.
- **Budget**: booleans and splits are the slow, fragile step — cut in a dedicated stage, modest
  counts on the first committed pass, then raise (predicted-solve gate calibrates from it).

## Verification checklist (committed outputs, not eyeballs)

- Closed products: `IsSolid` true and `DuplicateNakedEdgeCurves(true, true)` empty.
- Mesh-prism route: `IsClosed`, `DisjointMeshCount == 1`,
  `IsManifold(true, out oriented, out hasBoundary)` with `oriented && !hasBoundary`, and
  `GetNakedEdges()` empty — all four, before `Brep.CreateFromMesh`.
- The opening must be a REAL void: declare a generous acceptance predicate per house rules
  (e.g. outputCountInRange on the cut result, or geometryClosed on the final solid) so an
  uncut shell fails instead of committing green.
- Read `status`/`report` in committed.outputs: a committed stage that lists failures is
  unfinished work — say so and retry the failed pieces, never average over them.
