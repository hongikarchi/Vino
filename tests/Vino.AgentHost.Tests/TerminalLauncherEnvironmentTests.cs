using System.Diagnostics;
using System.Reflection;
using Vino.AgentHost.Hosting;

namespace Vino.AgentHost.Tests;

public sealed class TerminalLauncherEnvironmentTests
{
    [Fact]
    public void ConfigureChildEnvironmentRemovesInheritedVinoValuesAndSetsOnlySessionToken()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["VINO_API_TOKEN"] = "stale-token";
        startInfo.Environment["VINO_BRIDGE_SECRET"] = "bridge-secret";
        startInfo.Environment["vino_future_value"] = "future-value";
        startInfo.Environment["Vino:ApiToken"] = "colon-token";
        startInfo.Environment["Vino:FutureSecret"] = "colon-secret";
        startInfo.Environment["PATH"] = "preserved-path";
        startInfo.Environment["UNRELATED_VALUE"] = "preserved-value";
        var configure = typeof(TerminalLauncher).GetMethod(
            "ConfigureChildEnvironment",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(configure);
        configure.Invoke(null, [startInfo, "session-token"]);

        var vinoKeys = startInfo.Environment.Keys
            .Where(key =>
                key.StartsWith("VINO_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("VINO:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(["VINO_API_TOKEN"], vinoKeys);
        Assert.Equal("session-token", startInfo.Environment["VINO_API_TOKEN"]);
        Assert.Equal("preserved-path", startInfo.Environment["PATH"]);
        Assert.Equal("preserved-value", startInfo.Environment["UNRELATED_VALUE"]);
    }
}
