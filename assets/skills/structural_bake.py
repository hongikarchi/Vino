#! python 3
# Structural diagnosis bake: results.json -> verdict-colored member axes as REAL Rhino objects on Vino::Structural band layers. Wire verbatim; never edit per job.
#
# Vetted bake code in the bake_manager.py mold — same family-identity contract, so a re-bake
# replaces its own previous output and never touches the user's geometry. The color/band rule is
# shared with structural_viewer.py: "that member reads red" is part of the safety claim, so the
# mapping is shipped code, not model prose.
#
# Input sockets (declare via setComponentIo, EXACTLY these four, names exact, all WIRED):
#   resultsPath (item, str)   absolute path of the structural_solve results artifact
#                             (solve summary's resultsPathAbsolute; wire from a Panel)
#   scale       (item, float) displacement magnification baked into the axes (0 = undeformed —
#                             the usual persistent record; wire a Panel with 0 or a slider)
#   layerRoot   (item, str)   band layer parent, e.g. "Vino::Structural" (wire from a Panel)
#   bake        (item, bool)  wire a Boolean Toggle. False = dry-run report (nothing written)
# Output sockets:
#   report      (item)  what happened / what would happen (one-line JSON)
#   baked_ids   (list)  Guids of live objects belonging to this bake family after the bake
#
# Bands (sub-layers under layerRoot, colored on creation):
#   탐색  gray   — no verdict data (exploration / candidate axes)
#   통과  green  — severity < 0.7
#   주의  amber  — 0.7 <= severity < 1.0
#   초과  red    — severity >= 1.0
# Every object also carries its exact ramp color as the object color, plus user text:
# severity, mark, section, and the source object ids — so a baked axis can point back at the
# real geometry it grades.

import json

import Rhino
import System

doc = Rhino.RhinoDoc.ActiveDoc
FAMILY_KEY = "gptino_bake_family"
SOURCE_DOC_KEY = "GPTino.SourceDocKey"
COMPONENT_KEY = "gptino_bake_component"
FAMILY = "vino-structural-diagnosis"

_GRAY = (150, 150, 150)
_RED = (198, 40, 30)
_DEEP_RED = (120, 12, 8)
_BANDS = (
    ("탐색", (130, 130, 130)),
    ("통과", (90, 150, 100)),
    ("주의", (200, 150, 40)),
    ("초과", (198, 40, 30)),
)


def _severity_of(edge):
    values = [v for v in (edge.get("utilization"), edge.get("ratio")) if v is not None]
    return max(values) if values else None


def _band_of(severity):
    if severity is None:
        return "탐색"
    if severity >= 1.0:
        return "초과"
    return "주의" if severity >= 0.7 else "통과"


def _mix(a, b, t):
    return tuple(int(round(x + (y - x) * t)) for x, y in zip(a, b))


def _rgb_for(severity):
    if severity is None:
        return _GRAY
    if severity <= 1.0:
        return _mix(_GRAY, _RED, max(0.0, severity))
    return _mix(_RED, _DEEP_RED, min((severity - 1.0) / 0.5, 1.0))


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


_path = str(resultsPath).strip() if "resultsPath" in dir() and resultsPath else None
_scale = float(scale) if "scale" in dir() and scale is not None else None
_root = str(layerRoot).strip() if "layerRoot" in dir() and layerRoot else "Vino::Structural"
_bake = bool(bake) if "bake" in dir() and bake is not None else False

if _path and _scale is not None:
    with open(_path, encoding="utf-8") as _f:
        _data = json.load(_f)
    _viz = _data.get("viz") or {}
    _nodes = _viz.get("nodes") or {}
    _edges = _viz.get("edges") or []
    _to_doc = 1.0 / float(_data.get("unitScaleToMm") or 1.0)

    if not _bake:
        _counts = {}
        for _edge in _edges:
            _band = _band_of(_severity_of(_edge))
            _counts[_band] = _counts.get(_band, 0) + 1
        report = json.dumps({
            "dryRun": True,
            "wouldBake": len(_edges),
            "bands": _counts,
            "layerRoot": _root,
            "note": "wire True into bake to write",
        }, ensure_ascii=False)
    else:
        # Replace mode: this family's previous output is ours to retire; nothing else is.
        _removed = 0
        for _existing in list(doc.Objects):
            try:
                if _existing.Attributes.GetUserString(FAMILY_KEY) == FAMILY:
                    doc.Objects.Delete(_existing, True)
                    _removed += 1
            except Exception:
                pass

        _layer_index = {name: _ensure_layer(_root + "::" + name, rgb) for name, rgb in _BANDS}
        _doc_key = _source_doc_key()
        _component = _component_id()
        _ids = []
        _counts = {}
        for _i, _edge in enumerate(_edges):
            _na = _nodes.get(str(_edge["a"]))
            _nb = _nodes.get(str(_edge["b"]))
            if not _na or not _nb:
                continue
            _points = []
            for _n in (_na, _nb):
                _xyz = _n["xyzMm"]
                _disp = _n.get("dMm") or [0.0, 0.0, 0.0]
                _points.append(Rhino.Geometry.Point3d(
                    (_xyz[0] + _disp[0] * _scale) * _to_doc,
                    (_xyz[1] + _disp[1] * _scale) * _to_doc,
                    (_xyz[2] + _disp[2] * _scale) * _to_doc))
            _severity = _severity_of(_edge)
            _band = _band_of(_severity)
            _counts[_band] = _counts.get(_band, 0) + 1
            _r, _g, _b = _rgb_for(_severity)
            _attributes = Rhino.DocObjects.ObjectAttributes()
            _attributes.LayerIndex = _layer_index[_band]
            _attributes.ObjectColor = System.Drawing.Color.FromArgb(255, _r, _g, _b)
            _attributes.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject
            _attributes.Name = "structural-diag-%03d" % _i
            _attributes.SetUserString(FAMILY_KEY, FAMILY)
            if _doc_key:
                _attributes.SetUserString(SOURCE_DOC_KEY, _doc_key)
            if _component:
                _attributes.SetUserString(COMPONENT_KEY, _component)
            if _severity is not None:
                _attributes.SetUserString("vino_severity", "%.3f" % _severity)
            if _edge.get("mark"):
                _attributes.SetUserString("vino_mark", str(_edge["mark"]))
            if _edge.get("section"):
                _attributes.SetUserString("vino_section", str(_edge["section"]))
            _guid = doc.Objects.AddLine(Rhino.Geometry.Line(_points[0], _points[1]), _attributes)
            if _guid != System.Guid.Empty:
                _ids.append(_guid)
        doc.Views.Redraw()
        baked_ids = _ids
        report = json.dumps({
            "baked": len(_ids),
            "replaced": _removed,
            "bands": _counts,
            "layerRoot": _root,
            "scale": _scale,
        }, ensure_ascii=False)
# unwired/incomplete inputs: assign nothing but a status note
else:
    report = json.dumps({"waiting": "inputs incomplete (resultsPath/scale/layerRoot/bake)"})
