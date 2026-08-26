using System.Text.Json;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Data;
using Vino.AgentHost.Security;

namespace Vino.AgentHost.Mcp;

/// <summary>
/// Lets a backend client report which turn is active on a thread, so /mcp tool calls carry a real
/// turn id for logging/latency correlation. Optional — when absent (or the thread idle), calls are
/// stamped out-of-band.
/// </summary>
public interface IMcpTurnContext
{
    bool TryGetActiveTurn(string threadId, out string turnId);
}

/// <summary>
/// The loopback MCP server for CLI backends (Claude): a hand-rolled JSON-RPC handler on the
/// existing Kestrel host — measured against the live Claude CLI, exactly five methods matter
/// (spike 2026-08-19 §6), so no MCP SDK dependency:
///
///   server/discover            -> -32601 (a NON-STANDARD pre-initialize probe; an HTTP error
///                                 here risks the whole connection, the CLI wants the JSON-RPC
///                                 method-not-found to fall back to initialize)
///   initialize                 -> protocolVersion + tools capability
///   notifications/initialized  -> 202, no body
///   tools/list                 -> DynamicToolSpecs.Create() converted 1:1 (single source: the
///                                 same specs codex receives inline — never a second tool list)
///   tools/call                 -> the existing DynamicToolDispatcher, with the session identity
///                                 derived SERVER-SIDE from the per-session secret
///
/// Responses are plain JSON (the CLI accepts non-SSE), GET answers 405 (harmless SSE subscribe
/// attempt). Sits OUTSIDE the /api token guard; its auth is the X-Vino-Secret header, which the
/// CLI attaches to every request (lowercased in transit — IHeaderDictionary lookup is already
/// case-insensitive). The secret maps to a CONVERSATION id; only a conversation some session owns
/// (FindSessionByConversationIdAsync) may call tools — a model cannot name another session, and a
/// judge thread (bound to no session) authenticates but is refused tools.
/// </summary>
public static class VinoMcpEndpoint
{
    public const string ServerName = "vino";
    private const string ProtocolVersion = "2025-11-25";

    private static readonly Lazy<IReadOnlyList<McpToolDescriptor>> Tools = new(ConvertSpecs);

    public static void Map(WebApplication app)
    {
        app.MapPost("/mcp", HandleAsync);
        // SSE subscription attempt; refusing it is harmless (spike §6).
        app.MapGet("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
    }

    internal sealed record McpToolDescriptor(string Name, JsonElement Description, JsonElement InputSchema);

    /// <summary>
    /// Declared per tool in <c>tools/list</c> as <c>_meta["anthropic/maxResultSizeChars"]</c>. 500,000
    /// is the client's documented hard ceiling; asking for it costs nothing when a result is small and
    /// is the difference between a truncated read and a lost one when it is not.
    /// </summary>
    internal const int MaxResultSizeChars = 500_000;

    /// <summary>
    /// Flattens DynamicToolSpecs.Create() — [{type:"namespace", name:"vino_v1", tools:[{type:
    /// "function", name, description, inputSchema}]}] — into MCP tool descriptors. The wrapper
    /// shapes differ; the schemas travel verbatim.
    /// </summary>
    internal static IReadOnlyList<McpToolDescriptor> ConvertSpecs()
    {
        var specs = JsonSerializer.SerializeToElement(DynamicToolSpecs.Create(), JsonDefaults.Options);
        var tools = new List<McpToolDescriptor>();
        foreach (var ns in specs.EnumerateArray())
        {
            if (!ns.TryGetProperty("tools", out var nsTools))
            {
                continue;
            }
            foreach (var tool in nsTools.EnumerateArray())
            {
                tools.Add(new McpToolDescriptor(
                    tool.GetProperty("name").GetString() ?? throw new InvalidOperationException("Tool without a name."),
                    tool.GetProperty("description").Clone(),
                    tool.GetProperty("inputSchema").Clone()));
            }
        }
        return tools;
    }

    internal static async Task<IResult> HandleAsync(
        HttpContext context,
        McpSessionSecretStore secrets,
        SessionStore store,
        DynamicToolDispatcher dispatcher,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Vino.AgentHost.Mcp");
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return RpcError(default, -32700, "Parse error");
        }
        using var _ = document;
        var request = document.RootElement;
        // JSON-RPC: a request carries an id, a notification does not (null counts as absent).
        var hasId = request.TryGetProperty("id", out var idElement) &&
                    idElement.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
        var id = hasId ? idElement.Clone() : default;
        var method = request.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;

        // Every request must authenticate — the CLI sends the header on all of them, including the
        // pre-initialize probe. A JSON-RPC error (not an HTTP status) keeps the transport happy.
        if (!secrets.TryResolve(context.Request.Headers["X-Vino-Secret"], out var conversationId))
        {
            logger.LogWarning("/mcp request '{Method}' with a missing or unknown secret.", method);
            return RpcError(id, -32001, "Unauthorized: unknown or missing X-Vino-Secret.");
        }

        switch (method)
        {
            case "initialize":
                return RpcResult(id, new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = ServerName, version = "1" }
                });
            case "notifications/initialized":
                return Results.Accepted();
            case "tools/list":
                return RpcResult(id, new
                {
                    tools = Tools.Value.Select(tool => new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        inputSchema = tool.InputSchema,
                        // Raise the client's per-result ceiling for THIS server. Claude Code caps an
                        // MCP tool result at 25,000 tokens by default and, past that, writes the
                        // result to a file and hands back a reference — which our sessions cannot
                        // open, because we launch the CLI with `--tools ""` and there is no Read.
                        // On 2026-08-26 that turned a 64,003-char snapshot_read of a 50K script into
                        // a total loss: the model never saw one character of the source and the user
                        // had to paste it into chat. The annotation is the specified way to lift the
                        // cap without depending on an environment variable we do not control.
                        _meta = new Dictionary<string, object>
                        {
                            ["anthropic/maxResultSizeChars"] = MaxResultSizeChars,
                        },
                    })
                });
            case "tools/call":
                return await HandleToolCallAsync(
                    request, id, conversationId, store, dispatcher, context, cancellationToken);
            default:
                // server/discover lands here by design; so does anything else we do not serve.
                return method is not null && !hasId
                    ? Results.Accepted()
                    : RpcError(id, -32601, $"Method not found: {method}");
        }
    }

    private static async Task<IResult> HandleToolCallAsync(
        JsonElement request,
        JsonElement id,
        string conversationId,
        SessionStore store,
        DynamicToolDispatcher dispatcher,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!request.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("name", out var nameElement) ||
            nameElement.GetString() is not { Length: > 0 } toolName)
        {
            return RpcError(id, -32602, "tools/call requires params.name.");
        }

        // The identity chain is entirely server-side: secret -> conversation id -> owning session.
        // The model's payload cannot influence which session the dispatcher sees, and a thread no
        // session owns (e.g. the visual-review judge) authenticates but gets no tools.
        var session = await store.FindSessionByConversationIdAsync(conversationId, cancellationToken);
        if (session is null)
        {
            return RpcError(id, -32002, "No session owns this conversation; tools are unavailable on it.");
        }

        var arguments = parameters.TryGetProperty("arguments", out var argumentsElement)
            ? argumentsElement.Clone()
            : JsonSerializer.SerializeToElement(new { });
        // claudecode/toolUseId correlates this call with the CLI's own stream-json tool_use event.
        var callId =
            (parameters.TryGetProperty("_meta", out var meta) &&
             meta.TryGetProperty("claudecode/toolUseId", out var toolUseId)
                ? toolUseId.GetString()
                : null) ?? $"mcp-{Guid.NewGuid():N}";
        var turnContext = context.RequestServices.GetService<IMcpTurnContext>();
        var turnId = turnContext is not null &&
            turnContext.TryGetActiveTurn(conversationId, out var activeTurn)
                ? activeTurn
                : "mcp-oob";

        var call = new DynamicToolCall(
            callId,
            conversationId,
            turnId,
            "vino_v1",
            toolName,
            arguments);
        var result = await dispatcher.DispatchAsync(call, cancellationToken);
        // NOT ToProtocolResult(): that shape (contentItems/inputText) is the codex app-server
        // dialect. MCP wants content blocks + isError.
        return RpcResult(id, new
        {
            content = new[] { new { type = "text", text = result.Text } },
            isError = !result.Success
        });
    }

    private static IResult RpcResult(JsonElement id, object result) =>
        Results.Json(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = NormalizeId(id),
            ["result"] = result
        }, JsonDefaults.Options);

    private static IResult RpcError(JsonElement id, int code, string message) =>
        Results.Json(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = NormalizeId(id),
            ["error"] = new { code, message }
        }, JsonDefaults.Options);

    private static object? NormalizeId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number => id.GetDouble(),
        JsonValueKind.String => id.GetString(),
        _ => null
    };
}
