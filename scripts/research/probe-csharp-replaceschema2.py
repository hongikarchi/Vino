# Probe 2 for live-gate 2026-08-13 finding F1: replicate the adapter's FULL replaceSchema
# order on both script runtimes - SetSource -> IScriptObject.ReBuild -> clear GH sockets ->
# read RhinoCodePlatform.GH.IScriptComponent.Inputs/Outputs (the adapter's ReadParameterObjects
# source of truth) - to pin whether the script-parameter model omits the console 'out' (or
# everything) right after a source rebuild, which would make AppendMissingParameters target
# the reserved Output slot 0.
# ASCII-only; errors persisted to '<out>.err' (RunPythonScript swallows exceptions).
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

SCRIPT_COMPONENT_INTERFACE = "RhinoCodePlatform.GH.IScriptComponent"
SCRIPT_PARAMETER_INTERFACE = "RhinoCodePlatform.GH.IScriptParameter"


def find_interface(target, full_name):
    for contract in target.GetType().GetInterfaces():
        if contract.FullName == full_name:
            return contract
    return None


def interface_property(target, interface_name, prop):
    contract = find_interface(target, interface_name)
    if contract is None:
        return None
    info = contract.GetProperty(prop)
    if info is None:
        return None
    return info.GetValue(target)


def script_parameters(comp, prop):
    items = interface_property(comp, SCRIPT_COMPONENT_INTERFACE, prop)
    names = []
    if items is None:
        return ["<interface-or-property-missing>"]
    for item in items:
        if item is None:
            names.append("<null>")
            continue
        variable = interface_property(item, SCRIPT_PARAMETER_INTERFACE, "VariableName")
        gh_name = item.Name if hasattr(item, "Name") else None
        names.append("%s|gh:%s" % (variable, gh_name))
    return names


def gh_names(comp):
    return {
        "inputs": [p.Name for p in comp.Params.Input],
        "outputs": [p.Name for p in comp.Params.Output],
    }


def set_source(comp, source):
    method = comp.GetType().GetMethod("SetSource", System.Array[System.Type]([str]))
    if method is None:
        return "no SetSource(string)"
    method.Invoke(comp, System.Array[System.Object]([source]))
    return "ok"


def rebuild(comp):
    for contract in comp.GetType().GetInterfaces():
        if contract.Name == "IScriptObject":
            method = contract.GetMethod("ReBuild", System.Type.EmptyTypes)
            if method is not None:
                method.Invoke(comp, None)
                return "ok"
    return "no IScriptObject.ReBuild"


def probe(type_guid, label, source):
    entry = {"label": label}
    try:
        emitted = Grasshopper.Instances.ComponentServer.EmitObject(System.Guid(type_guid))
        comp = emitted
        doc = Grasshopper.Kernel.GH_Document()
        doc.AddObject(emitted, True)
        entry["step0_initial"] = {
            "gh": gh_names(comp),
            "scriptInputs": script_parameters(comp, "Inputs"),
            "scriptOutputs": script_parameters(comp, "Outputs"),
        }
        entry["setSource"] = set_source(comp, source)
        entry["rebuild"] = rebuild(comp)
        entry["step1_afterSourceRebuild"] = {
            "gh": gh_names(comp),
            "scriptInputs": script_parameters(comp, "Inputs"),
            "scriptOutputs": script_parameters(comp, "Outputs"),
        }
        for p in list(comp.Params.Input):
            comp.Params.UnregisterInputParameter(p, True)
        for p in list(comp.Params.Output):
            if p.Name == "out":
                continue
            comp.Params.UnregisterOutputParameter(p, True)
        try:
            emitted.VariableParameterMaintenance()
        except Exception as ex:  # noqa: BLE001
            entry["maintenanceError"] = str(ex)
        comp.Params.OnParametersChanged()
        entry["step2_afterClear"] = {
            "gh": gh_names(comp),
            "scriptInputs": script_parameters(comp, "Inputs"),
            "scriptOutputs": script_parameters(comp, "Outputs"),
            "canInsertOutput0": bool(emitted.CanInsertParameter(GH_ParameterSide.Output, 0)),
            "canInsertOutput1": bool(emitted.CanInsertParameter(GH_ParameterSide.Output, 1)),
        }
    except Exception as ex:  # noqa: BLE001
        entry["error"] = repr(ex)
    results.append(entry)


try:
    probe(
        "719467e6-7cf5-4848-99b0-c5dd57e5442c",
        "python3",
        "#! python 3\na = 1\n")
    probe(
        "b6ba1144-02d6-4a2d-b53c-ec62e290eeb7",
        "csharp",
        "// #! csharp\ndouble s = 0.0;\na = s;\n")

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
except Exception:  # noqa: BLE001
    err = open(OUT_PATH + ".err", "w")
    try:
        err.write(traceback.format_exc())
    finally:
        err.close()
