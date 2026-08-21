Freeform shell authoring — loft/sweep/network choice, degree/CV discipline, tangent-to-ground contact, offset limits, committable G1 checks.

# Freeform surfaces cookbook (C# script components)

Vetted RhinoCommon idioms for authoring freeform shells (vaults, canopies, doubly-curved skins).
Script-mode scaffold and the namespace/signature crib: gh-csharp-cookbook.md. Openings in the
finished shell: gh-shell-openings-cookbook.md. All signatures verified against Rhino 8; verify
anything beyond them via component_catalog/inspect before relying on it.

## Which constructor — choose by the data you actually have

| You have | Use | Call (returns null/empty on failure — check) |
|---|---|---|
| Parallel section profiles | Loft | `Brep.CreateFromLoft(curves, Point3d.Unset, Point3d.Unset, LoftType.Normal, closed)` → `Brep[]` |
| One rail + profile(s) | Sweep-1 | `Brep.CreateFromSweep(rail, shape, closed, tol)` → `Brep[]` |
| Two rails + profile(s) | Sweep-2 | `Brep.CreateFromSweep(rail1, rail2, shape, closed, tol)` → `Brep[]` |
| Curve cage in BOTH directions | Network | `NurbsSurface.CreateNetworkSurface(curves, continuity, edgeTol, interiorTol, angleTol, out int error)` — `error != 0` is a failure, report it |
| Rectangular grid of points | Point grid | `NurbsSurface.CreateFromPoints(points, uCount, vCount, uDegree, vDegree)` |
| 2-4 edge curves only | Edge surface | `Brep.CreateEdgeSurface(curves)` → `Brep` |

Prefer the SIMPLEST constructor that matches the data — a loft you control beats a network
surface you fight. NetworkSrf/Patch are solver-heavy: per house rules author them incrementally
(small curve counts first, commit, then densify). `LoftType.Normal` interpolates the profiles;
`LoftType.Loose` uses them as control points (smoother, does NOT touch the profiles) —
`Straight`/`Developable`/`Uniform` exist for special cases.

## Degree and control-point discipline

- Degree 3 is the default for everything freeform; degree 5 only when you must carry G2 across
  joined patches. Degree 1/2 profiles make faceted or kinked shells.
- FEW control points = fair surface. A profile needs 4-8 CVs, not 40; density buys wiggle, not
  quality, and multiplies every downstream solve. Fit tolerance comes AFTER fairness.
- Normalize inputs before construction: `curve.Rebuild(pointCount, 3, preserveTangents: true)`
  → `NurbsCurve`; a built surface with knot debris rebuilds as
  `srf.Rebuild(3, 3, uPointCount, vPointCount)` → `NurbsSurface`. Lofts across profiles with
  MATCHED CV counts and degrees produce far cleaner isocurves than mixed ones.

## Tangent-to-ground contact (making a shell lie down into a plane)

Tangency at a NURBS curve end is owned by the last two control points: the end tangent is the
line through them. So, with the ground at z = 0:

- **Last two CVs at the same height** → horizontal end tangent → the profile meets the ground
  tangentially (G1).
- **Last three CVs coplanar with the ground** → curvature also flattens (≈G2, a visually
  seamless "lie-down").
- Interpolation route when you have through-points instead of CVs:
  `Curve.CreateInterpolatedCurve(pts, 3, CurveKnotStyle.Chord, startTangent, endTangent)` with a
  unitized HORIZONTAL `endTangent` (e.g. `new Vector3d(1, 0, 0)`).

Loft profiles that each end tangent-to-ground at the SAME z, and the lofted shell is tangent to
the ground along the whole contact edge — then PROVE it with the G1 check below; never claim
tangency by eye.

## S-curve profile (the classic shell section)

```csharp
// Horizontal at the ground (z=0) AND at the crown (z=h): last-two-CVs rule at both ends.
var cvs = new List<Point3d>
{
    new Point3d(0.0,        0.0, 0.0), new Point3d(r1,       0.0, 0.0),   // ground tangency
    new Point3d(run * 0.5,  0.0, h * 0.5),                                 // inflection
    new Point3d(run - r2,   0.0, h),   new Point3d(run,      0.0, h)       // crown tangency
};
Curve profile = Curve.CreateControlPointCurve(cvs, 3);
```

`r1`/`r2` (sliders) set how long the profile hugs ground and crown. Array the profile along the
plan curve (vary `h`, `run` per station with sliders/graph inputs), then loft. Keep every
station's CV count identical so the loft stays clean.

## Offsets without self-intersection

An offset self-intersects wherever the offset distance exceeds the local concave radius of
curvature. Check BEFORE offsetting instead of debugging a broken result:

```csharp
double kappaMax = 0.0;
var uD = srf.Domain(0); var vD = srf.Domain(1);
for (int i = 0; i <= 8; i++)
  for (int j = 0; j <= 8; j++)
  {
    var sc = srf.CurvatureAt(uD.ParameterAt(i / 8.0), vD.ParameterAt(j / 8.0));
    if (sc == null) continue;
    kappaMax = Math.Max(kappaMax, Math.Max(Math.Abs(sc.Kappa(0)), Math.Abs(sc.Kappa(1))));
  }
minRadius = kappaMax > 1e-12 ? 1.0 / kappaMax : double.MaxValue;   // commit this output
// Rule of thumb: |offset| < ~0.7 * minRadius. Then:
Surface off = srf.Offset(dist, tol);                                // null on failure — report
```

For thick solid shells use `Brep.CreateOffsetBrep(brep, dist, true, true, tol, out _, out _)`
(paneling cookbook stage 3) under the same radius rule. A null offset with `|dist|` near
`minRadius` is a DESIGN limit — report it and expose `dist` as a slider; do not grind retries.

## G1 / continuity checks the model can run (committed outputs, not eyeballs)

```csharp
// Ground-contact G1: along the contact edge the surface normal must be parallel to ground normal.
double worst = 0.0;
double[] ts = contactEdge.DivideByCount(20, true);
foreach (double t in ts)
{
    Point3d p = contactEdge.PointAt(t);
    if (!srf.ClosestPoint(p, out double u, out double v)) continue;
    double ang = Vector3d.VectorAngle(srf.NormalAt(u, v), Vector3d.ZAxis);
    worst = Math.Max(worst, Math.Min(ang, Math.PI - ang));   // orientation-agnostic
}
maxDevDeg = Rhino.RhinoMath.ToDegrees(worst);   // commit; assert small (e.g. < 1.0) via predicate
```

- Curve-internal kinks: `curve.GetNextDiscontinuity(Continuity.G1_continuous, t0, t1, out double tKink)`
  returns true when a kink exists — a profile that reports one will loft a creased shell.
- Surface fairness map: sample `srf.CurvatureAt(u, v)` and commit `Gaussian`/`Mean` extremes;
  a sign flip in Gaussian marks the synclastic/anticlastic transition (expected in an S-profile
  shell — report where, don't suppress).
- Wire these checks as their own small stage whose numeric outputs are committed, so acceptance
  predicates (outputCountInRange on the sample set, bounds on the committed deviation) can gate
  the result per house rules.

## Staging (the predicted-solve gate is your friend)

- First pass LOW resolution: few profiles, 8x8 curvature samples, low isocurve/panel counts —
  commit it; that pass calibrates the server's solve prediction and every later densification
  scales from it. Never make the first execution the full-resolution one.
- One heavy constructor (loft/sweep/network) per stage component; checks and offsets are their
  own downstream stages so a slider tweak re-solves only its stage.
- On a timeout or cost rejection: reduce profile/sample counts or split the stage — never
  resubmit the same values.
