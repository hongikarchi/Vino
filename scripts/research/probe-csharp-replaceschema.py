# Headless probe for live-gate 2026-08-13 finding F1: python.replaceSchema on C# script
# components fails with "rejected an appended Output socket at position 0". Mirrors the
# adapter's socket dance (clear default inputs, clear outputs except the console 'out',
# VariableParameterMaintenance, then CanInsertParameter checks) on BOTH script component
# types so the fix is written from observed facts, not guesses.
#
# Run inside Rhino AFTER Grasshopper has loaded (chain `-_Grasshopper _Document _Open ...`).
# Writes JSON to $VINO_PROBE_OUT and an .ok marker beside it; RunPythonScript swallows
# exceptions (IronPython 2), so any failure is persisted to '<out>.err' instead.
# ASCII-only on purpose - non-ASCII source silently aborts the parse with no marker.
import os
import json
import traceback
import clr

clr.AddReference("Grasshopper")
import System
import Grasshopper
from Grasshopper.Kernel import GH_ParameterSide

OUT_PATH = os.environ.get("VINO_PROBE_OUT")
if not OUT_PATH:
    raise Exception("VINO_PROBE_OUT is not set")
results = []


def describe(comp):
    return {
        "inputs": [p.Name for p in comp.Params.Input],
        "outputs": [p.Name for p in comp.Params.Output],
    }


def can_insert(var, side, index):
    try:
        return bool(var.CanInsertParameter(side, index))
    except Exception as ex:  # noqa: BLE001 - evidence, not control flow
        return "error: " + str(ex)


def probe(type_guid, label):
    entry = {"label": label, "typeGuid": type_guid}
    try:
        emitted = Grasshopper.Instances.ComponentServer.EmitObject(System.Guid(type_guid))
        if emitted is None:
            entry["error"] = "EmitObject returned None"
            results.append(entry)
            return
        comp = emitted
        doc = Grasshopper.Kernel.GH_Document()
        entry["addedToDoc"] = bool(doc.AddObject(emitted, True))
        entry["initial"] = describe(comp)
        var = emitted
        output_count = comp.Params.Output.Count
        entry["preClear"] = {
            "canInsertOutput0": can_insert(var, GH_ParameterSide.Output, 0),
            "canInsertOutputEnd": can_insert(var, GH_ParameterSide.Output, output_count),
        }
        clear = {"inputsRemoved": [], "outputsRemoved": [], "failures": []}
        for p in list(comp.Params.Input):
            ok = comp.Params.UnregisterInputParameter(p, True)
            (clear["inputsRemoved"] if ok else clear["failures"]).append(p.Name)
        for p in list(comp.Params.Output):
            if p.Name == "out":
                continue
            ok = comp.Params.UnregisterOutputParameter(p, True)
            (clear["outputsRemoved"] if ok else clear["failures"]).append(p.Name)
        entry["clear"] = clear
        try:
            var.VariableParameterMaintenance()
        except Exception as ex:  # noqa: BLE001
            entry["maintenanceError"] = str(ex)
        comp.Params.OnParametersChanged()
        entry["postClear"] = describe(comp)
        post_count = comp.Params.Output.Count
        entry["postClearCan"] = {
            "outputCount": post_count,
            "canInsertOutput0": can_insert(var, GH_ParameterSide.Output, 0),
            "canInsertOutput1": can_insert(var, GH_ParameterSide.Output, 1),
            "canInsertOutputEnd": can_insert(var, GH_ParameterSide.Output, post_count),
            "canInsertInput0": can_insert(var, GH_ParameterSide.Input, 0),
        }
        # Try the adapter's actual append at the end index, CanInsert or not, and record both
        # what CanInsertParameter claimed and what CreateParameter/Register actually did.
        try:
            index = comp.Params.Output.Count
            p = var.CreateParameter(GH_ParameterSide.Output, index)
            created = p is not None
            registered = bool(comp.Params.RegisterOutputParam(p, index)) if created else False
            var.VariableParameterMaintenance()
            comp.Params.OnParametersChanged()
            entry["forcedCreateAtEnd"] = {
                "created": created,
                "registered": registered,
                "after": describe(comp),
            }
        except Exception as ex:  # noqa: BLE001
            entry["forcedCreateError"] = str(ex)
    except Exception as ex:  # noqa: BLE001
        entry["error"] = repr(ex)
    results.append(entry)


try:
    probe("719467e6-7cf5-4848-99b0-c5dd57e5442c", "python3")
    probe("b6ba1144-02d6-4a2d-b53c-ec62e290eeb7", "csharp")

    handle = open(OUT_PATH, "w")
    try:
        json.dump(results, handle, indent=1)
    finally:
        handle.close()
    marker = open(OUT_PATH + ".ok", "w")
    try:
        marker.write("ok")
    finally:
        marker.close()
except Exception:  # noqa: BLE001 - RunPythonScript swallows exceptions; persist them
    err = open(OUT_PATH + ".err", "w")
    try:
        err.write(traceback.format_exc())
    finally:
        err.close()
