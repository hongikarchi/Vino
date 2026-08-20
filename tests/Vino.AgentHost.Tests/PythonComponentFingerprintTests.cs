using Vino.ScriptAdapter;

namespace Vino.AgentHost.Tests;

/// <summary>
/// The script-state concurrency fingerprint hashes the AUTHORED state only (2026-08-19 audit):
/// runtime messages move on every solve — including solves the session itself caused — and hashing
/// them made a session's own committed write look like a manual edit (20/26 false "drifted"
/// refusals). Authored edits must still always move the hash.
/// </summary>
public sealed class PythonComponentFingerprintTests
{
    private static PythonComponentState State(
        string source = "print(1)",
        IReadOnlyList<ComponentRuntimeMessage>? messages = null) =>
        new(
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            source,
            "sha-of-source",
            PythonRuntime.Cpython3,
            new[] { new PythonParameter(Guid.Parse("20000000-0000-0000-0000-000000000002"), "x", "x", "curve", ParameterAccess.Item, false) },
            Array.Empty<PythonParameter>(),
            messages ?? Array.Empty<ComponentRuntimeMessage>());

    [Fact]
    public void RuntimeMessagesDoNotMoveTheFingerprint()
    {
        var quiet = PythonComponentFingerprint.Compute(State());
        var warned = PythonComponentFingerprint.Compute(State(messages: new[]
        {
            new ComponentRuntimeMessage(RuntimeMessageLevel.Warning, "Input parameter x failed to collect data"),
        }));

        Assert.Equal(quiet, warned);
    }

    [Fact]
    public void AuthoredSourceChangeMovesTheFingerprint()
    {
        Assert.NotEqual(
            PythonComponentFingerprint.Compute(State(source: "print(1)")),
            PythonComponentFingerprint.Compute(State(source: "print(2)")));
    }
}
