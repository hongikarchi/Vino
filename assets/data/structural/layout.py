# Vino structural layout asset (structural_layout host tool).
#
# SHIPPED, DETERMINISTIC CODE — the agent never writes or edits this. Secondary-beam candidates
# are geometry, and "the beam sits where the bay needs it" is a claim that must reproduce.
#
# Contract: argv[1] = input JSON path, stdout = result JSON, stderr = diagnostics, exit 0 on
# success. Pure geometry — no FE, no Rhino; runs on any Python 3.9+.
#
# Input (coordinates in DOCUMENT units × unitScaleToMm = mm):
# {
#   "members": [{"mark","a":[x,y,z],"b":[x,y,z],"role","sourceObjectIds":[...]}, ...],
#   "options": {
#     "unitScaleToMm": 1.0,
#     "spacingMm": 3000,          # target spacing; real spacing = bayLength / ceil(L/spacing)
#     "direction": "auto",        # "auto" = beams span the SHORT way | "x" | "y" (span axis)
#     "levelToleranceMm": 300,    # beams whose z varies less than this share a level
#     "gridMm": 30,               # node merge grid for the bay graph
#     "intersectionSnapMm": 150,  # endpoint-to-interior snap when building the graph
#     "minBeamMm": 600,           # candidate pieces shorter than this are dropped
#     "existingToleranceMm": 250, # a candidate this close and parallel to a drawn member is
#                                 # suppressed — the user's own beam always wins
#     "footprint": {              # optional: loaded plan cells (structural_loads sampling);
#       "cellMm": 250,            # a candidate is TRIMMED to where material stands above it,
#       "samples": [[x,y], ...]   # so a slab opening (void) gets no auto members
#     }
#   }
# }
#
# Output: bays (closed plan faces of the girder graph per level), candidate beams, and honesty
# counters (suppressed by existing members, trimmed by the void, dropped short pieces).
import json
import math
import sys


def scaled(p, s):
    return [float(p[0]) * s, float(p[1]) * s, float(p[2]) * s]


def polygon_area(points):
    total = 0.0
    for i in range(len(points)):
        x1, y1 = points[i]
        x2, y2 = points[(i + 1) % len(points)]
        total += x1 * y2 - x2 * y1
    return total * 0.5


def seg_intersect(a1, a2, b1, b2, eps=1e-9):
    """Proper/touching intersection parameters (ta, tb) of segments a and b, or None."""
    ax, ay = a2[0] - a1[0], a2[1] - a1[1]
    bx, by = b2[0] - b1[0], b2[1] - b1[1]
    denom = ax * by - ay * bx
    if abs(denom) < eps:
        return None
    dx, dy = b1[0] - a1[0], b1[1] - a1[1]
    ta = (dx * by - dy * bx) / denom
    tb = (dx * ay - dy * ax) / denom
    if -1e-7 <= ta <= 1 + 1e-7 and -1e-7 <= tb <= 1 + 1e-7:
        return (min(max(ta, 0.0), 1.0), min(max(tb, 0.0), 1.0))
    return None


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    with open(sys.argv[1], encoding="utf-8") as handle:
        payload = json.load(handle)
    members = payload.get("members", [])
    options = payload.get("options", {})
    scale = float(options.get("unitScaleToMm", 1.0) or 1.0)
    spacing = float(options.get("spacingMm", 3000.0))
    direction = str(options.get("direction", "auto") or "auto").lower()
    level_tol = float(options.get("levelToleranceMm", 300.0))
    grid = float(options.get("gridMm", 30.0))
    snap = float(options.get("intersectionSnapMm", 150.0))
    min_beam = float(options.get("minBeamMm", 600.0))
    existing_tol = float(options.get("existingToleranceMm", 250.0))
    footprint = options.get("footprint") or None
    warnings = []

    # ---- horizontal members grouped into levels -----------------------------------------
    beams = []
    for member in members:
        a = scaled(member["a"], scale)
        b = scaled(member["b"], scale)
        if member.get("role") != "beam":
            continue
        if abs(a[2] - b[2]) > level_tol:
            continue
        beams.append((a, b, member))
    if not beams:
        print(json.dumps({"error": "no horizontal (beam-role) members to form bays from"}))
        return 2
    levels = []  # [mean_z, [beam,...]]
    for a, b, member in sorted(beams, key=lambda item: (item[0][2] + item[1][2]) * 0.5):
        z = (a[2] + b[2]) * 0.5
        if levels and abs(levels[-1][0] - z) <= level_tol:
            group = levels[-1][1]
            group.append((a, b, member))
            levels[-1][0] = sum((p[0][2] + p[1][2]) * 0.5 for p in group) / len(group)
        else:
            levels.append([z, [(a, b, member)]])

    all_bays = []
    all_candidates = []
    suppressed_existing = 0
    removed_by_void_mm = 0.0
    skipped_short = 0

    for level_z, group in levels:
        # ---- 2D graph: merge nodes on the grid, split edges at crossings and T-landings --
        segments = []
        for a, b, member in group:
            segments.append([(a[0], a[1]), (b[0], b[1])])
        # endpoint -> interior snap (a secondary drawn to a girder face, not its axis)
        for si, seg in enumerate(segments):
            for end in range(2):
                p = seg[end]
                for sj, other in enumerate(segments):
                    if si == sj:
                        continue
                    ox, oy = other[1][0] - other[0][0], other[1][1] - other[0][1]
                    l2 = ox * ox + oy * oy
                    if l2 <= 0:
                        continue
                    t = ((p[0] - other[0][0]) * ox + (p[1] - other[0][1]) * oy) / l2
                    if t < 0.02 or t > 0.98:
                        continue
                    qx, qy = other[0][0] + t * ox, other[0][1] + t * oy
                    if math.dist(p, (qx, qy)) <= snap:
                        seg[end] = (qx, qy)
                        break
        # split every segment at its intersections with the others
        pieces = []
        for si, seg in enumerate(segments):
            cuts = [0.0, 1.0]
            for sj, other in enumerate(segments):
                if si == sj:
                    continue
                hit = seg_intersect(seg[0], seg[1], other[0], other[1])
                if hit is not None:
                    cuts.append(hit[0])
            cuts = sorted(set(round(c, 9) for c in cuts))
            for k in range(len(cuts) - 1):
                t0, t1 = cuts[k], cuts[k + 1]
                p0 = (seg[0][0] + t0 * (seg[1][0] - seg[0][0]), seg[0][1] + t0 * (seg[1][1] - seg[0][1]))
                p1 = (seg[0][0] + t1 * (seg[1][0] - seg[0][0]), seg[0][1] + t1 * (seg[1][1] - seg[0][1]))
                if math.dist(p0, p1) >= grid:
                    pieces.append((p0, p1))
        # node merge
        nodes = {}
        xy = []

        def node_of(p):
            key = (round(p[0] / grid), round(p[1] / grid))
            if key not in nodes:
                nodes[key] = len(xy)
                xy.append((p[0], p[1]))
            return nodes[key]

        edges = set()
        for p0, p1 in pieces:
            i, j = node_of(p0), node_of(p1)
            if i != j:
                edges.add((min(i, j), max(i, j)))
        # ---- face tracing (half-edge walk, most-clockwise turn) --------------------------
        outgoing = {}
        for i, j in edges:
            outgoing.setdefault(i, []).append(j)
            outgoing.setdefault(j, []).append(i)
        for i in outgoing:
            outgoing[i].sort(key=lambda j: math.atan2(xy[j][1] - xy[i][1], xy[j][0] - xy[i][0]))
        visited = set()
        faces = []
        for i, j in list(edges) + [(j, i) for i, j in edges]:
            if (i, j) in visited:
                continue
            face = []
            cur, nxt = i, j
            while (cur, nxt) not in visited:
                visited.add((cur, nxt))
                face.append(cur)
                # arrive at nxt from cur: take the MOST CLOCKWISE neighbor from the reversed
                # direction (largest CCW delta). This walks every bounded face CCW (positive
                # area) and the outer boundary CW — the smallest-delta rule walked straight
                # past interior chords and returned one big outer face.
                back = math.atan2(xy[cur][1] - xy[nxt][1], xy[cur][0] - xy[nxt][0])
                neighbors = outgoing[nxt]
                best = None
                best_delta = None
                for k in neighbors:
                    if k == cur and len(neighbors) > 1:
                        continue
                    angle = math.atan2(xy[k][1] - xy[nxt][1], xy[k][0] - xy[nxt][0])
                    delta = (angle - back) % (2 * math.pi)
                    if delta < 1e-9:
                        delta = 2 * math.pi
                    if best_delta is None or delta > best_delta:
                        best_delta = delta
                        best = k
                if best is None:
                    break
                cur, nxt = nxt, best
                if len(face) > 4 * len(edges):
                    break
            if len(face) >= 3:
                points = [xy[n] for n in face]
                area = polygon_area(points)
                if area > grid * grid:  # CCW bounded faces only; the outer face traces CW
                    faces.append(points)

        # ---- candidates per bay ----------------------------------------------------------
        for points in faces:
            area_m2 = abs(polygon_area(points)) / 1e6
            bay_index = len(all_bays)
            all_bays.append({
                "level": round(level_z, 1),
                "polygon": [[round(x, 1), round(y, 1)] for x, y in points],
                "areaM2": round(area_m2, 2),
            })
            # long axis: the direction to DISTRIBUTE along; beams span the perpendicular
            if direction in ("x", "y"):
                span_axis = (1.0, 0.0) if direction == "x" else (0.0, 1.0)
                u = (-span_axis[1], span_axis[0])
            else:
                # dominant edge direction with the LONGER projected extent distributes
                best_u = (1.0, 0.0)
                best_span = -1.0
                for k in range(len(points)):
                    px, py = points[k]
                    qx, qy = points[(k + 1) % len(points)]
                    ex, ey = qx - px, qy - py
                    norm = math.hypot(ex, ey)
                    if norm < grid:
                        continue
                    cand = (ex / norm, ey / norm)
                    extent = _extent(points, cand)
                    if extent > best_span:
                        best_span = extent
                        best_u = cand
                other = (-best_u[1], best_u[0])
                if _extent(points, other) > _extent(points, best_u):
                    best_u = other
                u = best_u
            v = (-u[1], u[0])
            lu = _extent(points, u)
            count = max(int(math.ceil(lu / spacing)), 1)
            if count < 2:
                continue  # bay narrower than one spacing: no interior beam needed
            u_min = min(px * u[0] + py * u[1] for px, py in points)
            for i in range(1, count):
                station = u_min + lu * i / count
                intervals = _clip_line(points, u, v, station)
                for (p0, p1) in intervals:
                    kept = [(p0, p1)]
                    if footprint:
                        kept, removed = _trim_to_footprint(p0, p1, footprint)
                        removed_by_void_mm += removed
                    for q0, q1 in kept:
                        length = math.dist(q0, q1)
                        if length < min_beam:
                            skipped_short += 1
                            continue
                        if _near_existing(q0, q1, group, existing_tol):
                            suppressed_existing += 1
                            continue
                        all_candidates.append({
                            "a": [round(q0[0], 1), round(q0[1], 1), round(level_z, 1)],
                            "b": [round(q1[0], 1), round(q1[1], 1), round(level_z, 1)],
                            "bay": bay_index,
                            "lengthMm": round(length, 1),
                        })

    report = {
        "levels": len(levels),
        "bayCount": len(all_bays),
        "bays": all_bays,
        "beamCount": len(all_candidates),
        "beams": all_candidates,
        "totalLengthM": round(sum(c["lengthMm"] for c in all_candidates) / 1000.0, 2),
        "spacingMm": spacing,
        "suppressedExisting": suppressed_existing,
        "removedByVoidM": round(removed_by_void_mm / 1000.0, 2),
        "skippedShort": skipped_short,
        "warnings": warnings,
    }
    print(json.dumps(report, ensure_ascii=False))
    return 0


def _extent(points, axis):
    values = [px * axis[0] + py * axis[1] for px, py in points]
    return max(values) - min(values)


def _clip_line(points, u, v, station):
    """Intersect the infinite line {u·p = station, direction v} with the polygon; return the
    inside intervals as point pairs (even-odd along v)."""
    crossings = []
    for k in range(len(points)):
        p = points[k]
        q = points[(k + 1) % len(points)]
        du_p = p[0] * u[0] + p[1] * u[1] - station
        du_q = q[0] * u[0] + q[1] * u[1] - station
        if (du_p > 0) == (du_q > 0):
            continue
        if abs(du_q - du_p) < 1e-12:
            continue
        t = du_p / (du_p - du_q)
        x = p[0] + t * (q[0] - p[0])
        y = p[1] + t * (q[1] - p[1])
        crossings.append((x * v[0] + y * v[1], (x, y)))
    crossings.sort(key=lambda item: item[0])
    intervals = []
    for k in range(0, len(crossings) - 1, 2):
        intervals.append((crossings[k][1], crossings[k + 1][1]))
    return intervals


def _trim_to_footprint(p0, p1, footprint):
    """Drop the WHOLE candidate when the void meaningfully cuts it; return
    (kept_segments, removed_length_mm). A partial stub ending mid-air at an opening edge is an
    unsupported cantilever nobody asked for (the first live gate left a 600 mm stub poking into
    the hole) — framing an opening is the human's design decision, so the tool proposes nothing
    there. Sampling step = half a cell."""
    cell = float(footprint.get("cellMm", 250.0))
    samples = footprint.get("samples") or []
    if not samples:
        return [(p0, p1)], 0.0
    radius = cell * 0.9
    length = math.dist(p0, p1)
    steps = max(int(length / (cell * 0.5)), 1)
    supported = []
    for i in range(steps + 1):
        t = i / steps
        x = p0[0] + t * (p1[0] - p0[0])
        y = p0[1] + t * (p1[1] - p0[1])
        ok = any(abs(x - sx) <= radius and abs(y - sy) <= radius for sx, sy in samples)
        supported.append(ok)
    kept = []
    removed = 0.0
    run_start = None
    for i, ok in enumerate(supported):
        if ok and run_start is None:
            run_start = i
        if (not ok or i == steps) and run_start is not None:
            end = i if not ok else i
            t0 = run_start / steps
            t1 = end / steps
            q0 = (p0[0] + t0 * (p1[0] - p0[0]), p0[1] + t0 * (p1[1] - p0[1]))
            q1 = (p0[0] + t1 * (p1[0] - p0[0]), p0[1] + t1 * (p1[1] - p0[1]))
            kept.append((q0, q1))
            run_start = None
    kept_length = sum(math.dist(q0, q1) for q0, q1 in kept)
    removed = max(length - kept_length, 0.0)
    if removed > cell:
        return [], length
    return kept, removed


def _near_existing(p0, p1, group, tolerance):
    mx, my = (p0[0] + p1[0]) * 0.5, (p0[1] + p1[1]) * 0.5
    dx, dy = p1[0] - p0[0], p1[1] - p0[1]
    norm = math.hypot(dx, dy)
    if norm <= 0:
        return True
    for a, b, _member in group:
        ex, ey = b[0] - a[0], b[1] - a[1]
        enorm = math.hypot(ex, ey)
        if enorm <= 0:
            continue
        cross = abs(dx * ey - dy * ex) / (norm * enorm)
        if cross > 0.06:  # ~3.5 degrees
            continue
        l2 = enorm * enorm
        t = ((mx - a[0]) * ex + (my - a[1]) * ey) / l2
        if t < -0.02 or t > 1.02:
            continue
        qx, qy = a[0] + t * ex, a[1] + t * ey
        if math.dist((mx, my), (qx, qy)) <= tolerance:
            return True
    return False


if __name__ == "__main__":
    sys.exit(main())
