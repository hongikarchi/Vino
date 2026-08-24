using System.Text.Json;
using Vino.AgentHost.Api;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Data;
using Vino.AgentHost.Hosting;
using Vino.AgentHost.Runtime;

namespace Vino.AgentHost.Tests;

/// <summary>
/// Phase 1 of the Claude-backend track: backend identity plumbing. These tests pin the additive
/// contract — codex-only behavior is unchanged, but every session and model now carries a backend
/// id that later phases partition on.
/// </summary>
public sealed class AgentBackendsTests
{
    [Theory]
    [InlineData(null, AgentBackends.Codex)]
    [InlineData("", AgentBackends.Codex)]
    [InlineData("   ", AgentBackends.Codex)]
    [InlineData("codex", AgentBackends.Codex)]
    [InlineData("CODEX", AgentBackends.Codex)]
    [InlineData(" Codex ", AgentBackends.Codex)]
    [InlineData("claude", AgentBackends.Codex)] // unknown (until Phase 3) collapses defensively
    public void NormalizeCollapsesToKnownBackend(string? value, string expected) =>
        Assert.Equal(expected, AgentBackends.Normalize(value));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("codex", true)]
    [InlineData(" CODEX ", true)]
    [InlineData("claude", false)] // strict write-side: unknown ids are refused, not coerced
    [InlineData("gpt", false)]
    public void TryNormalizeIsStrictAboutUnknownIds(string? value, bool accepted)
    {
        Assert.Equal(accepted, AgentBackends.TryNormalize(value, out var backend));
        Assert.Equal(AgentBackends.Codex, backend); // the only known id in Phase 1
    }

    [Fact]
    public void ModelViewSerializesProviderCamelCase()
    {
        var view = new ModelView("id-1", "gpt-x", "GPT X", "desc", IsDefault: true, ["low", "high"]);
        var json = JsonSerializer.Serialize(view, JsonDefaults.Options);
        Assert.Contains("\"provider\":\"codex\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackendColumnMigratesOntoLegacyDatabaseAndDefaultsToCodex()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.GetPath("legacy/sessions.db");
        var legacyId = Guid.NewGuid();
        await SessionStoreTests.CreateLegacySchemaDatabaseAsync(databasePath, legacyId);

        var store = new SessionStore(databasePath);
        await store.InitializeAsync();

        // Legacy rows surface as codex sessions with zero backfill drama.
        Assert.Equal(AgentBackends.Codex, (await store.FindSessionAsync(legacyId))?.Backend);

        // New sessions default to codex; an explicit request value round-trips (normalized).
        var created = await store.CreateSessionAsync(new CreateSessionRequest("Plain"));
        Assert.Equal(AgentBackends.Codex, created.Backend);
        var explicitBackend = await store.CreateSessionAsync(
            new CreateSessionRequest("Explicit", Backend: " CODEX "));
        Assert.Equal(AgentBackends.Codex, explicitBackend.Backend);
        Assert.Equal(AgentBackends.Codex, (await store.FindSessionAsync(explicitBackend.Id))?.Backend);

        // The migration is idempotent across restarts.
        var reopened = new SessionStore(databasePath);
        await reopened.InitializeAsync();
        var (sessions, _) = await reopened.ReadStateAsync();
        Assert.Equal(3, sessions.Count);
        Assert.All(sessions, s => Assert.Equal(AgentBackends.Codex, s.Backend));
    }

    [Fact]
    public async Task ProjectionEmitsTheStoredBackend()
    {
        using var directory = new TestDirectory();
        var store = new SessionStore(directory.GetPath("runtime.db"));
        await store.InitializeAsync();
        await store.CreateSessionAsync(new CreateSessionRequest("Backend session"));
        var options = new AgentHostOptions
        {
            ProjectId = Guid.NewGuid(),
            ProjectDirectory = directory.Path,
            DataDirectory = directory.GetPath("data")
        };
        var projector = new RuntimeStateProjector(
            store,
            options,
            new RuntimeIdentity(options.ProjectId, null, null, directory.Path, DateTimeOffset.UtcNow),
            new RuntimeControl(),
            new DisconnectedDocumentBackend(),
            new EffectiveModelState(),
            new EventHub());

        var projection = JsonSerializer.SerializeToElement(await projector.BuildAsync());

        // The value travels store -> projection (not a hardcoded constant): same string today,
        // but the projector now reads the row.
        Assert.Equal(
            AgentBackends.Codex,
            projection.GetProperty("sessions")[0].GetProperty("backend").GetString());
    }
}
