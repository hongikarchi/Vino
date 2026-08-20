# -*- coding: utf-8 -*-
# W2-a probe: which lever makes a broker-style created object's VolatileData populate?
# ASCII only: RunPythonScript is IronPython 2 and refuses undeclared non-ASCII source.
import clr
import json
import os
import time

clr.AddReference("Grasshopper")
clr.AddReference("System.Drawing")
import System
import System.Drawing
import Grasshopper
import Rhino
from Grasshopper.Kernel.Special import GH_NumberSlider

base_dir = os.path.dirname(__file__)
out_path = os.path.join(base_dir, "w2-probe-result.json")
marker = open(os.path.join(base_dir, "w2-probe-started.txt"), "w")
marker.write("started")
marker.close()
results = []


def flush():
    handle = open(out_path, "w")
    try:
        json.dump(results, handle, indent=1)
    finally:
        handle.close()


def record(stage, doc, slider, extra=None):
    entry = {"stage": stage}
    try:
        entry["docEnabled"] = bool(doc.Enabled)
        entry["enableSolutions"] = bool(Grasshopper.Kernel.GH_Document.EnableSolutions)
        entry["solutionState"] = str(doc.SolutionState)
        entry["solutionDepth"] = int(doc.SolutionDepth)
        if slider is not None:
            entry["sliderValue"] = str(slider.CurrentValue)
            entry["volatileCount"] = int(slider.VolatileData.DataCount)
    except Exception as read_error:
        entry["readError"] = str(read_error)
    if extra:
        entry.update(extra)
    results.append(entry)
    flush()


def attempt(stage, doc, slider, action):
    try:
        action()
        record(stage, doc, slider)
    except Exception as error:
        record(stage, doc, slider, {"error": str(error)})


# Load the Grasshopper editor from inside python (the scripted -_Grasshopper option prompt eats
# chained runscript tokens). Plain _Grasshopper opens the editor without an option prompt; if no
# document appears, open the tiny Desktop fixture the E2E also uses (never saved here).
Rhino.RhinoApp.RunScript("_Grasshopper", False)


def find_document():
    canvas = Grasshopper.Instances.ActiveCanvas
    found = canvas.Document if canvas is not None else None
    if found is None:
        server = Grasshopper.Instances.DocumentServer
        found = server[0] if server.DocumentCount > 0 else None
    return found


deadline = time.time() + 120
doc = None
opened_fixture = False
while time.time() < deadline:
    doc = find_document()
    if doc is not None:
        break
    if not opened_fixture and time.time() > deadline - 90:
        Rhino.RhinoApp.RunScript(
            "-_Grasshopper _Document _Open C:\\Users\\user\\Desktop\\unnamed.gh _Enter", False)
        opened_fixture = True
    Rhino.RhinoApp.Wait()
    time.sleep(0.5)

if doc is None:
    results.append({"error": "no grasshopper document after 120s"})
    flush()
else:
    try:
        slider = GH_NumberSlider()
        slider.CreateAttributes()
        slider.Slider.Minimum = System.Decimal(0)
        slider.Slider.Maximum = System.Decimal(100)
        slider.SetSliderValue(System.Decimal(6))
        slider.NickName = "W2Probe"
        slider.Attributes.Pivot = System.Drawing.PointF(30, 30)
        added = doc.AddObject(slider, True)  # update:True, the adapter's exact call
        record("r0-after-AddObject-update-true", doc, slider, {"added": bool(added)})

        attempt("r1-after-NewSolution-false", doc, slider, lambda: doc.NewSolution(False))
        attempt("r2-after-ExpireSolution-true", doc, slider, lambda: slider.ExpireSolution(True))
        attempt("r3-after-NewSolution-expireAll", doc, slider, lambda: doc.NewSolution(True))
        attempt("r4-after-CollectData", doc, slider, lambda: slider.CollectData())

        def schedule_and_wait():
            doc.ScheduleSolution(5)
            Rhino.RhinoApp.Wait()

        attempt("r5-after-ScheduleSolution-and-Wait", doc, slider, schedule_and_wait)
        attempt("r6-final-NewSolution-false", doc, slider, lambda: doc.NewSolution(False))
    except Exception as fatal:
        results.append({"fatal": str(fatal)})
        flush()
