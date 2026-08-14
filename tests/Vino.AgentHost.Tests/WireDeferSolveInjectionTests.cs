using System.Text.Json;
using Vino.AgentHost.Runtime;
using Vino.BridgeContract;
using Vino.Contracts;

namespace Vino.AgentHost.Tests;

/// <summary>
/// Rewire batching: the executor marks every canvas.setWire that a later solve-carrying op follows
/// with deferSolve=true, so an N-wire rewire runs one document solve instead of N. deferSolve is
/// server-owned — whatever the model authored is overwritten, so a stray true on the batch's last
/// wire can never suppress the final solve (that would recreate the empty-output class).
/// </summary>
public sealed class WireDeferSolveInjectionTests
{
    [Fact]
    public void EveryWireBeforeALaterSolveCarryingOpDefersAndTheLastOneSolves()
    {
        var operations = new[]
        {
            Wire("w-1"),
            Wire("w-2"),
            Create("c-1"),
            Wire("w-3"),
        };

        var injected = LiveDocumentBackend.InjectWireDeferSolve(operations);

        Assert.True(DeferSolve(injected[0]));  // w-2 and c-1 follow
        Assert.True(DeferSolve(injected[1]));  // c-1 follows
        Assert.False(injected[2].Arguments.TryGetProperty("deferSolve", out _)); // non-wire untouched
        Assert.False(DeferSolve(injected[3])); // nothing solve-carrying follows — this wire solves
    }

    [Fact]
    public void ModelAuthoredDeferSolveOnTheLastWireIsOverwritten()
    {
        var operations = new[]
        {
            Wire("w-1"),
            Wire("w-2", extraJson: ",\"deferSolve\":true"),
        };

        var injected = LiveDocumentBackend.InjectWireDeferSolve(operations);

        Assert.True(DeferSolve(injected[0]));
        Assert.False(DeferSolve(injected[1]));
    }

    [Fact]
    public void BatchesWithoutWiresPassThroughUntouched()
    {
        var operations = new[] { Create("c-1") };
        Assert.Same(operations, LiveDocumentBackend.InjectWireDeferSolve(operations));
    }

    private static bool DeferSolve(LiveDocumentBackend.PreparedOperation operation) =>
        operation.Arguments.GetProperty("deferSolve").GetBoolean();

    private static LiveDocumentBackend.PreparedOperation Wire(string operationId, string extraJson = "")
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var json =
            $$"""
            {"operationId":"{{operationId}}","wire":{"sourceObjectId":"{{source}}","sourceParameterId":"{{Guid.NewGuid()}}","targetObjectId":"{{target}}","targetParameterId":"{{Guid.NewGuid()}}"},"action":"connect","rejectCycles":true{{extraJson}}}
            """;
        using var document = JsonDocument.Parse(json);
        return new LiveDocumentBackend.PreparedOperation(
            new TypedOperation(
                operationId,
                OperationKind.ConnectWire,
                AdapterOwner.Canvas,
                Array.Empty<ResourceAddress>(),
                [new ResourceAddress(ResourceKind.GrasshopperWire, $"{source:N}>{target:N}")],
                true,
                $"operations/{operationId}.json"),
            BridgeAdapterOwner.Canvas,
            "canvas.setWire",
            document.RootElement.Clone(),
            Array.Empty<byte>(),
            "sha");
    }

    private static LiveDocumentBackend.PreparedOperation Create(string operationId)
    {
        var objectId = Guid.NewGuid();
        var json =
            $$"""
            {"operationId":"{{operationId}}","objectId":"{{objectId}}","componentTypeId":"{{Guid.NewGuid()}}","resultOutput":null,"pivot":{"x":0,"y":0} }
            """;
        using var document = JsonDocument.Parse(json);
        return new LiveDocumentBackend.PreparedOperation(
            new TypedOperation(
                operationId,
                OperationKind.CreateComponent,
                AdapterOwner.Canvas,
                Array.Empty<ResourceAddress>(),
                [new ResourceAddress(ResourceKind.GrasshopperComponent, objectId.ToString("D"))],
                true,
                $"operations/{operationId}.json"),
            BridgeAdapterOwner.Canvas,
            "canvas.create",
            document.RootElement.Clone(),
            Array.Empty<byte>(),
            "sha");
    }
}
