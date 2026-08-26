using System.Text.Json;
using Vino.AgentHost.Api;
using Vino.BridgeContract;
using Vino.Contracts;
using Vino.CanvasSceneAdapter;
using Vino.ScriptAdapter;

namespace Vino.AgentHost.Tests;

[Collection(LiveDocumentBackendCollection.Name)]
public sealed class DeterministicScriptFailureTests
{
    private const string InitialFingerprint = "python-f0";

    [Fact]
    public async Task PythonRuntimeErrorFailsDeterministicallyWithAppliedView()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.Canvas,
                BridgeAdapterOwner.Script
            ]);
        await using var responder = harness.StartResponder(responseFactory: request => request.Operation switch
        {
            "python.inspect" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: false,
                new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                afterFingerprint: InitialFingerprint),
            "python.execute" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { solved = false },
                beforeFingerprint: InitialFingerprint,
                afterFingerprint: "python-f1",
                diagnostics:
                [
                    new BridgeDiagnostic(
                        BridgeDiagnosticSeverity.Error,
                        "python_error",
                        "NameError: name 'pt' is not defined")
                ]),
            _ => null
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Runtime error"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "execute.json",
            new
            {
                bridgeOperation = "python.execute",
                arguments = new
                {
                    operationId = "execute-script",
                    componentId = harness.CanvasObjectId,
                    expireUpstream = false,
                    recomputeDocument = false
                }
            });
        // acceptancePredicates deliberately empty: the server must attach the default
        // runtimeErrorAbsent predicate for a script write.
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [],
            [new ResourceExpectation(resource, InitialFingerprint)],
            [
                new TypedOperation(
                    "execute-script",
                    OperationKind.ExecutePython,
                    AdapterOwner.Script,
                    [],
                    [resource],
                    Reversible: true,
                    artifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "runtime-error"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        // Deterministic failure: writes landed, loop completed — Failed, never RecoveryRequired.
        Assert.Equal("failed", state);
        Assert.Equal(JsonValueKind.Null, jobView.GetProperty("committed").ValueKind);
        var applied = jobView.GetProperty("applied");
        Assert.Equal(JsonValueKind.Object, applied.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(applied.GetProperty("snapshotId").GetString()));
        var appliedResource = Assert.Single(applied.GetProperty("resources").EnumerateArray());
        Assert.Equal(harness.CanvasObjectId.ToString("D"), appliedResource.GetProperty("id").GetString());
        var diagnostic = Assert.Single(
            jobView.GetProperty("diagnostics").EnumerateArray(),
            item => item.GetProperty("severity").GetString() == "error");
        Assert.Equal("execute-script", diagnostic.GetProperty("operationId").GetString());
        Assert.Equal("python_error", diagnostic.GetProperty("code").GetString());
        var message = jobView.GetProperty("message").GetString();
        Assert.Contains("execute-script", message, StringComparison.Ordinal);
        // The server-attached default predicate also reports, proving predicate defaulting ran.
        Assert.Contains("gptino:default", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompileErrorContinuesRemainingScriptOperations()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.Canvas,
                BridgeAdapterOwner.Script
            ]);
        await using var responder = harness.StartResponder(responseFactory: request => request.Operation switch
        {
            "python.inspect" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: false,
                new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                afterFingerprint: InitialFingerprint),
            "python.setSource" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { applied = true },
                beforeFingerprint: InitialFingerprint,
                afterFingerprint: "python-f1",
                diagnostics:
                [
                    new BridgeDiagnostic(
                        BridgeDiagnosticSeverity.Error,
                        "python_error",
                        "SyntaxError: invalid syntax")
                ]),
            "python.execute" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { solved = true },
                beforeFingerprint: "python-f1",
                afterFingerprint: "python-f2"),
            _ => null
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Compile error"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var sourceResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentSource,
            harness.CanvasObjectId.ToString("D"));
        var valueResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var sourceArtifact = await harness.WritePayloadAsync(
            session,
            "source.json",
            new
            {
                bridgeOperation = "python.setSource",
                arguments = new
                {
                    operationId = "set-source",
                    componentId = harness.CanvasObjectId,
                    expectedSourceSha256 = "source-v0",
                    source = "def broken(:",
                    runtime = PythonRuntime.Cpython3,
                    expireSolution = false
                }
            });
        var executeArtifact = await harness.WritePayloadAsync(
            session,
            "execute.json",
            new
            {
                bridgeOperation = "python.execute",
                arguments = new
                {
                    operationId = "execute-script",
                    componentId = harness.CanvasObjectId,
                    expireUpstream = false,
                    recomputeDocument = false
                }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [],
            [
                new ResourceExpectation(sourceResource, InitialFingerprint),
                new ResourceExpectation(valueResource, InitialFingerprint)
            ],
            [
                new TypedOperation(
                    "set-source",
                    OperationKind.UpdatePythonSource,
                    AdapterOwner.Script,
                    [],
                    [sourceResource],
                    Reversible: true,
                    sourceArtifact),
                new TypedOperation(
                    "execute-script",
                    OperationKind.ExecutePython,
                    AdapterOwner.Script,
                    [],
                    [valueResource],
                    Reversible: true,
                    executeArtifact)
            ],
            [new VerificationPredicate("No runtime errors", PredicateKind.RuntimeErrorAbsent, null, null)],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "compile-error"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        // The compile error did NOT abort the loop: both script operations were dispatched, so
        // the after-snapshot reflects the complete application.
        Assert.Equal(["set-source", "execute-script"], responder.WriteOperationIds);
        Assert.Equal("failed", state);
        Assert.Equal(JsonValueKind.Object, jobView.GetProperty("applied").ValueKind);
        Assert.Contains(
            jobView.GetProperty("diagnostics").EnumerateArray(),
            item => item.GetProperty("operationId").GetString() == "set-source" &&
                item.GetProperty("code").GetString() == "python_error");
    }

    [Fact]
    public async Task SchemaCompileErrorFailsDeterministicallyWithAppliedView()
    {
        // Live round R3: a staged compile error surfaces on the setComponentIo response because
        // the schema write triggers the solve. It must be an iterable Failed, not RecoveryRequired.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.Canvas,
                BridgeAdapterOwner.Script
            ]);
        await using var responder = harness.StartResponder(responseFactory: request => request.Operation switch
        {
            "python.inspect" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: false,
                new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                afterFingerprint: InitialFingerprint),
            "python.setSchema" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { applied = true },
                beforeFingerprint: InitialFingerprint,
                afterFingerprint: "python-f1",
                diagnostics:
                [
                    new BridgeDiagnostic(
                        BridgeDiagnosticSeverity.Error,
                        "python_error",
                        "The name 'missingOffset' does not exist in the current context")
                ]),
            _ => null
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Schema error"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentIo,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "schema.json",
            new
            {
                bridgeOperation = "python.setSchema",
                arguments = new
                {
                    operationId = "set-schema",
                    componentId = harness.CanvasObjectId,
                    inputs = new[] { new { name = "spacing", access = "item", typeHint = "double" } },
                    outputs = new[] { new { name = "pts", access = "list", typeHint = "point3d" } },
                    preserveIncidentWires = true
                }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [],
            [new ResourceExpectation(resource, InitialFingerprint)],
            [
                new TypedOperation(
                    "set-schema",
                    OperationKind.SetComponentIo,
                    AdapterOwner.Script,
                    [],
                    [resource],
                    Reversible: true,
                    artifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "schema-error"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        Assert.Equal("failed", state);
        Assert.Equal(JsonValueKind.Object, jobView.GetProperty("applied").ValueKind);
        Assert.Contains(
            jobView.GetProperty("diagnostics").EnumerateArray(),
            item => item.GetProperty("operationId").GetString() == "set-schema" &&
                item.GetProperty("code").GetString() == "python_error");
    }

    [Fact]
    public async Task RemovingAWiredSocketFailsBeforeAnyWriteInsteadOfRecoveryRequired()
    {
        // The component has two live inputs and 'y' still carries a wire; a schema declaring only 'x'
        // would cut it. That must be rejected BEFORE the source write lands (clean Failed, never
        // RecoveryRequired) — the original incident was the adapter throwing at execute time, after
        // the same ChangeSet's source write had already landed, which dead-ended the job.
        //
        // Dropping an UNWIRED socket is legal and is covered by
        // RemovingAnUnwiredSocketIsAllowed below: nothing refers to the parameter it destroys, and
        // re-declaring the socket puts it back. 8 of the 17 removals refused in the 07-21..08-26
        // corpus were factory-default sockets on a component the session had just created.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.Canvas,
                BridgeAdapterOwner.Script
            ]);
        var writeOps = new List<string>();
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (request.Access == BridgeOperationAccess.Write)
            {
                lock (writeOps) { writeOps.Add(request.Operation); }
            }
            return request.Operation == "canvas.snapshot"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    TwoInputScriptSnapshot(harness, wireSecondInput: true))
                : null;
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Socket removal"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var sourceResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentSource,
            harness.CanvasObjectId.ToString("D"));
        var ioResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentIo,
            harness.CanvasObjectId.ToString("D"));
        var sourceArtifact = await harness.WritePayloadAsync(
            session,
            "shrink-source.json",
            new
            {
                bridgeOperation = "python.setSource",
                arguments = new
                {
                    operationId = "shrink-source",
                    componentId = harness.CanvasObjectId,
                    expectedSourceSha256 = "gptino:auto",
                    source = "a = x",
                    runtime = PythonRuntime.Cpython3,
                    expireSolution = false
                }
            });
        var schemaArtifact = await harness.WritePayloadAsync(
            session,
            "shrink-schema.json",
            new
            {
                bridgeOperation = "python.setSchema",
                arguments = new
                {
                    operationId = "shrink-schema",
                    componentId = harness.CanvasObjectId,
                    inputs = new[] { new { name = "x", access = "item", typeHint = "double" } },
                    outputs = new[] { new { name = "a", access = "item", typeHint = "double" } },
                    preserveIncidentWires = true
                }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [],
            [
                new ResourceExpectation(sourceResource, InitialFingerprint),
                new ResourceExpectation(ioResource, InitialFingerprint)
            ],
            [
                new TypedOperation("shrink-source", OperationKind.UpdatePythonSource, AdapterOwner.Script, [], [sourceResource], true, sourceArtifact),
                new TypedOperation("shrink-schema", OperationKind.SetComponentIo, AdapterOwner.Script, [], [ioResource], true, schemaArtifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "socket-removal"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        Assert.Equal("failed", state);
        var message = jobView.GetProperty("message").GetString()!;
        Assert.Contains("still connected", message, StringComparison.Ordinal);
        // Name the socket and its wire count, or the caller cannot tell which connection is in the way.
        Assert.Contains("'y'", message, StringComparison.Ordinal);
        // Point at the way out rather than leaving "no" as the whole answer.
        Assert.Contains("disconnectWire", message, StringComparison.Ordinal);
        // The preflight runs before the write loop, so no source write reached the bridge.
        lock (writeOps)
        {
            Assert.DoesNotContain("python.setSource", writeOps);
        }
    }

    [Fact]
    public async Task RemovingAnUnwiredSocketIsAllowed()
    {
        // Same shape, but nothing is attached to 'y'. Removing it destroys a parameter instance
        // nothing refers to and is undone by re-declaring the socket, so the preflight lets it
        // through and the schema write reaches the bridge.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.Canvas,
                BridgeAdapterOwner.Script
            ]);
        var writeOps = new List<string>();
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (request.Access == BridgeOperationAccess.Write)
            {
                lock (writeOps) { writeOps.Add(request.Operation); }
            }
            return request.Operation == "canvas.snapshot"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    TwoInputScriptSnapshot(harness))
                : null;
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Socket removal"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var ioResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentIo,
            harness.CanvasObjectId.ToString("D"));
        var schemaArtifact = await harness.WritePayloadAsync(
            session,
            "shrink-unwired.json",
            new
            {
                bridgeOperation = "python.setSchema",
                arguments = new
                {
                    operationId = "shrink-schema",
                    componentId = harness.CanvasObjectId,
                    inputs = new[] { new { name = "x", access = "item", typeHint = "double" } },
                    outputs = new[] { new { name = "a", access = "item", typeHint = "double" } },
                    preserveIncidentWires = true
                }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [],
            [new ResourceExpectation(ioResource, InitialFingerprint)],
            [
                new TypedOperation("shrink-schema", OperationKind.SetComponentIo, AdapterOwner.Script, [], [ioResource], true, schemaArtifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "socket-removal-unwired"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        await harness.WaitForJobStateAsync(jobId);

        // The preflight did not stand in the way: the schema write reached the bridge.
        lock (writeOps)
        {
            Assert.Contains("python.setSchema", writeOps);
        }
    }

    [Fact]
    public async Task VerifiedRollbackWithNoCompletedOpsFailsDeterministically()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.Canvas,
                BridgeAdapterOwner.Script
            ]);
        await using var responder = harness.StartResponder(
            responseFactory: request => request.Operation == "python.inspect"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                    afterFingerprint: InitialFingerprint)
                : null,
            failureFactory: request => request.Operation == "python.setSource"
                ? new BridgeFailure(
                    "mutation_rolled_back",
                    "RhinoCode did not retain the requested executable source. The component was " +
                    "verifiably restored to its pre-write source — fix the payload and resubmit.",
                    Retryable: true)
                : null);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Verified rollback"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var sourceResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentSource,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "rolled-back-source.json",
            new
            {
                bridgeOperation = "python.setSource",
                arguments = new
                {
                    operationId = "rolled-back-source",
                    componentId = harness.CanvasObjectId,
                    expectedSourceSha256 = "gptino:auto",
                    source = "a = x",
                    runtime = PythonRuntime.Cpython3,
                    expireSolution = false
                }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [],
            [new ResourceExpectation(sourceResource, InitialFingerprint)],
            [
                new TypedOperation(
                    "rolled-back-source",
                    OperationKind.UpdatePythonSource,
                    AdapterOwner.Script,
                    [],
                    [sourceResource],
                    Reversible: true,
                    artifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "rolled-back-source"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var message = (await harness.ReadJobViewAsync(jobId)).GetProperty("message").GetString();

        // The adapter PROVED the rollback restored the pre-write source and no sibling op landed:
        // deterministic Failed the session can iterate on, never a RecoveryRequired review.
        Assert.Equal("failed", state);
        Assert.Contains("verifiably restored", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown outcome", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifiedRollbackAfterACompletedOpStaysRecoveryRequired()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.Canvas,
                BridgeAdapterOwner.Script
            ]);
        await using var responder = harness.StartResponder(
            responseFactory: request => request.Operation switch
            {
                "python.inspect" => BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                    afterFingerprint: InitialFingerprint),
                "python.setSource" => BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: true,
                    new { applied = true },
                    beforeFingerprint: InitialFingerprint,
                    afterFingerprint: "python-f1"),
                _ => null
            },
            failureFactory: request => request.Operation == "python.execute"
                ? new BridgeFailure(
                    "mutation_rolled_back",
                    "The execute failed and was verifiably rolled back.",
                    Retryable: true)
                : null);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Batch rollback"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var sourceResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentSource,
            harness.CanvasObjectId.ToString("D"));
        var valueResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var sourceArtifact = await harness.WritePayloadAsync(
            session,
            "batch-source.json",
            new
            {
                bridgeOperation = "python.setSource",
                arguments = new
                {
                    operationId = "set-source",
                    componentId = harness.CanvasObjectId,
                    expectedSourceSha256 = "gptino:auto",
                    source = "a = x",
                    runtime = PythonRuntime.Cpython3,
                    expireSolution = false
                }
            });
        var executeArtifact = await harness.WritePayloadAsync(
            session,
            "batch-execute.json",
            new
            {
                bridgeOperation = "python.execute",
                arguments = new
                {
                    operationId = "execute-script",
                    componentId = harness.CanvasObjectId,
                    expireUpstream = false,
                    recomputeDocument = false
                }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [],
            [
                new ResourceExpectation(sourceResource, InitialFingerprint),
                new ResourceExpectation(valueResource, InitialFingerprint)
            ],
            [
                new TypedOperation(
                    "set-source",
                    OperationKind.UpdatePythonSource,
                    AdapterOwner.Script,
                    [],
                    [sourceResource],
                    Reversible: true,
                    sourceArtifact),
                new TypedOperation(
                    "execute-script",
                    OperationKind.ExecutePython,
                    AdapterOwner.Script,
                    [],
                    [valueResource],
                    Reversible: true,
                    executeArtifact)
            ],
            [new VerificationPredicate("No runtime errors", PredicateKind.RuntimeErrorAbsent, null, null)],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "batch-rollback"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var message = (await harness.ReadJobViewAsync(jobId)).GetProperty("message").GetString();

        // The earlier source write LANDED: even a verified rollback of the second op cannot make
        // the batch a clean Failed — the document must still be reviewed. But the manifest stops
        // claiming the rolled-back op's outcome is unknown — and it must say the op DID write
        // and was rolled back, never "refused before write" (that label is a lie for a rollback).
        Assert.Equal("recoveryrequired", state);
        Assert.Contains("Applied: set-source", message, StringComparison.Ordinal);
        Assert.Contains(
            "execute-script (write rolled back — no net change)",
            message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("refused before write", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifiedRollbackAfterACompletedReadStillFailsDeterministically()
    {
        // A ChangeSet may legally carry read operations. A completed READ proves nothing changed,
        // so a verified rollback of the only WRITE must still classify as a deterministic Failed
        // — not RecoveryRequired just because completedOperationIds is non-empty.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.Canvas,
                BridgeAdapterOwner.Script
            ]);
        await using var responder = harness.StartResponder(
            responseFactory: request => request.Operation == "python.inspect"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                    afterFingerprint: InitialFingerprint)
                : null,
            failureFactory: request => request.Operation == "python.setSource"
                ? new BridgeFailure(
                    "mutation_rolled_back",
                    "RhinoCode did not retain the requested executable source. The component was " +
                    "verifiably restored to its pre-write source — fix the payload and resubmit.",
                    Retryable: true)
                : null);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Read then rollback"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var sourceResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentSource,
            harness.CanvasObjectId.ToString("D"));
        var inspectArtifact = await harness.WritePayloadAsync(
            session,
            "read-inspect.json",
            new
            {
                bridgeOperation = "python.inspect",
                arguments = new
                {
                    componentId = harness.CanvasObjectId
                }
            });
        var sourceArtifact = await harness.WritePayloadAsync(
            session,
            "read-then-rolled-back-source.json",
            new
            {
                bridgeOperation = "python.setSource",
                arguments = new
                {
                    operationId = "rolled-back-source",
                    componentId = harness.CanvasObjectId,
                    expectedSourceSha256 = "gptino:auto",
                    source = "a = x",
                    runtime = PythonRuntime.Cpython3,
                    expireSolution = false
                }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [new ResourceExpectation(sourceResource, InitialFingerprint)],
            [new ResourceExpectation(sourceResource, InitialFingerprint)],
            [
                new TypedOperation(
                    "inspect-source",
                    OperationKind.Read,
                    AdapterOwner.Script,
                    [sourceResource],
                    [],
                    Reversible: true,
                    inspectArtifact),
                new TypedOperation(
                    "rolled-back-source",
                    OperationKind.UpdatePythonSource,
                    AdapterOwner.Script,
                    [],
                    [sourceResource],
                    Reversible: true,
                    sourceArtifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "read-then-rollback"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var message = (await harness.ReadJobViewAsync(jobId)).GetProperty("message").GetString();

        // The read completed, but no WRITE landed and the rollback was verified: deterministic
        // Failed the session can iterate on, never a RecoveryRequired review.
        Assert.Equal("failed", state);
        Assert.Contains("verifiably restored", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown outcome", message, StringComparison.Ordinal);
    }

    private static CanvasSnapshot TwoInputScriptSnapshot(
        LiveDocumentBackendHarness harness,
        bool wireSecondInput = false)
    {
        CanvasParameterState Param(string name, CanvasParameterDirection direction) => new(
            harness.CanvasObjectId,
            Guid.NewGuid(),
            name,
            name,
            direction,
            "System.Object",
            "object",
            CanvasParameterAccess.Item,
            Optional: false,
            Array.Empty<CanvasParameterEndpoint>());
        var x = Param("x", CanvasParameterDirection.Input);
        // 'y' is the socket the shrink drops. With a wire on it the removal must be refused; without
        // one it is a legal, reversible removal.
        var upstream = Guid.NewGuid();
        var y = wireSecondInput
            ? Param("y", CanvasParameterDirection.Input) with
            {
                CurrentSources = [new CanvasParameterEndpoint(upstream, upstream)],
            }
            : Param("y", CanvasParameterDirection.Input);
        var component = new CanvasObjectState(
            harness.CanvasObjectId,
            Guid.Parse("719467e6-7cf5-4848-99b0-c5dd57e5442c"),
            "Script",
            new CanvasPoint(10, 20),
            new CanvasSize(90, 40),
            InitialFingerprint)
        {
            Inputs = [x, y],
            Outputs = [Param("a", CanvasParameterDirection.Output)],
            StructureFingerprint = InitialFingerprint,
            LayoutFingerprint = "layout-2in",
        };
        return new CanvasSnapshot(
            harness.Target.GrasshopperDocumentId!.Value,
            "two-input-document-v1",
            [component],
            wireSecondInput
                ? [new WireState(upstream, upstream, harness.CanvasObjectId, y.ParameterId)]
                : Array.Empty<WireState>(),
            Array.Empty<GroupState>());
    }

    [Fact]
    public async Task NonScriptErrorDiagnosticStillAborts()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        await using var responder = harness.StartResponder(responseFactory: request => request.Operation switch
        {
            "canvas.setNumberSlider" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { applied = false },
                beforeFingerprint: harness.ObjectFingerprint,
                afterFingerprint: "slider-after",
                diagnostics:
                [
                    new BridgeDiagnostic(
                        BridgeDiagnosticSeverity.Error,
                        "slider_error",
                        "Value could not be applied.")
                ]),
            _ => null
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Slider error"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "slider.json",
            new
            {
                bridgeOperation = "canvas.setNumberSlider",
                arguments = new
                {
                    operationId = "bad-slider",
                    objectId = harness.CanvasObjectId,
                    expectedFingerprint = harness.ObjectFingerprint,
                    value = 10m,
                    minimum = 0m,
                    maximum = 100m,
                    decimalPlaces = 0
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "bad-slider",
                OperationKind.SetValue,
                AdapterOwner.Canvas,
                [],
                [resource],
                Reversible: true,
                artifact),
            [new ResourceExpectation(resource, harness.ObjectFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "bad-slider"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        // A non-script Error diagnostic means the operation itself failed: the loop aborts and
        // the outcome stays RecoveryRequired (a live write may have landed in unknown shape).
        Assert.Equal("recoveryrequired", state);
        Assert.Equal(JsonValueKind.Null, jobView.GetProperty("applied").ValueKind);
        Assert.Contains(
            "slider_error",
            jobView.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmittedPredicatesGetServerDefaultsAndCommit()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Default predicates"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "default-slider.json",
            new
            {
                bridgeOperation = "canvas.setNumberSlider",
                arguments = new
                {
                    operationId = "default-slider",
                    objectId = harness.CanvasObjectId,
                    expectedFingerprint = harness.ObjectFingerprint,
                    value = 30m,
                    minimum = 0m,
                    maximum = 100m,
                    decimalPlaces = 0
                }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            [],
            [],
            [new ResourceExpectation(resource, harness.ObjectFingerprint)],
            [
                new TypedOperation(
                    "default-slider",
                    OperationKind.SetValue,
                    AdapterOwner.Canvas,
                    [],
                    [resource],
                    Reversible: true,
                    artifact)
            ],
            [],
            [],
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "default-predicates"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        Assert.True(state == "committed", jobView.GetProperty("message").GetString());
    }

    private static JsonElement Submission(
        ChangeSet changeSet,
        string snapshotId,
        string idempotencyKey) =>
        JsonSerializer.SerializeToElement(
            new
            {
                changeSet,
                expectedSnapshotId = snapshotId,
                idempotencyKey,
                summary = "Deterministic script failure regression"
            },
            BridgeProtocol.JsonOptions);

    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);
}
