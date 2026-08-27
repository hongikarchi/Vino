using Vino.BridgeContract;

namespace Vino.ScriptAdapter;

public sealed class ScriptBridgeOperationHandler : IBridgeOperationHandler
{
    private readonly IScriptDocumentAdapter _adapter;

    public ScriptBridgeOperationHandler(IScriptDocumentAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public BridgeAdapterOwner Owner => BridgeAdapterOwner.Script;

    public async Task<BridgeOperationResponse> HandleAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireOwner(request);
        request.Validate();

        return request.Operation switch
        {
            "python.inspect" => await InspectAsync(target, request, cancellationToken).ConfigureAwait(false),
            "python.setSource" => await SetSourceAsync(target, request, cancellationToken).ConfigureAwait(false),
            "python.setSchema" => await SetSchemaAsync(target, request, cancellationToken).ConfigureAwait(false),
            "python.replaceSchema" => await ReplaceSchemaAsync(target, request, cancellationToken).ConfigureAwait(false),
            "python.setTyping" => await SetTypingAsync(target, request, cancellationToken).ConfigureAwait(false),
            "python.execute" => await ExecuteAsync(target, request, cancellationToken).ConfigureAwait(false),
            "python.runtimeMessages" => await RuntimeMessagesAsync(target, request, cancellationToken).ConfigureAwait(false),
            _ => throw new BridgeProtocolException(
                "unknown_script_operation",
                $"Unknown script operation '{request.Operation}'."),
        };
    }

    private async Task<BridgeOperationResponse> InspectAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var arguments = request.DeserializeArguments<ComponentIdArguments>();
        var state = await _adapter.ReadPythonComponentAsync(
            target,
            arguments.ComponentId,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            state,
            afterFingerprint: PythonComponentFingerprint.Compute(state));
    }

    private async Task<BridgeOperationResponse> SetSourceAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var before = await ReadExpectedStateAsync(target, request, cancellationToken).ConfigureAwait(false);
        var result = await _adapter.SetSourceAsync(
            target,
            request.DeserializeArguments<SetPythonSourceRequest>(),
            cancellationToken).ConfigureAwait(false);
        return await MutationResponseAsync(target, request, result, before, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<BridgeOperationResponse> SetSchemaAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var before = await ReadExpectedStateAsync(target, request, cancellationToken).ConfigureAwait(false);
        var result = await _adapter.SetParameterSchemaAsync(
            target,
            request.DeserializeArguments<SetParameterSchemaRequest>(),
            cancellationToken).ConfigureAwait(false);
        return await MutationResponseAsync(target, request, result, before, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<BridgeOperationResponse> ReplaceSchemaAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        // The envelope fingerprint gates the REPLACED component (the resource this op consumes);
        // the replacement's fingerprint is the response's after side.
        var before = await ReadExpectedStateAsync(target, request, cancellationToken).ConfigureAwait(false);
        var result = await _adapter.ReplaceParameterSchemaAsync(
            target,
            request.DeserializeArguments<ReplaceParameterSchemaRequest>(),
            cancellationToken).ConfigureAwait(false);
        var after = await _adapter.ReadPythonComponentAsync(
            target,
            result.NewComponentId,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: true,
            result,
            beforeFingerprint: before,
            afterFingerprint: PythonComponentFingerprint.Compute(after),
            diagnostics: ToDiagnostics(result.RuntimeMessages, after.Runtime));
    }

    private async Task<BridgeOperationResponse> SetTypingAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var before = await ReadExpectedStateAsync(target, request, cancellationToken).ConfigureAwait(false);
        var result = await _adapter.SetInputTypingAsync(
            target,
            request.DeserializeArguments<SetInputTypingRequest>(),
            cancellationToken).ConfigureAwait(false);
        return await MutationResponseAsync(target, request, result, before, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<BridgeOperationResponse> ExecuteAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var before = await ReadExpectedStateAsync(target, request, cancellationToken).ConfigureAwait(false);
        var result = await _adapter.ExecuteAsync(
            target,
            request.DeserializeArguments<ExecutePythonComponentRequest>(),
            cancellationToken).ConfigureAwait(false);
        var after = await _adapter.ReadPythonComponentAsync(
            target,
            result.ComponentId,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: true,
            result,
            beforeFingerprint: before,
            afterFingerprint: PythonComponentFingerprint.Compute(after),
            diagnostics: ToDiagnostics(result.RuntimeMessages, after.Runtime));
    }

    private async Task<BridgeOperationResponse> RuntimeMessagesAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var arguments = request.DeserializeArguments<ComponentIdArguments>();
        var report = await _adapter.ReadRuntimeMessagesAsync(
            target,
            arguments.ComponentId,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            report,
            diagnostics: ToDiagnostics(report.Messages, report.Runtime));
    }

    private async Task<string> ReadExpectedStateAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        var componentId = request.Operation switch
        {
            "python.setSource" => request.DeserializeArguments<SetPythonSourceRequest>().ComponentId,
            "python.setSchema" => request.DeserializeArguments<SetParameterSchemaRequest>().ComponentId,
            "python.replaceSchema" => request.DeserializeArguments<ReplaceParameterSchemaRequest>().ComponentId,
            "python.setTyping" => request.DeserializeArguments<SetInputTypingRequest>().ComponentId,
            "python.execute" => request.DeserializeArguments<ExecutePythonComponentRequest>().ComponentId,
            _ => throw new BridgeProtocolException(
                "expected_fingerprint_operation",
                $"Operation '{request.Operation}' does not identify a Python component mutation."),
        };
        var state = await _adapter.ReadPythonComponentAsync(
            target,
            componentId,
            cancellationToken).ConfigureAwait(false);
        var actual = PythonComponentFingerprint.Compute(state);
        if (!string.IsNullOrWhiteSpace(request.ExpectedFingerprint) &&
            !string.Equals(actual, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                "expected_fingerprint_mismatch",
                $"Python component {componentId:D} changed after the request snapshot.");
        }

        return actual;
    }

    private async Task<BridgeOperationResponse> MutationResponseAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        ScriptMutationResult result,
        string beforeFingerprint,
        CancellationToken cancellationToken)
    {
        var componentId = request.Operation switch
        {
            "python.setSource" => request.DeserializeArguments<SetPythonSourceRequest>().ComponentId,
            "python.setSchema" => request.DeserializeArguments<SetParameterSchemaRequest>().ComponentId,
            "python.setTyping" => request.DeserializeArguments<SetInputTypingRequest>().ComponentId,
            _ => throw new BridgeProtocolException(
                "mutation_fingerprint_operation",
                $"Operation '{request.Operation}' does not identify a Python component mutation."),
        };
        var after = await _adapter.ReadPythonComponentAsync(
            target,
            componentId,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            result.Changed,
            result,
            beforeFingerprint,
            PythonComponentFingerprint.Compute(after),
            ToDiagnostics(result.RuntimeMessages, after.Runtime));
    }

    /// <summary>
    /// Turns a component's runtime messages into bridge diagnostics, labelled with the language that
    /// actually produced them.
    /// </summary>
    /// <remarks>
    /// The code used to be <c>python_*</c> for every script component, so a C# compile error arrived
    /// as <c>python_error: Operator '+=' cannot be applied ...</c> and the reader would go looking for
    /// a Python fault that does not exist. The code is a label only — every dispatch decision in the
    /// host keys off <see cref="BridgeDiagnosticSeverity"/>, never off this string — so it costs
    /// nothing to make it true, and it is the reader's first hint about which language to debug.
    /// </remarks>
    private static IReadOnlyList<BridgeDiagnostic> ToDiagnostics(
        IReadOnlyList<ComponentRuntimeMessage> messages,
        PythonRuntime runtime) =>
        messages.Select(message => new BridgeDiagnostic(
            message.Level switch
            {
                RuntimeMessageLevel.Remark => BridgeDiagnosticSeverity.Information,
                RuntimeMessageLevel.Warning => BridgeDiagnosticSeverity.Warning,
                RuntimeMessageLevel.Error => BridgeDiagnosticSeverity.Error,
                _ => throw new ArgumentOutOfRangeException(nameof(message.Level)),
            },
            $"{DiagnosticLanguage(runtime)}_{message.Level.ToString().ToLowerInvariant()}",
            message.Message)).ToArray();

    /// <summary>
    /// The language token in a diagnostic code. IronPython 2 is kept distinct from CPython 3 on
    /// purpose: they fail differently (no f-strings, .NET types), so collapsing them would hand the
    /// reader the same misleading label this method exists to remove.
    /// </summary>
    internal static string DiagnosticLanguage(PythonRuntime runtime) => runtime switch
    {
        PythonRuntime.Cpython3 => "python",
        PythonRuntime.IronPython2 => "ironpython",
        PythonRuntime.Csharp => "csharp",
        _ => throw new ArgumentOutOfRangeException(nameof(runtime), runtime, "Unknown script runtime."),
    };

    private void RequireOwner(BridgeOperationRequest request)
    {
        if (request.Owner != Owner)
        {
            throw new BridgeProtocolException("adapter_owner", "Script handler received another owner's request.");
        }
    }

    private static void RequireAccess(BridgeOperationRequest request, BridgeOperationAccess expected)
    {
        if (request.Access != expected)
        {
            throw new BridgeProtocolException(
                "operation_access",
                $"Operation '{request.Operation}' requires {expected} access.");
        }
    }

    private sealed record ComponentIdArguments(Guid ComponentId);
}
