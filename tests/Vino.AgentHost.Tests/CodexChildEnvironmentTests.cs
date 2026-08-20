using System.Diagnostics;
using System.Reflection;
using Vino.AgentHost.Codex;

namespace Vino.AgentHost.Tests;

public sealed class CodexChildEnvironmentTests
{
    [Fact]
    public void CodexChildDoesNotInheritAnyVinoEnvironmentVariables()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["VINO_API_TOKEN"] = "api-secret";
        startInfo.Environment["VINO_BRIDGE_SECRET"] = "bridge-secret";
        startInfo.Environment["VINO_BRIDGE_PIPE"] = "bridge-pipe";
        startInfo.Environment["vino_future_secret"] = "future-secret";
        startInfo.Environment["Vino:ApiToken"] = "colon-token";
        startInfo.Environment["Vino:FutureSecret"] = "colon-secret";
        startInfo.Environment["CODEX_HOME"] = "keep-me";
        var scrub = typeof(CodexAppServerClient).GetMethod(
            "RemoveVinoEnvironment",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(scrub);
        scrub.Invoke(null, [startInfo]);

        Assert.DoesNotContain(
            startInfo.Environment.Keys,
            key =>
                key.StartsWith("VINO_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("VINO:", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("keep-me", startInfo.Environment["CODEX_HOME"]);
    }
}
