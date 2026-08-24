using System.Text.Json;
using Vino.AgentHost.Api;
using Vino.AgentHost.Claude;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Data;
using Vino.AgentHost.Hosting;
using Vino.AgentHost.Runtime;

namespace Vino.AgentHost.Tests;

/// <summary>
/// Phase 4a: the Claude auth probe (CodexAuthProbe mirror) and the projection surface the panel
/// consumes (claudeAuth / backends[] / claudeLimits).
/// </summary>
public sealed class ClaudeAuthSurfaceTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private readonly string? _priorConfigDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");

    public ClaudeAuthSurfaceTests() =>
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _directory.GetPath("claude-home"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _priorConfigDir);
        _directory.Dispose();
    }

    [Fact]
    public void ProbeOrdersCliPresenceBeforeCredentials()
    {
        // A configured-but-missing executable is authoritative (no fallback), so the probe reads
        // cli-missing even if stale credentials exist — the same stale-auth trap CodexAuthProbe
        // documents.
        Directory.CreateDirectory(_directory.GetPath("claude-home"));
        File.WriteAllText(_directory.GetPath("claude-home\\.credentials.json"), "{\"claudeAiOauth\":{}}");
        var missing = new ClaudeAuthProbe(new AgentHostOptions
        {
            ClaudeExecutable = _directory.GetPath("nope\\claude.exe")
        });
        Assert.Equal("cli-missing", missing.Read().Wire);

        // A real executable + credentials -> logged-in; empty credentials file -> logged-out.
        var exePath = _directory.GetPath("bin\\claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllBytes(exePath, [0x4D, 0x5A]);
        var installed = new ClaudeAuthProbe(new AgentHostOptions { ClaudeExecutable = exePath });
        Assert.Equal("logged-in", installed.Read().Wire);

        File.WriteAllText(_directory.GetPath("claude-home\\.credentials.json"), string.Empty);
        var loggedOut = new ClaudeAuthProbe(new AgentHostOptions { ClaudeExecutable = exePath });
        Assert.Equal("logged-out", loggedOut.Read().Wire);
    }

    [Fact]
    public void ProbeCachesForAFewSeconds()
    {
        var exePath = _directory.GetPath("bin\\claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllBytes(exePath, [0x4D, 0x5A]);
        var probe = new ClaudeAuthProbe(new AgentHostOptions { ClaudeExecutable = exePath });
        Assert.Equal("logged-out", probe.Read().Wire);

        // Credentials appear, but the cached snapshot holds within the TTL window.
        Directory.CreateDirectory(_directory.GetPath("claude-home"));
        File.WriteAllText(_directory.GetPath("claude-home\\.credentials.json"), "{}");
        Assert.Equal("logged-out", probe.Read().Wire);
    }

    [Fact]
    public async Task ProjectionCarriesBothBackendsAuthAndOmitsWhenUnwired()
    {
        using var directory = new TestDirectory();
        var store = new SessionStore(directory.GetPath("runtime.db"));
        await store.InitializeAsync();
        await store.CreateSessionAsync(new CreateSessionRequest("s"));
        var options = new AgentHostOptions
        {
            ProjectId = Guid.NewGuid(),
            ProjectDirectory = directory.Path,
            DataDirectory = directory.GetPath("data"),
            // Both CLIs "missing" via authoritative bad paths — deterministic on any machine.
            CodexExecutable = directory.GetPath("nope\\codex.exe"),
            ClaudeExecutable = directory.GetPath("nope\\claude.exe")
        };
        var projector = new RuntimeStateProjector(
            store,
            options,
            new RuntimeIdentity(options.ProjectId, null, null, directory.Path, DateTimeOffset.UtcNow),
            new RuntimeControl(),
            new DisconnectedDocumentBackend(),
            new EffectiveModelState(),
            new EventHub(),
            codexAuth: new CodexAuthProbe(options),
            claudeAuth: new ClaudeAuthProbe(options));

        var projection = JsonSerializer.SerializeToElement(await projector.BuildAsync());
        Assert.Equal("cli-missing", projection.GetProperty("claudeAuth").GetProperty("status").GetString());
        var backends = projection.GetProperty("backends");
        Assert.Equal(2, backends.GetArrayLength());
        Assert.Equal("codex", backends[0].GetProperty("id").GetString());
        Assert.Equal("claude", backends[1].GetProperty("id").GetString());
        Assert.Equal("cli-missing", backends[1].GetProperty("auth").GetProperty("status").GetString());
        // No client wired -> no claude limits payload.
        Assert.Equal(JsonValueKind.Null, projection.GetProperty("claudeLimits").ValueKind);

        // Probeless composition (tests, headless) keeps the old shape: backends stays null.
        var bare = new RuntimeStateProjector(
            store,
            options,
            new RuntimeIdentity(options.ProjectId, null, null, directory.Path, DateTimeOffset.UtcNow),
            new RuntimeControl(),
            new DisconnectedDocumentBackend(),
            new EffectiveModelState(),
            new EventHub());
        var bareProjection = JsonSerializer.SerializeToElement(await bare.BuildAsync());
        Assert.Equal(JsonValueKind.Null, bareProjection.GetProperty("backends").ValueKind);
        Assert.Equal(JsonValueKind.Null, bareProjection.GetProperty("claudeAuth").ValueKind);
    }
}
