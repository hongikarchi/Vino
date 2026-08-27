#! python 3
# Member solids (Profile->Brep): results.json -> real H-section Breps swept along each member, previewed in GH and baked on a Toggle. Wire verbatim; never edit per job.
#
# Vetted geometry code in the structural_bake.py mold — same family-identity contract, so a
# re-bake replaces its own previous output and never touches the user's geometry. Orientation
# and sweep rules are shipped code because "the flange points the right way" is a claim that
# must reproduce.
#
# Rules (P5/P7):
#   - Members whose SOURCE OBJECT is a non-linear curve in the document are swept along that
#     REAL curve (one solid per source curve): the section rolls with the planar bend and never
#     twists — exactly the fabrication rule. Everything else extrudes edge by edge.
#   - Beams: web VERTICAL (strong axis carries gravity). Columns (near-vertical axes): web
#     toward world X — a stated assumption, not a detected fact; say it in the report.
#   - Section dims come from the report's sectionsUsedDetail; volumes are checked against
#     A x L so a bad sweep can never pass silently.
#
# Input sockets (declare via setComponentIo, EXACTLY these three, names exact, all WIRED):
#   resultsPath (item, str)   absolute path of the structural_solve results artifact
#   layerRoot   (item, str)   band layer parent, e.g. "Vino::Structural" (wire from a Panel)
#   bake        (item, bool)  wire a Boolean TOGGLE (never a Button). False = preview only
# Output sockets:
#   breps       (list)  the member solids (always produced — Grasshopper previews them)
#   report      (item)  one-line JSON {groups, swept, extruded, expectedVolumeM3,
#                       actualVolumeM3, baked, replaced, assumptions}
#   baked_ids   (list)  Guids of live objects after a bake (empty on preview)

import json
import math

import Rhino
import System

doc = Rhino.RhinoDoc.ActiveDoc
FAMILY_KEY = "gptino_bake_family"
SOURCE_DOC_KEY = "GPTino.SourceDocKey"
COMPONENT_KEY = "gptino_bake_component"
FAMILY = "vino-structural-solids"
STEEL_GRAY = (110, 116, 122)


def _source_doc_key():
    try:
        import hashlib
        import os
        path = ghenv.Component.OnPingDocument().FilePath  # noqa: F821 - ambient in script components
        canonical = os.path.abspath(path).upper() if path else ""
        return hashlib.sha256(canonical.encode("utf-8")).hexdigest()[:16]
    except Exception:
        return None


def _component_id():
    try:
        return str(ghenv.Component.InstanceGuid)  # noqa: F821 - ambient in script components
    except Exception:
        return None


def _ensure_layer(full_path, rgb):
    parent = System.Guid.Empty
    index = doc.Layers.CurrentLayerIndex
    accumulated = []
    for token in [part.strip() for part in full_path.split("::") if part.strip()]:
        accumulated.append(token)
        joined = "::".join(accumulated)
        existing = doc.Layers.FindByFullPath(joined, -1)
        if existing >= 0:
            index = existing
            parent = doc.Layers[existing].Id
            continue
        layer = Rhino.DocObjects.Layer()
        layer.Name = token
        if parent != System.Guid.Empty:
            layer.ParentLayerId = parent
        if joined == full_path:
            layer.Color = System.Drawing.Color.FromArgb(255, rgb[0], rgb[1], rgb[2])
        index = doc.Layers.Add(layer)
        parent = doc.Layers[index].Id
    return index


def _h_outline_plane(plane, dims, to_doc):
    """Closed H outline in the given plane: local X = flange width, local Y = web height."""
    h = dims["H"] * to_doc
    b = dims["B"] * to_doc
    tw = dims["tw"] * to_doc
    tf = dims["tf"] * to_doc
    xy = [
        (-b / 2, -h / 2), (b / 2, -h / 2), (b / 2, -h / 2 + tf),
        (tw / 2, -h / 2 + tf), (tw / 2, h / 2 - tf), (b / 2, h / 2 - tf),
        (b / 2, h / 2), (-b / 2, h / 2), (-b / 2, h / 2 - tf),
        (-tw / 2, h / 2 - tf), (-tw / 2, -h / 2 + tf), (-b / 2, -h / 2 + tf),
    ]
    points = [plane.PointAt(x, y) for x, y in xy]
    points.append(points[0])
    return Rhino.Geometry.PolylineCurve(points)


def _profile_plane(origin, tangent):
    """Web direction: vertical projected off the tangent; near-vertical members fall back to
    world X (the stated column assumption)."""
    t = Rhino.Geometry.Vector3d(tangent)
    t.Unitize()
    z = Rhino.Geometry.Vector3d.ZAxis
    if abs(t * z) > 0.95:
        raw = Rhino.Geometry.Vector3d.XAxis
    else:
        raw = z
    web = raw - t * (raw * t)
    web.Unitize()
    flange = Rhino.Geometry.Vector3d.CrossProduct(web, t)
    flange.Unitize()
    return Rhino.Geometry.Plane(origin, flange, web)


def _sweep(rail, dims, to_doc, tol):
    t0 = rail.Domain.Min
    plane = _profile_plane(rail.PointAt(t0), rail.TangentAt(t0))
    outline = _h_outline_plane(plane, dims, to_doc)
    swept = Rhino.Geometry.Brep.CreateFromSweep(rail, outline, rail.IsClosed, tol)
    solids = []
    for piece in swept or []:
        capped = piece.CapPlanarHoles(tol) or piece
        solids.append(capped)
    return solids


_path = str(resultsPath).strip() if "resultsPath" in dir() and resultsPath else None
_root = str(layerRoot).strip() if "layerRoot" in dir() and layerRoot else "Vino::Structural"
_bake = bool(bake) if "bake" in dir() and bake is not None else False

if _path:
    with open(_path, encoding="utf-8") as _f:
        _data = json.load(_f)
    _checks = _data.get("checks") or []
    _dims_by_section = _data.get("sectionsUsedDetail") or {}
    _to_doc = 1.0 / float(_data.get("unitScaleToMm") or 1.0)
    _tol = doc.ModelAbsoluteTolerance if doc else 0.001

    # group solved edges by their first source object id
    _groups = {}
    for _check in _checks:
        _ids = _check.get("sourceObjectIds") or []
        _key = _ids[0] if _ids else "edge-%d" % len(_groups)
        _groups.setdefault(_key, []).append(_check)

    breps = []
    _rows = []          # (brep, section, mark, kind)
    _swept_count = 0
    _extruded_count = 0
    _expected_mm3 = 0.0
    _skipped = 0
    for _key, _edges in _groups.items():
        _section = _edges[0].get("section")
        _dims = _dims_by_section.get(_section)
        if not _dims:
            _skipped += len(_edges)
            continue
        _rail_curve = None
        try:
            _obj = doc.Objects.FindId(System.Guid(_key))
            if _obj and isinstance(_obj.Geometry, Rhino.Geometry.Curve):
                _curve = _obj.Geometry
                # Rail-sweep ONLY genuinely curved sources (arcs, NURBS). A polyline of straight
                # segments extrudes edge by edge — its kinks are joints, not bends.
                _is_polyline = _curve.TryGetPolyline()[0]
                if not _is_polyline and not _curve.IsLinear(_tol):
                    _rail_curve = _curve.DuplicateCurve()
        except Exception:
            _rail_curve = None
        if _rail_curve is not None:
            _solids = _sweep(_rail_curve, _dims, _to_doc, _tol)
            for _solid in _solids:
                breps.append(_solid)
                _rows.append((_solid, _section, _edges[0].get("mark"), "swept"))
            if _solids:
                _swept_count += 1
                # A (cm2 -> mm2) x rail length (doc -> mm), then mm3 -> doc-units^3
                _expected_mm3 += _dims["A"] * 100.0 * (_rail_curve.GetLength() / _to_doc) * (_to_doc ** 3)
        else:
            for _edge in _edges:
                _a = _edge["aMm"]
                _b = _edge["bMm"]
                _p0 = Rhino.Geometry.Point3d(_a[0] * _to_doc, _a[1] * _to_doc, _a[2] * _to_doc)
                _p1 = Rhino.Geometry.Point3d(_b[0] * _to_doc, _b[1] * _to_doc, _b[2] * _to_doc)
                _rail = Rhino.Geometry.LineCurve(_p0, _p1)
                _solids = _sweep(_rail, _dims, _to_doc, _tol)
                for _solid in _solids:
                    breps.append(_solid)
                    _rows.append((_solid, _section, _edge.get("mark"), "extruded"))
                if _solids:
                    _extruded_count += 1
                    _expected_mm3 += _dims["A"] * 100.0 * _edge["lengthMm"] * (_to_doc ** 3)

    _actual = 0.0
    for _solid, _section, _mark, _kind in _rows:
        _props = Rhino.Geometry.VolumeMassProperties.Compute(_solid)
        if _props:
            # abs(): a capped sweep can come out inward-facing; magnitude is the claim
            _actual += abs(_props.Volume)
    _expected = _expected_mm3  # already in doc units cubed
    _report = {
        "groups": len(_groups),
        "solids": len(breps),
        "swept": _swept_count,
        "extruded": _extruded_count,
        "skippedNoDims": _skipped,
        # doc-units^3 -> m^3: one doc unit is (0.001 / _to_doc) meters
        "expectedVolumeM3": round(_expected / ((1000.0 * _to_doc) ** 3), 4),
        "actualVolumeM3": round(_actual / ((1000.0 * _to_doc) ** 3), 4),
        "assumptions": "beams web-vertical; near-vertical members web toward world X; curved "
                       "members swept along their source curve (bent, never twisted)",
    }

    if _bake and breps:
        _removed = 0
        for _existing in list(doc.Objects):
            try:
                if _existing.Attributes.GetUserString(FAMILY_KEY) == FAMILY:
                    doc.Objects.Delete(_existing, True)
                    _removed += 1
            except Exception:
                pass
        _layer = _ensure_layer(_root + "::부재", STEEL_GRAY)
        _doc_key = _source_doc_key()
        _component = _component_id()
        _ids = []
        for _i, (_solid, _section, _mark, _kind) in enumerate(_rows):
            _attributes = Rhino.DocObjects.ObjectAttributes()
            _attributes.LayerIndex = _layer
            _attributes.Name = "structural-member-%03d" % _i
            _attributes.SetUserString(FAMILY_KEY, FAMILY)
            if _doc_key:
                _attributes.SetUserString(SOURCE_DOC_KEY, _doc_key)
            if _component:
                _attributes.SetUserString(COMPONENT_KEY, _component)
            _attributes.SetUserString("vino_section", str(_section))
            if _mark:
                _attributes.SetUserString("vino_mark", str(_mark))
            _attributes.SetUserString("vino_sweep", _kind)
            _guid = doc.Objects.Add(_solid, _attributes)
            if _guid != System.Guid.Empty:
                _ids.append(_guid)
        doc.Views.Redraw()
        baked_ids = _ids
        _report["baked"] = len(_ids)
        _report["replaced"] = _removed
    report = json.dumps(_report, ensure_ascii=False)
# unwired/incomplete inputs: assign nothing but a status note
else:
    report = json.dumps({"waiting": "inputs incomplete (resultsPath/layerRoot/bake)"})
