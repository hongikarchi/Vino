namespace Vino.AgentHost.Runtime;

/// <summary>
/// The one table of required payload arguments per bridge operation. The preflight validator
/// enforces it and the tool schema renders it, from THIS single source — the 2026-08-27 유수지
/// session measured what the previous split cost: the contract lived only in prose, so a real
/// modeling session lost 21 of its 46 submits to one-field-at-a-time discovery of exactly these
/// lists.
/// </summary>
internal static class OperationPayloadContract
{
    internal static readonly IReadOnlyDictionary<string, string[]> RequiredArguments =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["canvas.move"] = ["operationId", "pivots", "expectedFingerprints"],
            ["canvas.setNumberSlider"] =
                ["operationId", "objectId", "expectedFingerprint", "value", "minimum", "maximum", "decimalPlaces"],
            // kind is required so the payload states which primitive it targets; the adapter checks
            // it against the live object BEFORE writing, so a payload aimed at the wrong component
            // refuses instead of half-applying. The value fields are per-kind and optional.
            ["canvas.setInputValue"] = ["operationId", "objectId", "expectedFingerprint", "kind"],
            ["canvas.setWire"] = ["operationId", "wire", "action", "rejectCycles"],
            // resultOutput is REQUIRED (present, may be null) so the model cannot silently skip
            // declaring whether this create produces a result — a non-null name makes the server
            // attach an outputCountInRange ">=1" that fails an empty producing change.
            ["canvas.create"] = ["operationId", "objectId", "componentTypeId", "pivot", "resultOutput"],
            ["canvas.referenceRhinoObjects"] = ["operationId", "objectId", "rhinoObjectIds", "paramType", "pivot"],
            ["canvas.delete"] = ["operationId", "objectId", "expectedFingerprint"],
            ["canvas.setGroup"] = ["operationId", "groupId", "name", "objectIds", "argbColor"],
            ["python.setSource"] =
                ["operationId", "componentId", "expectedSourceSha256", "source", "runtime", "expireSolution"],
            ["python.setSchema"] = ["operationId", "componentId", "inputs", "outputs", "preserveIncidentWires"],
            ["python.replaceBlock"] =
                ["operationId", "componentId", "expectedSourceSha256", "blockId", "source", "expireSolution"],
            // source/socketMap are optional (null source copies the original's); resultOutput is
            // required-but-nullable exactly like canvas.create — a replacement is a producing
            // create in disguise, so it makes the same produce-or-scaffold decision explicit.
            ["python.replaceSchema"] =
                ["operationId", "componentId", "newComponentId", "inputs", "outputs", "resultOutput"],
            ["python.setTyping"] = ["operationId", "componentId", "inputParameterId", "typeHint", "access"],
            ["python.execute"] = ["operationId", "componentId", "expireUpstream", "recomputeDocument"],
            ["python.runtimeMessages"] = ["componentId"],
            ["python.inspect"] = ["componentId"],
            ["canvas.inspect"] = ["objectId"],
            ["rhino.inspect"] = ["objectId"],
            ["rhino.createPrimitive"] = ["operationId", "objectId", "logicalEntityId", "kind"],
            ["rhino.transform"] = ["operationId", "objectId", "expectedFingerprint", "matrix"],
            ["rhino.upsert"] =
            [
                "operationId", "objectId", "logicalEntityId", "geometryType", "geometryJson",
                "attributesJson", "expectedFingerprint"
            ],
            ["rhino.delete"] = ["operationId", "objectId", "expectedFingerprint"],
            ["rhino.fixEndpointPair"] =
            [
                "operationId", "anchorObjectId", "anchorEnd", "moveObjectId", "moveEnd",
                "expectedAnchorFingerprint", "expectedFingerprint", "tolerance"
            ],
            ["rhino.purgeTableEntries"] = ["operationId", "entries"],
            // layerId is required even for a brand-new layer: the caller picks the identity so the
            // writeSet can declare it with the absent sentinel before it exists.
            ["rhino.ensureLayer"] = ["operationId", "layerId", "fullPath"],
            ["rhino.moveObjectsToLayer"] = ["operationId", "items", "targetLayerId"],
            ["rhino.updateLayer"] = ["operationId", "layerId", "expectedFingerprint"],
            ["rhino.deleteLayer"] = ["operationId", "layerId", "expectedFingerprint"],
            ["rhino.layerState"] = ["operationId", "action", "name"],
        };

    /// <summary>
    /// The table rendered for the tool schema, so the model reads the same contract the validator
    /// enforces — drift-proof because both sides call this class.
    /// </summary>
    internal static string DescribeForSchema()
    {
        var lines = RequiredArguments
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => FormattableString.Invariant($"{pair.Key}: {string.Join(", ", pair.Value)}"));
        return "Required arguments per bridge operation — every listed name must be present " +
            "(resultOutput and a create's expectedFingerprint may be null; nothing may be absent): " +
            string.Join(" | ", lines);
    }
}
