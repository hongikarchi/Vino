using System.Text.Json;
using Vino.AgentHost.Runtime;
using Vino.BridgeContract;
using Vino.Contracts;

namespace Vino.AgentHost.Tests;

/// <summary>
/// Dispatch-time watchdog injection: only C# python.setSource operations are rewritten, only
/// their dispatched Arguments change (FrozenPayload — the idempotency identity — is untouched),
/// and everything else in the batch passes through by reference.
/// </summary>
public sealed class CSharpWatchdogInjectionTests
{
    private const string LoopSource = "for (var i = 0; i < 100; i++)\\n{\\n    System.Console.WriteLine(i);\\n}\\n";

    [Fact]
    public void CsharpSetSourceGainsTheGuardAndKeepsItsFrozenPayload()
    {
        var operations = new[]
        {
            SetSource("op-1", "csharp", LoopSource),
            SetSource("op-2", "cpython3", "for i in range(10):\\n    print(i)\\n"),
        };

        var injected = LiveDocumentBackend.InjectCSharpWatchdog(operations, 30_000);

        var rewritten = injected[0].Arguments.GetProperty("source").GetString()!;
        Assert.Contains("__vino_sw", rewritten, StringComparison.Ordinal);
        Assert.Same(operations[0].FrozenPayload, injected[0].FrozenPayload);
        Assert.Equal(operations[0].PayloadSha256, injected[0].PayloadSha256);
        // Every non-source argument survives the rewrite verbatim.
        Assert.Equal("csharp", injected[0].Arguments.GetProperty("runtime").GetString());
        Assert.Equal("sha-before", injected[0].Arguments.GetProperty("expectedSourceSha256").GetString());
        // The Python op is untouched.
        Assert.DoesNotContain(
            "__vino_",
            injected[1].Arguments.GetProperty("source").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BatchesWithoutCsharpSourceWritesPassThroughByReference()
    {
        var operations = new[] { SetSource("op-1", "cpython3", "print(1)\\n") };

        Assert.Same(operations, LiveDocumentBackend.InjectCSharpWatchdog(operations, 30_000));
    }

    [Fact]
    public void StraightLineCsharpSourceIsLeftAlone()
    {
        // Nothing loop-shaped: the injector returns the source unchanged, so the operation list
        // passes through by reference (no needless Arguments churn).
        var operations = new[] { SetSource("op-1", "csharp", "a = 1 + 2;\\n") };

        Assert.Same(operations, LiveDocumentBackend.InjectCSharpWatchdog(operations, 30_000));
    }

    private static LiveDocumentBackend.PreparedOperation SetSource(
        string operationId,
        string runtime,
        string escapedSource)
    {
        var componentId = Guid.NewGuid();
        var json =
            $$"""
            {"operationId":"{{operationId}}","componentId":"{{componentId}}","expectedSourceSha256":"sha-before","source":"{{escapedSource}}","runtime":"{{runtime}}","expireSolution":true}
            """;
        using var document = JsonDocument.Parse(json);
        return new LiveDocumentBackend.PreparedOperation(
            new TypedOperation(
                operationId,
                OperationKind.UpdatePythonSource,
                AdapterOwner.Script,
                Array.Empty<ResourceAddress>(),
                [new ResourceAddress(ResourceKind.GrasshopperComponentSource, componentId.ToString("D"))],
                true,
                $"operations/{operationId}.json"),
            BridgeAdapterOwner.Script,
            "python.setSource",
            document.RootElement.Clone(),
            new byte[] { 1, 2, 3 },
            "payload-sha");
    }
}
