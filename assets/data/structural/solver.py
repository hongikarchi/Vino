# Vino structural solver asset (structural_solve host tool).
#
# SHIPPED, DETERMINISTIC CODE — the agent never writes or edits this. It is the promotion of the
# live-validated scripts/pynite-real-model.py (1,199-member production model: 0.85 s solve, exact
# equilibrium, 0.9 % cross-validation against an independent commercial FE engine during
# development), parameterized for tool use. Reproducibility
# is the point: "the frame was solved correctly" is a safety claim, and a safety claim regenerated
# by a language model on every call is not a claim at all.
#
# Contract: argv[1] = input JSON path, stdout = result JSON, stderr = diagnostics, exit 0 on
# success. Runs on Python 3.9+ (Rhino's py39-rh8 environment) with PyNiteFEA installed.
#
# Input (coordinates in DOCUMENT units × unitScaleToMm = mm; forces kN unless noted):
# {
#   "members":  [{"mark","a":[x,y,z],"b":[x,y,z],"kind","sourceObjectIds":[...]}, ...],
#   "sections": {"H-300x300x10x15": {"H","B","tw","tf","A","Ix","Iy"}, ...}   (KS catalog rows),
#   "markSections": {"SC1": "H-300x300x10x15", ...},
#   "defaultSection": "H-300x300x10x15",
#   "options": {
#     "unitScaleToMm": 1.0,               # 1000 when the document is in meters
#     "gridMm": 30, "snapMm": 350, "repairSnapMm": 1500,
#     "repairFreeEnds": false,            # wide-radius repair ONLY after the user said so
#     "cantileverPoints": [[x,y,z],...],  # user-confirmed intended free ends: never repaired
#     "columnMarkPrefixes": ["SC"],
#     "roleSections": {"column": "H-300x300x10x15", "beam": "H-400x200x8x13", "brace": "..."},
#                                         # section by GEOMETRIC role when the mark has none
#                                         # (curves drawn on 'Default' carry no section mark)
#     "supportType": "fixed" | "pinned",  # fixed = all 6 DOF; pinned = translations only
#     "supportPoints": [[x,y,z],...],     # user-named support nodes (snapped within snapMm)
#     "autoSupports": true,               # geometric support detection (base band + column feet)
#     "lineLoads": [{"role": "beam" | "mark": "SG1", "kNPerM": 5.0, "case": "G"|"Q"}],
#     "pointLoadsKn": [{"point": [x,y,z], "fx": 0, "fy": 0, "fz": -50, "case": "Q"}],
#     "extraDistributedKnPerM": {"SG1": 5.0},   # legacy per-mark dead line load (case G)
#     "loadFactors": {"G": 1.35, "Q": 1.5},     # ULS partial factors (EC0 base case; KDS 1.2/1.6)
#     "fyMPa": 275,                       # steel yield for the elastic utilization screen
#     "deflectionLimitRatio": 250,        # SLS member check: max local deflection <= L / ratio
#     "maxUtilization": 1.0,              # ULS elastic stress screen: sigma / fy <= this
#     "slendernessLimit": 200             # compression members: L / r_min <= this
#   }
# }
#
# Load discipline (structural-analysis.md): every load is tagged G (permanent) or Q (variable).
# Self-weight is G, automatically. Combination "SLS" = 1.0G + 1.0Q feeds the deflection check;
# "ULS" = gammaG·G + gammaQ·Q feeds the elastic utilization screen. The utilization here is an
# ELASTIC STRESS SCREEN (N/A + My/Sy + Mz/Sz against fy) — an early-design signal, not a code
# member design (no lateral-torsional buckling, no flexural buckling, no shear, no connections).
import collections
import json
import math
import sys
import time

from Pynite import FEModel3D

E = 2.1e8        # kN/m2 (S235/S355 steel)
G = 8.076e7
RHO_KNM3 = 78.5  # steel unit weight kN/m3
# Pinned supports leave the rotations free; a straight member between two pins has a free torsion
# DOF and the stiffness matrix goes singular. A rotational spring six orders of magnitude below
# EI/L of any catalog member restrains that mechanism with no measurable effect (verified against
# the simply-supported UDL closed form to 0.01 %).
PIN_ROTATION_SPRING_KNM_PER_RAD = 1.0e-3
COLUMN_VERTICAL_RATIO = 0.85   # |dz| / L at or above this is a column
BEAM_VERTICAL_RATIO = 0.10     # |dz| / L at or below this is a beam; between = brace


def torsion_j_cm4(H, B, tw, tf):
    # thin-walled open-section estimate: J = sum(b*t^3)/3 (cm^4); inputs mm
    return ((2.0 * B * tf ** 3) + ((H - 2.0 * tf) * tw ** 3)) / 3.0 / 1e4


def role_of(a, b):
    """Geometric member role from the axis direction. Same thresholds as the extraction side."""
    length = math.dist(a, b)
    if length <= 0.0:
        return "beam"
    vertical = abs(b[2] - a[2]) / length
    if vertical >= COLUMN_VERTICAL_RATIO:
        return "column"
    if vertical <= BEAM_VERTICAL_RATIO:
        return "beam"
    return "brace"


def scaled_point(raw, scale):
    return [float(raw[0]) * scale, float(raw[1]) * scale, float(raw[2]) * scale]


def main():
    # The report is UTF-8 regardless of the console code page: marks are layer names, and a
    # Korean layer name ("기둥") must not crash the solver on a cp949 host — the C# runner reads
    # stdout as UTF-8 to match.
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    with open(sys.argv[1], encoding="utf-8") as handle:
        payload = json.load(handle)
    members = payload["members"]
    sections = payload["sections"]
    mark_sections = payload.get("markSections", {})
    default_section = payload.get("defaultSection")
    options = payload.get("options", {})

    scale = float(options.get("unitScaleToMm", 1.0) or 1.0)
    grid = float(options.get("gridMm", 30.0))
    snap = float(options.get("snapMm", 350.0))
    repair_snap = float(options.get("repairSnapMm", 1500.0))
    repair_free_ends = bool(options.get("repairFreeEnds", False))
    cantilevers = [tuple(scaled_point(point, scale)) for point in options.get("cantileverPoints", [])]
    column_prefixes = tuple(options.get("columnMarkPrefixes", ["SC"]))
    role_sections = options.get("roleSections") or {}
    support_type = str(options.get("supportType", "fixed") or "fixed").lower()
    if support_type not in ("fixed", "pinned"):
        print(json.dumps({"error": "supportType must be 'fixed' or 'pinned', got %r" % support_type}))
        return 2
    support_points = [scaled_point(point, scale) for point in options.get("supportPoints", [])]
    auto_supports = bool(options.get("autoSupports", True))
    line_loads = list(options.get("lineLoads") or [])
    for mark, value in (options.get("extraDistributedKnPerM") or {}).items():
        line_loads.append({"mark": mark, "kNPerM": float(value), "case": "G"})
    point_loads = list(options.get("pointLoadsKn") or [])
    factors = options.get("loadFactors") or {}
    gamma_g = float(factors.get("G", 1.35))
    gamma_q = float(factors.get("Q", 1.5))
    fy = float(options.get("fyMPa", 275.0)) * 1000.0   # MPa -> kN/m2
    limit_ratio = float(options.get("deflectionLimitRatio", 250.0))
    max_util = float(options.get("maxUtilization", 1.0))
    slender_limit = float(options.get("slendernessLimit", 200.0))

    for member in members:
        member["a"] = scaled_point(member["a"], scale)
        member["b"] = scaled_point(member["b"], scale)
        member["role"] = role_of(member["a"], member["b"])

    def is_confirmed_cantilever(p):
        return any(math.dist(p, c) <= snap for c in cantilevers)

    # ---- node merge pass 1: exact grid --------------------------------------------------
    nodes = {}
    node_xyz = []

    def node_of(p):
        k = (round(p[0] / grid), round(p[1] / grid), round(p[2] / grid))
        if k not in nodes:
            nodes[k] = len(node_xyz)
            node_xyz.append(list(p))
        return nodes[k]

    edges = []
    for index, member in enumerate(members):
        a, b = node_of(member["a"]), node_of(member["b"])
        if a == b:
            continue
        edges.append({"a": a, "b": b, "member": index, "mark": member["mark"], "role": member["role"]})

    def build_deg():
        deg = collections.Counter()
        for e in edges:
            deg[e["a"]] += 1
            deg[e["b"]] += 1
        return deg

    # ---- pass 2a: endpoint->endpoint snap (beam drawn to a column FACE, not its axis) ---
    snapped = 0
    for _round in range(4):
        deg = build_deg()
        remap = {}
        for ni, xyz in enumerate(node_xyz):
            if deg[ni] == 0 or ni in remap:
                continue
            best, bd = None, snap
            for nj, other in enumerate(node_xyz):
                if nj == ni or nj in remap or deg[nj] == 0:
                    continue
                d = math.dist(xyz, other)
                if 1.0 < d < bd:
                    best, bd = nj, d
            if best is not None and (deg[best], -best) >= (deg[ni], -ni):
                remap[ni] = best
                snapped += 1
        if not remap:
            break
        for e in edges:
            e["a"] = remap.get(e["a"], e["a"])
            e["b"] = remap.get(e["b"], e["b"])
        edges = [e for e in edges if e["a"] != e["b"]]

    def seg_project(p, a, b):
        vx, vy, vz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
        l2 = vx * vx + vy * vy + vz * vz
        if l2 <= 0:
            return None, None
        t = ((p[0] - a[0]) * vx + (p[1] - a[1]) * vy + (p[2] - a[2]) * vz) / l2
        if t < 0.02 or t > 0.98:
            return None, None
        q = (a[0] + t * vx, a[1] + t * vy, a[2] + t * vz)
        return math.dist(p, q), q

    # ---- pass 2b: T-junctions (secondary landing mid-span on a girder) ------------------
    tsplit = 0
    for _round in range(4):
        deg = build_deg()
        ends = [n for n in range(len(node_xyz)) if deg[n] == 1]
        did = False
        for ni in ends:
            p = node_xyz[ni]
            best = None
            for ei, e in enumerate(edges):
                if ni in (e["a"], e["b"]):
                    continue
                d, q = seg_project(p, node_xyz[e["a"]], node_xyz[e["b"]])
                if d is not None and d < snap and (best is None or d < best[0]):
                    best = (d, ei, q)
            if best is None:
                continue
            _, ei, q = best
            host = edges[ei]
            node_xyz[ni] = list(q)
            edges.append({"a": ni, "b": host["b"], "member": host["member"], "mark": host["mark"],
                          "role": host["role"]})
            host["b"] = ni
            tsplit += 1
            did = True
        if not did:
            break

    # ---- pass 2c: wide-radius repair, ONLY with the user's explicit yes -----------------
    # Repair mutates the ANALYSIS GRAPH (never the Rhino document), but pulling a member's end
    # 1.5 m is still a judgment call: confirmed-cantilever points are exempt, and without
    # repairFreeEnds the surviving free ends are REPORTED for the ask-back instead of fixed.
    repaired = 0
    if repair_free_ends:
        deg = build_deg()
        for ni in [n for n in range(len(node_xyz)) if deg[n] == 1]:
            p = node_xyz[ni]
            if is_confirmed_cantilever(p):
                continue
            best_n, bd = None, repair_snap
            for nj in range(len(node_xyz)):
                if nj == ni or deg[nj] == 0:
                    continue
                d = math.dist(p, node_xyz[nj])
                if 1.0 < d < bd:
                    best_n, bd = nj, d
            if best_n is not None and deg[best_n] > 1:
                for e in edges:
                    if e["a"] == ni:
                        e["a"] = best_n
                    if e["b"] == ni:
                        e["b"] = best_n
                repaired += 1
                continue
            best_e = None
            for ei, e in enumerate(edges):
                if ni in (e["a"], e["b"]):
                    continue
                dq, q = seg_project(p, node_xyz[e["a"]], node_xyz[e["b"]])
                if dq is not None and dq < repair_snap and (best_e is None or dq < best_e[0]):
                    best_e = (dq, ei, q)
            if best_e is not None:
                _, ei, q = best_e
                host = edges[ei]
                node_xyz[ni] = list(q)
                edges.append({"a": ni, "b": host["b"], "member": host["member"], "mark": host["mark"],
                              "role": host["role"]})
                host["b"] = ni
                repaired += 1

    for e in edges:
        e["len"] = math.dist(node_xyz[e["a"]], node_xyz[e["b"]])
    edges = [e for e in edges if e["a"] != e["b"] and e["len"] > 50.0]
    if not edges:
        print(json.dumps({"error": "no edges after merge"}))
        return 2

    # ---- supports: base band, column feet, and the user's named points -------------------
    all_used = sorted({e["a"] for e in edges} | {e["b"] for e in edges})
    zmin = min(node_xyz[n][2] for n in all_used)
    touch = collections.defaultdict(list)
    for e in edges:
        touch[e["a"]].append(e)
        touch[e["b"]].append(e)
    deg = build_deg()
    auto_support_nodes = set()
    if auto_supports:
        auto_support_nodes = set(n for n in all_used if node_xyz[n][2] < zmin + 200.0)
        for e in edges:
            az, bz = node_xyz[e["a"]][2], node_xyz[e["b"]][2]
            if e["len"] <= 0 or abs(az - bz) / e["len"] < COLUMN_VERTICAL_RATIO:
                continue
            lo = e["a"] if az < bz else e["b"]
            lo_z = node_xyz[lo][2]
            hangs_below = any(
                min(node_xyz[o["a"]][2], node_xyz[o["b"]][2]) < lo_z - 100.0
                for o in touch[lo] if o is not e)
            if hangs_below:
                continue
            marked_column = any(e["mark"].startswith(prefix) for prefix in column_prefixes)
            # A section-marked column stands on its foot wherever that is (podium bearings in the
            # production model). An UNMARKED vertical member — a curve on 'Default' — is a column
            # foot only when nothing else meets that node: a post landing on a beam has degree > 1
            # there and must NOT be mistaken for a support.
            if marked_column or deg[lo] == 1:
                auto_support_nodes.add(lo)
    explicit_support_nodes = set()
    unmatched_support_points = []
    for point in support_points:
        best, bd = None, snap
        for n in all_used:
            d = math.dist(point, node_xyz[n])
            if d < bd:
                best, bd = n, d
        if best is None:
            unmatched_support_points.append([round(v, 1) for v in point])
        else:
            explicit_support_nodes.add(best)
    supports = sorted(auto_support_nodes | explicit_support_nodes)
    if not supports:
        print(json.dumps({
            "error": "no supports: nothing sits in the base band, no column foot was found, and no "
                     "supportPoints matched a node - name the support points (answers.supportPoints)"}))
        return 2

    # ---- connectivity: solve every component that has a support; the rest are islands ----
    parent = list(range(len(node_xyz)))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for e in edges:
        parent[find(e["a"])] = find(e["b"])
    supported_roots = {find(n) for n in supports}
    main_edges = [e for e in edges if find(e["a"]) in supported_roots]
    island_edges = len(edges) - len(main_edges)
    components_solved = len({find(e["a"]) for e in main_edges})

    # Islands are DROPPED from the solve but must never vanish from the report: a member that
    # connects to nothing supported is exactly the ask-back condition (mis-drawn? belongs to
    # another structure? intended?), and hiding it inside a bare count would bury the question.
    island_members = []
    seen_island = set()
    for e in edges:
        if find(e["a"]) in supported_roots:
            continue
        member = members[e["member"]]
        key = e["member"]
        if key in seen_island:
            continue
        seen_island.add(key)
        if len(island_members) < 20:
            island_members.append({
                "mark": member["mark"],
                "role": member["role"],
                "aMm": [round(v, 1) for v in member["a"]],
                "bMm": [round(v, 1) for v in member["b"]],
                "sourceObjectIds": member.get("sourceObjectIds", []),
            })

    used = sorted({e["a"] for e in main_edges} | {e["b"] for e in main_edges})
    touch = collections.defaultdict(list)
    for e in main_edges:
        touch[e["a"]].append(e)
        touch[e["b"]].append(e)

    # ---- remaining free ends (post-merge truth; the ask-back list) ----------------------
    deg = collections.Counter()
    for e in main_edges:
        deg[e["a"]] += 1
        deg[e["b"]] += 1
    free_remaining = []
    for n in used:
        if deg[n] != 1:
            continue
        if n in supports:
            continue
        point = node_xyz[n]
        source = []
        for e in touch[n]:
            source.extend(members[e["member"]].get("sourceObjectIds", []))
        free_remaining.append({
            "xyzMm": [round(v, 1) for v in point],
            "confirmedCantilever": is_confirmed_cantilever(point),
            "sourceObjectIds": sorted(set(source)),
        })

    # ---- PyNite model (meters / kN) -----------------------------------------------------
    fe = FEModel3D()
    fe.add_material("steel", E, G, 0.3, RHO_KNM3)
    section_props = {}
    for name, s in sections.items():
        # PyNite add_section(name, A, Iy, Iz, J): the Iy argument is the STRONG axis. This is the
        # documented axis-convention trap (cross-validation caught an 18x disagreement when these
        # were swapped) — Ix (strong) goes SECOND, Iy (weak) third. Do not "fix" the order.
        area_m2 = s["A"] / 1e4
        i_strong_m4 = s["Ix"] / 1e8
        i_weak_m4 = s["Iy"] / 1e8
        fe.add_section(name, area_m2, i_strong_m4, i_weak_m4,
                       torsion_j_cm4(s["H"], s["B"], s["tw"], s["tf"]) / 1e8)
        section_props[name] = {
            "A": area_m2,
            # elastic section moduli about each axis: S = I / (half depth), depth in m
            "Sstrong": i_strong_m4 / (s["H"] / 2000.0),
            "Sweak": i_weak_m4 / (s["B"] / 2000.0),
            "rMin": math.sqrt(i_weak_m4 / area_m2) if area_m2 > 0 else 0.0,
        }

    for n in used:
        x, y, z = node_xyz[n]
        fe.add_node("N%d" % n, x / 1000.0, y / 1000.0, z / 1000.0)
    for n in supports:
        if n not in used:
            continue
        if support_type == "fixed":
            fe.def_support("N%d" % n, True, True, True, True, True, True)
        else:
            fe.def_support("N%d" % n, True, True, True, False, False, False)
            for dof in ("RX", "RY", "RZ"):
                fe.def_support_spring("N%d" % n, dof, PIN_ROTATION_SPRING_KNM_PER_RAD)
    supports_in_model = [n for n in supports if n in used]

    def section_for(e):
        mark = e["mark"]
        profile = mark_sections.get(mark)
        if profile is None:
            space = mark.find(" ")
            if space > 0:
                profile = mark_sections.get(mark[:space])
        if profile is None:
            profile = role_sections.get(e["role"])
        return profile

    missing_sections = collections.Counter()
    edge_section = {}
    self_weight_kn = 0.0
    load_totals = {"G": 0.0, "Q": 0.0}   # signed FZ per case (self weight + line + point)
    role_counts = collections.Counter()
    for i, e in enumerate(main_edges):
        role_counts[e["role"]] += 1
        profile = section_for(e)
        if profile not in sections:
            missing_sections[e["mark"]] += 1
            profile = default_section
        if profile not in sections:
            print(json.dumps({"error": "no usable section for mark %s" % e["mark"]}))
            return 2
        edge_section[i] = profile
        fe.add_member("M%d" % i, "N%d" % e["a"], "N%d" % e["b"], "steel", profile)
        w = sections[profile]["A"] / 1e4 * RHO_KNM3  # kN/m self weight
        fe.add_member_dist_load("M%d" % i, "FZ", -w, -w, case="G")
        self_weight_kn += w * e["len"] / 1000.0
        load_totals["G"] -= w * e["len"] / 1000.0

    # ---- line loads by role or mark, tagged G/Q ----------------------------------------
    line_load_kn = {"G": 0.0, "Q": 0.0}
    unmatched_line_loads = []
    for entry in line_loads:
        case = str(entry.get("case", "G")).upper()
        if case not in ("G", "Q"):
            case = "G"
        w = float(entry.get("kNPerM", 0.0))
        if w == 0.0:
            continue
        role = entry.get("role")
        mark = entry.get("mark")
        matched = 0
        for i, e in enumerate(main_edges):
            if role and e["role"] != role:
                continue
            if mark and not (e["mark"] == mark or e["mark"].startswith(mark + " ")):
                continue
            if not role and not mark and e["role"] != "beam":
                continue   # an untargeted line load is a floor/roof load: beams only
            fe.add_member_dist_load("M%d" % i, "FZ", -w, -w, case=case)
            line_load_kn[case] += w * e["len"] / 1000.0
            load_totals[case] -= w * e["len"] / 1000.0
            matched += 1
        if matched == 0:
            unmatched_line_loads.append(entry)

    # ---- point loads: nearest node within snap, else onto a member's interior -----------
    point_load_kn = {"G": 0.0, "Q": 0.0}
    applied_point_loads = []
    unapplied_point_loads = []
    for entry in point_loads:
        case = str(entry.get("case", "G")).upper()
        if case not in ("G", "Q"):
            case = "G"
        point = scaled_point(entry.get("point", [0, 0, 0]), scale)
        forces = {"FX": float(entry.get("fx", 0.0)), "FY": float(entry.get("fy", 0.0)),
                  "FZ": float(entry.get("fz", 0.0))}
        if all(v == 0.0 for v in forces.values()):
            continue
        best_n, bd = None, snap
        for n in used:
            d = math.dist(point, node_xyz[n])
            if d < bd:
                best_n, bd = n, d
        target = None
        if best_n is not None:
            for direction, value in forces.items():
                if value != 0.0:
                    fe.add_node_load("N%d" % best_n, direction, value, case=case)
            target = {"node": best_n, "xyzMm": [round(v, 1) for v in node_xyz[best_n]]}
        else:
            best_e = None
            for i, e in enumerate(main_edges):
                d, q = seg_project(point, node_xyz[e["a"]], node_xyz[e["b"]])
                if d is not None and d < snap and (best_e is None or d < best_e[0]):
                    best_e = (d, i, q)
            if best_e is not None:
                _, i, q = best_e
                e = main_edges[i]
                x_m = math.dist(node_xyz[e["a"]], q) / 1000.0
                for direction, value in forces.items():
                    if value != 0.0:
                        fe.add_member_pt_load("M%d" % i, direction, value, x_m, case=case)
                target = {"member": i, "mark": e["mark"], "xyzMm": [round(v, 1) for v in q]}
        if target is None:
            unapplied_point_loads.append({"point": [round(v, 1) for v in point], "case": case, **{
                k.lower(): v for k, v in forces.items()}})
            continue
        point_load_kn[case] += forces["FZ"]
        load_totals[case] += forces["FZ"]
        applied_point_loads.append({"case": case, "fzKn": forces["FZ"], "fxKn": forces["FX"],
                                    "fyKn": forces["FY"], "target": target})

    fe.add_load_combo("SLS", {"G": 1.0, "Q": 1.0})
    fe.add_load_combo("ULS", {"G": gamma_g, "Q": gamma_q})
    t0 = time.time()
    try:
        fe.analyze(check_statics=False)
    except Exception as exc:  # PyNite raises on an unstable structure (mechanism / no restraint)
        print(json.dumps({
            "error": "the structure is unstable as modeled: %s - check supports (a pinned-only "
                     "model with an unrestrained mechanism, or a component whose only support "
                     "is a single pin) and free ends" % exc}))
        return 2
    solve_s = time.time() - t0

    # ---- results ------------------------------------------------------------------------
    disps = []
    for n in used:
        node = fe.nodes["N%d" % n]
        d = float(math.sqrt(node.DX["SLS"] ** 2 + node.DY["SLS"] ** 2 + node.DZ["SLS"] ** 2))
        disps.append((d, n))
    disps.sort(reverse=True)
    max_d, max_n = disps[0] if disps else (0.0, None)
    if max_n is not None and not math.isfinite(max_d):
        print(json.dumps({"error": "the solve produced non-finite displacements (singular model): "
                                   "check supports and connectivity"}))
        return 2
    applied_fz = load_totals["G"] + load_totals["Q"]
    sum_rz = float(sum(fe.nodes["N%d" % n].RxnFZ["SLS"] for n in supports_in_model))
    sum_rz_uls = float(sum(fe.nodes["N%d" % n].RxnFZ["ULS"] for n in supports_in_model))
    equilibrium_error = (abs(sum_rz + applied_fz) / abs(applied_fz) * 100.0) if abs(applied_fz) > 1e-9 else 0.0

    # Member checks. Deflection (SLS) is the "does it sag" verdict; the utilization (ULS) is an
    # ELASTIC STRESS SCREEN — N/A + My/Sy + Mz/Sz against fy — and the slenderness L/r_min a
    # compression sanity limit. Every failed member carries its source object ids so the agent
    # can POINT at the real geometry instead of describing coordinates in prose.
    checks = []
    utilization_by_edge = {}
    ratio_by_edge = {}
    for i, e in enumerate(main_edges):
        member = fe.members["M%d" % i]
        props = section_props[edge_section[i]]
        try:
            # float() casts strip numpy scalar types — json.dumps refuses numpy.bool_/float64,
            # and PyNite's deflection API returns numpy scalars.
            dy = float(abs(member.max_deflection("dy", "SLS"))) * 1000.0
            dy_min = float(abs(member.min_deflection("dy", "SLS"))) * 1000.0
            dz = float(abs(member.max_deflection("dz", "SLS"))) * 1000.0
            dz_min = float(abs(member.min_deflection("dz", "SLS"))) * 1000.0
            axial_max = float(member.max_axial("ULS"))     # PyNite: compression POSITIVE
            axial_min = float(member.min_axial("ULS"))
            my = max(abs(float(member.max_moment("My", "ULS"))), abs(float(member.min_moment("My", "ULS"))))
            mz = max(abs(float(member.max_moment("Mz", "ULS"))), abs(float(member.min_moment("Mz", "ULS"))))
        except Exception:
            continue
        worst = max(dy, dy_min, dz, dz_min)
        limit = e["len"] / limit_ratio
        axial = max(abs(axial_max), abs(axial_min))
        stress = axial / props["A"] + my / props["Sstrong"] + mz / props["Sweak"]   # kN/m2
        utilization = stress / fy if fy > 0 else None
        compression = axial_max > 0.0
        slenderness = (e["len"] / 1000.0) / props["rMin"] if compression and props["rMin"] > 0 else None
        deflection_ok = bool(worst <= limit)
        utilization_ok = bool(utilization is None or utilization <= max_util)
        slenderness_ok = bool(slenderness is None or slenderness <= slender_limit)
        reasons = []
        if not deflection_ok:
            reasons.append("deflection")
        if not utilization_ok:
            reasons.append("utilization")
        if not slenderness_ok:
            reasons.append("slenderness")
        utilization_by_edge[i] = round(utilization, 3) if utilization is not None else None
        ratio_by_edge[i] = round(worst / limit, 3) if limit > 0 else None
        checks.append({
            "mark": e["mark"],
            "role": e["role"],
            "section": edge_section[i],
            "lengthMm": round(e["len"], 1),
            "deflectionMm": round(worst, 3),
            "limitMm": round(limit, 3),
            "ratio": round(worst / limit, 3) if limit > 0 else None,
            "deflectionPassed": deflection_ok,
            "axialKn": round(axial_max if compression else axial_min, 3),
            "momentStrongKnm": round(my, 3),
            "momentWeakKnm": round(mz, 3),
            "stressMPa": round(stress / 1000.0, 2),
            "utilization": round(utilization, 3) if utilization is not None else None,
            "utilizationPassed": utilization_ok,
            "slenderness": round(slenderness, 1) if slenderness is not None else None,
            "slendernessPassed": slenderness_ok,
            "passed": bool(deflection_ok and utilization_ok and slenderness_ok),
            "failedChecks": reasons,
            "aMm": [round(v, 1) for v in node_xyz[e["a"]]],
            "bMm": [round(v, 1) for v in node_xyz[e["b"]]],
            "sourceObjectIds": members[e["member"]].get("sourceObjectIds", []),
        })

    def severity(check):
        return max(
            check["ratio"] or 0.0,
            (check["utilization"] or 0.0) / max(max_util, 1e-9),
            (check["slenderness"] or 0.0) / max(slender_limit, 1e-9))

    failed = sorted([c for c in checks if not c["passed"]], key=lambda c: -severity(c))[:20]

    # Warnings are conditions the numbers alone would hide: a mechanism that the pin springs
    # made "solvable" shows up as a displacement out of all proportion to the model, and a
    # load the user asked for that landed nowhere is silently missing from the verdict.
    warnings = []
    extent = 0.0
    if used:
        lo = [min(node_xyz[n][k] for n in used) for k in range(3)]
        hi = [max(node_xyz[n][k] for n in used) for k in range(3)]
        extent = math.dist(lo, hi)
    if extent > 0 and max_d * 1000.0 > 0.1 * extent:
        warnings.append("max displacement %.0f mm exceeds 10%% of the model extent (%.0f mm): the model "
                        "is likely a mechanism (pinned-only supports on a frame with no bracing, or "
                        "a single pinned member) - results are not meaningful" % (max_d * 1000.0, extent))
    if unapplied_point_loads:
        warnings.append("%d point load(s) matched no node or member within %.0f mm and were NOT applied"
                        % (len(unapplied_point_loads), snap))
    if unmatched_line_loads:
        warnings.append("%d line load entr(y/ies) matched no member and were NOT applied" % len(unmatched_line_loads))
    if unmatched_support_points:
        warnings.append("%d supportPoints matched no node within %.0f mm" % (len(unmatched_support_points), snap))
    if missing_sections:
        warnings.append("marks with no section (fell to the default %s): %s"
                        % (default_section, ", ".join(sorted(missing_sections))))
    worst_util = max([c for c in checks if c["utilization"] is not None],
                     key=lambda c: c["utilization"], default=None)

    viz_nodes = {}
    for n in used:
        node = fe.nodes["N%d" % n]
        viz_nodes[str(n)] = {
            "xyzMm": [round(v, 1) for v in node_xyz[n]],
            "dMm": [round(float(node.DX["SLS"]) * 1000.0, 3), round(float(node.DY["SLS"]) * 1000.0, 3),
                    round(float(node.DZ["SLS"]) * 1000.0, 3)],
            "support": n in supports_in_model,
        }

    report = {
        "solveSeconds": round(solve_s, 3),
        "unitScaleToMm": scale,
        "membersIn": len(members),
        "edgesSolved": len(main_edges),
        "componentsSolved": components_solved,
        "islandEdgesDropped": island_edges,
        "islandMembers": island_members,
        "nodes": len(used),
        "roles": dict(role_counts),
        "supports": len(supports_in_model),
        "supportDetail": {
            "type": support_type,
            "auto": len([n for n in auto_support_nodes if n in used]),
            "explicit": len([n for n in explicit_support_nodes if n in used]),
            "points": [[round(v, 1) for v in node_xyz[n]] for n in supports_in_model][:50],
            "unmatchedSupportPoints": unmatched_support_points,
        },
        "snappedFreeEnds": snapped,
        "tJunctionSplits": tsplit,
        "repairedFreeEnds": repaired,
        "freeEndsRemaining": free_remaining,
        "missingSectionMarks": dict(missing_sections),
        "sectionsUsed": dict(collections.Counter(edge_section.values())),
        "loads": {
            "selfWeightKn": round(self_weight_kn, 3),
            "lineLoadKn": {k: round(v, 3) for k, v in line_load_kn.items()},
            "pointLoadFzKn": {k: round(v, 3) for k, v in point_load_kn.items()},
            "appliedFzKn": {k: round(v, 3) for k, v in load_totals.items()},
            "appliedPointLoads": applied_point_loads,
            "unappliedPointLoads": unapplied_point_loads,
            "unmatchedLineLoads": unmatched_line_loads,
            "combos": {"SLS": "1.0G + 1.0Q", "ULS": "%gG + %gQ" % (gamma_g, gamma_q)},
            "fyMPa": fy / 1000.0,
        },
        "totalLoadKn": round(-applied_fz, 2),
        "sumReactionsFzKn": round(sum_rz, 2),
        "sumReactionsFzUlsKn": round(sum_rz_uls, 2),
        "equilibriumErrorPercent": round(equilibrium_error, 3),
        "maxDisplacementMm": round(max_d * 1000.0, 3),
        "maxDisplacementXyzMm": [round(v, 1) for v in node_xyz[max_n]] if max_n is not None else None,
        "deflectionLimit": "L/%d" % int(limit_ratio),
        "maxUtilization": worst_util["utilization"] if worst_util else None,
        "maxUtilizationMember": {
            "mark": worst_util["mark"], "section": worst_util["section"],
            "sourceObjectIds": worst_util["sourceObjectIds"]} if worst_util else None,
        "utilizationNote": "elastic stress screen (N/A + My/S + Mz/S vs fy) under ULS - not a code "
                           "member design: no lateral-torsional or flexural buckling, shear, or "
                           "connection checks",
        "warnings": warnings,
        "memberChecks": {
            "checked": len(checks),
            "passed": sum(1 for c in checks if c["passed"]),
            "failed": len(checks) - sum(1 for c in checks if c["passed"]),
            "deflectionFailed": sum(1 for c in checks if not c["deflectionPassed"]),
            "utilizationFailed": sum(1 for c in checks if not c["utilizationPassed"]),
            "slendernessFailed": sum(1 for c in checks if not c["slendernessPassed"]),
        },
        "failedMembers": failed,
        "checks": checks,
        "viz": {
            "nodes": viz_nodes,
            "edges": [{
                "a": e["a"], "b": e["b"], "mark": e["mark"], "role": e["role"],
                "section": edge_section[i],
                "utilization": utilization_by_edge.get(i),
                "ratio": ratio_by_edge.get(i),
            } for i, e in enumerate(main_edges)],
        },
    }
    print(json.dumps(report, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
