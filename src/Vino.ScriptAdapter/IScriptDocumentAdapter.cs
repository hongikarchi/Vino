using Vino.BridgeContract;

namespace Vino.ScriptAdapter;

/// <summary>
/// Owns Python source, parameter schemas and typing, execution, and runtime errors.
/// Canvas topology and layout are deliberately absent; those belong to the Canvas domain.
/// </summary>
public interface IScriptDocumentAdapter
{
    Task<PythonComponentState> ReadPythonComponentAsync(
        DocumentTarget target,
        Guid componentId,
        CancellationToken cancellationToken = default);

    Task<ScriptMutationResult> SetSourceAsync(
        DocumentTarget target,
        SetPythonSourceRequest request,
        CancellationToken cancellationToken = default);

    Task<ScriptMutationResult> SetParameterSchemaAsync(
        DocumentTarget target,
        SetParameterSchemaRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Socket removal by replacement (python.replaceSchema): atomically creates a fresh component of
    /// the original's type, rebuilds its sockets from the declared schema, copies (or sets) the
    /// source, rewires the original's connections onto same-named sockets, deletes the original,
    /// and solves once. The original is never mutated before its final delete, so any failure
    /// before that point rolls back to an untouched document.
    /// </summary>
    Task<ComponentReplacementResult> ReplaceParameterSchemaAsync(
        DocumentTarget target,
        ReplaceParameterSchemaRequest request,
        CancellationToken cancellationToken = default);

    Task<ScriptMutationResult> SetInputTypingAsync(
        DocumentTarget target,
        SetInputTypingRequest request,
        CancellationToken cancellationToken = default);

    Task<PythonExecutionResult> ExecuteAsync(
        DocumentTarget target,
        ExecutePythonComponentRequest request,
        CancellationToken cancellationToken = default);

    Task<ComponentRuntimeReport> ReadRuntimeMessagesAsync(
        DocumentTarget target,
        Guid componentId,
        CancellationToken cancellationToken = default);
}

public sealed record PythonComponentState(
    Guid ComponentId,
    string Source,
    string SourceSha256,
    PythonRuntime Runtime,
    IReadOnlyList<PythonParameter> Inputs,
    IReadOnlyList<PythonParameter> Outputs,
    IReadOnlyList<ComponentRuntimeMessage> RuntimeMessages);

// Historic name: this enum predates C# support and is baked into the bridge payload contract
// ("runtime" on python.setSource). It now covers every Rhino 8 script-component language.
public enum PythonRuntime
{
    Cpython3,
    IronPython2,
    Csharp,
}

public enum ParameterAccess
{
    Item,
    List,
    Tree,
}

public sealed record PythonParameter(
    Guid ParameterId,
    string Name,
    string NickName,
    string TypeHint,
    ParameterAccess Access,
    bool Optional);

public sealed record SetPythonSourceRequest(
    string OperationId,
    Guid ComponentId,
    string ExpectedSourceSha256,
    string Source,
    PythonRuntime Runtime,
    bool ExpireSolution);

public sealed record SetParameterSchemaRequest(
    string OperationId,
    Guid ComponentId,
    IReadOnlyList<PythonParameter> Inputs,
    IReadOnlyList<PythonParameter> Outputs,
    bool PreserveIncidentWires);

public sealed record SetInputTypingRequest(
    string OperationId,
    Guid ComponentId,
    Guid InputParameterId,
    string TypeHint,
    ParameterAccess Access);

/// <summary>
/// Inputs/Outputs are the replacement's COMPLETE socket schema (sockets absent from it are the
/// removals). Source null copies the original's source verbatim. SocketMap maps an original socket
/// name to its declared successor for renames ("oldName" -> "newName"); unmapped original sockets
/// rewire to the same name when it survives, and their wires are dropped (reported) otherwise.
/// </summary>
public sealed record ReplaceParameterSchemaRequest(
    string OperationId,
    Guid ComponentId,
    Guid NewComponentId,
    IReadOnlyList<PythonParameter> Inputs,
    IReadOnlyList<PythonParameter> Outputs,
    string? Source = null,
    IReadOnlyDictionary<string, string>? SocketMap = null,
    // Consumed SERVER-side (the auto-attached outputCountInRange predicate); carried here only
    // because the payload requires the field and the bridge deserializes with Disallow-unmapped —
    // without this member every python.replaceSchema died in DeserializeArguments.
    string? ResultOutput = null);

public sealed record ExecutePythonComponentRequest(
    string OperationId,
    Guid ComponentId,
    bool ExpireUpstream,
    bool RecomputeDocument);

/// <param name="PreviousSource">
/// The source text this write replaced, when the operation replaced one. The adapter reads the
/// component's pre-write state anyway (to gate on the expected fingerprint and to roll back in
/// place), so carrying the text out costs nothing, and it is the only moment it exists: once the
/// write lands, the old text is gone from the document forever. The managed history stores it so a
/// rewind can put a script back, which a fingerprint alone can never do.
///
/// This never reaches the model. The host consumes the operation result for fingerprints and
/// history capture and then drops it; no job projection, tool response, or commit message carries
/// it.
/// </param>
/// <param name="Source">
/// The source the component holds AFTER the write — which is not always the text the caller sent:
/// the adapter normalises a script's language directive on the way in. The history stores what the
/// component actually holds, so a later restore compares like with like instead of re-writing a
/// component that already has the right code.
/// </param>
public sealed record ScriptMutationResult(
    string OperationId,
    bool Changed,
    string BeforeFingerprint,
    string AfterFingerprint,
    IReadOnlyList<ComponentRuntimeMessage> RuntimeMessages,
    string? PreviousSource = null,
    string? Source = null);

/// <summary>
/// BeforeFingerprint is the REPLACED component's pre-op Python-state fingerprint; AfterFingerprint
/// is the REPLACEMENT's. DroppedWires lists original connections that had no surviving socket to
/// rewire onto (human-readable, for the job report).
/// </summary>
public sealed record ComponentReplacementResult(
    string OperationId,
    Guid OldComponentId,
    Guid NewComponentId,
    string BeforeFingerprint,
    string AfterFingerprint,
    int RewiredInputs,
    int RewiredOutputs,
    IReadOnlyList<string> DroppedWires,
    IReadOnlyList<ComponentRuntimeMessage> RuntimeMessages);

public sealed record PythonExecutionResult(
    string OperationId,
    Guid ComponentId,
    bool Solved,
    string OutputFingerprint,
    IReadOnlyList<ComponentRuntimeMessage> RuntimeMessages);

public sealed record ComponentRuntimeMessage(
    RuntimeMessageLevel Level,
    string Message);

/// <summary>
/// Runtime messages together with the language that produced them. The language is not decoration:
/// a diagnostic labelled for the wrong language sends the reader down the wrong debugging path, and
/// a C# compile error announced as a Python error is exactly the failure this pairing prevents.
/// </summary>
public sealed record ComponentRuntimeReport(
    PythonRuntime Runtime,
    IReadOnlyList<ComponentRuntimeMessage> Messages);

public enum RuntimeMessageLevel
{
    Remark,
    Warning,
    Error,
}
