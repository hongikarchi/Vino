using System.Text.Json;
using Vino.AgentHost.Api;
using Vino.AgentHost.Data;
using Vino.AgentHost.Runtime;
using Vino.BridgeContract;

namespace Vino.AgentHost.Tests;

public sealed class PermissionModeTests
{
    [Theory]
    [InlineData(null, "standard")]
    [InlineData("", "standard")]
    [InlineData("standard", "standard")]
    [InlineData("review", "review")]
    [InlineData("fullAuto", "fullAuto")]
    [InlineData(" fullAuto ", "fullAuto")]
    // Unknown or miscased values fail closed to the default-deny baseline, never to fullAuto.
    [InlineData("FULLAUTO", "standard")]
    [InlineData("full-auto", "standard")]
    public void NormalizeFailsClosedToStandard(string? value, string expected) =>
        Assert.Equal(expected, PermissionModes.Normalize(value));

    [Fact]
    public void StandingApprovalsGrantAndReleaseRoundTrip()
    {
        var standing = new StandingApprovals();
        var session = Guid.NewGuid();
        Assert.False(standing.IsGranted(session));
        standing.Grant(session);
        Assert.True(standing.IsGranted(session));
        Assert.True(standing.Release(session));
        Assert.False(standing.IsGranted(session));
        Assert.False(standing.Release(session));
    }

    [Fact]
    public async Task PermissionModePersistsAndUnknownStoredValueReadsAsStandard()
    {
        using var directory = new TestDirectory();
        var store = new SessionStore(directory.GetPath("runtime.db"));
        await store.InitializeAsync();
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Modeling"));
        Assert.Equal(PermissionModes.Standard, session.PermissionMode);

        await store.SetPermissionModeAsync(session.Id, PermissionModes.FullAuto);
        var reloaded = await store.FindSessionAsync(session.Id);
        Assert.Equal(PermissionModes.FullAuto, reloaded!.PermissionMode);

        // The store normalizes on write too: garbage never lands in the column as-is.
        await store.SetPermissionModeAsync(session.Id, "garbage");
        reloaded = await store.FindSessionAsync(session.Id);
        Assert.Equal(PermissionModes.Standard, reloaded!.PermissionMode);
    }

    [Fact]
    public void InjectApprovalFlagsBlanketApprovesEveryApprovableOperation()
    {
        var deleteArgs = JsonSerializer.SerializeToElement(
            new { operationId = "op-1", objectId = Guid.NewGuid(), expectedFingerprint = "fp-1" },
            BridgeProtocol.JsonOptions);
        var createArgs = JsonSerializer.SerializeToElement(
            new { operationId = "op-2" },
            BridgeProtocol.JsonOptions);
        var frozen = new byte[] { 1 };
        var operations = new[]
        {
            new LiveDocumentBackend.PreparedOperation(
                null!, BridgeAdapterOwner.RhinoScene, "rhino.delete", deleteArgs, frozen, "s1"),
            new LiveDocumentBackend.PreparedOperation(
                null!, BridgeAdapterOwner.Canvas, "canvas.create", createArgs, frozen, "s2"),
        };

        var injected = LiveDocumentBackend.InjectApprovalFlags(
            operations, approvalItems: null, blanketApprove: true);

        // Blanket approval covers approvable ops without any (objectId, fingerprint) grant…
        Assert.True(injected[0].Arguments.GetProperty("approved").GetBoolean());
        // …but never touches non-approvable operations.
        Assert.False(injected[1].Arguments.TryGetProperty("approved", out _));
    }

    [Fact]
    public void InjectApprovalFlagsWithoutBlanketStillRequiresAGrant()
    {
        var deleteArgs = JsonSerializer.SerializeToElement(
            new { operationId = "op-1", objectId = Guid.NewGuid(), expectedFingerprint = "fp-1" },
            BridgeProtocol.JsonOptions);
        var operations = new[]
        {
            new LiveDocumentBackend.PreparedOperation(
                null!, BridgeAdapterOwner.RhinoScene, "rhino.delete", deleteArgs, new byte[] { 1 }, "s1"),
        };

        var injected = LiveDocumentBackend.InjectApprovalFlags(operations, approvalItems: null);

        Assert.False(injected[0].Arguments.TryGetProperty("approved", out _));
    }
}
