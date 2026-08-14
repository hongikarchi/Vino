using System.Text.Json;
using Vino.AgentHost.Api;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Data;
using Vino.AgentHost.Hosting;
using Vino.AgentHost.Runtime;
using Vino.CanvasSceneAdapter;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vino.AgentHost.Tests;

/// <summary>
/// The scheme card: the model proposes naming rules for observed groups (that IS the judgement we
/// ask it for), but the members must be layers the draft actually saw and the material must be a
/// real palette family, because colour is derived from it. Nothing is stored until the user
/// approves — there is deliberately no unguarded scheme-write tool.
/// </summary>
public sealed class LayerSchemeCardTests
{
    private const string PaletteJson = """
        {
          "variantStopsL": [0.75, 0.55],
          "presets": [
            {
              "id": "material-realistic", "label": "재료 사실색", "default": true,
              "families": [
                { "family": "concrete", "hueDeg": 75, "chroma": 0.025, "baseL": 0.65 },
                { "family": "steel", "hueDeg": 250, "chroma": 0.025, "baseL": 0.55 }
              ]
            }
          ]
        }
        """;

    private const string AliasJson = """
        { "entries": [ { "canonical": "WALL", "material": "concrete", "aliases": ["벽"] } ] }
        """;

    private static async Task<(DynamicToolDispatcher Dispatcher, SessionStore Store, ProjectContextStore Context)>
        CreateAsync(TestDirectory directory)
    {
        var shippedRoot = directory.GetPath("shipped-data");
        Directory.CreateDirectory(Path.Combine(shippedRoot, "layers"));
        await File.WriteAllTextAsync(Path.Combine(shippedRoot, MaterialPalette.ShippedRelativePath), PaletteJson);
        await File.WriteAllTextAsync(Path.Combine(shippedRoot, LayerAliasMatcher.ShippedRelativePath), AliasJson);
        var store = new SessionStore(directory.GetPath("state.db"));
        await store.InitializeAsync();
        var options = new AgentHostOptions { DataDirectory = directory.GetPath("data") };
        var context = new ProjectContextStore(directory.GetPath("context-root"));
        var dispatcher = new DynamicToolDispatcher(
            store, new LayerStubBackend(), options,
            problems: new ProblemLog(options, NullLogger<ProblemLog>.Instance),
            context: context,
            data: new DataLibrary(shippedRoot));
        return (dispatcher, store, context);
    }

    private static DynamicToolCall Call(string tool, string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        return new DynamicToolCall("call", "scheme-thread", "turn", "vino_v1", tool, document.RootElement.Clone());
    }

    private static async Task<SessionRecord> BindAsync(SessionStore store)
    {
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Scheme"));
        await store.SetThreadIdAsync(session.Id, "scheme-thread");
        return session;
    }

    [Fact]
    public async Task ASchemeCardNeedsADraftFirst()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateAsync(directory);
        await BindAsync(store);

        var result = await dispatcher.DispatchAsync(
            Call("approval_request", """
                {
                  "kind": "layerScheme",
                  "summary": "체계",
                  "items": [{ "id": "a", "label": "철골", "scheme": { "members": ["철골::SC1"], "material": "steel" } }]
                }
                """), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("layer_scheme_draft", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RowsAreCheckedAgainstTheDocumentAndThePaletteBeforeTheCardRenders()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateAsync(directory);
        var session = await BindAsync(store);
        await dispatcher.DispatchAsync(Call("layer_scheme_draft", "{}"), CancellationToken.None);

        var result = await dispatcher.DispatchAsync(
            Call("approval_request", """
                {
                  "kind": "layerScheme",
                  "summary": "체계 제안",
                  "items": [
                    { "id": "steel", "label": "철골 계열",
                      "scheme": { "groupKey": "철골", "groupKind": "parent",
                                  "members": ["철골::철골_3D::SC1", "철골::철골_3D::SG1"],
                                  "material": "steel", "underPath": "철골" } },
                    { "id": "col", "label": "기둥",
                      "scheme": { "groupKey": "SC", "groupKind": "markFamily",
                                  "members": ["철골::철골_3D::SC1"], "element": "기둥" } },
                    { "id": "ghost", "label": "없는 레이어",
                      "scheme": { "members": ["없는::레이어"], "element": "X" } },
                    { "id": "badmat", "label": "없는 재료",
                      "scheme": { "members": ["철골::철골_3D::SC1"], "material": "kryptonite" } }
                  ]
                }
                """), CancellationToken.None);

        Assert.True(result.Success, result.Text);
        using var payload = JsonDocument.Parse(result.Text);
        var rejected = payload.RootElement.GetProperty("rejectedSchemeRows").EnumerateArray()
            .Select(entry => entry.GetString()!).ToArray();
        Assert.Equal(2, rejected.Length);
        Assert.Contains(rejected, reason => reason.Contains("없는::레이어", StringComparison.Ordinal));
        Assert.Contains(rejected, reason => reason.Contains("kryptonite", StringComparison.Ordinal));

        var stored = (await store.FindSessionAsync(session.Id))!.ApprovalCard;
        var card = JsonSerializer.Deserialize<ApprovalCard>(
            stored!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("layerScheme", card.Kind);
        Assert.Equal(2, card.Items.Count);
        // A scheme row reviews rules, not geometry: no grant targets are demanded.
        Assert.All(card.Items, item => Assert.Empty(item.Targets));
        var scoped = card.Items.Single(item => item.Id == "steel").SchemeRow!;
        Assert.Equal("steel", scoped.Material);
        Assert.Equal("철골", scoped.UnderPath);
        Assert.Null(scoped.Element);
    }

    [Fact]
    public void ApprovedRowsMergeIntoTheProjectSchemeWithoutLosingWhatWasThere()
    {
        using var directory = new TestDirectory();
        var context = new ProjectContextStore(directory.GetPath("context-root"));

        Assert.True(LayerCurationTables.TryWriteScheme(
            context,
            [new SchemeElementRule("기둥", ["SC"], ["^SC[- ]?\\d"])],
            [new SchemeMaterialRule("steel", "철골", [])]));

        // A later conversation settles one more element; the first must survive.
        Assert.True(LayerCurationTables.TryWriteScheme(
            context,
            [new SchemeElementRule("보", ["SB"], ["^SB[- ]?\\d"])],
            []));

        var scheme = LayerScheme.Parse(File.ReadAllText(context.LayerStandardPath));
        Assert.Equal(2, scheme.ElementCount);
        Assert.Equal(1, scheme.MaterialCount);
        // And it resolves the real model's shape: element from the mark, material from the parent.
        var resolved = scheme.Resolve("철골::철골_3D::SC7");
        Assert.Equal("기둥", resolved.Element);
        Assert.Equal("steel", resolved.Material);
        // SC7 was never on screen — the stored pattern is what generalises past the drafted rows.
        Assert.Equal("보", scheme.Resolve("철골::철골_3D::SB4").Element);
    }

    private sealed class LayerStubBackend : ILiveDocumentBackend
    {
        public bool IsConnected => true;

        public Vino.Contracts.DocumentRuntime? CurrentTarget => null;

        public int QueueLength => 0;

        public string? WriterSessionId => null;

        public Task<object> ReadRhinoLayersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<object>(new
            {
                result = new
                {
                    layers = new[]
                    {
                        new { fullPath = "철골::철골_3D::SC1" },
                        new { fullPath = "철골::철골_3D::SG1" },
                        new { fullPath = "3D::A-Wall" },
                    },
                },
                fingerprint = "layers-fp",
                diagnostics = Array.Empty<object>(),
            });

        public Task<object> ReadSnapshotAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<object> SearchComponentCatalogAsync(JsonElement arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<object> ListRhinoObjectsAsync(JsonElement arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<object> InspectCanvasOutputsAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<object> InspectCanvasOutputsAsync(JsonElement arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<object> SubmitChangeAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<object> ArrangeLayoutAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<object> ConsolidateStagesAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<object> ReadJobAsync(JsonElement arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<object> ResumeSessionAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<object> ReadDataFlowAsync(SessionRecord session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<object> ReadRhinoAuditAsync(JsonElement arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<object> ReadStructuralExtractAsync(JsonElement arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopCurrentAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
