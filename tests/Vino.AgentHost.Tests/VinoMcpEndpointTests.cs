using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vino.AgentHost.Api;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Data;
using Vino.AgentHost.Hosting;
using Vino.AgentHost.Mcp;
using Vino.AgentHost.Runtime;
using Vino.AgentHost.Security;

namespace Vino.AgentHost.Tests;

/// <summary>
/// The hand-rolled /mcp JSON-RPC handler, exercised exactly the way the live Claude CLI drives it
/// (spike 2026-08-19 §6): the pre-initialize server/discover probe, initialize, tools/list,
/// tools/call with _meta correlation — all authenticated per request by X-Vino-Secret.
/// </summary>
public sealed class VinoMcpEndpointTests
{
    [Fact]
    public void ConvertSpecsMirrorsDynamicToolSpecsExactly()
    {
        var tools = VinoMcpEndpoint.ConvertSpecs();

        // Single source of truth: the same names codex receives inline, no second tool list.
        var specs = JsonSerializer.SerializeToElement(DynamicToolSpecs.Create(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var expected = specs.EnumerateArray()
            .SelectMany(ns => ns.GetProperty("tools").EnumerateArray())
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToHashSet();
        Assert.Equal(expected, tools.Select(tool => tool.Name).ToHashSet());
        Assert.True(tools.Count >= 20, $"Expected the full tool surface, got {tools.Count}.");

        // The payload guide is embedded in change_submit's description — it must ride the MCP
        // schema too (Claude has no other delivery vector for it).
        var changeSubmit = tools.Single(tool => tool.Name == "change_submit");
        Assert.Contains("payload", changeSubmit.Description.GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JsonValueKind.Object, changeSubmit.InputSchema.ValueKind);
    }

    [Fact]
    public async Task RejectsMissingAndUnknownSecretsWithoutTouchingTheStore()
    {
        using var directory = new TestDirectory();
        var harness = await CreateHarnessAsync(directory);

        var missing = await InvokeAsync(harness, Rpc(1, "tools/list"), secret: null);
        Assert.Equal(-32001, missing.GetProperty("error").GetProperty("code").GetInt32());

        var unknown = await InvokeAsync(harness, Rpc(2, "tools/list"), secret: new string('a', 64));
        Assert.Equal(-32001, unknown.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ServesTheFiveMethodContract()
    {
        using var directory = new TestDirectory();
        var harness = await CreateHarnessAsync(directory);
        var secret = harness.Secrets.Issue(harness.Session.Id);

        // Non-standard pre-initialize probe: MUST answer JSON-RPC -32601 (an HTTP error would
        // break the CLI's fallback to initialize).
        var discover = await InvokeAsync(harness, Rpc(0, "server/discover"), secret);
        Assert.Equal(-32601, discover.GetProperty("error").GetProperty("code").GetInt32());

        var initialize = await InvokeAsync(harness, Rpc(1, "initialize"), secret);
        Assert.Equal("2025-11-25", initialize.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("vino", initialize.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        // notifications/initialized is a notification (no id) -> bodyless 202.
        var initialized = await InvokeRawAsync(harness, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", secret);
        Assert.Equal(StatusCodes.Status202Accepted, initialized.StatusCode);

        var list = await InvokeAsync(harness, Rpc(2, "tools/list"), secret);
        var names = list.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("snapshot_read", names);
        Assert.Contains("change_submit", names);

        var unknownMethod = await InvokeAsync(harness, Rpc(3, "resources/list"), secret);
        Assert.Equal(-32601, unknownMethod.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ToolCallDerivesTheSessionIdentityServerSide()
    {
        using var directory = new TestDirectory();
        var harness = await CreateHarnessAsync(directory);
        var secret = harness.Secrets.Issue(harness.Session.Id);
        await harness.Store.SetExternalConversationIdAsync(harness.Session.Id, "claude-conv-1");

        // artifact_write persists under the session that OWNS the secret — the payload named no
        // session and no thread; a forged threadId in arguments would be ignored by the schema.
        var call = """
            {"jsonrpc":"2.0","id":7,"method":"tools/call","params":{
                "name":"artifact_write",
                "arguments":{"path":"drafts/mcp.txt","content":"via-mcp"},
                "_meta":{"claudecode/toolUseId":"toolu_123"}}}
            """;
        var response = await InvokeAsync(harness, call, secret);

        Assert.False(response.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("drafts/mcp.txt", text, StringComparison.Ordinal);
        Assert.Equal(
            "via-mcp",
            await File.ReadAllTextAsync(directory.GetPath($"data/artifacts/{harness.Session.Id:N}/drafts/mcp.txt")));

        // A failing tool maps to isError:true, not a transport error.
        var badCall = """
            {"jsonrpc":"2.0","id":8,"method":"tools/call","params":{
                "name":"artifact_read","arguments":{"path":"missing/nope.txt"}}}
            """;
        var failure = await InvokeAsync(harness, badCall, secret);
        Assert.True(failure.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task ToolCallRefusesASessionWithoutAConversation()
    {
        using var directory = new TestDirectory();
        var harness = await CreateHarnessAsync(directory);
        var secret = harness.Secrets.Issue(harness.Session.Id); // no conversation id bound

        var response = await InvokeAsync(
            harness,
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"snapshot_read","arguments":{}}}""",
            secret);
        Assert.Equal(-32002, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void SecretStoreRotatesRevokesAndNeverStoresPlaintext()
    {
        var store = new McpSessionSecretStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var secretA = store.Issue(first);
        var secretB = store.Issue(second);
        Assert.True(store.TryResolve(secretA, out var resolvedA) && resolvedA == first);
        Assert.True(store.TryResolve(secretB, out var resolvedB) && resolvedB == second);

        // Rotation invalidates the old secret immediately.
        var rotated = store.Issue(first);
        Assert.False(store.TryResolve(secretA, out _));
        Assert.True(store.TryResolve(rotated, out var resolvedRotated) && resolvedRotated == first);

        // Revocation removes resolution; the other session is untouched.
        store.Revoke(first);
        Assert.False(store.TryResolve(rotated, out _));
        Assert.True(store.TryResolve(secretB, out _));

        // Shape guards: wrong length / null never resolve.
        Assert.False(store.TryResolve(null, out _));
        Assert.False(store.TryResolve("short", out _));

        // No plaintext retained: every stored value is a 32-byte hash, not the 64-char secret.
        var field = typeof(McpSessionSecretStore).GetField(
            "_hashBySession",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var map = (Dictionary<Guid, byte[]>)field.GetValue(store)!;
        Assert.All(map.Values, hash => Assert.Equal(32, hash.Length));
    }

    private sealed record EndpointHarness(
        McpSessionSecretStore Secrets,
        SessionStore Store,
        DynamicToolDispatcher Dispatcher,
        SessionRecord Session);

    private static async Task<EndpointHarness> CreateHarnessAsync(TestDirectory directory)
    {
        var store = new SessionStore(directory.GetPath("state.db"));
        await store.InitializeAsync();
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Mcp session"));
        var options = new AgentHostOptions { DataDirectory = directory.GetPath("data") };
        var dispatcher = new DynamicToolDispatcher(
            store,
            new DynamicToolDispatcherTests.FakeLiveDocumentBackend(),
            options,
            problems: new ProblemLog(options, NullLogger<ProblemLog>.Instance));
        return new EndpointHarness(new McpSessionSecretStore(), store, dispatcher, session);
    }

    private static string Rpc(int id, string method) =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}""";

    private static async Task<JsonElement> InvokeAsync(EndpointHarness harness, string body, string? secret)
    {
        var context = await ExecuteAsync(harness, body, secret);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();
        Assert.False(string.IsNullOrWhiteSpace(payload), $"Expected a JSON-RPC body, got status {context.Response.StatusCode}.");
        return JsonSerializer.Deserialize<JsonElement>(payload);
    }

    private static async Task<HttpResponse> InvokeRawAsync(EndpointHarness harness, string body, string? secret)
    {
        var context = await ExecuteAsync(harness, body, secret);
        return context.Response;
    }

    private static async Task<HttpContext> ExecuteAsync(EndpointHarness harness, string body, string? secret)
    {
        var context = new DefaultHttpContext
        {
            // Results.Json's executor resolves ILoggerFactory from the request services.
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        if (secret is not null)
        {
            // The CLI lowercases header names in transit; IHeaderDictionary lookup must not care.
            context.Request.Headers["x-vino-secret"] = secret;
        }
        var result = await VinoMcpEndpoint.HandleAsync(
            context,
            harness.Secrets,
            harness.Store,
            harness.Dispatcher,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        await result.ExecuteAsync(context);
        return context;
    }
}
