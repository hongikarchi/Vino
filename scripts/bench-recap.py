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

    # Force previews on: baseline arms sometimes disable component preview mid-session,
    # which blanks BOTH session captures (PrintWindow and capture_viewport alike -- observed
    # on three round-1 cells whose outputData proved the geometry existed). The recap is the
    # visual source of record, so it must show what the definition computes.
    try:
        import Grasshopper
        canvas = Grasshopper.Instances.ActiveCanvas
        ghdoc_live = canvas.Document if canvas else None
        if ghdoc_live:
            for obj in list(ghdoc_live.Objects):
                try:
                    if hasattr(obj, "Hidden") and obj.Hidden:
                        obj.Hidden = False
                except Exception:
                    pass
            ghdoc_live.NewSolution(False)
            unhide_deadline = time.time() + 10
            while time.time() < unhide_deadline:
                Rhino.RhinoApp.Wait()
                time.sleep(0.1)
    except Exception:
        pass

    # Hide the scene fixture (every pre-existing Rhino doc object): it is not part of any
    # arm's design and it confused blind judges repeatedly (mis-read as a boundary rect, as
    # panel spill). GH conduit previews are not doc objects, so the design stays visible.
    try:
        for doc_obj in list(sc.doc.Objects):
            try:
                sc.doc.Objects.Hide(doc_obj.Id, True)
            except Exception:
                pass
        sc.doc.Views.Redraw()
    except Exception:
        pass

    shots = []
    for view_name in ("Perspective", "Top"):
        view = sc.doc.Views.Find(view_name, False)
        if view is None:
            continue
        sc.doc.Views.ActiveView = view
        # VINO_RECAP_MODE=Arctic gives a white-clay render that ignores every object/preview
        # color - the fair basis for form-only judging (a red default preview and a custom
        # material must read identically). Default stays Shaded for the regression rounds.
        display_mode = os.environ.get("VINO_RECAP_MODE", "Shaded")
        rs.Command("_-SetDisplayMode _Mode=" + display_mode + " _Enter", False)
        # ZoomExtents still frames the design with the fixture hidden: GH conduit preview
        # contributes to the view bounds (verified on T5 renders where an arm placed its
        # building far outside the fixture bbox and it was framed).
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
