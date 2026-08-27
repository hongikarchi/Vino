using System.Text.Json;
using Vino.BridgeContract;
using Vino.Contracts;
using Vino.ScriptAdapter;

namespace Vino.AgentHost.Tests;

/// <summary>
/// A script diagnostic must name the language that produced it. The bridge used to stamp
/// <c>python_*</c> on every script component regardless of runtime, so a C# compile error was
/// reported as <c>python_error: Operator '+=' cannot be applied ...</c> and whoever read it went
/// hunting for a Python fault that did not exist (log review 2026-08-26, A12).
/// </summary>
public sealed class ScriptDiagnosticLanguageTests
{
    private static readonly DocumentRuntime Target = DocumentRuntimeTarget.Create(
        Guid.NewGuid(),
        Environment.ProcessId,
        DateTimeOffset.UtcNow.AddMinutes(-1),
        17,
        Guid.NewGuid(),
        Path.Combine(Path.GetTempPath(), "vino-diag.3dm"),
        Path.Combine(Path.GetTempPath(), "vino-diag.gh"));

    [Theory]
    [InlineData(PythonRuntime.Csharp, "csharp_error")]
    [InlineData(PythonRuntime.Cpython3, "python_error")]
    [InlineData(PythonRuntime.IronPython2, "ironpython_error")]
    public async Task ExecuteLabelsTheErrorWithTheComponentsOwnLanguage(
        PythonRuntime runtime,
        string expectedCode)
    {
        var componentId = Guid.NewGuid();
        var handler = new ScriptBridgeOperationHandler(new StubScriptAdapter(runtime, componentId));

        var response = await handler.HandleAsync(
            Target,
            Request(
                "python.execute",
                componentId,
                arguments: new
                {
                    operationId = "op-1",
                    componentId,
                    expireUpstream = false,
                    recomputeDocument = true,
                }));

        var diagnostic = Assert.Single(response.Diagnostics);
        Assert.Equal(BridgeDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Equal(StubScriptAdapter.ErrorText, diagnostic.Message);
    }

    [Fact]
    public async Task SourceWriteLabelsACSharpCompileErrorAsCSharp()
    {
        var componentId = Guid.NewGuid();
        var handler = new ScriptBridgeOperationHandler(
            new StubScriptAdapter(PythonRuntime.Csharp, componentId));

        var response = await handler.HandleAsync(
            Target,
            Request(
                "python.setSource",
                componentId,
                BridgeOperationAccess.Write,
                new
                {
                    operationId = "op-1",
                    componentId,
                    expectedSourceSha256 = string.Empty,
                    source = "int x = 1; x += new object();",
                    runtime = nameof(PythonRuntime.Csharp),
                    expireSolution = true,
                }));

        Assert.Equal("csharp_error", Assert.Single(response.Diagnostics).Code);
    }

    /// <summary>
    /// The runtime-message read carries the language too, so a polled diagnostic cannot disagree
    /// with the same diagnostic seen through an execute.
    /// </summary>
    [Fact]
    public async Task RuntimeMessageReadReportsTheLanguageAlongsideTheMessages()
    {
        var componentId = Guid.NewGuid();
        var handler = new ScriptBridgeOperationHandler(
            new StubScriptAdapter(PythonRuntime.Csharp, componentId));

        var response = await handler.HandleAsync(
            Target,
            Request("python.runtimeMessages", componentId, BridgeOperationAccess.Read));

        Assert.Equal("csharp_error", Assert.Single(response.Diagnostics).Code);
        var report = response.Result.Deserialize<ComponentRuntimeReport>(BridgeProtocol.JsonOptions);
        Assert.NotNull(report);
        Assert.Equal(PythonRuntime.Csharp, report!.Runtime);
        Assert.Equal(StubScriptAdapter.ErrorText, Assert.Single(report.Messages).Message);
    }

    private static BridgeOperationRequest Request(
        string operation,
        Guid componentId,
        BridgeOperationAccess access = BridgeOperationAccess.Write,
        object? arguments = null) =>
        new(
            "op-1",
            BridgeAdapterOwner.Script,
            operation,
            access,
            BaseSnapshotRevision: 1,
            ExpectedFingerprint: null,
            // Write operations are lease-gated at the protocol boundary; the lease is irrelevant to
            // what this test measures, so it carries a placeholder rather than a broker round-trip.
            WriterLeaseToken: access == BridgeOperationAccess.Write ? "lease-token" : null,
            JsonSerializer.SerializeToElement(
                arguments ?? new { componentId },
                BridgeProtocol.JsonOptions));

    /// <summary>
    /// Reports one error message for a component of the configured language. Everything else is the
    /// minimum the handler needs to reach the diagnostic projection.
    /// </summary>
    private sealed class StubScriptAdapter : IScriptDocumentAdapter
    {
        internal const string ErrorText = "Operator '+=' cannot be applied to operands of type 'object' and 'int' [186:5]";

        private static readonly IReadOnlyList<ComponentRuntimeMessage> Messages =
            [new ComponentRuntimeMessage(RuntimeMessageLevel.Error, ErrorText)];

        private readonly PythonRuntime _runtime;
        private readonly Guid _componentId;

        internal StubScriptAdapter(PythonRuntime runtime, Guid componentId)
        {
            _runtime = runtime;
            _componentId = componentId;
        }

        public Task<PythonComponentState> ReadPythonComponentAsync(
            DocumentRuntime target,
            Guid componentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PythonComponentState(
                componentId,
                "// source",
                "sha",
                _runtime,
                [],
                [],
                Messages));

        public Task<ScriptMutationResult> SetSourceAsync(
            DocumentRuntime target,
            SetPythonSourceRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScriptMutationResult(request.OperationId, true, "before", "after", Messages));

        public Task<ScriptMutationResult> SetParameterSchemaAsync(
            DocumentRuntime target,
            SetParameterSchemaRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScriptMutationResult(request.OperationId, true, "before", "after", Messages));

        public Task<ComponentReplacementResult> ReplaceParameterSchemaAsync(
            DocumentRuntime target,
            ReplaceParameterSchemaRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ComponentReplacementResult(
                request.OperationId, _componentId, _componentId, "before", "after", 0, 0, [], Messages));

        public Task<ScriptMutationResult> SetInputTypingAsync(
            DocumentRuntime target,
            SetInputTypingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScriptMutationResult(request.OperationId, true, "before", "after", Messages));

        public Task<PythonExecutionResult> ExecuteAsync(
            DocumentRuntime target,
            ExecutePythonComponentRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PythonExecutionResult(
                request.OperationId, _componentId, false, "outputs", Messages));

        public Task<ComponentRuntimeReport> ReadRuntimeMessagesAsync(
            DocumentRuntime target,
            Guid componentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ComponentRuntimeReport(_runtime, Messages));
    }
}
