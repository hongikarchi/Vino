using System.Text.Json;
using Vino.AgentHost.Runtime;
using Vino.BridgeContract;
using Vino.CanvasSceneAdapter;

namespace Vino.AgentHost.Tests;

/// <summary>
/// The bake-provenance contract — models can never author GPTino.SourceDocKey (attribution would
/// be spoofable), and the executor stamps every dispatched rhino.upsert with the job's target
/// docKey without touching the frozen payload — plus the submit-time payload validators that keep
/// model payloads inside their lane (move/purge/layer-update argument rules, including the
/// vino. user-text namespace guard).
/// </summary>
public sealed class SourceDocKeyProvenanceTests
{
    private static UpsertRhinoObjectRequest ValidUpsert(string? sourceDocKey = null) => new(
        "op-1",
        Guid.NewGuid(),
        "entity-1",
        "Curve",
        "{}",
        "{}",
        ExpectedFingerprint: null,
        SourceDocKey: sourceDocKey);

    [Fact]
    public void ValidateUpsertRejectsModelAuthoredSourceDocKey()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidateUpsertArguments(ValidUpsert("abcdef0123456789"), "op-1"));
        Assert.Contains("sourceDocKey", exception.Message, StringComparison.Ordinal);

        // The same payload without the field passes this gate (provenance is server business).
        LiveDocumentBackend.ValidateUpsertArguments(ValidUpsert(), "op-1");
    }

    [Fact]
    public void ValidatorsRejectModelAuthoredApprovedFlag()
    {
        // The human-wins default-deny would be bypassable by prompt alone if a model payload
        // could carry approved:true — Disallow no longer catches it (the member is mapped).
        Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidateUpsertArguments(
                ValidUpsert() with { Approved = true }, "op-1"));
        Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidateTransformArguments(
                new TransformRhinoObjectRequest(
                    "op-1", Guid.NewGuid(), "fp", new RhinoTransformMatrix(
                        1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1), Approved: true),
                "op-1"));
        Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidateFixEndpointPairArguments(
                new FixEndpointPairRequest(
                    "op-1", Guid.NewGuid(), 0, Guid.NewGuid(), 1, "fp-a", "fp-b", 0.001, Approved: true),
                "op-1"));
        // The same payloads without the flag pass their approval gates.
        LiveDocumentBackend.ValidateFixEndpointPairArguments(
            new FixEndpointPairRequest("op-1", Guid.NewGuid(), 0, Guid.NewGuid(), 1, "fp-a", "fp-b", 0.001),
            "op-1");
    }

    [Fact]
    public void InjectApprovalFlagsCoversOnlyGrantedObjectAtAuditedFingerprint()
    {
        var covered = Guid.NewGuid();
        var wrongFingerprint = Guid.NewGuid();
        var deleteCovered = JsonSerializer.SerializeToElement(
            new { operationId = "op-1", objectId = covered, expectedFingerprint = "fp-good" },
            BridgeProtocol.JsonOptions);
        var deleteWrongFp = JsonSerializer.SerializeToElement(
            new { operationId = "op-2", objectId = wrongFingerprint, expectedFingerprint = "fp-stale" },
            BridgeProtocol.JsonOptions);
        var fixPair = JsonSerializer.SerializeToElement(
            new { operationId = "op-3", moveObjectId = covered, anchorObjectId = Guid.NewGuid(), expectedFingerprint = "fp-good" },
            BridgeProtocol.JsonOptions);
        var frozen = new byte[] { 1 };
        var operations = new[]
        {
            new LiveDocumentBackend.PreparedOperation(null!, BridgeAdapterOwner.RhinoScene, "rhino.delete", deleteCovered, frozen, "s1"),
            new LiveDocumentBackend.PreparedOperation(null!, BridgeAdapterOwner.RhinoScene, "rhino.delete", deleteWrongFp, frozen, "s2"),
            new LiveDocumentBackend.PreparedOperation(null!, BridgeAdapterOwner.RhinoScene, "rhino.fixEndpointPair", fixPair, frozen, "s3"),
        };
        var grant = new Dictionary<Guid, string>
        {
            [covered] = "fp-good",
            [wrongFingerprint] = "fp-fresh", // the object changed since the card was shown
        };

        var injected = LiveDocumentBackend.InjectApprovalFlags(operations, grant);

        Assert.True(injected[0].Arguments.GetProperty("approved").GetBoolean());
        // Fingerprint mismatch = the user did NOT approve this state — no flag.
        Assert.False(injected[1].Arguments.TryGetProperty("approved", out _));
        // fixEndpointPair keys coverage on the MOVE object.
        Assert.True(injected[2].Arguments.GetProperty("approved").GetBoolean());
    }

    [Fact]
    public void MoveObjectsValidatorRejectsPreApprovalAndDuplicateTargets()
    {
        var objectId = Guid.NewGuid();
        var layerId = Guid.NewGuid();
        var items = new[] { new MoveObjectItem(objectId, "fp-1") };
        // Server-injected approval only.
        Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidateMoveObjectsArguments(
                new MoveObjectsToLayerRequest("op-1", items, layerId, Approved: true), "op-1"));
        // One object listed twice would make its per-item fingerprint expectations ambiguous.
        Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidateMoveObjectsArguments(
                new MoveObjectsToLayerRequest(
                    "op-1",
                    new[] { new MoveObjectItem(objectId, "fp-1"), new MoveObjectItem(objectId, "fp-2") },
                    layerId),
                "op-1"));
        LiveDocumentBackend.ValidateMoveObjectsArguments(
            new MoveObjectsToLayerRequest("op-1", items, layerId), "op-1");
    }

    [Fact]
    public void LayerUpdateValidatorAcceptsUserTextOnlyAndGuardsTheNamespace()
    {
        var layerId = Guid.NewGuid();
        // A label-only update is a legitimate payload: userText alone satisfies the
        // at-least-one-field rule.
        LiveDocumentBackend.ValidateLayerUpdateArguments(
            new UpdateRhinoLayerRequest(
                "op-1", layerId, "fp-1",
                UserText: new Dictionary<string, string> { ["gptino.material"] = "concrete" }),
            "op-1");
        // No field at all is still invalid.
        Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidateLayerUpdateArguments(
                new UpdateRhinoLayerRequest("op-1", layerId, "fp-1"), "op-1"));
        // Foreign namespaces belong to other tools — refused at submit time.
        var foreign = Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidateLayerUpdateArguments(
                new UpdateRhinoLayerRequest(
                    "op-1", layerId, "fp-1",
                    UserText: new Dictionary<string, string> { ["other.plugin"] = "value" }),
                "op-1"));
        Assert.Contains("gptino.", foreign.Message, StringComparison.Ordinal);

        // renderMaterial alone is a valid change, but only the defined template is accepted —
        // an invented one fails at submit where the model can still fix it.
        LiveDocumentBackend.ValidateLayerUpdateArguments(
            new UpdateRhinoLayerRequest("op-1", layerId, "fp-1", RenderMaterial: "plaster"), "op-1");
        var unknownTemplate = Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidateLayerUpdateArguments(
                new UpdateRhinoLayerRequest("op-1", layerId, "fp-1", RenderMaterial: "brushed-gold"),
                "op-1"));
        Assert.Contains("plaster", unknownTemplate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LayerUpdateValidatorEnforcesSetCurrentRules()
    {
        var layerId = Guid.NewGuid();
        // setCurrent:true alone is a legitimate update — the self-service step that makes a safe
        // layer current BEFORE hiding the one that was current.
        LiveDocumentBackend.ValidateLayerUpdateArguments(
            new UpdateRhinoLayerRequest("op-1", layerId, "fp-1", SetCurrent: true), "op-1");
        // Rhino requires the current layer to be visible — this combination can never succeed,
        // so it fails at submit time where the model can still split it into two updates.
        var combined = Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidateLayerUpdateArguments(
                new UpdateRhinoLayerRequest("op-1", layerId, "fp-1", Visible: false, SetCurrent: true),
                "op-1"));
        Assert.Contains("visible", combined.Message, StringComparison.OrdinalIgnoreCase);
        // setCurrent:false has no meaning (a document always has a current layer) — refused with
        // the remedy named, never silently ignored.
        var falseValue = Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidateLayerUpdateArguments(
                new UpdateRhinoLayerRequest("op-1", layerId, "fp-1", Visible: true, SetCurrent: false),
                "op-1"));
        Assert.Contains("setCurrent", falseValue.Message, StringComparison.Ordinal);
        // NOTE: the companion rule — "the CURRENT layer cannot be hidden" — needs the live
        // document's Layers.CurrentLayerIndex, so it is pre-checked (before any write) in
        // RhinoSceneFoundationAdapter.UpdateLayerCoreAsync, which has no unit harness; it is
        // covered by the live layer-curation gate instead.
    }

    [Fact]
    public void PurgeValidatorRejectsUnknownTables()
    {
        Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidatePurgeArguments(
                new PurgeTableEntriesRequest("op-1", new[] { new PurgeTableEntry("layer", Guid.NewGuid()) }),
                "op-1"));
        LiveDocumentBackend.ValidatePurgeArguments(
            new PurgeTableEntriesRequest("op-1", new[] { new PurgeTableEntry("block", Guid.NewGuid()) }),
            "op-1");
    }

    [Fact]
    public void ValidatePrimitiveRejectsModelAuthoredSourceDocKey()
    {
        var request = new CreateRhinoPrimitiveRequest(
            "op-1",
            Guid.NewGuid(),
            "entity-1",
            RhinoPrimitiveKind.Point,
            Point: new RhinoPointPrimitive(new RhinoPoint3d(0, 0, 0)),
            SourceDocKey: "abcdef0123456789");
        var exception = Assert.Throws<InvalidOperationException>(
            () => LiveDocumentBackend.ValidatePrimitiveArguments(request, "op-1"));
        Assert.Contains("sourceDocKey", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectRewritesOnlyRhinoUpsertArgumentsAndKeepsFrozenPayload()
    {
        var upsertArguments = JsonSerializer.SerializeToElement(
            new { operationId = "op-1", objectId = Guid.NewGuid() },
            BridgeProtocol.JsonOptions);
        var primitiveArguments = JsonSerializer.SerializeToElement(
            new { operationId = "op-3" },
            BridgeProtocol.JsonOptions);
        var wireArguments = JsonSerializer.SerializeToElement(
            new { operationId = "op-2" },
            BridgeProtocol.JsonOptions);
        var frozen = new byte[] { 1, 2, 3 };
        var operations = new[]
        {
            new LiveDocumentBackend.PreparedOperation(
                null!, BridgeAdapterOwner.RhinoScene, "rhino.upsert", upsertArguments, frozen, "sha-upsert"),
            new LiveDocumentBackend.PreparedOperation(
                null!, BridgeAdapterOwner.RhinoScene, "rhino.createPrimitive", primitiveArguments, frozen, "sha-prim"),
            new LiveDocumentBackend.PreparedOperation(
                null!, BridgeAdapterOwner.Canvas, "canvas.setWire", wireArguments, frozen, "sha-wire"),
        };

        var injected = LiveDocumentBackend.InjectRhinoUpsertSourceDocKey(operations, "abcdef0123456789");

        var stamped = injected[0];
        Assert.Equal(
            "abcdef0123456789",
            stamped.Arguments.GetProperty("sourceDocKey").GetString());
        // Existing fields survive the rewrite and the frozen idempotency payload is untouched.
        Assert.Equal("op-1", stamped.Arguments.GetProperty("operationId").GetString());
        Assert.Same(frozen, stamped.FrozenPayload);
        Assert.Equal("sha-upsert", stamped.PayloadSha256);
        // createPrimitive is stamped too — the live gate caught an agent baking "one point"
        // through the primitive op, which had been left out of the injection.
        Assert.Equal(
            "abcdef0123456789",
            injected[1].Arguments.GetProperty("sourceDocKey").GetString());
        // Non-Rhino-creation operations pass through by reference, arguments unmodified.
        Assert.Same(operations[2], injected[2]);
        Assert.False(injected[2].Arguments.TryGetProperty("sourceDocKey", out _));

        // The stamped shape still deserializes under the strict bridge options (Disallow unmapped),
        // which is exactly what rhino.validateUpsert and rhino.upsert do on the GH side.
        var roundTripped = stamped.Arguments.Deserialize<UpsertRhinoObjectRequest>(BridgeProtocol.JsonOptions);
        Assert.Equal("abcdef0123456789", roundTripped!.SourceDocKey);
    }
}
