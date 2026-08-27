#! python 3
# Structural diagnosis viewer: results.json -> member axis lines colored by severity + deformed shape at a slider scale. Wire verbatim; never edit per job.
#
# Vetted display code in the bake_manager.py / structural_check.py mold: Vino creates this as a
# Python 3 component and WIRES it — the model never rewrites the mapping from verdict numbers to
# color, because "that member reads red" is part of the safety claim.
#
# Input sockets (declare via setComponentIo, EXACTLY these two, names exact, both WIRED —
# Grasshopper treats unwired script inputs as required and will not run the component):
#   resultsPath (item, str)   absolute path of the structural_solve results artifact — the solve
#                             summary returns it as resultsPathAbsolute; wire it from a Panel
#   scale       (item, float) displacement magnification for the deformed shape (Number Slider,
#                             suggested range 0..500, default 100; 0 = undeformed axes)
# Output sockets:
#   lines    (list) deformed member axis lines, one per solved edge, in DOCUMENT units
#   colors   (list) per-line display colour — wire lines+colors into ONE Custom Preview
#                   (a Colour wired into the Shader input auto-converts)
#   severity (list) per-line severity: max(deflection ratio, utilization); -1 = no verdict data
#   report   (item) one-line JSON {edges, maxSeverity, maxDisplacementMm, scale}
#
# Colour rule (the user's diagnosis convention): members with NO verdict data — exploration or
# candidate axes — stay GRAY; verdict-carrying members ramp gray -> red as severity approaches
# 1.0 and darken beyond it. Severity is dimensionless: 1.0 = at its limit.

import json

_GRAY = (150, 150, 150)
_RED = (198, 40, 30)
_DEEP_RED = (120, 12, 8)


def _severity_of(edge):
    values = [v for v in (edge.get("utilization"), edge.get("ratio")) if v is not None]
    return max(values) if values else None


def _mix(a, b, t):
    return tuple(int(round(x + (y - x) * t)) for x, y in zip(a, b))


def _rgb_for(severity):
    if severity is None:
        return _GRAY
    if severity <= 1.0:
        return _mix(_GRAY, _RED, max(0.0, severity))
    return _mix(_RED, _DEEP_RED, min((severity - 1.0) / 0.5, 1.0))


_path = str(resultsPath).strip() if "resultsPath" in dir() and resultsPath else None
_scale = float(scale) if "scale" in dir() and scale is not None else None

if _path and _scale is not None:
    with open(_path, encoding="utf-8") as _f:   # a wrong path must fail loudly, not render nothing
        _data = json.load(_f)
    _viz = _data.get("viz") or {}
    _nodes = _viz.get("nodes") or {}
    _to_doc = 1.0 / float(_data.get("unitScaleToMm") or 1.0)

    import Rhino
    from System.Drawing import Color

    def _deformed(node):
        xyz = node["xyzMm"]
        disp = node.get("dMm") or [0.0, 0.0, 0.0]
        return Rhino.Geometry.Point3d(
            (xyz[0] + disp[0] * _scale) * _to_doc,
            (xyz[1] + disp[1] * _scale) * _to_doc,
            (xyz[2] + disp[2] * _scale) * _to_doc)

    _lines = []
    _colors = []
    _sev = []
    for _edge in _viz.get("edges") or []:
        _na = _nodes.get(str(_edge["a"]))
        _nb = _nodes.get(str(_edge["b"]))
        if not _na or not _nb:
            continue
        _s = _severity_of(_edge)
        _r, _g, _b = _rgb_for(_s)
        _lines.append(Rhino.Geometry.Line(_deformed(_na), _deformed(_nb)))
        _colors.append(Color.FromArgb(255, _r, _g, _b))
        _sev.append(_s if _s is not None else -1.0)

    lines = _lines
    colors = _colors
    severity = _sev
    _graded = [s for s in _sev if s >= 0.0]
    report = json.dumps({
        "edges": len(_lines),
        "maxSeverity": max(_graded) if _graded else None,
        "maxDisplacementMm": _data.get("maxDisplacementMm"),
        "scale": _scale,
    })
# unwired/incomplete inputs: assign nothing but a status note (outputs stay unassigned)
else:
    report = json.dumps({"waiting": "inputs incomplete (resultsPath/scale)"})
