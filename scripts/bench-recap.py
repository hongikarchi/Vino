# Re-open a bench cell's saved Grasshopper definition against its original scene and
# capture clean, identically-framed viewport images (Perspective + Top) so arms can be
# compared visually without the luck-of-the-viewport problem PrintWindow captures have.
# Driven by env vars because RunPythonScript takes no CLI args:
#   VINO_RECAP_GH  = path to the saved .gh definition
#   VINO_RECAP_OUT = output directory for <tag>-perspective.png / <tag>-top.png
#   VINO_RECAP_TAG = cell tag used in filenames
# Writes VINO_RECAP_OUT\<tag>.recap-ok on success (poll target for the harness).
import os
import time
import traceback

import Rhino
import System.Drawing as SD
import rhinoscriptsyntax as rs
import scriptcontext as sc

gh_path = os.environ["VINO_RECAP_GH"]
out_dir = os.environ["VINO_RECAP_OUT"]
tag = os.environ["VINO_RECAP_TAG"]

try:
    gh = Rhino.RhinoApp.GetPlugInObject("Grasshopper")
    if not gh.OpenDocument(gh_path):
        raise Exception("Grasshopper.OpenDocument returned False for %s" % gh_path)

    # Solving happens on idle; pump the message loop long enough for a 300-panel solve
    # (measured ~2-4 s) plus Cordyceps' server start inside the definition.
    deadline = time.time() + 25
    while time.time() < deadline:
        Rhino.RhinoApp.Wait()
        time.sleep(0.1)

    shots = []
    for view_name in ("Perspective", "Top"):
        view = sc.doc.Views.Find(view_name, False)
        if view is None:
            continue
        sc.doc.Views.ActiveView = view
        rs.Command("_-SetDisplayMode _Mode=Shaded _Enter", False)
        # Doc geometry (fixture surface) bounds the GH preview sitting on it, so
        # ZoomExtents frames the result even though conduit preview has no bbox here.
        view.ActiveViewport.ZoomExtents()
        sc.doc.Views.Redraw()
        Rhino.RhinoApp.Wait()
        bmp = view.CaptureToBitmap(SD.Size(1600, 1000))
        shot = os.path.join(out_dir, "%s-%s.png" % (tag, view_name.lower()))
        bmp.Save(shot)
        shots.append(shot)

    if not shots:
        raise Exception("no captureable viewports found")
    with open(os.path.join(out_dir, tag + ".recap-ok"), "w") as handle:
        handle.write("\n".join(shots))
except Exception:
    with open(os.path.join(out_dir, tag + ".recap-err"), "w") as handle:
        handle.write(traceback.format_exc())
