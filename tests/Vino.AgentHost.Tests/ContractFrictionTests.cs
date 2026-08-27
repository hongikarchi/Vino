using System.Text.Json;
using Vino.AgentHost.Api;
using Vino.BridgeContract;
using Vino.Contracts;
using Vino.CanvasSceneAdapter;

namespace Vino.AgentHost.Tests;

/// <summary>
/// The submit contract's ergonomics, pinned against the failure shape a real session measured
/// (유수지, 2026-08-27: 21 of 46 submits bounced on ceremony — one missing field per round trip,
/// near-miss bridgeOperation names, invented UUIDs). The server now derives everything derivable
/// and reports every violation it can see at once.
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class ContractFrictionTests
{
    /// <summary>
    /// The minimal submission: inline payload, no bridgeOperation, no changeSetId, no
    /// idempotencyKey, no expectedSnapshotId, no dependencies/readSet/predicates/beforeImages/
    /// createdAt. Everything omitted is derived, and the job queues.
    /// </summary>
    [Fact]
    public async Task MinimalSubmissionWithInlinePayloadQueues()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Minimal submission"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var groupId = Guid.NewGuid();
        var resource = new ResourceAddress(ResourceKind.GrasshopperGroup, groupId.ToString("D"));

        var submission = JsonSerializer.SerializeToElement(
            new
            {
                changeSet = new
                {
                    projectId = harness.Target.ProjectId,
                    sessionId = session.Id,
                    baseSnapshotRevision = snapshot.Revision,
                    writeSet = new[]
                    {
                        new { resource = new { kind = "grasshopperGroup", id = groupId.ToString("D"), field = "*" }, expectedFingerprint = ResourceExpectation.AbsentFingerprint }
                    },
                    operations = new object[]
                    {
                        new
                        {
                            operationId = "inline-group",
                            kind = "setGroup",
                            owner = "canvas",
                            reads = Array.Empty<object>(),
                            writes = new[] { new { kind = "grasshopperGroup", id = groupId.ToString("D"), field = "*" } },
                            reversible = true,
                            // No bridgeOperation and no payloadArtifact: the payload rides inline
                            // and the operation name is derived from the kind.
                            payload = new
                            {
                                arguments = new
                                {
                                    operationId = "inline-group",
                                    groupId,
                                    name = "Inline group",
                                    objectIds = new[] { harness.CanvasObjectId },
                                    argbColor = -16_777_216
                                }
                            }
                        }
                    }
                },
                summary = "Minimal inline submission"
            },
            BridgeProtocol.JsonOptions);

        var submitted = JsonSerializer.SerializeToElement(
            await harness.Backend.SubmitChangeAsync(session, submission, CancellationToken.None),
            typeof(object),
            BridgeProtocol.JsonOptions);

        Assert.Equal("queued", submitted.GetProperty("state").GetString());
        Assert.Single(harness.Backend.ReadQueue());
        // The server minted the id the model no longer has to invent...
        Assert.True(Guid.TryParse(submitted.GetProperty("changeSetId").GetString(), out var mintedId));
        Assert.NotEqual(Guid.Empty, mintedId);
        // ...and materialized the inline payload into a content-addressed session artifact.
        var sessionRoot = Path.Combine(
            harness.Options.ResolveDataDirectory(),
            "artifacts",
            session.Id.ToString("N"));
        var inline = Assert.Single(Directory.GetFiles(sessionRoot, "inline-inline-group-*.json"));
        using var stored = JsonDocument.Parse(await File.ReadAllBytesAsync(inline));
        Assert.Equal(
            groupId,
            stored.RootElement.GetProperty("arguments").GetProperty("groupId").GetGuid());
    }

    /// <summary>
    /// One refusal names EVERY missing argument plus the full requirement — the measured
    /// alternative was discovering them one bounce at a time, eight bounces in a row.
    /// </summary>
    [Fact]
    public async Task MissingArgumentsAreReportedTogetherWithTheFullRequirement()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Batched violations"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var groupId = Guid.NewGuid();

        var submission = JsonSerializer.SerializeToElement(
            new
            {
                changeSet = new
                {
                    projectId = harness.Target.ProjectId,
                    sessionId = session.Id,
                    baseSnapshotRevision = snapshot.Revision,
                    writeSet = new[]
                    {
                        new { resource = new { kind = "grasshopperGroup", id = groupId.ToString("D"), field = "*" }, expectedFingerprint = ResourceExpectation.AbsentFingerprint }
                    },
                    operations = new object[]
                    {
                        new
                        {
                            operationId = "sparse-group",
                            kind = "setGroup",
                            owner = "canvas",
                            reads = Array.Empty<object>(),
                            writes = new[] { new { kind = "grasshopperGroup", id = groupId.ToString("D"), field = "*" } },
                            reversible = true,
                            // groupId/name/objectIds/argbColor all absent — the refusal must name
                            // all four at once, not the first alone.
                            payload = new { arguments = new { operationId = "sparse-group" } }
                        }
                    }
                },
                summary = "Sparse payload"
            },
            BridgeProtocol.JsonOptions);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(session, submission, CancellationToken.None));

        Assert.Contains("'groupId'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'name'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'objectIds'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'argbColor'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'canvas.setGroup' requires:", exception.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Backend.ReadQueue());
    }

    /// <summary>
    /// A dependency the scheduler has never seen would wait forever with no diagnostic — the one
    /// silent failure mode this codebase had. It is refused at the door instead.
    /// </summary>
    [Fact]
    public async Task UnknownDependencyIsRefusedAtSubmit()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Phantom dependency"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var groupId = Guid.NewGuid();
        var phantom = Guid.NewGuid();

        var submission = JsonSerializer.SerializeToElement(
            new
            {
                changeSet = new
                {
                    projectId = harness.Target.ProjectId,
                    sessionId = session.Id,
                    baseSnapshotRevision = snapshot.Revision,
                    dependencies = new[] { phantom },
                    writeSet = new[]
                    {
                        new { resource = new { kind = "grasshopperGroup", id = groupId.ToString("D"), field = "*" }, expectedFingerprint = ResourceExpectation.AbsentFingerprint }
                    },
                    operations = new object[]
                    {
                        new
                        {
                            operationId = "dependent-group",
                            kind = "setGroup",
                            owner = "canvas",
                            reads = Array.Empty<object>(),
                            writes = new[] { new { kind = "grasshopperGroup", id = groupId.ToString("D"), field = "*" } },
                            reversible = true,
                            payload = new
                            {
                                arguments = new
                                {
                                    operationId = "dependent-group",
                                    groupId,
                                    name = "Dependent group",
                                    objectIds = new[] { harness.CanvasObjectId },
                                    argbColor = 0
                                }
                            }
                        }
                    }
                },
                summary = "Depends on a phantom"
            },
            BridgeProtocol.JsonOptions);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(session, submission, CancellationToken.None));

        Assert.Contains(phantom.ToString("D"), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wait forever", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Backend.ReadQueue());
    }

    /// <summary>
    /// A present-but-wrong bridgeOperation still refuses (it signals real confusion), and the
    /// refusal teaches the fix: the field may simply be omitted.
    /// </summary>
    [Fact]
    public async Task MismatchedBridgeOperationTeachesThatItMayBeOmitted()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Near-miss name"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var groupId = Guid.NewGuid();

        var submission = JsonSerializer.SerializeToElement(
            new
            {
                changeSet = new
                {
                    projectId = harness.Target.ProjectId,
                    sessionId = session.Id,
                    baseSnapshotRevision = snapshot.Revision,
                    writeSet = new[]
                    {
                        new { resource = new { kind = "grasshopperGroup", id = groupId.ToString("D"), field = "*" }, expectedFingerprint = ResourceExpectation.AbsentFingerprint }
                    },
                    operations = new object[]
                    {
                        new
                        {
                            operationId = "misnamed-group",
                            kind = "setGroup",
                            owner = "canvas",
                            reads = Array.Empty<object>(),
                            writes = new[] { new { kind = "grasshopperGroup", id = groupId.ToString("D"), field = "*" } },
                            reversible = true,
                            payload = new
                            {
                                // The 유수지 shape: a plausible-but-wrong name ("canvas.connect" was
                                // the live one). The kind is the authority.
                                bridgeOperation = "canvas.group",
                                arguments = new
                                {
                                    operationId = "misnamed-group",
                                    groupId,
                                    name = "Misnamed group",
                                    objectIds = new[] { harness.CanvasObjectId },
                                    argbColor = 0
                                }
                            }
                        }
                    }
                },
                summary = "Near-miss bridgeOperation"
            },
            BridgeProtocol.JsonOptions);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(session, submission, CancellationToken.None));

        Assert.Contains("'canvas.group'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("expected 'canvas.setGroup'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("may simply be omitted", exception.Message, StringComparison.Ordinal);
    }
}
