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
/// The layer-curation proposal pipeline through the dispatcher: a layerSemantics audit synthesizes
/// the proposal table SERVER-side (matcher + palette over reported facts) and caches it; the
/// approval card can only carry those server rows — model-authored confidence/colors are ignored,
/// items pointing at unscanned layers are dropped, and the card cannot exist before the scan.
/// </summary>
public sealed class LayerCurationProposalTests
{
    private static readonly Guid MatchedLayerId = Guid.Parse("80ace29e-9912-41de-88af-de9a7b6a57f0");
    private static readonly Guid TriageLayerId = Guid.Parse("2f927896-83f3-43c2-8a84-29b779547b7a");
    private static readonly Guid SampleObjectId = Guid.Parse("660eb647-3699-4f8c-a9dc-bfeb010f5d0f");

    private const string PaletteJson = """
        {
          "variantStopsL": [0.75, 0.55],
          "presets": [
            {
              "id": "material-realistic",
              "label": "재료 사실색",
              "default": true,
              "families": [
                { "family": "concrete", "hueDeg": 75, "chroma": 0.025, "baseL": 0.65 },
                { "family": "steel", "hueDeg": 250, "chroma": 0.025, "baseL": 0.55 }
              ]
            },
            {
              "id": "drafting-traditional",
              "label": "수기 도면 관례",
              "default": false,
              "families": [
                { "family": "concrete", "hueDeg": 145, "chroma": 0.10, "baseL": 0.60 },
                { "family": "steel", "hueDeg": 262, "chroma": 0.08, "baseL": 0.42 }
              ]
            }
          ]
        }
        """;

    private const string AliasJson = """
        {
          "entries": [
            { "canonical": "WALL", "material": "concrete", "aliases": ["벽"], "prefixes": [], "patterns": [] }
          ]
        }
        """;

    private static RhinoAuditResult CannedAudit() => new(
        "layerSemantics", 0.001, "Millimeters", 0.001, null, 2,
        new[]
        {
            new RhinoAuditFinding(
                "f-1", "layerSemantics", new[] { MatchedLayerId }, new[] { "fp-matched" },
                null, "Layer '구조::벽' has no semantic label.", new[] { "updateLayer" }, null,
                new RhinoLayerFacts(
                    "구조::벽", "벽", unchecked((int)0xFF000000), null, 3, 0,
                    new[] { SampleObjectId })),
            new RhinoAuditFinding(
                "f-2", "layerSemantics", new[] { TriageLayerId }, new[] { "fp-triage" },
                null, "Layer 'misc-01' has no semantic label.", new[] { "updateLayer" }, null,
                new RhinoLayerFacts(
                    "misc-01", "misc-01", unchecked((int)0xFF123456), null, 1, 0,
                    Array.Empty<Guid>())),
        },
        Truncated: false,
        "audit-fp");

    private static async Task<(DynamicToolDispatcher Dispatcher, SessionStore Store)> CreateAsync(
        TestDirectory directory)
    {
        var shippedRoot = directory.GetPath("shipped-data");
        Directory.CreateDirectory(Path.Combine(shippedRoot, "layers"));
        await File.WriteAllTextAsync(
            Path.Combine(shippedRoot, MaterialPalette.ShippedRelativePath), PaletteJson);
        await File.WriteAllTextAsync(
            Path.Combine(shippedRoot, LayerAliasMatcher.ShippedRelativePath), AliasJson);

        var store = new SessionStore(directory.GetPath("state.db"));
        await store.InitializeAsync();
        var backend = new StubBackend();
        var options = new AgentHostOptions { DataDirectory = directory.GetPath("data") };
        var dispatcher = new DynamicToolDispatcher(
            store, backend, options,
            problems: new ProblemLog(options, NullLogger<ProblemLog>.Instance),
            data: new DataLibrary(shippedRoot));
        return (dispatcher, store);
    }

    private static DynamicToolCall Call(string tool, string argumentsJson, string threadId = "layer-thread")
    {
        using var document = JsonDocument.Parse(argumentsJson);
        return new DynamicToolCall("call", threadId, "turn", "vino_v1", tool, document.RootElement.Clone());
    }

    private static async Task<SessionRecord> BindAsync(SessionStore store, string threadId = "layer-thread")
    {
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Layers"));
        await store.SetThreadIdAsync(session.Id, threadId);
        return session;
    }

    [Fact]
    public async Task AuditSynthesizesProposalsAndApprovalCardCarriesServerRowsOnly()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store) = await CreateAsync(directory);
        var session = await BindAsync(store);

        var audit = await dispatcher.DispatchAsync(
            Call("rhino_audit", """{"kind":"layerSemantics"}"""), CancellationToken.None);
        Assert.True(audit.Success, audit.Text);
        using var auditPayload = JsonDocument.Parse(audit.Text);
        var proposals = auditPayload.RootElement.GetProperty("proposals");
        // The matched layer resolves through the shipped seed; the triage row proposes nothing.
        var matched = proposals.GetProperty(MatchedLayerId.ToString("D"));
        Assert.Equal("WALL", matched.GetProperty("canonical").GetString());
        Assert.Equal("high", matched.GetProperty("confidence").GetString());
        Assert.True(matched.GetProperty("preChecked").GetBoolean());
        var proposedArgb = matched.GetProperty("proposedArgbColor").GetInt32();
        Assert.NotEqual(unchecked((int)0xFF000000), proposedArgb);
        var triage = proposals.GetProperty(TriageLayerId.ToString("D"));
        Assert.Equal("low", triage.GetProperty("confidence").GetString());
        Assert.False(triage.GetProperty("preChecked").GetBoolean());
        Assert.Equal(
            unchecked((int)0xFF123456),
            triage.GetProperty("proposedArgbColor").GetInt32());
        // familyColors is THE color source for later ops — it must cover the active preset.
        Assert.Equal(
            proposedArgb,
            auditPayload.RootElement.GetProperty("familyColors").GetProperty("concrete").GetInt32());

        // The card request spoofs a layerRow and points one item at an unscanned layer.
        var request = await dispatcher.DispatchAsync(
            Call("approval_request", $$"""
                {
                  "kind": "layerSemantics",
                  "summary": "레이어 라벨 제안",
                  "items": [
                    {
                      "id": "row-1",
                      "label": "구조::벽",
                      "targets": [{ "objectId": "{{MatchedLayerId}}", "fingerprint": "fp-matched" }],
                      "layerRow": { "confidence": "high", "proposedArgbColor": 123, "canonical": "SPOOFED" }
                    },
                    {
                      "id": "row-2",
                      "label": "유령 레이어",
                      "targets": [{ "objectId": "{{Guid.NewGuid()}}", "fingerprint": "fp-ghost" }]
                    }
                  ]
                }
                """), CancellationToken.None);
        Assert.True(request.Success, request.Text);
        Assert.Contains("DROPPED", request.Text, StringComparison.Ordinal);

        var stored = (await store.FindSessionAsync(session.Id))!.ApprovalCard;
        var card = JsonSerializer.Deserialize<ApprovalCard>(
            stored!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("layerSemantics", card.Kind);
        var item = Assert.Single(card.Items);
        Assert.NotNull(item.LayerRow);
        // Server values, not the model's spoof.
        Assert.Equal("WALL", item.LayerRow.Canonical);
        Assert.Equal(proposedArgb, item.LayerRow.ProposedArgbColor);
        Assert.Equal(SampleObjectId, Assert.Single(item.LayerRow.FocusObjectIds!));
    }

    /// <summary>
    /// The project table is the preset's home: a stored preset drives the audit's colours, and the
    /// tables loader must preserve the accumulated alias entries when the preset is written.
    /// </summary>
    [Fact]
    public async Task StoredPresetDrivesProposedColoursAndSurvivesAliasEntries()
    {
        using var directory = new TestDirectory();
        var shippedRoot = directory.GetPath("shipped-data");
        Directory.CreateDirectory(Path.Combine(shippedRoot, "layers"));
        await File.WriteAllTextAsync(Path.Combine(shippedRoot, MaterialPalette.ShippedRelativePath), PaletteJson);
        await File.WriteAllTextAsync(Path.Combine(shippedRoot, LayerAliasMatcher.ShippedRelativePath), AliasJson);
        var data = new DataLibrary(shippedRoot);
        var context = new ProjectContextStore(directory.GetPath("context-root"));
        await File.WriteAllTextAsync(
            EnsureContextDirectory(context),
            """
            {
              "preset": "drafting-traditional",
              "entries": [
                { "canonical": "SPECIAL", "material": "steel", "aliases": ["특수"], "prefixes": [], "patterns": [] }
              ]
            }
            """);

        var tables = LayerCurationTables.Load(data, context);
        Assert.Equal("drafting-traditional", tables.PresetId);
        // The stored preset's palette is what FamilyColors reports — a different convention, so a
        // different colour for the same family.
        var draftingConcrete = tables.FamilyColors()["concrete"];
        Assert.NotEqual(
            tables.Palette.BaseArgb("material-realistic", "concrete"),
            draftingConcrete);
        // The project entry is layered in alongside the shipped ones.
        Assert.Equal("SPECIAL", tables.Matcher.Match("특수")?.Canonical);
        Assert.Equal("WALL", tables.Matcher.Match("벽")?.Canonical);

        // Writing a preset must not drop the accumulated entries.
        Assert.True(LayerCurationTables.TryWritePreset(context, "material-realistic"));
        var reloaded = LayerCurationTables.Load(data, context);
        Assert.Equal("material-realistic", reloaded.PresetId);
        Assert.Equal("SPECIAL", reloaded.Matcher.Match("특수")?.Canonical);
    }

    private static string EnsureContextDirectory(ProjectContextStore context)
    {
        Directory.CreateDirectory(context.ContextDirectory);
        return context.LayerStandardPath;
    }

    /// <summary>
    /// Measured on the real document: un-ticking every coloured row left NOTHING pre-checked
    /// (most layers there carry a colour), so a bulk approve became thirty manual ticks. Rows now
    /// arrive ticked and an existing colour is a MARKER; the card-level colour policy is the lever.
    /// </summary>
    [Fact]
    public async Task ColouredLayersStayPreCheckedAndAreMarkedInstead()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store) = await CreateAsync(directory);
        await BindAsync(store);

        var audit = await dispatcher.DispatchAsync(
            Call("rhino_audit", """{"kind":"layerSemantics"}"""), CancellationToken.None);
        using var payload = JsonDocument.Parse(audit.Text);
        var proposals = payload.RootElement.GetProperty("proposals");

        // The matched layer's fixture colour is black — Rhino's own default, not a human choice.
        var matched = proposals.GetProperty(MatchedLayerId.ToString("D"));
        Assert.True(matched.GetProperty("preChecked").GetBoolean());
        Assert.False(matched.GetProperty("customColour").GetBoolean());

        // The triage layer carries 0xFF123456 — nothing Rhino hands out, so it reads as chosen.
        var triage = proposals.GetProperty(TriageLayerId.ToString("D"));
        Assert.True(triage.GetProperty("customColour").GetBoolean());
        // …and is STILL not pre-checked, but only because no rule resolved it — not the colour.
        Assert.False(triage.GetProperty("preChecked").GetBoolean());
    }

    [Fact]
    public async Task LayerCardWithoutAPriorScanIsRefused()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store) = await CreateAsync(directory);
        await BindAsync(store);

        var result = await dispatcher.DispatchAsync(
            Call("approval_request", $$"""
                {
                  "kind": "layerSemantics",
                  "summary": "스캔 없이",
                  "items": [{ "id": "x", "label": "x", "targets": [{ "objectId": "{{MatchedLayerId}}", "fingerprint": "fp" }] }]
                }
                """), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("rhino_audit", result.Text, StringComparison.Ordinal);
    }

    private sealed class StubBackend : ILiveDocumentBackend
    {
        public bool IsConnected => true;

        public Vino.Contracts.DocumentRuntime? CurrentTarget => null;

        public int QueueLength => 0;

        public string? WriterSessionId => null;

        public Task<object> ReadRhinoAuditAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new
            {
                result = CannedAudit(),
                fingerprint = "audit-fp",
                diagnostics = Array.Empty<object>(),
            });

        public Task<object> ReadSnapshotAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<object> SearchComponentCatalogAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<object> ListRhinoObjectsAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<object> InspectCanvasOutputsAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<object> InspectCanvasOutputsAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<object> SubmitChangeAsync(SessionRecord session, JsonElement arguments, bool autoApprove, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ApprovalGrantMint MintApprovalGrant(IReadOnlyList<(Guid ObjectId, string Fingerprint)> items) =>
            throw new NotSupportedException();

        public Task<object> ArrangeLayoutAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<object> ConsolidateStagesAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<object> ReadJobAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<object> ResumeSessionAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<object> ReadDataFlowAsync(SessionRecord session, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<object> ReadStructuralExtractAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<object> ReadRhinoLayersAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task StopCurrentAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
