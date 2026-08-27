using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vino.AgentHost.Claude;
using Vino.AgentHost.Api;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Data;
using Vino.AgentHost.Hosting;
using Vino.AgentHost.Mcp;
using Vino.AgentHost.Runtime;
using Vino.AgentHost.Security;
using Vino.BridgeContract;

// Before anything else: drop any disk-file handle inherited from the Rhino parent at spawn. The
// stdio-redirected launch forces handle inheritance, leaking Rhino's open .3dm handle into this
// long-lived process and blocking the user's saves. See InheritedHandleGuard.
var releasedInheritedHandles = InheritedHandleGuard.CloseInheritedDiskHandles();
// Also before spawning anything: crash dialogs must never reach the desktop from this process
// tree (codex → shells → scratch exes inherit the error mode). See CrashDialogSuppression.
CrashDialogSuppression.Apply();

var packagedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Directory.Exists(packagedWebRoot) ? packagedWebRoot : null
});
builder.WebHost.UseUrls("http://127.0.0.1:0");
// No policy cap on message-attachment size: the user decides what is worth attaching (large images
// cost tokens/context, which is their call, not ours). Kestrel's default ~28 MiB body cap would
// otherwise reject a big paste with an opaque 413, so lift it entirely. This host is loopback-only
// and token-gated (see the middleware below), so an unbounded body is not an external DoS surface.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = null);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
});

var options = AgentHostArguments.Parse(args, builder.Configuration);
var developmentDataDirectory = DevelopmentDataDirectoryPolicy.ResolveFromEnvironment();
if (developmentDataDirectory is not null &&
    !string.Equals(
        developmentDataDirectory,
        options.ResolveDataDirectory(),
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "The explicit AgentHost data directory does not match the validated development run directory.");
}
using var runtimeInstance = RuntimeInstanceLock.Acquire(options.ResolveDataDirectory());
// Release installs previously logged only to a console nobody captures (the Rhino parent reads
// one READY line off stdout and discards the rest), which made "share your log" impossible.
// host.log in the data root is the file a user can actually attach to a bug report.
builder.Logging.AddProvider(new FileLoggerProvider(
    Path.Combine(options.ResolveDataDirectory(), "host.log")));
// One-time legacy adoption must run while this process owns the new root's instance lock and
// before the SessionStore below opens runtime.db. It only applies to the default fingerprint
// root: an explicit --data-directory (dev-mode/benchmark sandboxes) is skipped inside TryAdopt
// so production project data is never imported into an isolated run. The app logger pipeline
// does not exist until Build(), so adoption logs through a short-lived console factory matching
// the app's format.
using (var bootstrapLoggers = LoggerFactory.Create(logging => logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
})))
{
    LegacyDataDirectoryAdoption.TryAdopt(
        options,
        bootstrapLoggers.CreateLogger(nameof(LegacyDataDirectoryAdoption)));
}
var identity = new RuntimeIdentity(
    options.ProjectId,
    options.RhinoPath,
    options.GrasshopperPath,
    options.ProjectDirectory,
    DateTimeOffset.UtcNow);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton(new SessionStore(Path.Combine(options.ResolveDataDirectory(), "runtime.db")));
builder.Services.AddSingleton(new AttachmentStore(options.ResolveDataDirectory()));
builder.Services.AddSingleton<ImageUrlAttachmentFetcher>();
builder.Services.AddSingleton(new ProjectContextStore(options.ResolveDataDirectory()));
builder.Services.AddSingleton(new ProjectArchiveReader(
    ProjectArchiveReader.DefaultProjectsParentDirectory(),
    options.ResolveDataDirectory(),
    ProjectArchiveReader.LegacyProjectsParentDirectories()));
builder.Services.AddSingleton<SkillLibrary>();
builder.Services.AddSingleton<DataLibrary>();
builder.Services.AddSingleton<IStructuralSolver>(services =>
    new PythonStructuralSolver(services.GetRequiredService<DataLibrary>()));
builder.Services.AddSingleton<SessionActivityLog>();
builder.Services.AddSingleton<IThreadInstructionComposer, InstructionAssembler>();
builder.Services.AddSingleton<RuntimeControl>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<EndpointRegistry>();
builder.Services.AddSingleton<PanelBootstrapNonceStore>();
builder.Services.AddSingleton<ProblemLog>();
builder.Services.AddSingleton(services => new LiveDocumentBackend(
    services.GetRequiredService<SessionStore>(),
    services.GetRequiredService<AgentHostOptions>(),
    services.GetRequiredService<EventHub>(),
    services.GetRequiredService<ILogger<LiveDocumentBackend>>(),
    services.GetService<ProblemLog>(),
    services.GetService<PendingJobDigests>(),
    // Evaluated per tidy, so editing rules.md takes effect on the next turn with no restart.
    () => services.GetRequiredService<ProjectContextStore>().ReadAutoTidyEnabled()));
builder.Services.AddSingleton<ILiveDocumentBackend>(services =>
    services.GetRequiredService<LiveDocumentBackend>());
builder.Services.AddSingleton<ILiveDocumentQueueControl>(services =>
    services.GetRequiredService<LiveDocumentBackend>());
builder.Services.AddSingleton<ISelectionContextSource>(services =>
    services.GetRequiredService<LiveDocumentBackend>());
builder.Services.AddSingleton<ILayoutTidyService>(services =>
    services.GetRequiredService<LiveDocumentBackend>());
builder.Services.AddHostedService(services => services.GetRequiredService<LiveDocumentBackend>());
builder.Services.AddSingleton<CodexAppServerClient>();
// Backends register as IAgentBackend entries (client + catalog + a backend-private ModelSelector
// whose cache is therefore per-backend); the resolver is what the orchestrator and /models see.
// Nothing binds IAgentSessionClient/IModelCatalog directly anymore — a second backend is one more
// AddSingleton<IAgentBackend> line.
builder.Services.AddSingleton<IAgentBackend>(services =>
{
    var codexClient = services.GetRequiredService<CodexAppServerClient>();
    return new AgentBackend(
        AgentBackends.Codex,
        codexClient,
        codexClient,
        new ModelSelector(codexClient, services.GetRequiredService<ILogger<ModelSelector>>()));
});
// The Claude backend: the subscription CLI behind the same IAgentSessionClient contract. The
// client also serves /mcp turn-id correlation (IMcpTurnContext). Registration alone makes
// backend="claude" sessions creatable — AgentBackends.All gates the API in lockstep.
builder.Services.AddSingleton<ClaudeWorkspacePlanner>();
builder.Services.AddSingleton<ClaudeHomeScaffolder>();
builder.Services.AddSingleton<ClaudeCliSessionClient>();
builder.Services.AddSingleton<IMcpTurnContext>(services => services.GetRequiredService<ClaudeCliSessionClient>());
builder.Services.AddSingleton<IAgentBackend>(services =>
{
    var claudeCatalog = new ClaudeModelCatalog();
    return new AgentBackend(
        AgentBackends.Claude,
        services.GetRequiredService<ClaudeCliSessionClient>(),
        claudeCatalog,
        new ModelSelector(claudeCatalog, services.GetRequiredService<ILogger<ModelSelector>>()));
});
builder.Services.AddSingleton<IAgentBackendResolver>(services =>
    new AgentBackendRegistry(services.GetServices<IAgentBackend>()));
builder.Services.AddSingleton<EffectiveModelState>();
builder.Services.AddSingleton<SessionUsageState>();
builder.Services.AddSingleton<StandingApprovals>();
builder.Services.AddSingleton<FullAutoContinuation>();
builder.Services.AddSingleton<PendingViewCaptures>();
builder.Services.AddSingleton<PendingJobDigests>();
builder.Services.AddSingleton<VisualReviewState>();
builder.Services.AddSingleton<DynamicToolDispatcher>();
builder.Services.AddSingleton<McpSessionSecretStore>();
builder.Services.AddSingleton<SessionOrchestrator>();
builder.Services.AddSingleton<RuntimeStateProjector>();
builder.Services.AddSingleton<TerminalLauncher>();
builder.Services.AddSingleton<CodexAuthProbe>();
builder.Services.AddSingleton<CodexLoginLauncher>();
builder.Services.AddSingleton<ClaudeAuthProbe>();
builder.Services.AddSingleton<ClaudeLoginLauncher>();
builder.Services.AddHostedService<ReadySignalService>();
builder.Services.AddHostedService<ParentProcessMonitor>();

var app = builder.Build();
// Serving the embedded instruction copy is invisible to the model (identical text), so a fallback
// event is the only evidence that this install's loose instruction files are missing or unreadable.
InstructionAssets.DiagnosticSink = message => app.Logger.LogWarning("{Message}", message);
if (releasedInheritedHandles.Count > 0)
{
    app.Logger.LogInformation(
        "Released {Count} inherited disk-file handle(s) leaked from the Rhino parent at launch: {Paths}",
        releasedInheritedHandles.Count,
        string.Join("; ", releasedInheritedHandles));
}
var store = app.Services.GetRequiredService<SessionStore>();
await store.InitializeAsync();
app.Services.GetRequiredService<ProjectContextStore>().EnsureScaffolded(
    identity.ProjectId,
    string.IsNullOrWhiteSpace(options.RhinoPath)
        ? "Untitled Rhino"
        : Path.GetFileNameWithoutExtension(options.RhinoPath),
    options.RhinoPath,
    options.GrasshopperPath);
var events = app.Services.GetRequiredService<EventHub>();
var control = app.Services.GetRequiredService<RuntimeControl>();
var backend = app.Services.GetRequiredService<ILiveDocumentBackend>();
var codex = app.Services.GetRequiredService<CodexAppServerClient>();
var dispatcher = app.Services.GetRequiredService<DynamicToolDispatcher>();
var queueControl = app.Services.GetRequiredService<ILiveDocumentQueueControl>();
_ = app.Services.GetRequiredService<SessionOrchestrator>();
codex.DynamicToolHandler = dispatcher.DispatchAsync;
await queueControl.RefreshScheduleAsync();

// The Codex login/install flow happens in an external terminal while the panel sits on its login
// gate; nothing in that flow touches an endpoint, so no activity-driven Publish() would ever
// re-project the flipped auth state. This watcher is the missing publisher: it re-reads the probe
// on a cadence just above its cache TTL and publishes ONLY on a status change, so the gate (and
// the header chip) lift by themselves shortly after auth.json appears — and re-gate if it goes.
var authProbe = app.Services.GetRequiredService<CodexAuthProbe>();
var claudeAuthProbe = app.Services.GetRequiredService<ClaudeAuthProbe>();
_ = Task.Run(async () =>
{
    // One watcher, both backends: a tuple compare instead of a second Task.Run, so either
    // backend's login flipping publishes exactly once and quiet ticks publish nothing.
    var lastStatus = (Codex: authProbe.Read().Status, Claude: claudeAuthProbe.Read().Status);
    try
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
        {
            var status = (Codex: authProbe.Read().Status, Claude: claudeAuthProbe.Read().Status);
            if (status == lastStatus)
            {
                continue;
            }
            lastStatus = status;
            events.Publish();
        }
    }
    catch (OperationCanceledException)
    {
        // Host shutdown.
    }
});

app.Use(async (context, next) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (remoteAddress is not null && !IPAddress.IsLoopback(remoteAddress))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ApiError("loopback_required", "Vino only accepts loopback clients."));
        return;
    }

    if (context.Request.Headers.TryGetValue("Origin", out var originValues) &&
        !RequestOriginPolicy.AllowsPresentedOrigin(
            originValues,
            context.Request.Scheme,
            context.Request.Host.Value))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ApiError("origin_rejected", "The request origin is not this Vino runtime."));
        return;
    }

    if (context.Request.Path.StartsWithSegments("/api") &&
        !HasValidApiToken(context, options.ApiToken))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiError(
            "authentication_required",
            "A valid Vino runtime token is required."));
        return;
    }

    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'";
    try
    {
        await next();
    }
    catch (SessionOrderConcurrencyException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status409Conflict, "order_version_conflict", exception.Message);
    }
    catch (SessionPausedException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status409Conflict, "session_paused", exception.Message);
    }
    catch (KeyNotFoundException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status404NotFound, "not_found", exception.Message);
    }
    catch (ArgumentException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request", exception.Message);
    }
    catch (InvalidOperationException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status409Conflict, "invalid_state", exception.Message);
    }
    catch (Vino.BridgeContract.BridgeProtocolException exception)
    {
        // Bridge reads (e.g. GET /data-flow) surface adapter failures as protocol exceptions;
        // a typed 502 beats a raw 500 with a non-ApiError body.
        await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "bridge_error", exception.Message);
    }
    catch (TimeoutException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status504GatewayTimeout, "bridge_timeout", exception.Message);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/panel/bootstrap", (HttpContext context, PanelBootstrapNonceStore panelBootstrap) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    var parentCredential = context.Request.Headers["X-Vino-Panel-Parent"].FirstOrDefault();
    var documentSerialText = context.Request.Query["documentSerial"].FirstOrDefault();
    if (!uint.TryParse(documentSerialText, NumberStyles.None, CultureInfo.InvariantCulture, out var documentSerial) ||
        !panelBootstrap.TryIssue(parentCredential, documentSerial, out var nonce))
    {
        return Results.Json(
            new ApiError(
                "panel_parent_rejected",
                "The Rhino panel parent credential or target document is invalid."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    return Results.Ok(new { nonce });
});

app.MapGet("/panel", async (HttpContext context, PanelBootstrapNonceStore panelBootstrap) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    var supplied = context.Request.Query["bootstrap"].FirstOrDefault();
    var documentSerialText = context.Request.Query["documentSerial"].FirstOrDefault();
    if (!uint.TryParse(documentSerialText, NumberStyles.None, CultureInfo.InvariantCulture, out var documentSerial) ||
        !panelBootstrap.IsBoundDocument(documentSerial))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiError(
            "panel_bootstrap_rejected",
            "The Rhino panel bootstrap nonce or target document is missing, expired, or invalid."));
        return;
    }

    if (HasValidApiToken(context, options.ApiToken))
    {
        context.Response.Redirect("/");
        return;
    }

    if (!panelBootstrap.TryConsume(supplied, documentSerial))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiError(
            "panel_bootstrap_rejected",
            "The Rhino panel bootstrap nonce is missing, expired, or invalid."));
        return;
    }

    context.Response.Cookies.Append("vino_runtime", options.ApiToken, new CookieOptions
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Strict,
        Secure = false,
        Path = "/"
    });
    context.Response.Redirect("/");
});

// The loopback MCP endpoint for CLI backends. Deliberately OUTSIDE the /api group: the token
// guard above only covers the /api prefix, and /mcp authenticates every request itself with the
// per-session X-Vino-Secret. Loopback/Origin/CSP guards still apply (they are path-agnostic).
VinoMcpEndpoint.Map(app);

var api = app.MapGroup("/api/v1");

api.MapGet("/runtime", async (RuntimeStateProjector projector, CancellationToken cancellationToken) =>
    Results.Ok(await projector.BuildAsync(cancellationToken)));

api.MapGet("/events", async (HttpContext context, RuntimeStateProjector projector, EventHub eventHub) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Connection = "keep-alive";
    using var subscription = eventHub.Subscribe();
    await SendStateEventAsync(context, projector, context.RequestAborted);
    await foreach (var _ in subscription.Reader.ReadAllAsync(context.RequestAborted))
    {
        await SendStateEventAsync(context, projector, context.RequestAborted);
    }
});

// On-demand Rhino<->GH data-flow detail for the panel drawer: per-parameter references (with
// existence/layer) plus the stamped-bake census. ?doc= selects the GH docKey; omitted = the only
// registered document. Refreshes the summary cache as a side effect.
api.MapGet("/data-flow", async (
    string? doc,
    LiveDocumentBackend liveBackend,
    CancellationToken cancellationToken) =>
    Results.Ok(await liveBackend.ReadDataFlowDetailAsync(doc, cancellationToken)));

// Layer table + named layer states.
api.MapGet("/layers", async (
    LiveDocumentBackend liveBackend,
    CancellationToken cancellationToken) =>
    Results.Ok(await liveBackend.ReadRhinoLayersAsync(cancellationToken)));

// Viewport focus for the audit card: select + zoom the objects behind a finding, optionally
// isolating or locking everything else so the user can judge it themselves. Not a ChangeSet step —
// a human pressed a row — and deliberately absent from the agent's tool schema.
api.MapPost("/focus", async (
    FocusRequest request,
    LiveDocumentBackend liveBackend,
    CancellationToken cancellationToken) =>
{
    var arguments = JsonSerializer.SerializeToElement(new
    {
        objectIds = request.ObjectIds ?? Array.Empty<Guid>(),
        mode = string.IsNullOrWhiteSpace(request.Mode) ? "select" : request.Mode,
        zoom = request.Zoom ?? true,
        ownerToken = string.IsNullOrWhiteSpace(request.OwnerToken) ? null : request.OwnerToken,
    });
    return Results.Ok(await liveBackend.FocusRhinoObjectsAsync(arguments, cancellationToken));
});

// Canvas focus for the panel's [[ghfocus:…]] chip: select + frame the Grasshopper components the
// conversation is pointing at, the canvas mirror of POST /focus. View-only (selection + viewport),
// never a ChangeSet step, and absent from the agent's tool schema.
api.MapPost("/canvas/focus", async (
    CanvasFocusEndpointRequest request,
    LiveDocumentBackend liveBackend,
    CancellationToken cancellationToken) =>
{
    var arguments = JsonSerializer.SerializeToElement(new
    {
        objectIds = request.ObjectIds ?? Array.Empty<Guid>(),
        zoom = request.Zoom ?? true,
    });
    try
    {
        return Results.Ok(await liveBackend.FocusCanvasObjectsAsync(arguments, request.DocId, cancellationToken));
    }
    catch (InvalidOperationException exception)
    {
        // An unregistered / ambiguous docKey is a 400 the panel can render next to the chip, not a
        // 500. Before this carried a document at all the call just went to the wrong definition.
        return Results.BadRequest(new ApiError("canvas_focus_target", exception.Message));
    }
});

// The complete current selection (Rhino objects + Grasshopper components) for the composer's "pin
// selection" affordance. Unlike the streamed runtime state — which caps ids to keep SSE frames slim —
// this returns every id (up to the plugin's selection cap) so a pinned set is the full selection the
// user captured, never a silent 32-object truncation. Read-only snapshot at call time.
api.MapGet("/selection/current", (LiveDocumentBackend liveBackend) =>
{
    var selection = liveBackend.CurrentSelection;
    if (selection is null)
    {
        return Results.Ok(new
        {
            rhinoObjectIds = Array.Empty<string>(),
            grasshopperObjects = Array.Empty<object>(),
            activeLayer = (string?)null,
            docId = (string?)null,
        });
    }
    return Results.Ok(new
    {
        rhinoObjectIds = selection.RhinoObjectIds.Select(id => id.ToString("D")).ToArray(),
        grasshopperObjects = (selection.GrasshopperObjects ?? [])
            .Select(item => new
            {
                id = item.ObjectId.ToString("D"),
                label = string.IsNullOrWhiteSpace(item.NickName) ? item.Name : item.NickName,
            })
            .ToArray(),
        activeLayer = selection.ActiveLayerName,
        // The durable docKey the GH selection came from — the runtime projection already carries it,
        // but the capture endpoint dropped it, so a pin was later resolved against whichever document
        // the session happened to be bound to. Emit it so a pin travels with its own definition.
        docId = liveBackend.CurrentSelectionDocId,
    });
});

// Which language Vino writes its prose in. A project-level preference (not per session):
// the panel toggles it, and the next thread start/resume composes it into instructions.
api.MapGet("/language", (ProjectContextStore context) =>
    Results.Ok(new LanguageSetting(context.ReadLanguage())));

api.MapPost("/language", (LanguageSetting request, ProjectContextStore context) =>
{
    context.WriteLanguage(request.Language);
    return Results.Ok(new LanguageSetting(context.ReadLanguage()));
});

// Goal cards travel as camelCase JSON in one column and one SSE field; the panel and the agent
// tool both read this exact shape.
var GoalCardJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
// The user's answer to a proposed approval card. Approving mints ONE grant bound to exactly the
// (objectId, fingerprint) pairs of the items they ticked — not the whole proposal — and stores it
// on the card so the agent's next turn can carry it in a ChangeSet. A human pressed this.
api.MapPut("/sessions/{id:guid}/approval", async (
    Guid id,
    AnswerApprovalRequest request,
    SessionStore sessionStore,
    LiveDocumentBackend liveBackend,
    DataLibrary dataLibrary,
    ProjectContextStore contextStore,
    SessionOrchestrator orchestrator,
    StandingApprovals standingApprovals,
    CancellationToken cancellationToken) =>
{
    // The answer is delivered as a turn (see DeliverCardAnswerAsync), so it shows up in the
    // transcript as prose the user can read — in the project's own language.
    var korean = string.Equals(contextStore.ReadLanguage(), "ko", StringComparison.OrdinalIgnoreCase);
    var session = await sessionStore.FindSessionAsync(id, cancellationToken);
    if (session?.ApprovalCard is null)
    {
        return Results.NotFound(new ApiError("approval_card_absent", "This session has no approval card to answer."));
    }
    var card = JsonSerializer.Deserialize<ApprovalCard>(session.ApprovalCard, GoalCardJson);
    if (card is null)
    {
        return Results.NotFound(new ApiError("approval_card_unreadable", "The stored approval card could not be read."));
    }
    // Idempotency: a card already answered (granted/rejected) must not be answered again. A second
    // PUT — a double-click, a network retry, a stale panel — would mint a fresh grant (reviving an
    // expired one), deliver the answer as another turn, and could even flip the decision. The answer
    // is recorded once; re-answering is a conflict.
    if (!string.Equals(card.Status, "proposing", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Conflict(new ApiError(
            "approval_card_answered", "This approval card has already been answered."));
    }
    if (!string.Equals(request.Status, "granted", StringComparison.OrdinalIgnoreCase))
    {
        var rejected = card with { Status = "rejected", RejectedReason = request.Reason };
        await sessionStore.SetApprovalCardAsync(
            id, JsonSerializer.Serialize(rejected, GoalCardJson), cancellationToken);
        events.Publish();
        // A refusal is an answer, so it gets delivered like one. Without this the agent sat
        // waiting on a question the user had already closed, and the user had to type "하지 마"
        // to make a button they already pressed mean anything.
        var refusal = string.IsNullOrWhiteSpace(request.Reason)
            ? (korean ? "승인하지 않았습니다." : "I did not approve this.")
            : (korean ? $"승인하지 않았습니다. {request.Reason}" : $"I did not approve this. {request.Reason}");
        // If the turn could not start (the session is paused), keep the "no" so ComposeApprovalBlock
        // delivers it once when work resumes, instead of losing a decision the user made.
        if (!await orchestrator.DeliverCardAnswerAsync(id, refusal, cancellationToken))
        {
            await sessionStore.SetApprovalCardAsync(
                id, JsonSerializer.Serialize(rejected with { DeliveryPending = true }, GoalCardJson), cancellationToken);
        }
        return Results.NoContent();
    }
    var approvedIds = request.ApprovedItemIds ?? [];

    // A scheme card settles RULES, not geometry: there is nothing to mint a grant against. The
    // approved rows are written straight into this project's scheme (merged with what is already
    // stored), and every later layer proposal resolves against it. This is the only path that
    // writes a scheme — there is deliberately no unguarded write tool.
    if (string.Equals(card.Kind, "layerScheme", StringComparison.Ordinal))
    {
        var approvedRows = card.Items
            .Where(item => approvedIds.Contains(item.Id) && item.SchemeRow is not null)
            .Select(item => item.SchemeRow!)
            .ToArray();
        if (approvedRows.Length == 0)
        {
            return Results.BadRequest(new ApiError("nothing_approved", "Approving requires at least one row."));
        }
        var elementRules = approvedRows
            .Where(row => !string.IsNullOrWhiteSpace(row.Element))
            .GroupBy(row => row.Element!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SchemeElementRule(
                group.Key,
                // The group keys the user confirmed become the vocabulary; a key that is a mark
                // family also earns a digit pattern so SC7 matches even though only SC1..SC5 were
                // on screen. That generalisation is the whole point of storing a scheme.
                group.Select(row => row.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                group
                    .Where(row => string.Equals(row.GroupKind, "markFamily", StringComparison.Ordinal))
                    .Select(row => $"^{System.Text.RegularExpressions.Regex.Escape(row.GroupKey)}[- ]?\\d")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
        var materialRules = approvedRows
            .Where(row => !string.IsNullOrWhiteSpace(row.Material))
            .GroupBy(
                row => (Material: row.Material!, Scope: row.UnderPath ?? string.Empty),
                new SchemeMaterialKeyComparer())
            .Select(group => new SchemeMaterialRule(
                group.Key.Material,
                group.Key.Scope.Length > 0 ? group.Key.Scope : null,
                group.Key.Scope.Length > 0
                    ? []
                    : group.Select(row => row.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
        // The scheme must be on disk BEFORE the card settles. Settling first left a granted card
        // over rules that were never stored, and the answered-guard above turned every retry into
        // a 409 — an unrecoverable state a user could reach with a full disk. Failing here keeps
        // the card "proposing" so pressing approve again is a real retry.
        if (!LayerCurationTables.TryWriteScheme(contextStore, elementRules, materialRules))
        {
            return Results.Problem(
                "The scheme could not be written to the project context folder.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
        var granted = card with { Status = "granted", ApprovedItemIds = approvedIds.ToArray() };
        await sessionStore.SetApprovalCardAsync(
            id, JsonSerializer.Serialize(granted, GoalCardJson), cancellationToken);
        events.Publish();
        // A scheme approval mints no grant, but it is still an answer the agent must hear — otherwise
        // the buttons settle the rules and the agent sits idle, the dead-button the other card
        // branches were already fixed for. A grant-less card has no every-turn granted block, so a
        // delivery that could not run (paused) is kept as DeliveryPending and rides the next turn
        // once via ComposeApprovalBlock, like a refusal.
        var schemeText = korean
            ? "레이어 규칙(스킴)을 확정했습니다. 이 규칙대로 정리를 진행해 주세요."
            : "The layer scheme is confirmed. Please tidy the layers by these rules.";
        if (!await orchestrator.DeliverCardAnswerAsync(id, schemeText, cancellationToken))
        {
            await sessionStore.SetApprovalCardAsync(
                id,
                JsonSerializer.Serialize(granted with { DeliveryPending = true }, GoalCardJson),
                cancellationToken);
        }
        return Results.NoContent();
    }

    var targets = card.Items
        .Where(item => approvedIds.Contains(item.Id))
        .SelectMany(item => item.Targets)
        .Select(target => (target.ObjectId, target.Fingerprint))
        .ToArray();
    if (targets.Length == 0)
    {
        return Results.BadRequest(new ApiError("nothing_approved", "Approving requires at least one item."));
    }
    var grantJson = JsonSerializer.SerializeToElement(liveBackend.MintApprovalGrant(targets), GoalCardJson);
    if (request.RememberSession)
    {
        // "허용 + 이 세션에서 계속 허용": besides this card's grant, later destructive submits
        // from this session auto-issue their grant without a card (until released or restart).
        standingApprovals.Grant(id);
    }
    // Keep the expiry the mint just handed back. It used to be dropped here, leaving the card
    // claiming "승인됨" over a key that had already lapsed — the panel could not warn, and the
    // user discovered it only when the write was refused.
    var grantExpiresAt = grantJson.TryGetProperty("expiresAt", out var expiresElement) &&
        expiresElement.TryGetDateTimeOffset(out var parsedExpiry)
            ? parsedExpiry
            : (DateTimeOffset?)null;
    // Choices are kept only for items that were actually granted: a choice attached to a refused
    // item is not a decision the user made about anything that will happen, and injecting it next
    // turn would read as permission to act on the item they declined.
    var choices = request.Choices?
        .Where(entry => approvedIds.Contains(entry.Key))
        .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    var items = card.Items;
    var preset = card.Preset;
    // Layer cards settle two things at approval time, and both must land BEFORE the granted card
    // is stored — the agent's next turn copies these values verbatim.
    //   1. A switched colour convention: the rows were computed under the old preset.
    //   2. A triage row's material: the user picked a family from the choices, so the row's empty
    //      canonical/material and its unchanged colour all have to resolve now. Without this the
    //      block would hand the agent the layer's CURRENT colour for exactly the row the user just
    //      classified, and the audit could never see the layer as labeled (it needs canonical too).
    var keepColors = string.Equals(
        request.ColorPolicy ?? card.ColorPolicy,
        "keep",
        StringComparison.OrdinalIgnoreCase);
    if (string.Equals(card.Kind, "layerSemantics", StringComparison.Ordinal))
    {
        try
        {
            var tables = LayerCurationTables.Load(dataLibrary, contextStore);
            var presetId = tables.PresetId;
            if (!string.IsNullOrWhiteSpace(request.Preset) &&
                tables.Palette.Presets.Any(option =>
                    string.Equals(option.Id, request.Preset, StringComparison.OrdinalIgnoreCase)))
            {
                presetId = request.Preset!;
                if (!string.Equals(presetId, card.Preset?.Selected, StringComparison.OrdinalIgnoreCase))
                {
                    LayerCurationTables.TryWritePreset(contextStore, presetId);
                }
                preset = card.Preset is null ? null : card.Preset with { Selected = presetId };
            }
            items = card.Items
                .Select(item =>
                {
                    if (item.LayerRow is not { } row)
                    {
                        return item;
                    }
                    var material = row.Material;
                    var canonical = row.Canonical;
                    var confidence = row.Confidence;
                    var evidence = row.Evidence;
                    if (string.IsNullOrEmpty(material) &&
                        approvedIds.Contains(item.Id) &&
                        request.Choices is not null &&
                        request.Choices.TryGetValue(item.Id, out var chosen) &&
                        tables.Palette.TryGetFamily(presetId, chosen, out _))
                    {
                        // A user-classified layer is labeled by MATERIAL: the canonical name is the
                        // family they chose, uppercased, so the audit's labeled-predicate (which
                        // needs both keys) closes on the next scan.
                        material = chosen;
                        canonical = chosen.ToUpperInvariant();
                        confidence = "high";
                        evidence = $"user choice: {chosen}";
                    }
                    // "keep" pins every proposed colour to the current one, so the granted block
                    // asks for labels and nothing else. Unticking a row could not express this —
                    // it would drop that row's label too.
                    var argb = keepColors
                        ? row.CurrentArgbColor
                        : !string.IsNullOrEmpty(material) &&
                            tables.Palette.TryGetFamily(presetId, material, out _)
                                ? tables.Palette.BaseArgb(presetId, material)
                                : row.ProposedArgbColor;
                    return item with
                    {
                        LayerRow = row with
                        {
                            Material = material,
                            Canonical = canonical,
                            Confidence = confidence,
                            Evidence = evidence,
                            ProposedArgbColor = argb,
                        },
                    };
                })
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or JsonException or FormatException)
        {
            // An unreadable palette must not lose the user's approval: keep the card's own values.
        }
    }
    var updated = card with
    {
        Status = "granted",
        GrantId = grantJson.GetProperty("grantId").GetString(),
        ApprovedItemIds = approvedIds.ToArray(),
        Choices = choices is { Count: > 0 } ? choices : null,
        Items = items,
        Preset = preset,
        GrantExpiresAt = grantExpiresAt,
        RejectedReason = null,
        ColorPolicy = card.ColorPolicy is null ? null : (keepColors ? "keep" : "recolor"),
    };
    await sessionStore.SetApprovalCardAsync(id, JsonSerializer.Serialize(updated, GoalCardJson), cancellationToken);
    events.Publish();
    // Pressing 승인 IS the instruction to proceed — it carries the same grant a typed
    // "승인했어, 진행해줘" would have carried, through the same ComposeApprovalBlock. Resuming here
    // removes the typed sentence AND the failure mode it hid: the grant is minted with a 15-minute
    // TTL, so a card approved and left alone died before anyone spent it.
    var approvalText = korean
        ? $"승인했습니다. 승인한 {approvedIds.Count}개 항목만 진행해 주세요."
        : $"Approved. Proceed with the {approvedIds.Count} approved item(s) only.";
    await orchestrator.DeliverCardAnswerAsync(id, approvalText, cancellationToken);
    return Results.NoContent();
});

// The user's click on an ask card. This is the whole point of the card: the answer becomes a turn,
// so pressing a button and typing the sentence are the same act — and the agent, which stopped
// because it had asked something, simply continues.
api.MapPut("/sessions/{id:guid}/ask", async (
    Guid id,
    AnswerAskRequest request,
    SessionStore sessionStore,
    ProjectContextStore contextStore,
    SessionOrchestrator orchestrator,
    CancellationToken cancellationToken) =>
{
    var session = await sessionStore.FindSessionAsync(id, cancellationToken);
    if (session?.AskCard is null)
    {
        return Results.NotFound(new ApiError("ask_card_absent", "This session has no question to answer."));
    }
    var card = JsonSerializer.Deserialize<AskCard>(session.AskCard, GoalCardJson);
    if (card is null)
    {
        return Results.NotFound(new ApiError("ask_card_unreadable", "The stored question could not be read."));
    }
    // Idempotency: an answered question must not be re-answered — a second PUT would deliver the
    // choice as another turn. The answer rides once.
    if (!string.Equals(card.Status, "asking", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Conflict(new ApiError(
            "ask_card_answered", "This question has already been answered."));
    }
    var chosen = card.Options.FirstOrDefault(option =>
        string.Equals(option.Id, request.OptionId, StringComparison.Ordinal));
    if (chosen is null)
    {
        return Results.BadRequest(new ApiError(
            "ask_option_unknown",
            $"'{request.OptionId}' is not one of this question's options: " +
            string.Join(", ", card.Options.Select(option => option.Id)) + "."));
    }
    var answered = card with
    {
        Status = "answered",
        ChosenOptionId = chosen.Id,
        Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note!.Trim(),
        AnsweredAt = DateTimeOffset.UtcNow,
    };
    await sessionStore.SetAskCardAsync(id, JsonSerializer.Serialize(answered, GoalCardJson), cancellationToken);
    events.Publish();

    // The LABEL, not the id: the agent should read back what the user chose in the words the user
    // saw, so the transcript reads like the conversation it replaced.
    var korean = string.Equals(contextStore.ReadLanguage(), "ko", StringComparison.OrdinalIgnoreCase);
    var text = korean
        ? $"\"{chosen.Label}\"(으)로 진행해 주세요."
        : $"Go with \"{chosen.Label}\".";
    if (answered.Note is { Length: > 0 } note)
    {
        text += korean ? $" {note}" : $" {note}";
    }
    // If the turn could not start (the session is paused), keep the choice so ComposeAskBlock delivers
    // it once when work resumes, instead of losing the answer the user clicked.
    if (!await orchestrator.DeliverCardAnswerAsync(id, text, cancellationToken))
    {
        await sessionStore.SetAskCardAsync(
            id, JsonSerializer.Serialize(answered with { DeliveryPending = true }, GoalCardJson), cancellationToken);
    }
    return Results.NoContent();
});

// Clears an answered approval card. `sessions.approval_card` is a single column that nothing ever
// emptied, so a card stayed on screen for the rest of the session — long after it was answered,
// with its stale zoom errors — and a granted-then-expired one kept injecting its expiry notice
// into every turn. Answered cards only: a proposing card is a live question and dismissing it
// would silently drop the agent's request.
api.MapDelete("/sessions/{id:guid}/approval", async (
    Guid id,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    var session = await sessionStore.FindSessionAsync(id, cancellationToken);
    if (session?.ApprovalCard is null)
    {
        return Results.NoContent(); // already gone — dismissing twice is not an error
    }
    var card = JsonSerializer.Deserialize<ApprovalCard>(session.ApprovalCard, GoalCardJson);
    if (card is not null && string.Equals(card.Status, "proposing", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new ApiError(
            "approval_card_pending",
            "This card is still waiting for an answer. Approve or refuse it instead of dismissing it."));
    }
    await sessionStore.SetApprovalCardAsync(id, null, cancellationToken);
    events.Publish();
    return Results.NoContent();
});

// Clears an answered ask card, mirroring DELETE /approval. The ask column had no dismiss path, so an
// answered question stayed on screen for the rest of the session. Answered cards only: a card still
// "asking" is a live question and dismissing it would silently drop the agent's request.
api.MapDelete("/sessions/{id:guid}/ask", async (
    Guid id,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    var session = await sessionStore.FindSessionAsync(id, cancellationToken);
    if (session?.AskCard is null)
    {
        return Results.NoContent(); // already gone — dismissing twice is not an error
    }
    var card = JsonSerializer.Deserialize<AskCard>(session.AskCard, GoalCardJson);
    if (card is not null && string.Equals(card.Status, "asking", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new ApiError(
            "ask_card_pending",
            "This question is still waiting for an answer. Answer it instead of dismissing it."));
    }
    await sessionStore.SetAskCardAsync(id, null, cancellationToken);
    events.Publish();
    return Results.NoContent();
});

// Clears a settled goal card, mirroring DELETE /approval. A confirmed goal otherwise rides EVERY
// later turn forever (ComposeGoalBlock) with no way to retire it once the work is done. A goal still
// "proposing" is a live question — confirm or reject it instead of dismissing.
api.MapDelete("/sessions/{id:guid}/goal", async (
    Guid id,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    var session = await sessionStore.FindSessionAsync(id, cancellationToken);
    if (session?.GoalCard is null)
    {
        return Results.NoContent();
    }
    var card = JsonSerializer.Deserialize<GoalCard>(session.GoalCard, GoalCardJson);
    if (card is not null && string.Equals(card.Status, "proposing", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new ApiError(
            "goal_card_pending",
            "This goal is still proposed. Confirm or reject it instead of dismissing it."));
    }
    await sessionStore.SetGoalCardAsync(id, null, cancellationToken);
    events.Publish();
    return Results.NoContent();
});

// SHARED CONTRACT: lifts the host-enforced recovery halt of one session (the projection's "halt"
// field). Empty body -> 204; idempotent (resuming a non-halted session is also 204); 404 for an
// unknown session. Same base path and token auth as the approval endpoint above. A human pressed
// this — the model-side twin is the recovery_resume tool.
api.MapPost("/sessions/{id:guid}/resume", async (
    Guid id,
    SessionStore sessionStore,
    LiveDocumentBackend liveBackend,
    CancellationToken cancellationToken) =>
{
    var session = await sessionStore.FindSessionAsync(id, cancellationToken);
    if (session is null)
    {
        return Results.NotFound(new ApiError("session_not_found", $"Session {id:D} was not found."));
    }
    await liveBackend.ResumeSessionFromPanelAsync(id, cancellationToken);
    return Results.NoContent();
});

api.MapPost("/sessions", async (
    CreateSessionRequest request,
    SessionStore sessionStore,
    ILiveDocumentQueueControl queue,
    CancellationToken cancellationToken) =>
{
    // The backend is fixed at creation (conversation stores are not portable across backends).
    // Null/blank means default (codex); unknown ids are refused loudly rather than coerced.
    if (!AgentBackends.TryNormalize(request.Backend, out var backend))
    {
        return Results.BadRequest(new ApiError(
            "unknown_backend",
            $"Backend '{request.Backend}' is not supported."));
    }
    // ModelProfile now carries the reasoning-effort level directly (low..ultra) — manual effort, no
    // adaptive routing. NormalizeEffort validates and maps any legacy profile value for back-compat.
    var session = await sessionStore.CreateSessionAsync(
        request with { ModelProfile = NormalizeEffort(request.ModelProfile), Backend = backend },
        cancellationToken);
    await queue.RefreshScheduleAsync(cancellationToken);
    events.Publish();
    return Results.Created($"/api/v1/sessions/{session.Id:D}", session);
});

api.MapPut("/sessions/order", async (
    ReorderSessionsRequest request,
    SessionStore sessionStore,
    ILiveDocumentQueueControl queue,
    CancellationToken cancellationToken) =>
{
    await sessionStore.ReorderAsync(request.OrderedSessionIds, request.OrderVersion, cancellationToken);
    await queue.RefreshScheduleAsync(cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapPut("/sessions/{id:guid}/pause", async (
    Guid id,
    SetPausedRequest request,
    SessionOrchestrator orchestrator,
    CancellationToken cancellationToken) =>
{
    await orchestrator.SetSessionPausedAsync(id, request.Paused, cancellationToken);
    await queueControl.RefreshScheduleAsync(cancellationToken);
    return Results.NoContent();
});

// Stop & edit: interrupt the turn and pull the last user message back for editing.
api.MapPost("/sessions/{id:guid}/retract-last", async (
    Guid id,
    SessionOrchestrator orchestrator,
    CancellationToken cancellationToken) =>
{
    var content = await orchestrator.StopAndRetractLastMessageAsync(id, cancellationToken);
    await queueControl.RefreshScheduleAsync(cancellationToken);
    return Results.Ok(new { content });
});

api.MapPut("/sessions/{id:guid}/target", async (
    Guid id,
    SetSessionTargetRequest request,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    await sessionStore.SetGrasshopperDocAsync(id, request.GrasshopperDoc, cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapPut("/sessions/{id:guid}/title", async (
    Guid id,
    RenameSessionRequest request,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    await sessionStore.SetSessionTitleAsync(id, request.Name, cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapPut("/sessions/{id:guid}/model", async (
    Guid id,
    SetModelRequest request,
    SessionStore sessionStore,
    IAgentBackendResolver agentBackends,
    CancellationToken cancellationToken) =>
{
    // Fixed-per-session enforcement's real surface: there is no "change backend" endpoint to
    // refuse, but a model pin could smuggle one backend's model onto another's session. Reject
    // only DEFINITIVE mismatches (the model provably lives in a different backend's catalog);
    // unknown strings keep today's permissive behavior.
    if (!string.IsNullOrWhiteSpace(request.Model) &&
        await sessionStore.FindSessionAsync(id, cancellationToken) is { } pinTarget)
    {
        var sessionBackend = AgentBackends.NormalizeStored(pinTarget.Backend);
        foreach (var candidate in agentBackends.All)
        {
            if (string.Equals(candidate.Id, sessionBackend, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var foreignModels = await candidate.Models.ReadModelsAsync(cancellationToken);
            if (foreignModels.Any(entry =>
                    string.Equals(entry.Model, request.Model, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.Id, request.Model, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.BadRequest(new ApiError(
                    "model_backend_mismatch",
                    $"Model '{request.Model}' belongs to backend '{candidate.Id}', not this session's '{sessionBackend}'."));
            }
        }
    }
    await sessionStore.UpdatePreferencesAsync(
        id,
        NormalizeEffort(request.ModelProfile),
        request.Model,
        true,
        cancellationToken);
    events.Publish();
    return Results.NoContent();
});

// The session's permission level (review / standard / fullAuto). A human presses this — it is a
// deliberate setting like model/effort and persists with the session. Dropping OUT of fullAuto or
// into review also releases any standing consent: lowering the level must actually lower it.
api.MapPut("/sessions/{id:guid}/permission", async (
    Guid id,
    SetPermissionRequest request,
    SessionStore sessionStore,
    StandingApprovals standingApprovals,
    CancellationToken cancellationToken) =>
{
    var mode = PermissionModes.Normalize(request.Mode);
    await sessionStore.SetPermissionModeAsync(id, mode, cancellationToken);
    if (!PermissionModes.IsFullAuto(mode))
    {
        standingApprovals.Release(id);
    }
    events.Publish();
    return Results.NoContent();
});

// Releases the session's standing consent ("이 세션에서 계속 허용") without touching the mode.
api.MapDelete("/sessions/{id:guid}/permission/standing", (
    Guid id,
    StandingApprovals standingApprovals) =>
{
    standingApprovals.Release(id);
    events.Publish();
    return Results.NoContent();
});


// The user's verdict on a proposed goal card. Confirming (optionally with edits) is what the
// agent is held to afterwards: the confirmed card rides every following turn's input, and the
// self-score at the end must answer these criteria. A human pressed this — never an agent step.
api.MapPut("/sessions/{id:guid}/goal", async (
    Guid id,
    SetGoalRequest request,
    SessionStore sessionStore,
    ProjectContextStore contextStore,
    SessionOrchestrator orchestrator,
    CancellationToken cancellationToken) =>
{
    var korean = string.Equals(contextStore.ReadLanguage(), "ko", StringComparison.OrdinalIgnoreCase);
    var session = await sessionStore.FindSessionAsync(id, cancellationToken);
    if (session?.GoalCard is null)
    {
        return Results.NotFound(new ApiError("goal_card_absent", "This session has no goal card to answer."));
    }
    var card = JsonSerializer.Deserialize<GoalCard>(session.GoalCard, GoalCardJson);
    if (card is null)
    {
        return Results.NotFound(new ApiError("goal_card_unreadable", "The stored goal card could not be read."));
    }
    // Idempotency: only a proposed goal can be answered. Re-confirming or flipping an already-settled
    // goal would deliver another turn; clear it with DELETE and re-propose instead.
    if (!string.Equals(card.Status, "proposing", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Conflict(new ApiError(
            "goal_card_answered", "This goal card has already been answered."));
    }
    var status = string.Equals(request.Status, "confirmed", StringComparison.OrdinalIgnoreCase)
        ? "confirmed"
        : "rejected";
    var updated = card with
    {
        Status = status,
        // Edits win: the user is held to what they approved, not to what was proposed.
        Objective = string.IsNullOrWhiteSpace(request.Objective) ? card.Objective : request.Objective!,
        Criteria = request.Criteria is { Count: > 0 } ? request.Criteria : card.Criteria,
        ChosenOption = request.ChosenOption ?? card.ChosenOption,
        ConfirmedAt = DateTimeOffset.UtcNow,
    };
    await sessionStore.SetGoalCardAsync(id, JsonSerializer.Serialize(updated, GoalCardJson), cancellationToken);
    events.Publish();
    // Answering the goal card starts the work, exactly like answering an approval card does.
    // Without this the buttons stored a verdict and returned 204: the card flipped to "confirmed"
    // and NOTHING RAN, so picking "step by step" or "all at once" looked like a dead button. The
    // chosen option is the instruction, so it rides the turn — ComposeGoalBlock then carries the
    // confirmed card into this and every later turn.
    var goalText = status == "confirmed"
        ? (korean
            ? $"목표를 확정했습니다{DescribeChosenOption(updated.ChosenOption, card)}. 이 목표대로 진행해 주세요."
            : $"Goal confirmed{DescribeChosenOption(updated.ChosenOption, card)}. Proceed on that basis.")
        : (korean
            ? "그 목표는 제가 원하는 것이 아닙니다. 다시 정리해 주세요."
            : "That is not the goal I want. Please reframe it.");
    // A confirmed goal has a backup channel — ComposeGoalBlock rides it on every later turn. A
    // REJECTED one has none: if this delivery cannot run (the session is paused), the agent never
    // hears the "no" and keeps framing work against a goal the user refused. Keep the verdict as
    // DeliveryPending so it rides the next turn exactly once.
    if (!await orchestrator.DeliverCardAnswerAsync(id, goalText, cancellationToken))
    {
        await sessionStore.SetGoalCardAsync(
            id,
            JsonSerializer.Serialize(updated with { DeliveryPending = true }, GoalCardJson),
            cancellationToken);
    }
    return Results.NoContent();
});

// The option the user picked, named so the agent acts on the choice instead of re-asking which
// one it was. Falls back to the raw id when the card carries no matching option label.
static string DescribeChosenOption(string? chosenId, GoalCard card)
{
    if (string.IsNullOrWhiteSpace(chosenId)) return string.Empty;
    var label = card.Options?.FirstOrDefault(option =>
        string.Equals(option.Id, chosenId, StringComparison.Ordinal))?.Label;
    return $" — \"{(string.IsNullOrWhiteSpace(label) ? chosenId : label)}\"";
}

// Soft-delete: hide from the active list but keep everything, so it can be restored.
api.MapDelete("/sessions/{id:guid}", async (
    Guid id,
    SessionStore sessionStore,
    LiveDocumentBackend liveBackend,
    McpSessionSecretStore mcpSecrets,
    CancellationToken cancellationToken) =>
{
    // A deleted session's MCP secret must stop resolving immediately — a still-running CLI child
    // holding the old mcp.json loses tool access the moment the session is gone. Look up the
    // conversation id BEFORE the delete hides the row.
    var deletedSession = await sessionStore.FindSessionAsync(id, cancellationToken);
    await sessionStore.SetSessionDeletedAsync(id, deleted: true, cancellationToken);
    if (deletedSession?.ExternalConversationId is { Length: > 0 } deletedConversation)
    {
        mcpSecrets.Revoke(deletedConversation);
    }
    // A hidden session can never be resumed from the panel, so its recovery-halt latch (and the
    // other session-scoped runtime latches) must not outlive the delete. Runtime state ONLY: the
    // session's resource-ledger baselines stay (in memory and durably) so a later restore comes
    // back with gptino:auto working — ledger removal happens only on purge.
    liveBackend.ForgetSessionRuntimeState(id);
    await queueControl.RefreshScheduleAsync(cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapGet("/sessions/deleted", async (
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
    Results.Ok(await sessionStore.ReadDeletedSessionsAsync(cancellationToken)));

api.MapPost("/sessions/{id:guid}/restore", async (
    Guid id,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    await sessionStore.SetSessionDeletedAsync(id, deleted: false, cancellationToken);
    await queueControl.RefreshScheduleAsync(cancellationToken);
    events.Publish();
    return Results.NoContent();
});

// Permanent delete: removes the session and its transcript for good (panel gates this behind an
// explicit confirmation).
api.MapDelete("/sessions/{id:guid}/purge", async (
    Guid id,
    SessionStore sessionStore,
    LiveDocumentBackend liveBackend,
    CancellationToken cancellationToken) =>
{
    await sessionStore.PurgeSessionAsync(id, cancellationToken);
    // Purge is the point of no return: runtime latches AND the session's resource-ledger rows
    // (memory + durable) go — a purged session can never submit again.
    liveBackend.ForgetSessionCompletely(id);
    await queueControl.RefreshScheduleAsync(cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapGet("/sessions/{id:guid}/messages", async (
    Guid id,
    long? after,
    int? limit,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
    Results.Ok(await sessionStore.ReadMessagesAsync(id, after ?? 0, limit ?? 250, cancellationToken)));

api.MapPost("/sessions/{id:guid}/messages", async (
    Guid id,
    SendMessageRequest request,
    SessionOrchestrator orchestrator,
    CancellationToken cancellationToken) =>
    Results.Accepted(value: await orchestrator.SubmitMessageAsync(id, request, cancellationToken)));

api.MapPost("/sessions/{id:guid}/terminal", async (
    Guid id,
    SessionStore sessionStore,
    TerminalLauncher launcher,
    CancellationToken cancellationToken) =>
{
    var session = await sessionStore.FindSessionAsync(id, cancellationToken)
        ?? throw new KeyNotFoundException($"Session {id:D} was not found.");
    await launcher.LaunchAsync(session, cancellationToken);
    return Results.NoContent();
});

api.MapPut("/runtime/pause", (SetPausedRequest request) =>
{
    control.SetPaused(request.Paused);
    queueControl.SetPaused(request.Paused);
    events.Publish();
    return Results.NoContent();
});

api.MapPost("/runtime/stop-current", async (CancellationToken cancellationToken) =>
{
    await backend.StopCurrentAsync(cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapPost("/runtime/login-terminal", (CodexLoginLauncher loginLauncher) =>
{
    if (loginLauncher.TryLaunch(out var message))
    {
        events.Publish();
        return Results.NoContent();
    }
    return Results.Content(message, "text/plain", System.Text.Encoding.UTF8, 409);
});

api.MapPost("/runtime/claude-login-terminal", (ClaudeLoginLauncher loginLauncher) =>
{
    if (loginLauncher.TryLaunch(out var message))
    {
        events.Publish();
        return Results.NoContent();
    }
    return Results.Content(message, "text/plain", System.Text.Encoding.UTF8, 409);
});

api.MapGet("/models", async (IAgentBackendResolver backends, CancellationToken cancellationToken) =>
{
    // Flat union across backends — partitioning is the Provider field plus the panel's
    // session-backend filter, not separate endpoints.
    var models = new List<ModelView>();
    foreach (var backend in backends.All)
    {
        models.AddRange(await backend.Models.ReadModelsAsync(cancellationToken));
    }
    return Results.Ok(models);
});

api.MapGet("/archive", async (ProjectArchiveReader archive, CancellationToken cancellationToken) =>
    Results.Ok(await archive.ListProjectsAsync(cancellationToken)));

api.MapGet("/archive/{fingerprint}/sessions/{sessionId:guid}/messages", async (
    string fingerprint,
    Guid sessionId,
    int? limit,
    ProjectArchiveReader archive,
    CancellationToken cancellationToken) =>
    Results.Ok(await archive.ReadMessagesAsync(fingerprint, sessionId, limit ?? 500, cancellationToken)));

api.MapPost("/archive/{fingerprint}/sessions/{sessionId:guid}/import", async (
    string fingerprint,
    Guid sessionId,
    ProjectArchiveReader archive,
    SessionStore sessionStore,
    ILiveDocumentQueueControl queue,
    CancellationToken cancellationToken) =>
{
    // Read-only from the foreign root, then a deterministic seed and one transactional insert into
    // the live runtime.db. The POST /sessions ritual (RefreshScheduleAsync + events.Publish) makes
    // the new session appear over SSE without any client refetch. A missing project/session is a 404
    // (KeyNotFoundException) and an unreadable root is a 409 (InvalidOperationException) via the
    // shared exception middleware.
    var export = await archive.ReadSessionForImportAsync(fingerprint, sessionId, cancellationToken);
    var seed = ImportedSessionSeedBuilder.Build(fingerprint, export);
    var session = await sessionStore.ImportSessionAsync(seed, cancellationToken);
    await queue.RefreshScheduleAsync(cancellationToken);
    events.Publish();
    return Results.Created($"/api/v1/sessions/{session.Id:D}", session);
});

api.MapGet("/health", () =>
{
    var codexProcess = codex.ReadProcessIdentity();
    return Results.Ok(new
    {
        status = "ok",
        bridgeConnected = backend.IsConnected,
        codexRunning = codexProcess is not null,
        codexProcessId = codexProcess?.ProcessId,
        codexProcessStartTimeUtc = codexProcess?.ProcessStartTimeUtc,
        processId = Environment.ProcessId
    });
});

if (developmentDataDirectory is not null)
{
    api.MapGet("/dev/snapshot", async (
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        // The model-facing default became the cheap meta read; this dev gate's consumers
        // (Vino.LiveE2E) verify canvas objects/wires, so it pins the full-dump canvas scope.
        var arguments = JsonSerializer.SerializeToElement(new { scopes = new[] { "canvas" } });
        return Results.Ok(await liveBackend.ReadSnapshotAsync(
            arguments,
            cancellationToken));
    });
    // Model-free write path for live gates: submit a ChangeSet exactly as change_submit would,
    // with no agent turn in the loop. A gate that has to prompt a model to exercise a write cannot
    // assert what it is grading (the model may word it differently, or not do it at all) and burns
    // subscription quota per run. Dev-only, like every endpoint in this block.
    api.MapPost("/dev/change/{sessionId:guid}", async (
        Guid sessionId,
        JsonElement request,
        SessionStore sessionStore,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        var session = await sessionStore.FindSessionAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return Results.NotFound(new { error = $"Session {sessionId:D} was not found." });
        }
        return Results.Ok(await liveBackend.SubmitInlineChangeAsync(session, request, cancellationToken));
    });
    // arrange_layout without a model turn, so a gate can move components deterministically and then
    // grade the rewind that puts them back.
    api.MapPost("/dev/arrange/{sessionId:guid}", async (
        Guid sessionId,
        JsonElement request,
        SessionStore sessionStore,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        var session = await sessionStore.FindSessionAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return Results.NotFound(new { error = $"Session {sessionId:D} was not found." });
        }
        return Results.Ok(await liveBackend.ArrangeLayoutAsync(session, request, cancellationToken));
    });
    // The two read/restore halves of layout rewind, model-free, for the same reason.
    api.MapGet("/dev/layout-history/{sessionId:guid}", async (
        Guid sessionId,
        int? limit,
        SessionStore sessionStore,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        var session = await sessionStore.FindSessionAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return Results.NotFound(new { error = $"Session {sessionId:D} was not found." });
        }
        var arguments = JsonSerializer.SerializeToElement(
            limit is > 0 ? new Dictionary<string, object> { ["limit"] = limit.Value } : new Dictionary<string, object>());
        return Results.Ok(await liveBackend.ReadLayoutHistoryAsync(session, arguments));
    });
    api.MapPost("/dev/rewind-layout", async (
        DevRewindRequest request,
        SessionStore sessionStore,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        var session = await sessionStore.FindSessionAsync(request.SessionId, cancellationToken);
        if (session is null)
        {
            return Results.NotFound(new { error = $"Session {request.SessionId:D} was not found." });
        }
        var arguments = JsonSerializer.SerializeToElement(new
        {
            sha = request.Sha,
            restoreStateBefore = request.RestoreStateBefore,
            scope = request.Scope ?? "positions",
            wait = true,
        });
        return Results.Ok(await liveBackend.RewindLayoutAsync(session, arguments, cancellationToken));
    });
    api.MapGet("/dev/rhino-objects", async (
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        // 500 is the adapter's hard cap (ValidateListRequest); 1000 made every dev call fail.
        var arguments = JsonSerializer.SerializeToElement(new { limit = 500 });
        return Results.Ok(await liveBackend.ListRhinoObjectsAsync(arguments, cancellationToken));
    });
    // Same rationale as /dev/audit: the structural-extract live gate needs the extraction result
    // with no model in the loop. Product surface is the structural_extract tool.
    api.MapGet("/dev/structural-extract", async (
        string? layerFilter,
        double? prototypeHeight,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        // Omitted, not null: StructuralExtractRequest's threshold properties are non-nullable
        // (they carry validated defaults), so a null in the JSON fails deserialization outright.
        var query = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(layerFilter))
        {
            query["layerFilter"] = layerFilter;
        }
        if (prototypeHeight is > 0)
        {
            query["prototypeHeight"] = prototypeHeight.Value;
        }
        var arguments = JsonSerializer.SerializeToElement(query);
        return Results.Ok(await liveBackend.ReadStructuralExtractAsync(arguments, cancellationToken));
    });
    // Model-free grading for the load sampler: the live gate proves "thickness x grid = volume"
    // against the fixture's closed-form volumes before any agent turn runs. Product surface is
    // the structural_loads tool.
    api.MapGet("/dev/structural-load-sample", async (
        string? layerFilter,
        double? gridSpacing,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            sources = new[] { new { name = layerFilter ?? "probe", layerFilter = layerFilter ?? string.Empty } },
            gridSpacing = gridSpacing is > 0 ? gridSpacing.Value : 250.0,
        });
        return Results.Ok(await liveBackend.ReadStructuralLoadSampleAsync(arguments, cancellationToken));
    });
    // Viewport capture with no model in the loop: the live gate for rhino_view_capture (and any
    // harness wanting a clean render) hits the same bridge op the tool uses.
    api.MapGet("/dev/viewport-capture", async (
        string? viewName,
        int? width,
        int? height,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        var query = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(viewName))
        {
            query["viewName"] = viewName;
        }
        if (width is > 0)
        {
            query["width"] = width.Value;
        }
        if (height is > 0)
        {
            query["height"] = height.Value;
        }
        var arguments = JsonSerializer.SerializeToElement(query);
        // Serve the PNG itself, matching /dev/canvas-capture — the JSON envelope made every
        // consumer (gate scripts, the quality runner, even a critic model) decode base64 first.
        var wrapped = await liveBackend.CaptureRhinoViewAsync(arguments, cancellationToken);
        var envelope = JsonSerializer.SerializeToElement(wrapped, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var pngBase64 = envelope.TryGetProperty("result", out var captureResult) &&
            captureResult.TryGetProperty("pngBase64", out var png)
                ? png.GetString()
                : null;
        return pngBase64 is null
            ? Results.Problem("The capture returned no image data.", statusCode: 502)
            : Results.File(Convert.FromBase64String(pngBase64), "image/png");
    });
    // Grasshopper canvas render with no model in the loop: the canvas mirror of
    // /dev/viewport-capture. Serves the PNG itself (not the JSON envelope) so a browser or a
    // gate script can look at the canvas directly; width/height clamp the output raster only —
    // the framing is always the whole definition, adapter-side.
    api.MapGet("/dev/canvas-capture", async (
        int? width,
        int? height,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        var query = new Dictionary<string, object>();
        if (width is > 0)
        {
            query["width"] = width.Value;
        }
        if (height is > 0)
        {
            query["height"] = height.Value;
        }
        var arguments = JsonSerializer.SerializeToElement(query);
        var response = await liveBackend.CaptureCanvasImageAsync(arguments, cancellationToken);
        var envelope = JsonSerializer.SerializeToElement(response, BridgeProtocol.JsonOptions);
        var pngBase64 = envelope.GetProperty("result").GetProperty("pngBase64").GetString()
            ?? throw new InvalidOperationException("canvas.capture returned no pngBase64.");
        return Results.File(Convert.FromBase64String(pngBase64), "image/png");
    });
    // Deterministic canvas-layout audit with no model in the loop, computed host-side from the
    // SAME snapshot /dev/snapshot serves. Standalone on purpose: the /dev/audit pipeline is the
    // adapter-side rhino.audit (Rhino geometry, adapter detection code), while this measures
    // Grasshopper canvas layout from data the host already holds — forcing it through the bridge
    // audit surface would add a wire operation for a computation that needs none.
    api.MapGet("/dev/canvas-layout-audit", async (
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
        Results.Ok(await liveBackend.ComputeCanvasLayoutAuditAsync(cancellationToken)));
    // Server-computed document audit, for harnesses that must check a claim WITHOUT asking the
    // agent whether it is true. The product surface for this is the rhino_audit tool; this is the
    // same backend call with no model in the loop, which is what a live gate needs to grade one.
    api.MapGet("/dev/audit", async (
        string kind,
        double? tolerance,
        double? bandFactor,
        int? limit,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            kind,
            tolerance,
            bandFactor,
            limit = limit ?? 50,
        });
        return Results.Ok(await liveBackend.ReadRhinoAuditAsync(arguments, cancellationToken));
    });
    api.MapGet("/dev/grasshopper/{objectId:guid}/outputs", async (
        Guid objectId,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        var arguments = JsonSerializer.SerializeToElement(new { objectId });
        return Results.Ok(await liveBackend.InspectCanvasOutputsAsync(
            arguments,
            cancellationToken));
    });
    api.MapGet("/dev/grasshopper/{componentId:guid}/python", async (
        Guid componentId,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            scopes = new[]
            {
                $"script:{componentId:D}",
                $"script-messages:{componentId:D}"
            }
        });
        return Results.Ok(await liveBackend.ReadSnapshotAsync(
            arguments,
            cancellationToken));
    });
    api.MapGet("/dev/terminals/{sessionId:guid}", (
        Guid sessionId,
        TerminalLauncher launcher) =>
        Results.Ok(launcher.ReadStatus(sessionId)));
    api.MapPut("/dev/writer/pause", (
        SetPausedRequest request,
        ILiveDocumentQueueControl writerQueue,
        EventHub eventHub) =>
    {
        writerQueue.SetPaused(request.Paused);
        eventHub.Publish();
        return Results.NoContent();
    });
}

app.MapFallback(async context =>
{
    var indexPath = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "index.html");
    if (File.Exists(indexPath))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(indexPath, context.RequestAborted);
        return;
    }
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(
        "<html><body><h1>Vino AgentHost</h1><p>Panel assets are not installed in this build.</p></body></html>",
        context.RequestAborted);
});

await app.RunAsync();

static async Task SendStateEventAsync(
    HttpContext context,
    RuntimeStateProjector projector,
    CancellationToken cancellationToken)
{
    var state = await projector.BuildAsync(cancellationToken);
    var json = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await context.Response.WriteAsync($"event: state\ndata: {json}\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);
}

static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
{
    if (context.Response.HasStarted)
    {
        return;
    }
    context.Response.StatusCode = statusCode;
    await context.Response.WriteAsJsonAsync(new ApiError(code, message));
}

static bool HasValidApiToken(HttpContext context, string expected)
{
    var header = context.Request.Headers["X-Vino-Token"].FirstOrDefault();
    var cookie = context.Request.Cookies["vino_runtime"];
    return TokenEquals(header, expected) || TokenEquals(cookie, expected);
}

// The session's reasoning-effort level (low..ultra) — clamped to the chosen model's advertised set at
// turn time. Legacy profile values (auto/fast/standard/deep) map to the nearest effort for back-compat.
static string NormalizeEffort(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
{
    "low" or "medium" or "high" or "xhigh" or "max" or "ultra" => (value ?? string.Empty).Trim().ToLowerInvariant(),
    "fast" or "fast-safe" => "low",
    "standard" => "medium",
    "deep" or "high-assurance" or "recovery" or "auto" or "" => "xhigh",
    "extra-high" => "xhigh",
    "maximum" => "max",
    "minimal" => "low",
    _ => throw new ArgumentException("Reasoning effort must be one of low, medium, high, xhigh, max, ultra.")
};

static bool TokenEquals(string? supplied, string expected)
{
    if (string.IsNullOrEmpty(supplied))
    {
        return false;
    }
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    return suppliedBytes.Length == expectedBytes.Length &&
        CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
}

/// <summary>Groups material rules by family AND scope, both case-insensitively.</summary>
file sealed class SchemeMaterialKeyComparer : IEqualityComparer<(string Material, string Scope)>
{
    public bool Equals((string Material, string Scope) first, (string Material, string Scope) second) =>
        string.Equals(first.Material, second.Material, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.Scope, second.Scope, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string Material, string Scope) value) => HashCode.Combine(
        value.Material.ToLowerInvariant(),
        value.Scope.ToLowerInvariant());
}

/// <summary>Dev-only: restore component positions from a managed-history revision.</summary>
internal sealed record DevRewindRequest(
    Guid SessionId,
    string Sha,
    bool RestoreStateBefore = false,
    string? Scope = null);
