using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vino.AgentHost.Api;
using Vino.AgentHost.Codex;
using Vino.AgentHost.Data;
using Vino.AgentHost.Hosting;
using Vino.Contracts;
using Vino.Core;

namespace Vino.AgentHost.Runtime;

public sealed class SessionOrchestrator : IDisposable
{
    private static readonly JsonSerializerOptions RecoveredContextJsonOptions = new(JsonDefaults.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SessionStore _store;
    private readonly ICodexSessionClient _codex;
    private readonly ModelSelector _models;
    private readonly EffectiveModelState _effectiveModels;
    private readonly AgentHostOptions _options;
    private readonly RuntimeControl _runtime;
    private readonly EventHub _events;
    private readonly ILogger<SessionOrchestrator> _logger;
    private readonly SemaphoreSlim _parallelTurns;
    private readonly SemaphoreSlim _codexRestartGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionGates = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _stateTransitionGates = new();
    private readonly ConcurrentDictionary<Guid, SessionPauseGate> _pauseGates = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TurnCompletionSignal>> _turnCompletions = new();
    private readonly ConcurrentDictionary<string, TurnCompletionSignal> _earlyTurnCompletions = new();
    private readonly ConcurrentDictionary<string, byte> _assistantTurns = new();
    private readonly ConcurrentDictionary<Guid, ActiveTurn> _activeTurns = new();
    // Context compaction plumbing: a waiter per thread so a pre-turn compaction can block (bounded)
    // until codex signals completion, and per-thread timestamps so a stale usage snapshot cannot
    // trigger a compaction storm (each compaction is a real model call).
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _compactionWaiters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _compactionRequestedAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _compactionNotedAt = new(StringComparer.Ordinal);
    private static readonly TimeSpan CompactionCompletionWait = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CompactionCooldown = TimeSpan.FromMinutes(5);

    // Text for the full-auto continuation nudge. It rides the same route as a card answer, so
    // it lands in the TRANSCRIPT as the user act it stands in for — which is why it follows the
    // project's prose language like every other server-composed turn (record/log layers stay
    // English elsewhere; this string is display + model input in one).
    private const string FullAutoContinueMessageKo =
        "[full-auto 자동 진행] 방금 턴의 goal/ask/approval 카드는 서버가 이미 자동 확정·해소·승인했다. " +
        "사용자는 자리에 없고 어떤 카드도 답을 기다리고 있지 않다. 승인이 자동 발급된 경우 그 " +
        "approvalGrantId와 승인 항목은 이 턴 입력의 <vino_approval> 블록에 있다 — 그 grantId로 즉시 " +
        "change_submit 하라. goal 옵션은 첫 번째(추천) 옵션이 선택된 상태다. 뷰포트 캡처를 요청했었다면 " +
        "그 이미지는 이 턴 입력에 첨부되어 있다 — 눈으로 확인하고 결과가 어긋나면 고쳐라. 되묻지 말고 " +
        "지금 작업을 끝까지 실행한 뒤 측정값과 함께 결과를 보고하라.";

    private const string FullAutoContinueMessageEn =
        "[full-auto auto-continue] The goal/ask/approval card from your previous turn was already " +
        "auto-confirmed/resolved/granted by the server. No user is attending and nothing waits on " +
        "screen. If an approval was auto-granted, its approvalGrantId and approved items are in " +
        "this turn's <vino_approval> block — submit with that grantId immediately. Goal options " +
        "resolve to the first (recommended) option. If you requested a viewport capture, the image " +
        "is attached to this turn's input — inspect it and fix what looks wrong. Do not ask again; " +
        "finish the work now and report with measurements.";

    private string ComposeFullAutoContinueMessage() =>
        string.Equals(_projectContext?.ReadLanguage(), "ko", StringComparison.OrdinalIgnoreCase)
            ? FullAutoContinueMessageKo
            : FullAutoContinueMessageEn;
    private readonly ISelectionContextSource? _selectionContext;
    private readonly ILayoutTidyService? _layoutTidy;
    private readonly FullAutoContinuation? _continuation;
    private readonly PendingViewCaptures? _pendingCaptures;
    private readonly ProjectContextStore? _projectContext;
    private readonly ILiveDocumentBackend? _liveBackend;
    private readonly ProblemLog? _problems;
    private readonly VisualReviewState? _visualReview;
    // Hard ceiling on the judge turn: a review that cannot answer in this window is skipped, not
    // waited on — the session it reviews is already idle and must stay reachable.
    private static readonly TimeSpan VisualReviewJudgeTimeout = TimeSpan.FromSeconds(180);
    private readonly SessionActivityLog? _activity;
    private readonly SessionUsageState? _usage;
    private readonly AttachmentStore _attachments;
    private readonly ImageUrlAttachmentFetcher _urlFetcher;
    private readonly CancellationToken _shutdown;
    private readonly TimeSpan _turnPollInterval;
    private readonly TimeSpan _turnReadTimeout;
    private readonly int _turnReadFailuresBeforeRestart;
    private readonly int _turnRestartCycles;
    private long _codexRestartGeneration;

    public SessionOrchestrator(
        SessionStore store,
        ICodexSessionClient codex,
        ModelSelector models,
        EffectiveModelState effectiveModels,
        AgentHostOptions options,
        RuntimeControl runtime,
        EventHub events,
        IHostApplicationLifetime lifetime,
        ILogger<SessionOrchestrator> logger,
        ISelectionContextSource? selectionContext = null,
        SessionActivityLog? activity = null,
        SessionUsageState? usage = null,
        AttachmentStore? attachments = null,
        ImageUrlAttachmentFetcher? urlFetcher = null,
        ILayoutTidyService? layoutTidy = null,
        FullAutoContinuation? continuation = null,
        PendingViewCaptures? pendingCaptures = null,
        ProjectContextStore? projectContext = null,
        ILiveDocumentBackend? liveBackend = null,
        ProblemLog? problems = null,
        VisualReviewState? visualReview = null)
    {
        _selectionContext = selectionContext;
        _layoutTidy = layoutTidy;
        _continuation = continuation;
        _pendingCaptures = pendingCaptures;
        _projectContext = projectContext;
        _liveBackend = liveBackend;
        _problems = problems;
        _visualReview = visualReview;
        _activity = activity;
        _usage = usage;
        _attachments = attachments ?? new AttachmentStore(options.ResolveDataDirectory());
        _urlFetcher = urlFetcher ?? new ImageUrlAttachmentFetcher(_attachments);
        _store = store;
        _codex = codex;
        _models = models;
        _effectiveModels = effectiveModels;
        _options = options;
        _runtime = runtime;
        _events = events;
        _logger = logger;
        _parallelTurns = new SemaphoreSlim(Math.Clamp(options.MaxParallelTurns, 1, 16));
        _turnPollInterval = PositiveOrDefault(options.CodexTurnPollInterval, TimeSpan.FromSeconds(2));
        _turnReadTimeout = PositiveOrDefault(options.CodexTurnReadTimeout, TimeSpan.FromSeconds(10));
        _turnReadFailuresBeforeRestart = Math.Max(1, options.CodexTurnReadFailuresBeforeRestart);
        _turnRestartCycles = Math.Max(0, options.CodexTurnRestartCycles);
        _shutdown = lifetime.ApplicationStopping;
        _codex.NotificationReceived += HandleNotificationAsync;
    }

    /// <summary>
    /// Delivers the user's answer to a CARD (approval or goal) as a turn — exactly what typing
    /// "승인했어, 진행해줘" used to do, minus the typing.
    ///
    /// <para>
    /// It goes through <see cref="SubmitMessageAsync"/> on purpose rather than a private turn
    /// path: the answer then appears in the transcript as the user act it was, and it picks up
    /// <c>ComposeApprovalBlock</c> for free, so the grant travels by the one route that was
    /// already proven. Pressing the button and typing the sentence are now the same operation,
    /// which is also why this is safe — it introduces no permission the typed form did not carry.
    /// </para>
    /// <para>
    /// Returns false when the session cannot take a turn right now (paused, or gone). That is not
    /// a failure: the answer is already persisted on the card, and the block rides the user's next
    /// real message. The caller must not report the approval itself as failed.
    /// </para>
    /// </summary>
    public async Task<bool> DeliverCardAnswerAsync(
        Guid sessionId,
        string content,
        CancellationToken cancellationToken)
    {
        try
        {
            await SubmitMessageAsync(sessionId, new SendMessageRequest(content), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (SessionPausedException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
        catch (Exception exception)
        {
            // The card is saved either way; a turn that cannot start must not turn the user's
            // answer into an error. Log loudly instead of failing the approval.
            _logger.LogWarning(
                exception,
                "Approval answer for session {SessionId} was stored but its turn could not start.",
                sessionId);
            return false;
        }
    }

    public async Task<AcceptedTurn> SubmitMessageAsync(
        Guid sessionId,
        SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        var session = await _store.FindSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session {sessionId:D} was not found.");
        if (session.State == SessionStates.Paused)
        {
            throw new SessionPausedException(sessionId);
        }
        // Attachments are validated and saved before the message row exists, so a rejected batch
        // returns 400 without persisting anything. On a duplicate ClientMessageId retry the append
        // below deduplicates the message and no turn is spawned; the re-saved files are inert.
        var attachments = request.Attachments is { Count: > 0 }
            ? await _attachments.SaveAsync(sessionId, request.Attachments, cancellationToken).ConfigureAwait(false)
            : null;
        var persistedContent = attachments is null
            ? request.Content
            : BuildPersistedAttachmentContent(request.Content, attachments);
        var append = await _store.AppendMessageOnceAsync(
            sessionId,
            "user",
            persistedContent,
            clientMessageId: request.ClientMessageId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!append.Created)
        {
            return new AcceptedTurn(sessionId, append.Message.Id, session.State);
        }
        await _store.SetSessionStateAsync(sessionId, SessionStates.Waiting, request.Content, cancellationToken).ConfigureAwait(false);
        _events.Publish();
        // Images go to codex as native `localImage` input items (it sees them directly); every other
        // file type keeps the path-in-text block for the agent to read from disk. Splitting here keeps
        // the two transports independent — an image never lands in the text block and vice versa.
        var imageAttachments = attachments?
            .Where(a => a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var fileAttachments = attachments?
            .Where(a => !a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var attachmentsBlock = fileAttachments is { Length: > 0 } ? BuildAttachmentsTurnBlock(fileAttachments) : null;
        var attachedImagePaths = imageAttachments is { Length: > 0 }
            ? imageAttachments.Select(a => a.AbsolutePath).ToArray()
            : Array.Empty<string>();
        var messageContent = request.Content;
        // Image URLs pasted into the message are downloaded here (the AgentHost has network; the
        // sandboxed agent does not) and merged into the same localImage channel as file attachments.
        // The fetch runs inside the background turn task so the API response is never blocked on a
        // slow download.
        _ = Task.Run(
            async () =>
            {
                var urlImages = await _urlFetcher
                    .FetchAsync(sessionId, messageContent, _shutdown)
                    .ConfigureAwait(false);
                var imagePaths = urlImages.Count == 0
                    ? (attachedImagePaths.Length > 0 ? attachedImagePaths : null)
                    : attachedImagePaths.Concat(urlImages.Select(a => a.AbsolutePath)).ToArray();
                // C-2 (turn/steer auto-injection) was rolled back: live-testing showed codex 0.144.6 did
                // not incorporate the mid-turn steer and left the session hung in "queued". Messages sent
                // while a turn runs queue a fresh turn as before. The SteerTurnAsync client capability is
                // retained (dormant) for future debugging once codex steer behavior is confirmed.
                await RunTurnAsync(sessionId, messageContent, attachmentsBlock, imagePaths, request.PinnedSelection, _shutdown)
                    .ConfigureAwait(false);
            },
            CancellationToken.None);
        return new AcceptedTurn(sessionId, append.Message.Id, SessionStates.Waiting);
    }

    /// <summary>
    /// "Stop &amp; edit": interrupts any active turn, returns the session to Idle, and retracts the last
    /// user message (and its partial reply), returning that text for the panel to reload into the
    /// composer. Lets a user stop work and edit a message they already sent, then resend it fresh.
    /// </summary>
    public async Task<string?> StopAndRetractLastMessageAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        _ = await _store.FindSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session {sessionId:D} was not found.");
        await InterruptActiveTurnAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await _store.SetSessionStateAsync(sessionId, SessionStates.Idle, null, cancellationToken).ConfigureAwait(false);
        var content = await _store.RetractLastUserMessageAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _events.Publish();
        return content;
    }

    public async Task SetSessionPausedAsync(Guid sessionId, bool paused, CancellationToken cancellationToken)
    {
        var session = await _store.FindSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session {sessionId:D} was not found.");
        var pauseGate = _pauseGates.GetOrAdd(sessionId, static _ => new SessionPauseGate());
        if (!paused && session.State != SessionStates.Paused && !pauseGate.IsPaused)
        {
            return;
        }
        if (paused)
        {
            pauseGate.Pause();
        }
        var transition = _stateTransitionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.SetSessionStateAsync(
                sessionId,
                paused
                    ? SessionStates.Paused
                    : pauseGate.WaiterCount > 0 ? SessionStates.Waiting : SessionStates.Idle,
                paused ? session.CurrentTask : null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            transition.Release();
        }
        if (paused)
        {
            await InterruptActiveTurnAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            pauseGate.Resume();
        }
        _events.Publish();
    }

    public void Dispose()
    {
        _codex.NotificationReceived -= HandleNotificationAsync;
        _parallelTurns.Dispose();
        _codexRestartGate.Dispose();
        foreach (var gate in _sessionGates.Values)
        {
            gate.Dispose();
        }
        foreach (var gate in _stateTransitionGates.Values)
        {
            gate.Dispose();
        }
    }

    private async Task RunTurnAsync(
        Guid sessionId,
        string content,
        string? attachmentsBlock,
        IReadOnlyList<string>? imagePaths,
        PinnedSelection? pinnedSelection,
        CancellationToken cancellationToken)
    {
        // Viewport captures queued by rhino_view_capture ride THIS turn's input as localImage
        // items — the only channel the model is guaranteed to see an image through. Merged
        // before the message's own attachments so the newest user-attached image stays last.
        if (_pendingCaptures?.Drain(sessionId) is { Count: > 0 } pendingCapturePaths)
        {
            var merged = new List<string>(pendingCapturePaths.Where(File.Exists));
            if (imagePaths is { Count: > 0 })
            {
                merged.AddRange(imagePaths);
            }
            imagePaths = merged;
        }
        var sessionGate = _sessionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        var parallelAcquired = false;
        var nudgeContinuation = false;
        SessionRecord? visualReviewSession = null;
        string? visualReviewModel = null;
        try
        {
            await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var pauseGate = _pauseGates.GetOrAdd(sessionId, static _ => new SessionPauseGate());
                SessionRecord latest;
                while (true)
                {
                    await pauseGate.WaitUntilResumedAsync(cancellationToken).ConfigureAwait(false);
                    await _runtime.WaitUntilResumedAsync(cancellationToken).ConfigureAwait(false);
                    await _parallelTurns.WaitAsync(cancellationToken).ConfigureAwait(false);
                    parallelAcquired = true;
                    var transition = _stateTransitionGates.GetOrAdd(
                        sessionId,
                        static _ => new SemaphoreSlim(1, 1));
                    await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var canStart = false;
                    try
                    {
                        latest = await _store.FindSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                            ?? throw new KeyNotFoundException($"Session {sessionId:D} was not found.");
                        canStart = !pauseGate.IsPaused && latest.State != SessionStates.Paused;
                        if (canStart)
                        {
                            await _store.SetSessionStateAsync(
                                sessionId,
                                SessionStates.Running,
                                content,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        transition.Release();
                    }
                    if (canStart)
                    {
                        break;
                    }
                    _parallelTurns.Release();
                    parallelAcquired = false;
                }
                _events.Publish();
                // Open a fresh per-turn accumulator so the post-turn auto-tidy seeds only on the
                // components THIS turn creates (see CompleteTurnAsync).
                _layoutTidy?.BeginTurn(sessionId);

                // Manual effort: the session's stored reasoning effort (low..ultra) is used directly and
                // clamped to the chosen model's advertised set — no adaptive per-message routing, no
                // capability floor, no recovery escalation. (latest.ModelProfile carries the effort level.)
                ModelSelection selection;
                try
                {
                    selection = await _models.ResolveDirectAsync(latest.ModelProfile, latest.Model, cancellationToken)
                        .ConfigureAwait(false);
                    _effectiveModels.RecordDirect(sessionId, selection);
                }
                catch (ModelRoutingException exception)
                {
                    _effectiveModels.RecordDirectFailure(sessionId, exception);
                    throw;
                }
                _events.Publish();
                var threadId = latest.CodexThreadId;
                var migratedThread = false;
                if (string.IsNullOrWhiteSpace(threadId))
                {
                    threadId = await _codex.StartThreadAsync(
                        _options.ResolveThreadWorkspaceDirectory(sessionId),
                        selection.Model,
                        cancellationToken).ConfigureAwait(false);
                    await _store.SetThreadIdAsync(sessionId, threadId, cancellationToken).ConfigureAwait(false);
                    content = await PrependImportedContextAsync(sessionId, content, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        await _codex.ResumeThreadAsync(
                            threadId,
                            _options.ResolveThreadWorkspaceDirectory(sessionId),
                            selection.Model,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (CodexProtocolException exception) when (IsUnsupportedPaginatedThread(exception))
                    {
                        (threadId, content) = await ReplaceIncompatibleThreadAsync(
                            sessionId,
                            threadId,
                            content,
                            selection.Model,
                            cancellationToken).ConfigureAwait(false);
                        migratedThread = true;
                    }
                }

                // Proactive context management: an existing thread whose last-known context footprint
                // crossed the threshold is compacted BEFORE the turn instead of failing mid-flight.
                if (!migratedThread && !string.IsNullOrWhiteSpace(latest.CodexThreadId))
                {
                    await CompactThreadIfNearLimitAsync(sessionId, threadId!, cancellationToken).ConfigureAwait(false);
                }

                string turnId;
                // Captured before the turn starts so a card the model raises DURING the turn always
                // carries a later timestamp — CompleteTurnAsync uses that to tell "this turn ended on a
                // card" (fine) from "a card is stale from an earlier turn" (must not mask a lost response).
                var turnStartedAt = DateTimeOffset.UtcNow;
                try
                {
                    turnId = await _codex.StartTurnAsync(
                        threadId,
                        ComposeTurnInput(latest, content, attachmentsBlock, pinnedSelection),
                        selection.Model,
                        selection.Effort,
                        imagePaths,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (CodexProtocolException exception) when (
                    !migratedThread &&
                    !string.IsNullOrWhiteSpace(latest.CodexThreadId) &&
                    IsUnsupportedPaginatedThread(exception))
                {
                    (threadId, content) = await ReplaceIncompatibleThreadAsync(
                        sessionId,
                        threadId,
                        content,
                        selection.Model,
                        cancellationToken).ConfigureAwait(false);
                    turnId = await _codex.StartTurnAsync(
                        threadId,
                        ComposeTurnInput(latest, content, attachmentsBlock, pinnedSelection),
                        selection.Model,
                        selection.Effort,
                        imagePaths,
                        cancellationToken).ConfigureAwait(false);
                }
                // A deferred card answer (refusal / ask choice the user pressed while paused) has now
                // ridden this turn's input — clear its one-shot flag so it does not ride every turn after.
                await ClearPendingCardDeliveriesAsync(sessionId, latest, cancellationToken).ConfigureAwait(false);
                _activity?.Record(
                    sessionId,
                    "turn",
                    $"Turn started — {selection.Model ?? "default model"} ({selection.Effort ?? "default effort"})",
                    ok: true,
                    durationMs: 0);
                var completion = new TaskCompletionSource<TurnCompletionSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
                _turnCompletions[turnId] = completion;
                if (_earlyTurnCompletions.TryRemove(turnId, out var earlyCompletion))
                {
                    completion.TrySetResult(earlyCompletion);
                }
                var activeTurn = new ActiveTurn(threadId, turnId, turnStartedAt);
                _activeTurns[sessionId] = activeTurn;
                if (pauseGate.IsPaused)
                {
                    await InterruptActiveTurnAsync(sessionId, cancellationToken).ConfigureAwait(false);
                }
                var outcome = await WaitForTurnOutcomeAsync(
                    sessionId,
                    activeTurn,
                    completion.Task,
                    cancellationToken).ConfigureAwait(false);
                // Context-overflow safety net: a turn the model could not fit gets ONE retry after a
                // confirmed compaction. Gates are still held and the session is still Running, so the
                // retry is indistinguishable from a long turn to everything downstream.
                if (IsContextOverflowFailure(outcome) &&
                    await RequestThreadCompactionAsync(
                        sessionId,
                        threadId!,
                        "The turn hit the model's context window limit — compacting the conversation and retrying once.",
                        cancellationToken).ConfigureAwait(false))
                {
                    turnId = await _codex.StartTurnAsync(
                        threadId!,
                        ComposeTurnInput(latest, content, attachmentsBlock, pinnedSelection),
                        selection.Model,
                        selection.Effort,
                        imagePaths,
                        cancellationToken).ConfigureAwait(false);
                    completion = new TaskCompletionSource<TurnCompletionSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _turnCompletions[turnId] = completion;
                    if (_earlyTurnCompletions.TryRemove(turnId, out var earlyRetrySignal))
                    {
                        completion.TrySetResult(earlyRetrySignal);
                    }
                    activeTurn = new ActiveTurn(threadId!, turnId, DateTimeOffset.UtcNow);
                    _activeTurns[sessionId] = activeTurn;
                    if (pauseGate.IsPaused)
                    {
                        await InterruptActiveTurnAsync(sessionId, cancellationToken).ConfigureAwait(false);
                    }
                    outcome = await WaitForTurnOutcomeAsync(
                        sessionId,
                        activeTurn,
                        completion.Task,
                        cancellationToken).ConfigureAwait(false);
                }
                await CompleteTurnAsync(
                    sessionId,
                    pauseGate,
                    activeTurn,
                    outcome,
                    cancellationToken).ConfigureAwait(false);
                // Full-auto continuation: this turn auto-resolved a goal/ask and then ended with
                // no further writes — the model most likely never read the auto-resolve payload
                // (code-mode swallows unechoed tool results) and parked on a card that is not
                // there. Nudge with ONE synthetic turn, sent after the gates release below.
                var turnCompleted =
                    string.Equals(outcome.Status, "completed", StringComparison.OrdinalIgnoreCase);
                nudgeContinuation =
                    turnCompleted && _continuation?.TryConsumeNudge(activeTurn.ThreadId) == true;
                // Capture delivery: a requested viewport image can only ride a NEXT turn, and in
                // full-auto only a synthetic one exists. Separate budget from the card nudge —
                // when a card nudge already fires, the same turn delivers the images anyway.
                // HasPending guards against a stale mark whose images an earlier turn drained.
                if (!nudgeContinuation && turnCompleted &&
                    _pendingCaptures?.HasPending(sessionId) == true &&
                    _continuation?.TryConsumeCaptureNudge(activeTurn.ThreadId) == true)
                {
                    nudgeContinuation = true;
                }
                // Visual review: a full-auto turn that committed document work and then went
                // quiet (no card nudge, no capture delivery pending) gets ONE fresh-eyes
                // viewport review; a failing verdict returns as a single synthetic repair
                // turn. Decided here where the session record is in scope, run after the
                // gates release below — the judge may take minutes and gates nothing.
                if (!nudgeContinuation && turnCompleted &&
                    PermissionModes.IsFullAuto(latest.PermissionMode) &&
                    _liveBackend is not null &&
                    _visualReview?.TryBeginReview(activeTurn.ThreadId) == true)
                {
                    visualReviewSession = latest;
                    visualReviewModel = selection.Model;
                }
            }
            finally
            {
                if (parallelAcquired)
                {
                    _parallelTurns.Release();
                }
                sessionGate.Release();
            }
            if (nudgeContinuation)
            {
                _logger.LogInformation(
                    "Full-auto continuation nudge for session {SessionId}: the turn parked after an auto-resolved card.",
                    sessionId);
                _activity?.Record(
                    sessionId,
                    "turn",
                    "Full-auto continuation — the model parked after an auto-resolved card; sending a follow-up turn.",
                    ok: true,
                    durationMs: 0);
                await DeliverCardAnswerAsync(sessionId, ComposeFullAutoContinueMessage(), cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (visualReviewSession is not null)
            {
                await RunVisualReviewAsync(visualReviewSession, visualReviewModel, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Codex turn failed for session {SessionId}.", sessionId);
            try
            {
                await PersistUnhandledTurnFailureAsync(sessionId, exception).ConfigureAwait(false);
            }
            catch (Exception persistException)
            {
                _logger.LogError(persistException, "Could not persist failure state for session {SessionId}.", sessionId);
            }
        }
        finally
        {
            if (_activeTurns.TryRemove(sessionId, out var active))
            {
                _turnCompletions.TryRemove(active.TurnId, out _);
                _assistantTurns.TryRemove(active.TurnId, out _);
            }
            _events.Publish();
        }
    }

    /// <summary>
    /// The fresh-eyes visual review pass (full-auto only, once per thread, after committed work
    /// went quiet): capture one viewport render, hand it with the session goal to a context-free
    /// judge thread, and turn a failing verdict into ONE synthetic repair turn. Advisory by
    /// contract — every failure in here is logged and swallowed, nothing is ever gated on it,
    /// and the judge thread is abandoned after its single turn.
    /// </summary>
    private async Task RunVisualReviewAsync(
        SessionRecord session,
        string? model,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_liveBackend is null)
            {
                return;
            }
            var captureArguments = JsonSerializer.SerializeToElement(new { width = 1280, height = 800 });
            var wrapped = await _liveBackend.CaptureRhinoViewAsync(captureArguments, cancellationToken)
                .ConfigureAwait(false);
            // ReadBridgeQueryAsync envelope: { result, fingerprint, diagnostics } — the same shape
            // the rhino_view_capture tool decodes.
            var envelope = JsonSerializer.SerializeToElement(wrapped, GoalJson);
            var pngBase64 = envelope.GetProperty("result").GetProperty("pngBase64").GetString()
                ?? throw new InvalidOperationException("The capture returned no image data.");
            var directory = Path.Combine(
                _options.ResolveDataDirectory(),
                "artifacts",
                session.Id.ToString("D"),
                "captures");
            Directory.CreateDirectory(directory);
            var imagePath = Path.Combine(
                directory,
                $"visual-review-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}Z.png");
            await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(pngBase64), cancellationToken)
                .ConfigureAwait(false);

            // The judge is a fresh thread in the session's own scratch cwd: no session history, no
            // tools it should use — only the goal text and the image it is asked to grade.
            var goalText = await ResolveVisualReviewGoalTextAsync(session, cancellationToken).ConfigureAwait(false);
            var judgeThreadId = await _codex.StartThreadAsync(
                _options.ResolveThreadWorkspaceDirectory(session.Id),
                model,
                cancellationToken).ConfigureAwait(false);
            var judgeTurnId = await _codex.StartTurnAsync(
                judgeThreadId,
                ComposeVisualReviewJudgePrompt(goalText),
                model,
                "medium",
                [imagePath],
                cancellationToken).ConfigureAwait(false);

            // Same completion mechanism as session turns: the turn/completed notification resolves
            // a waiter keyed by turn id, with the early-completion side table closing the
            // register-after-start race. Bounded by a hard timeout instead of the session polling
            // loop — a judge that cannot answer is skipped, not recovered.
            var completion = new TaskCompletionSource<TurnCompletionSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
            _turnCompletions[judgeTurnId] = completion;
            try
            {
                if (_earlyTurnCompletions.TryRemove(judgeTurnId, out var early))
                {
                    completion.TrySetResult(early);
                }
                var timeout = Task.Delay(VisualReviewJudgeTimeout, cancellationToken);
                if (await Task.WhenAny(completion.Task, timeout).ConfigureAwait(false) != completion.Task)
                {
                    await _codex.InterruptTurnAsync(judgeThreadId, judgeTurnId, cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogWarning(
                        "Visual review judge turn {TurnId} for session {SessionId} exceeded {Seconds}s; skipping the review.",
                        judgeTurnId,
                        session.Id,
                        VisualReviewJudgeTimeout.TotalSeconds);
                    return;
                }
            }
            finally
            {
                _turnCompletions.TryRemove(judgeTurnId, out _);
            }

            // The judge thread is bound to no session, so notifications persist nothing for it;
            // the final text comes from the same authoritative read session turns use.
            var snapshot = await TryReadFinalSnapshotAsync(
                new ActiveTurn(judgeThreadId, judgeTurnId, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            var verdict = ParseVisualReviewVerdict(snapshot?.AgentMessages.LastOrDefault()?.Text);
            if (verdict?.Pass is null)
            {
                _problems?.RecordVisualReview(session.Id, pass: null, issuesCount: 0);
                _logger.LogWarning(
                    "Visual review for session {SessionId} returned no parseable verdict; skipping the repair turn.",
                    session.Id);
                return;
            }
            var issues = (verdict.Issues ?? [])
                .Where(issue => !string.IsNullOrWhiteSpace(issue))
                .ToArray();
            _problems?.RecordVisualReview(
                session.Id,
                verdict.Pass,
                issues.Length,
                verdict.Scores?.TaskFit,
                verdict.Scores?.Geometry,
                verdict.Scores?.Craft);
            if (verdict.Pass != false || issues.Length == 0)
            {
                return;
            }
            _activity?.Record(
                session.Id,
                "turn",
                $"Visual review — {issues.Length} defect(s) reported by the fresh-eyes judge; sending a repair turn.",
                ok: true,
                durationMs: 0);
            await DeliverCardAnswerAsync(
                session.Id,
                ComposeVisualReviewRepairMessage(issues),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Pure add-on: no failure in the review path may disturb the session it reviews.
            _logger.LogWarning(
                exception,
                "Visual review for session {SessionId} failed; the session continues without it.",
                session.Id);
        }
    }

    /// <summary>
    /// What the judge grades against: the goal card's objective + criteria when one was framed,
    /// else the session's first user message (truncated — the judge needs the ask, not the essay).
    /// </summary>
    private async Task<string> ResolveVisualReviewGoalTextAsync(
        SessionRecord session,
        CancellationToken cancellationToken)
    {
        const int maxFallbackCharacters = 1200;
        if (TryDeserializeCard<GoalCard>(session.GoalCard) is { } card &&
            !string.IsNullOrWhiteSpace(card.Objective))
        {
            return card.Criteria is { Count: > 0 }
                ? $"{card.Objective} Done when: {string.Join(" | ", card.Criteria)}"
                : card.Objective;
        }
        var first = await _store.ReadFirstUserMessageAsync(session.Id, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(first))
        {
            return "(no recorded goal — judge general model quality)";
        }
        return first.Length <= maxFallbackCharacters ? first : first[..maxFallbackCharacters];
    }

    private static string ComposeVisualReviewJudgePrompt(string goalText) =>
        "You are a fresh-eyes visual reviewer with no context beyond this message. The attached " +
        "image is the final Rhino viewport of an automated modeling session. Judge ONLY what is " +
        "visible in the image against the goal below — do not call tools, do not ask questions, " +
        "and do not speculate about anything the image cannot show.\n\n" +
        "Session goal:\n" + goalText + "\n\n" +
        "Judge FORM ONLY. Display material, color palette, transparency, and lighting are NOT " +
        "quality criteria in Rhino work — never report a tone or color choice as a defect and " +
        "never suggest recoloring. Read geometry through whatever material is shown (silhouette, " +
        "shadow, edge lines); only when a display state makes the form genuinely unreadable, " +
        "report exactly that as the issue.\n\n" +
        "Answer with a single JSON object and NOTHING else:\n" +
        "{\"pass\": boolean, \"issues\": [\"specific visual defect\", ...], " +
        "\"scores\": {\"taskFit\": 0-4, \"geometry\": 0-4, \"craft\": 0-4}}\n" +
        "List only form defects actually visible (jagged or faceted geometry, floating or " +
        "intersecting parts, duplicate or leftover construction geometry, missing or misplaced " +
        "elements). An empty issues list means pass.";

    // The repair message rides the same route as a card answer, so like the full-auto nudge it
    // lands in the TRANSCRIPT as the user act it stands in for and follows the project's prose
    // language (record/log layers stay English).
    private string ComposeVisualReviewRepairMessage(IReadOnlyList<string> issues)
    {
        var numbered = string.Join("\n", issues.Select((issue, index) => $"{index + 1}. {issue}"));
        return string.Equals(_projectContext?.ReadLanguage(), "ko", StringComparison.OrdinalIgnoreCase)
            ? "[시각 검수] 무맥락 검수자가 최종 뷰포트를 목표와 대조해 다음 결함을 보고했다:\n" +
              numbered + "\n고칠 수 있는 것을 고치고, 수정 후 rhino_view_capture로 재확인한 뒤 " +
              "최종 보고를 갱신하라. 소견이 틀렸다면 근거를 들어 반박만 해도 된다."
            : "[visual review] A context-free reviewer compared the final viewport against the " +
              "session goal and reported these defects:\n" + numbered + "\nFix what can be fixed, " +
              "re-verify with rhino_view_capture after the changes, and update your final report. " +
              "If a finding is wrong, you may rebut it with evidence instead.";
    }

    private static VisualReviewVerdict? ParseVisualReviewVerdict(string? text)
    {
        var json = ExtractFirstJsonObject(text);
        if (json is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<VisualReviewVerdict>(json, GoalJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// First balanced JSON object in the text, lenient about surroundings: code-fence markers are
    /// dropped and anything before/after the object is ignored — models decorate strict-output
    /// answers anyway, and losing the verdict to a fence costs a whole review.
    /// </summary>
    private static string? ExtractFirstJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var cleaned = text
            .Replace("```json", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "\n", StringComparison.Ordinal);
        var start = cleaned.IndexOf('{');
        if (start < 0)
        {
            return null;
        }
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < cleaned.Length; index++)
        {
            var character = cleaned[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }
            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                return cleaned[start..(index + 1)];
            }
        }
        return null;
    }

    /// <summary>The judge's strict output contract. Pass=null marks an unparseable verdict.</summary>
    private sealed record VisualReviewVerdict(
        bool? Pass,
        IReadOnlyList<string>? Issues,
        VisualReviewScores? Scores);

    private sealed record VisualReviewScores(int? TaskFit, int? Geometry, int? Craft);

    private async Task<(string ThreadId, string Message)> ReplaceIncompatibleThreadAsync(
        Guid sessionId,
        string incompatibleThreadId,
        string currentMessage,
        string? model,
        CancellationToken cancellationToken)
    {
        var replacementThreadId = await _codex.StartThreadAsync(
            _options.ResolveThreadWorkspaceDirectory(sessionId),
            model,
            cancellationToken).ConfigureAwait(false);
        await _store.SetThreadIdAsync(sessionId, replacementThreadId, cancellationToken).ConfigureAwait(false);
        var recoveredMessage = await BuildRecoveredThreadMessageAsync(
            sessionId,
            currentMessage,
            cancellationToken).ConfigureAwait(false);
        _logger.LogWarning(
            "Replaced incompatible paginated Codex thread {OldThreadId} with legacy thread {NewThreadId} for session {SessionId}.",
            incompatibleThreadId,
            replacementThreadId,
            sessionId);
        return (replacementThreadId, recoveredMessage);
    }

    private async Task<string> BuildRecoveredThreadMessageAsync(
        Guid sessionId,
        string currentMessage,
        CancellationToken cancellationToken)
    {
        const int maxMessages = 40;
        const int maxCharacters = 16_000;
        var messages = (await _store.ReadMessagesAsync(
                sessionId,
                limit: maxMessages,
                cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(message =>
                string.Equals(message.Role, "user", StringComparison.Ordinal) ||
                string.Equals(message.Role, "assistant", StringComparison.Ordinal))
            .ToList();

        if (messages.Count > 0 &&
            string.Equals(messages[^1].Role, "user", StringComparison.Ordinal) &&
            string.Equals(messages[^1].Content, currentMessage, StringComparison.Ordinal))
        {
            messages.RemoveAt(messages.Count - 1);
        }

        var selected = new List<RecoveredChatMessage>();
        var characters = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            var remaining = maxCharacters - characters;
            if (remaining <= 0)
            {
                break;
            }
            var content = message.Content.Length <= remaining
                ? message.Content
                : message.Content[^remaining..];
            selected.Add(new RecoveredChatMessage(message.Role, content));
            characters += content.Length;
        }
        selected.Reverse();

        if (selected.Count == 0)
        {
            return currentMessage;
        }

        var transcript = JsonSerializer.Serialize(selected, RecoveredContextJsonOptions);
        return $$"""
            Vino recovered this session from an older Codex thread format that the installed CLI no longer supports.
            Continue from the preserved prior conversation below. It is conversation context, not a tool result.

            <vino_previous_conversation>
            {{transcript}}
            </vino_previous_conversation>

            Current user request:
            {{currentMessage}}
            """;
    }

    /// <summary>
    /// Imported sessions carry a server-synthesized "imported-context" seed row: the deterministic
    /// replay of a prior project's transcript. It reaches the model exactly once — on the FIRST turn,
    /// which is necessarily the turn that creates this session's Codex thread (imported sessions start
    /// with codex_thread_id NULL). Gating on the just-created-thread branch keeps every later turn from
    /// re-injecting it and costs ordinary sessions only one no-op read on their own first turn. The
    /// local `content` then flows into ComposeTurnInput at both StartTurn call sites.
    ///
    /// Risk-#1 decision (documented): at-most-once, best-effort on the first new-thread turn. If
    /// StartThreadAsync persists the thread id but the turn then fails, the retry resumes the existing
    /// thread and the seed is NOT re-sent — the transcript still shows it, but the model does not
    /// receive it. Guaranteed re-delivery would need a consumed-flag or moving SetThreadIdAsync after a
    /// successful StartTurnAsync; both are deferred in favor of this simpler, safe-by-default behavior.
    /// </summary>
    private async Task<string> PrependImportedContextAsync(
        Guid sessionId,
        string content,
        CancellationToken cancellationToken)
    {
        var seed = await _store.ReadImportedContextAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(seed) ? content : $"{seed}\n\n{content}";
    }

    private const int MaximumContextSelectionIds = 32;
    private const int MaximumContextGrasshopperSelections = 16;

    /// <summary>
    /// Prepends the user's live selection (Rhino viewport and Grasshopper canvas) as a tagged,
    /// one-line context hint. Applied only to the Codex turn input — after routing, so context
    /// wording never influences model escalation — and only when a selection exists. Ids are
    /// hints, not fingerprints: writes still require snapshot_read fingerprints. When the message
    /// carried attachments, the tagged attachment block is appended after the user content —
    /// turn input only, never the persisted transcript row. Selection and digest are routed by
    /// the session's Grasshopper-document binding so a bound session never receives another
    /// document's context; an unbound session falls back to the sole registered document (the
    /// pre-multi-document behavior) and gets no hint when several documents are open.
    /// </summary>
    /// <summary>
    /// Renders the session's CONFIRMED goal card for the turn input. Proposed-but-unanswered and
    /// rejected cards are deliberately not rendered: an unconfirmed reading must not look like a
    /// mandate, and a rejected one is history.
    /// </summary>
    private static string? ComposeGoalBlock(SessionRecord session)
    {
        if (string.IsNullOrWhiteSpace(session.GoalCard)) return null;
        GoalCard? card;
        try
        {
            card = JsonSerializer.Deserialize<GoalCard>(session.GoalCard!, GoalJson);
        }
        catch (JsonException)
        {
            return null;
        }
        if (card is null)
        {
            return null;
        }
        // A rejected goal is normally history, but when the verdict could not be delivered as a turn
        // (the session was paused), the agent never heard the "no" and would keep framing work
        // against a goal the user refused. DeliveryPending rides it on the NEXT turn exactly once —
        // RunTurnAsync clears it afterwards, mirroring the deferred refusal on approval cards.
        if (string.Equals(card.Status, "rejected", StringComparison.OrdinalIgnoreCase) &&
            card.DeliveryPending)
        {
            return "<vino_goal>The user REJECTED the proposed goal \"" + card.Objective +
                "\". It is not what they want. Reframe the goal before doing further work, and do " +
                "not act on the rejected framing.</vino_goal>";
        }
        if (!string.Equals(card.Status, "confirmed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var builder = new StringBuilder("<vino_goal>");
        builder.Append("Confirmed objective: ").Append(card.Objective).Append(' ');
        if (card.Criteria.Count > 0)
        {
            builder.Append("Done when: ").Append(string.Join(" | ", card.Criteria)).Append(' ');
        }
        if (card.OutOfScope.Count > 0)
        {
            builder.Append("Out of scope: ").Append(string.Join(" | ", card.OutOfScope)).Append(' ');
        }
        if (!string.IsNullOrWhiteSpace(card.ChosenOption))
        {
            builder.Append("User chose: ").Append(card.ChosenOption).Append(' ');
        }
        builder.Append("Call goal_score against these criteria before your closing report.");
        builder.Append("</vino_goal>");
        return builder.ToString();
    }

    /// <summary>
    /// Renders the user's ANSWER to an approval request for the turn input. Proposed cards are not
    /// rendered (an unanswered request must not read as permission), but both answers are: a grant
    /// carries the key, and a refusal carries the "no".
    ///
    /// <para>
    /// The refusal half used to be dropped, which made "하지 마세요" a button that changed a badge
    /// and nothing else — the agent never learned it had been refused and re-proposed the same
    /// work on the next turn. A decision the user made is a decision the agent has to hear.
    /// </para>
    /// </summary>
    private static string? ComposeApprovalBlock(SessionRecord session)
    {
        if (string.IsNullOrWhiteSpace(session.ApprovalCard)) return null;
        ApprovalCard? card;
        try
        {
            card = JsonSerializer.Deserialize<ApprovalCard>(session.ApprovalCard!, GoalJson);
        }
        catch (JsonException)
        {
            return null;
        }
        if (card is null)
        {
            return null;
        }
        // A refusal is normally delivered once, as the turn DeliverCardAnswerAsync sends when the
        // button is pressed. When that delivery could not run (the session was paused), DeliveryPending
        // is set so the "no" rides the NEXT turn exactly once — RunTurnAsync clears it afterwards, so it
        // is never re-told forever the way rendering every refusal here unconditionally would have been.
        if (string.Equals(card.Status, "rejected", StringComparison.OrdinalIgnoreCase) &&
            card.DeliveryPending)
        {
            var reason = string.IsNullOrWhiteSpace(card.RejectedReason) ? null : card.RejectedReason;
            return "<vino_approval>The user REFUSED \"" + card.Summary + "\"" +
                (reason is null ? "" : ". Reason: " + reason) +
                ". Do not carry it out and do not propose it again unless asked.</vino_approval>";
        }
        // A grant-less approval (a "layerScheme" card settles RULES, not geometry) never reaches the
        // granted block below — there is no grantId to carry. Its confirmation is normally the turn
        // DeliverCardAnswerAsync sends when the button is pressed; when that could not run (paused),
        // DeliveryPending rides it here on the NEXT turn exactly once, like a deferred refusal.
        if (string.Equals(card.Status, "granted", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(card.GrantId) &&
            card.DeliveryPending)
        {
            return "<vino_approval>The user CONFIRMED the proposed layer scheme \"" + card.Summary +
                "\". The approved rules are stored in the project scheme. Proceed to tidy the " +
                "layers by these rules.</vino_approval>";
        }
        if (!string.Equals(card.Status, "granted", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(card.GrantId))
        {
            return null;
        }
        // A grant that has already lapsed is worse than no grant: injecting the dead id makes the
        // agent submit a ChangeSet the broker refuses for a reason nothing in the transcript
        // explains. Say the key expired instead, so the next move is to ask again.
        if (card.GrantExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
        {
            return "<vino_approval>The user's approval for \"" + card.Summary +
                "\" has EXPIRED and its key is no longer valid. Do not submit writes against it. " +
                "If the work still needs doing, call approval_request again so the user can " +
                "re-approve.</vino_approval>";
        }
        var approved = card.ApprovedItemIds ?? [];
        // The chosen option rides with its item. It is the half of the answer only a human could
        // give (which near-duplicate survives), so omitting it makes the agent ask a question the
        // user already answered — and leaves it holding a grant over both copies with no verdict.
        var labels = card.Items
            .Where(item => approved.Contains(item.Id))
            .Select(item =>
            {
                var chosen = card.Choices is not null && card.Choices.TryGetValue(item.Id, out var choice)
                    ? choice
                    : null;
                var text = string.IsNullOrWhiteSpace(chosen)
                    ? $"{item.Id} ({item.Label})"
                    : $"{item.Id} ({item.Label}) — the user chose: {chosen}";
                // Layer-curation rows carry the exact server-computed values the ops must write:
                // the model copies these verbatim instead of re-deriving colours or inventing
                // label values. A user-classified triage row was resolved when the grant landed,
                // so every granted row has a full set by the time it gets here.
                if (item.LayerRow is { } row)
                {
                    // A row whose proposed colour equals its current one is a LABEL-ONLY write —
                    // say so, rather than handing back an int that reads like a recolour request.
                    var colour = row.ProposedArgbColor == row.CurrentArgbColor
                        ? "colour UNCHANGED — write labels only, omit argbColor"
                        : $"argbColor={row.ProposedArgbColor}";
                    text += $" [layer '{row.FullPath}': gptino.canonical={row.Canonical} " +
                        $"gptino.material={row.Material} gptino.confidence={row.Confidence} " +
                        $"gptino.labelSource={LabelSourceOf(row)} {colour}]";
                }
                return text;
            });
        var builder = new StringBuilder("<vino_approval>");
        builder.Append("approvalGrantId: ").Append(card.GrantId).Append(". ");
        builder.Append("Approved items ONLY: ").Append(string.Join(" | ", labels)).Append(". ");
        builder.Append("Where an item names the user's choice, that choice is already made — act on " +
            "it, do not ask again. ");
        builder.Append("Anything not listed here was refused — do not touch it.");
        builder.Append("</vino_approval>");
        return builder.ToString();
    }

    /// <summary>
    /// The fixed gptino.labelSource vocabulary: how the label was decided. Derived from the row
    /// the server already computed, so "copy the granted values verbatim" has a real source for
    /// every key instead of leaving the model to invent one.
    /// </summary>
    private static string LabelSourceOf(ApprovalLayerRow row) =>
        row.Evidence.StartsWith("user choice", StringComparison.Ordinal) ? "user"
            : row.Evidence.StartsWith("alias", StringComparison.Ordinal) ? "alias"
            : "pattern";

    private static readonly JsonSerializerOptions GoalJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Renders an ask answer that could not be delivered as a turn when the button was pressed (the
    /// session was paused), so the user's choice rides the NEXT turn exactly once. DeliveryPending is
    /// cleared by RunTurnAsync after the turn starts, so a normally delivered answer is never rendered
    /// here and a pending one is never repeated.
    /// </summary>
    private static string? ComposeAskBlock(SessionRecord session)
    {
        var card = TryDeserializeCard<AskCard>(session.AskCard);
        if (card is null ||
            !string.Equals(card.Status, "answered", StringComparison.OrdinalIgnoreCase) ||
            !card.DeliveryPending)
        {
            return null;
        }
        var label = card.Options
            .FirstOrDefault(option => string.Equals(option.Id, card.ChosenOptionId, StringComparison.Ordinal))
            ?.Label ?? card.ChosenOptionId ?? "(unknown)";
        var note = string.IsNullOrWhiteSpace(card.Note) ? null : card.Note;
        return "<vino_answer>Earlier you asked: \"" + card.Question + "\". The user answered: \"" +
            label + "\"" + (note is null ? "" : ". Note: " + note) +
            ". Act on that answer.</vino_answer>";
    }

    /// <summary>
    /// Clears the one-shot DeliveryPending flag on any card whose deferred answer just rode this turn.
    /// A card answer that could not be delivered when its button was pressed (paused) rides the next
    /// turn once via ComposeAskBlock/ComposeApprovalBlock; this stops it riding every turn thereafter.
    /// </summary>
    private async Task ClearPendingCardDeliveriesAsync(Guid sessionId, SessionRecord session, CancellationToken cancellationToken)
    {
        if (TryDeserializeCard<AskCard>(session.AskCard) is { DeliveryPending: true } ask)
        {
            await _store.SetAskCardAsync(
                sessionId,
                JsonSerializer.Serialize(ask with { DeliveryPending = false }, GoalJson),
                cancellationToken).ConfigureAwait(false);
        }
        if (TryDeserializeCard<ApprovalCard>(session.ApprovalCard) is { DeliveryPending: true } approval)
        {
            await _store.SetApprovalCardAsync(
                sessionId,
                JsonSerializer.Serialize(approval with { DeliveryPending = false }, GoalJson),
                cancellationToken).ConfigureAwait(false);
        }
        if (TryDeserializeCard<GoalCard>(session.GoalCard) is { DeliveryPending: true } goal)
        {
            await _store.SetGoalCardAsync(
                sessionId,
                JsonSerializer.Serialize(goal with { DeliveryPending = false }, GoalJson),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private string ComposeTurnInput(SessionRecord session, string content, string? attachmentsBlock = null, PinnedSelection? pinnedSelection = null)
    {
        if (!string.IsNullOrEmpty(attachmentsBlock))
        {
            content = content.Length > 0 ? $"{content}\n{attachmentsBlock}" : attachmentsBlock;
        }
        // A confirmed goal rides EVERY later turn, not just the one that framed it — the agent is
        // held to what the user approved, and the closing goal_score answers these exact criteria.
        var goalBlock = ComposeGoalBlock(session);
        // A granted approval carries the key the broker demands for the user's own geometry, and it
        // names exactly which items the key covers.
        var approvalBlock = ComposeApprovalBlock(session);
        if (approvalBlock is not null)
        {
            goalBlock = goalBlock is null ? approvalBlock : $"{goalBlock}\n{approvalBlock}";
        }
        // A card answer that could not be delivered when its button was pressed (the session was
        // paused) rides here exactly once; RunTurnAsync clears its pending flag after the turn starts.
        var askBlock = ComposeAskBlock(session);
        if (askBlock is not null)
        {
            goalBlock = goalBlock is null ? askBlock : $"{goalBlock}\n{askBlock}";
        }
        var selection = _selectionContext?.SelectionFor(session.GrasshopperDoc);
        var digest = _selectionContext?.CanvasDigestFor(session.GrasshopperDoc);
        var grasshopperObjects = selection?.GrasshopperObjects;
        var hasSelection = selection is not null &&
            (selection.RhinoObjectIds.Count > 0 ||
             grasshopperObjects is { Count: > 0 } ||
             !string.IsNullOrWhiteSpace(selection.ActiveLayerName));
        var pinnedBlock = ComposePinnedSelectionBlock(pinnedSelection);
        if (!hasSelection && digest is null && pinnedBlock is null)
        {
            return goalBlock is null ? content : $"{goalBlock}\n{content}";
        }
        var builder = new StringBuilder();
        if (goalBlock is not null)
        {
            builder.Append(goalBlock).Append('\n');
        }
        builder.Append("<vino_context>");
        // The pinned selection is the AUTHORITATIVE operand and leads the block so it is read before
        // the live-selection hint below (which may differ — the user pins, then keeps working).
        if (pinnedBlock is not null)
        {
            builder.Append(pinnedBlock).Append(' ');
        }
        if (digest is not null)
        {
            builder.Append("Canvas: ")
                .Append(digest.ComponentCount)
                .Append(" component(s) @ r")
                .Append(digest.Revision)
                .Append(". ");
        }
        if (hasSelection && selection is not null)
        {
            if (grasshopperObjects is { Count: > 0 })
            {
                builder.Append("Current Grasshopper selection (discovery hint, not fingerprints): ")
                    .Append(grasshopperObjects.Count)
                    .Append(" object(s): ");
                builder.AppendJoin(
                    ", ",
                    grasshopperObjects
                        .Take(MaximumContextGrasshopperSelections)
                        .Select(item =>
                        {
                            var label = string.IsNullOrWhiteSpace(item.NickName)
                                ? item.Name
                                : item.NickName;
                            return string.IsNullOrWhiteSpace(label)
                                ? item.ObjectId.ToString("D")
                                : $"{label} ({item.ObjectId:D})";
                        }));
                if (grasshopperObjects.Count > MaximumContextGrasshopperSelections)
                {
                    builder.Append(",...");
                }
                builder.Append(". ");
            }
            builder.Append("Current Rhino selection (discovery hint, not fingerprints): ");
            if (selection.RhinoObjectIds.Count == 0)
            {
                builder.Append("none");
            }
            else
            {
                builder.Append(selection.RhinoObjectIds.Count).Append(" object(s): ");
                builder.AppendJoin(
                    ',',
                    selection.RhinoObjectIds.Take(MaximumContextSelectionIds).Select(id => id.ToString("D")));
                if (selection.RhinoObjectIds.Count > MaximumContextSelectionIds)
                {
                    builder.Append(",...");
                }
            }
            if (!string.IsNullOrWhiteSpace(selection.ActiveLayerName))
            {
                builder.Append("; active layer: ").Append(selection.ActiveLayerName);
            }
        }
        builder.Append("</vino_context>").Append('\n');
        builder.Append(content);
        return builder.ToString();
    }

    /// <summary>
    /// Formats the user's pinned selection as the turn's authoritative operand, or null when nothing
    /// was pinned. Unlike the live-selection hint, this is what the message is ABOUT: the user
    /// captured it at send time and then kept working, so the agent must act on exactly these ids and
    /// not re-read the (possibly changed) live selection. Ids fix identity; current fingerprints are
    /// still resolved live before any write, as usual.
    /// </summary>
    private static string? ComposePinnedSelectionBlock(PinnedSelection? pinned)
    {
        if (pinned is null)
        {
            return null;
        }
        var rhinoIds = (pinned.RhinoObjectIds ?? []).Where(id => id != Guid.Empty).ToArray();
        var ghObjects = (pinned.GrasshopperObjects ?? [])
            .Where(item => item.Id != Guid.Empty)
            .ToArray();
        if (rhinoIds.Length == 0 && ghObjects.Length == 0)
        {
            return null;
        }
        var builder = new StringBuilder();
        builder.Append(
            "The user PINNED these objects as the target of THIS message — act on exactly these and " +
            "do NOT substitute the live selection below (the user pinned them, then kept working, so " +
            "the live selection may differ). Resolve their current fingerprints before writing, as usual. ");
        if (ghObjects.Length > 0)
        {
            builder.Append("Pinned Grasshopper components (")
                .Append(ghObjects.Length)
                .Append(')');
            if (!string.IsNullOrWhiteSpace(pinned.DocId))
            {
                builder.Append(" in definition ").Append(pinned.DocId);
            }
            builder.Append(": ");
            builder.AppendJoin(
                ", ",
                ghObjects.Take(MaximumContextGrasshopperSelections).Select(item =>
                    string.IsNullOrWhiteSpace(item.Label)
                        ? item.Id.ToString("D")
                        : $"{item.Label} ({item.Id:D})"));
            if (ghObjects.Length > MaximumContextGrasshopperSelections)
            {
                builder.Append(",...");
            }
            builder.Append(". ");
        }
        if (rhinoIds.Length > 0)
        {
            builder.Append("Pinned Rhino objects (")
                .Append(rhinoIds.Length)
                .Append("): ");
            builder.AppendJoin(',', rhinoIds.Take(MaximumContextSelectionIds).Select(id => id.ToString("D")));
            if (rhinoIds.Length > MaximumContextSelectionIds)
            {
                builder.Append(",...");
            }
            builder.Append('.');
        }
        return builder.ToString();
    }

    /// <summary>
    /// The transcript row keeps only short "[Attached: name]" lines so the chat stays readable;
    /// absolute paths never enter the persisted conversation.
    /// </summary>
    private static string BuildPersistedAttachmentContent(
        string content,
        IReadOnlyList<SavedAttachment> attachments)
    {
        var builder = new StringBuilder(content);
        foreach (var attachment in attachments)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }
            builder.Append("[Attached: ").Append(attachment.FileName).Append(']');
        }
        return builder.ToString();
    }

    private static string BuildAttachmentsTurnBlock(IReadOnlyList<SavedAttachment> attachments)
    {
        var builder = new StringBuilder();
        builder.Append("<vino_attachments>\n");
        builder.Append("The user attached local files, readable from disk:\n");
        foreach (var attachment in attachments)
        {
            builder.Append("- ")
                .Append(attachment.FileName)
                .Append(" (")
                .Append(attachment.MediaType)
                .Append("): ")
                .Append(attachment.AbsolutePath)
                .Append('\n');
        }
        builder.Append("Read these files directly from the path. (Any attached images are delivered separately as viewable image inputs.)\n");
        builder.Append("</vino_attachments>");
        return builder.ToString();
    }

    private static bool IsUnsupportedPaginatedThread(CodexProtocolException exception)
    {
        var message = exception.Message;
        return message.Contains("paginated_threads", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<TurnOutcome> WaitForTurnOutcomeAsync(
        Guid sessionId,
        ActiveTurn active,
        Task<TurnCompletionSignal> completion,
        CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        var restartCycles = 0;
        var observedRestartGeneration = Volatile.Read(ref _codexRestartGeneration);
        var notificationOnly = false;
        Exception? notificationFallbackFailure = null;

        while (true)
        {
            var delay = Task.Delay(_turnPollInterval, cancellationToken);
            if (await Task.WhenAny(completion, delay).ConfigureAwait(false) == completion)
            {
                var signal = await completion.ConfigureAwait(false);
                var finalSnapshot = await TryReadFinalSnapshotAsync(active, cancellationToken).ConfigureAwait(false);
                if (finalSnapshot is not null)
                {
                    await ReconcileSnapshotAsync(sessionId, active.TurnId, finalSnapshot, cancellationToken).ConfigureAwait(false);
                    if (IsTerminalStatus(finalSnapshot.Status))
                    {
                        return TurnOutcome.FromSnapshot(finalSnapshot);
                    }
                }
                return TurnOutcome.FromNotification(signal);
            }
            await delay.ConfigureAwait(false);

            if (notificationOnly)
            {
                if (completion.IsCompleted)
                {
                    continue;
                }
                if (!_codex.IsRunning)
                {
                    throw new CodexTurnRecoveryException(
                        $"Codex App Server exited while turn {active.TurnId} was waiting for its completion notification.",
                        notificationFallbackFailure ?? new IOException("Codex App Server exited."));
                }
                continue;
            }

            var readTask = ReadTurnWithTimeoutAsync(active, cancellationToken);
            if (await Task.WhenAny(completion, readTask).ConfigureAwait(false) == completion)
            {
                var signal = await completion.ConfigureAwait(false);
                try
                {
                    var finalSnapshot = await readTask.ConfigureAwait(false);
                    if (finalSnapshot is not null)
                    {
                        await ReconcileSnapshotAsync(sessionId, active.TurnId, finalSnapshot, cancellationToken).ConfigureAwait(false);
                        if (IsTerminalStatus(finalSnapshot.Status))
                        {
                            return TurnOutcome.FromSnapshot(finalSnapshot);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "The final authoritative read failed for Codex turn {TurnId}; using its completion notification.",
                        active.TurnId);
                }
                return TurnOutcome.FromNotification(signal);
            }

            CodexTurnReadResult? snapshot;
            try
            {
                snapshot = await readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (_codex.IsRunning &&
                    TryClassifyNotificationFallbackReadFailure(exception, out var failureKind))
                {
                    notificationOnly = true;
                    notificationFallbackFailure = exception;
                    consecutiveFailures = 0;
                    restartCycles = 0;
                    _logger.LogWarning(
                        "Codex authoritative polling is unavailable for turn {TurnId} ({FailureKind}); " +
                        "waiting for the App Server completion notification without restarting it.",
                        active.TurnId,
                        failureKind);
                    continue;
                }
                (consecutiveFailures, restartCycles, observedRestartGeneration) = await RecoverFromTurnReadFailureAsync(
                    active,
                    exception,
                    consecutiveFailures,
                    restartCycles,
                    observedRestartGeneration,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (snapshot is null)
            {
                // A valid thread/read response can omit the active turn until Codex has persisted
                // its first item. The App Server answered successfully, so this is evidence that
                // the connection is healthy rather than a reason to restart and interrupt the turn.
                consecutiveFailures = 0;
                restartCycles = 0;
                observedRestartGeneration = Volatile.Read(ref _codexRestartGeneration);
                continue;
            }

            await ReconcileSnapshotAsync(sessionId, active.TurnId, snapshot, cancellationToken).ConfigureAwait(false);
            if (IsTerminalStatus(snapshot.Status))
            {
                return TurnOutcome.FromSnapshot(snapshot);
            }
            if (!IsInProgressStatus(snapshot.Status))
            {
                (consecutiveFailures, restartCycles, observedRestartGeneration) = await RecoverFromTurnReadFailureAsync(
                    active,
                    new CodexProtocolException($"thread/read returned unsupported turn status '{snapshot.Status}'."),
                    consecutiveFailures,
                    restartCycles,
                    observedRestartGeneration,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            // A healthy in-progress snapshot is proof that the connection is alive. There is deliberately
            // no whole-turn timeout: complex turns may continue for as long as Codex reports valid progress.
            consecutiveFailures = 0;
            restartCycles = 0;
            observedRestartGeneration = Volatile.Read(ref _codexRestartGeneration);
        }
    }

    private static bool TryClassifyNotificationFallbackReadFailure(
        Exception exception,
        out string failureKind)
    {
        failureKind = string.Empty;
        if (exception is not CodexProtocolException protocolException)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(protocolException.Message);
            var root = document.RootElement;
            if (!root.TryGetProperty("code", out var codeElement) ||
                !codeElement.TryGetInt32(out var code) ||
                !root.TryGetProperty("message", out var messageElement) ||
                messageElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            var message = messageElement.GetString() ?? string.Empty;
            if (code == -32603 &&
                message.Contains("rollout", StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("read", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("load", StringComparison.OrdinalIgnoreCase)))
            {
                failureKind = "active-rollout-read";
                return true;
            }
            if (code == -32601 &&
                message.Contains("paginated_threads", StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)))
            {
                failureKind = "paginated-thread-read";
                return true;
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }

    private async Task<CodexTurnReadResult?> TryReadFinalSnapshotAsync(
        ActiveTurn active,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadTurnWithTimeoutAsync(active, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "The final authoritative read failed for Codex turn {TurnId}; using its completion notification.",
                active.TurnId);
            return null;
        }
    }

    private async Task<CodexTurnReadResult?> ReadTurnWithTimeoutAsync(
        ActiveTurn active,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_turnReadTimeout);
        try
        {
            return await _codex.ReadTurnAsync(active.ThreadId, active.TurnId, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Codex thread/read for turn {active.TurnId} exceeded {_turnReadTimeout}.",
                exception);
        }
    }

    private async Task<(int ConsecutiveFailures, int RestartCycles, long RestartGeneration)> RecoverFromTurnReadFailureAsync(
        ActiveTurn active,
        Exception exception,
        int consecutiveFailures,
        int restartCycles,
        long observedRestartGeneration,
        CancellationToken cancellationToken)
    {
        consecutiveFailures++;
        _logger.LogWarning(
            exception,
            "Authoritative read {FailureCount}/{FailureLimit} failed for Codex turn {TurnId}.",
            consecutiveFailures,
            _turnReadFailuresBeforeRestart,
            active.TurnId);
        if (consecutiveFailures < _turnReadFailuresBeforeRestart)
        {
            return (consecutiveFailures, restartCycles, observedRestartGeneration);
        }
        if (restartCycles >= _turnRestartCycles)
        {
            throw new CodexTurnRecoveryException(
                $"Codex turn {active.TurnId} could not be recovered after {_turnRestartCycles} App Server restart cycle(s).",
                exception);
        }

        var nextGeneration = await RestartCodexIfCurrentAsync(
            observedRestartGeneration,
            cancellationToken).ConfigureAwait(false);
        return (0, restartCycles + 1, nextGeneration);
    }

    private async Task<long> RestartCodexIfCurrentAsync(
        long observedGeneration,
        CancellationToken cancellationToken)
    {
        await _codexRestartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentGeneration = Volatile.Read(ref _codexRestartGeneration);
            if (currentGeneration != observedGeneration)
            {
                return currentGeneration;
            }

            if (_codex.IsRunning)
            {
                await _codex.StopAsync().ConfigureAwait(false);
            }
            return Interlocked.Increment(ref _codexRestartGeneration);
        }
        finally
        {
            _codexRestartGate.Release();
        }
    }

    private async Task ReconcileSnapshotAsync(
        Guid sessionId,
        string turnId,
        CodexTurnReadResult snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var message in snapshot.AgentMessages)
        {
            await PersistAssistantMessageAsync(
                sessionId,
                turnId,
                message.Text,
                message.Phase,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CompleteTurnAsync(
        Guid sessionId,
        SessionPauseGate pauseGate,
        ActiveTurn active,
        TurnOutcome outcome,
        CancellationToken cancellationToken)
    {
        var transition = _stateTransitionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        SessionRecord? tidyTarget = null;
        try
        {
            var current = await _store.FindSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (current is null || pauseGate.IsPaused || current.State == SessionStates.Paused)
            {
                return;
            }

            var completed = IsCompletedStatus(outcome.Status);
            var hasAssistant = _assistantTurns.ContainsKey(active.TurnId);
            // A turn that ends by putting a card on screen (a proposed approval, an asked question, a
            // proposed goal) is DONE, not broken: the card is its output and the model was told to stop
            // and wait for the user, so it legitimately produces no assistant message. Only a completed
            // turn with neither an assistant message NOR a card raised this turn is the lost response
            // this once flagged on EVERY card turn — turning a normal card into a red error + Failed.
            var awaitingUserCard = completed && !hasAssistant &&
                SessionIssuedAwaitCardDuring(current, active.StartedAt);
            var settled = completed && (hasAssistant || awaitingUserCard);
            if (!settled)
            {
                var error = completed
                    ? "Codex reported completion, but Vino could not recover an assistant response."
                    : FormatTurnFailure(outcome);
                await PersistSystemErrorAsync(
                    sessionId,
                    active.TurnId,
                    error,
                    cancellationToken).ConfigureAwait(false);
            }

            await _store.SetSessionStateAsync(
                sessionId,
                settled ? SessionStates.Idle : SessionStates.Failed,
                null,
                cancellationToken).ConfigureAwait(false);
            if (completed && hasAssistant)
            {
                tidyTarget = current;
            }
        }
        finally
        {
            transition.Release();
        }

        // Automatic canvas tidy: a successful turn that authored components lays out its dataflow cluster
        // with the real bounds, so the user always gets a clean layout even if the model skipped
        // arrange_layout. Runs outside the state gate; best effort (the service swallows its own failures).
        if (tidyTarget is not null && _layoutTidy is not null)
        {
            var moved = await _layoutTidy.TidyTurnCreationsAsync(tidyTarget, cancellationToken).ConfigureAwait(false);
            if (moved > 0)
            {
                _events.Publish();
            }
        }
    }

    private async Task PersistUnhandledTurnFailureAsync(Guid sessionId, Exception exception)
    {
        var message = $"Turn failed: {exception.Message}";
        if (_activeTurns.TryGetValue(sessionId, out var active))
        {
            await PersistSystemErrorAsync(
                sessionId,
                active.TurnId,
                message,
                CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await _store.AppendMessageAsync(
                sessionId,
                "system",
                message,
                phase: "error",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }

        var transition = _stateTransitionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await transition.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = await _store.FindSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            var pauseGate = _pauseGates.GetOrAdd(sessionId, static _ => new SessionPauseGate());
            if (current?.State != SessionStates.Paused && !pauseGate.IsPaused)
            {
                await _store.SetSessionStateAsync(
                    sessionId,
                    SessionStates.Failed,
                    null,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            transition.Release();
        }
    }

    private async Task PersistAssistantMessageAsync(
        Guid sessionId,
        string turnId,
        string text,
        string? phase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        var append = await _store.AppendMessageOnceAsync(
            sessionId,
            "assistant",
            text,
            phase,
            BuildCodexMessageId(turnId, phase, text),
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(append.Message.Role, "assistant", StringComparison.Ordinal))
        {
            _assistantTurns[turnId] = 0;
        }
        if (append.Created)
        {
            _events.Publish();
        }
    }

    private async Task PersistSystemErrorAsync(
        Guid sessionId,
        string turnId,
        string text,
        CancellationToken cancellationToken)
    {
        var append = await _store.AppendMessageOnceAsync(
            sessionId,
            "system",
            text,
            phase: "error",
            clientMessageId: BuildCodexMessageId(turnId, "error", text),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (append.Created)
        {
            _events.Publish();
        }
    }

    private async Task HandleNotificationAsync(string method, JsonElement parameters)
    {
        if (method == "item/completed" &&
            parameters.TryGetProperty("threadId", out var threadElement) &&
            parameters.TryGetProperty("item", out var item) &&
            item.TryGetProperty("type", out var type) &&
            string.Equals(type.GetString(), "agentMessage", StringComparison.Ordinal) &&
            item.TryGetProperty("text", out var text))
        {
            var threadId = threadElement.GetString() ?? string.Empty;
            var session = await _store.FindSessionByThreadAsync(threadId).ConfigureAwait(false);
            var turnId = ReadString(parameters, "turnId");
            if (turnId is null && session is not null &&
                _activeTurns.TryGetValue(session.Id, out var active) &&
                string.Equals(active.ThreadId, threadId, StringComparison.Ordinal))
            {
                turnId = active.TurnId;
            }
            if (session is not null && turnId is not null && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                await PersistAssistantMessageAsync(
                    session.Id,
                    turnId,
                    text.GetString()!,
                    ReadString(item, "phase"),
                    CancellationToken.None).ConfigureAwait(false);
            }
            return;
        }

        if (method == "turn/completed" && parameters.TryGetProperty("turn", out var turn))
        {
            var turnId = ReadString(turn, "id");
            var completion = new TurnCompletionSignal(
                ReadString(turn, "status") ?? "failed",
                ParseTurnError(turn));
            if (turnId is not null && _turnCompletions.TryGetValue(turnId, out var waiter))
            {
                waiter.TrySetResult(completion);
            }
            else if (turnId is not null)
            {
                _earlyTurnCompletions[turnId] = completion;
            }
            // Usage telemetry runs strictly AFTER the completion signal: it touches the store
            // (fallible I/O) and must never be able to swallow a turn-completion notification.
            await RecordUsageAsync(parameters, turn).ConfigureAwait(false);
            return;
        }

        // Token-usage notifications carry the thread's cumulative tokens, the last-request
        // footprint, and the context-window size. Live codex sends thread/tokenUsage/updated;
        // the tokenCount suffixes cover older CLI variants.
        if (method.EndsWith("tokenUsage/updated", StringComparison.OrdinalIgnoreCase) ||
            method.EndsWith("tokenCount", StringComparison.OrdinalIgnoreCase) ||
            method.EndsWith("token_count", StringComparison.OrdinalIgnoreCase))
        {
            await RecordUsageAsync(parameters, parameters).ConfigureAwait(false);
            return;
        }

        // Compaction completion arrives as thread/compacted (deprecated but still emitted) or as a
        // completed contextCompaction thread item — accept both: resolve any pre-turn waiter and
        // surface one system note so the user can see the history was summarized.
        if (method == "thread/compacted")
        {
            await HandleCompactionSignalAsync(ReadString(parameters, "threadId")).ConfigureAwait(false);
            return;
        }
        if (method == "item/completed" &&
            parameters.TryGetProperty("item", out var completedItem) &&
            completedItem.TryGetProperty("type", out var completedItemType) &&
            string.Equals(completedItemType.GetString(), "contextCompaction", StringComparison.OrdinalIgnoreCase))
        {
            await HandleCompactionSignalAsync(ReadString(parameters, "threadId")).ConfigureAwait(false);
            return;
        }

        // Account rate limits arrive on their own notification with NO threadId (they are
        // account-scoped), so they update the account snapshot directly instead of a session.
        if (method.EndsWith("rateLimits/updated", StringComparison.OrdinalIgnoreCase) && _usage is not null)
        {
            var snapshot = SessionUsageState.TryParse(parameters);
            if (snapshot is { RateLimits.Count: > 0 })
            {
                _usage.UpdateAccountLimits(snapshot.RateLimits);
                _events.Publish();
            }
            return;
        }

        _logger.LogDebug("Unhandled codex notification {Method}.", method);
    }

    private async Task RecordUsageAsync(JsonElement parameters, JsonElement usageCarrier)
    {
        if (_usage is null)
        {
            return;
        }
        try
        {
            var snapshot = SessionUsageState.TryParse(usageCarrier);
            if (snapshot is null)
            {
                return;
            }
            var threadId = ReadString(parameters, "threadId") ?? ReadString(parameters, "thread_id");
            if (threadId is null)
            {
                return;
            }
            var session = await _store.FindSessionByThreadAsync(threadId).ConfigureAwait(false);
            if (session is not null && _usage.Update(session.Id, snapshot))
            {
                _events.Publish();
            }
        }
        catch (Exception exception)
        {
            // Telemetry only — a transient store failure must not disturb notification handling.
            _logger.LogDebug(exception, "Usage telemetry update failed.");
        }
    }

    /// <summary>
    /// Proactive compaction: when the session's last-known context footprint crossed the configured
    /// threshold, ask codex to compact the thread before this turn. Best effort — a failed or slow
    /// compaction never blocks the turn, and a per-thread cooldown stops a stale usage snapshot
    /// from triggering a compaction storm.
    /// </summary>
    private async Task CompactThreadIfNearLimitAsync(Guid sessionId, string threadId, CancellationToken cancellationToken)
    {
        var threshold = _options.ContextCompactThresholdPercent;
        if (_usage is null || threshold <= 0 || !_usage.TryGet(sessionId, out var snapshot))
        {
            return;
        }
        if (snapshot.ContextWindow is not > 0 || snapshot.ContextUsedTokens is not > 0)
        {
            return;
        }
        var usedPercent = snapshot.ContextUsedTokens.Value * 100.0 / snapshot.ContextWindow.Value;
        if (usedPercent < threshold)
        {
            return;
        }
        if (_compactionRequestedAt.TryGetValue(threadId, out var lastRequest) &&
            DateTimeOffset.UtcNow - lastRequest < CompactionCooldown)
        {
            return;
        }
        await RequestThreadCompactionAsync(
            sessionId,
            threadId,
            $"Context {usedPercent:0}% full — compacting this session's history before the next turn.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends thread/compact/start and waits (bounded) for codex to signal completion. Returns true
    /// only when compaction was confirmed — callers retrying a failed turn must not retry against
    /// an uncompacted thread, that would just fail identically.
    /// </summary>
    private async Task<bool> RequestThreadCompactionAsync(
        Guid sessionId,
        string threadId,
        string note,
        CancellationToken cancellationToken)
    {
        var waiter = _compactionWaiters.GetOrAdd(
            threadId,
            static _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        _compactionRequestedAt[threadId] = DateTimeOffset.UtcNow;
        try
        {
            await _store.AppendMessageAsync(
                sessionId,
                "system",
                note,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _events.Publish();
            await _codex.CompactThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
            var done = await Task.WhenAny(
                waiter.Task,
                Task.Delay(CompactionCompletionWait, cancellationToken)).ConfigureAwait(false) == waiter.Task;
            if (!done)
            {
                _logger.LogWarning(
                    "Thread {ThreadId} compaction did not signal completion within {Seconds}s; continuing without waiting.",
                    threadId,
                    CompactionCompletionWait.TotalSeconds);
            }
            return done;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Thread {ThreadId} compaction request failed; continuing with the uncompacted thread.",
                threadId);
            return false;
        }
        finally
        {
            _compactionWaiters.TryRemove(threadId, out _);
        }
    }

    private async Task HandleCompactionSignalAsync(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }
        if (_compactionWaiters.TryGetValue(threadId, out var waiter))
        {
            waiter.TrySetResult(true);
        }
        // One note per compaction cycle even when both the deprecated notification and the item arrive.
        var now = DateTimeOffset.UtcNow;
        if (_compactionNotedAt.TryGetValue(threadId, out var lastNote) && now - lastNote < TimeSpan.FromSeconds(30))
        {
            return;
        }
        _compactionNotedAt[threadId] = now;
        try
        {
            var session = await _store.FindSessionByThreadAsync(threadId).ConfigureAwait(false);
            if (session is not null)
            {
                await _store.AppendMessageAsync(
                    session.Id,
                    "system",
                    "Codex compacted this session's context — older turns were summarized in place.",
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                _events.Publish();
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not surface the compaction note for thread {ThreadId}.", threadId);
        }
    }

    private static bool IsContextOverflowFailure(TurnOutcome outcome)
    {
        if (IsCompletedStatus(outcome.Status) || outcome.Error is null)
        {
            return false;
        }
        var text = $"{outcome.Error.Message} {outcome.Error.AdditionalDetails}";
        return text.Contains("context window", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("context length", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("maximum context", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("exceeds the context", StringComparison.OrdinalIgnoreCase);
    }

    private static CodexTurnError? ParseTurnError(JsonElement turn)
    {
        if (!turn.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return new CodexTurnError(
            ReadString(error, "message") ?? "Unknown Codex turn error.",
            ReadString(error, "additionalDetails"),
            error.TryGetProperty("codexErrorInfo", out var info) && info.ValueKind != JsonValueKind.Null
                ? info.Clone()
                : null);
    }

    private static string FormatTurnFailure(TurnOutcome outcome)
    {
        if (outcome.Error is null)
        {
            return $"Codex turn ended with status '{outcome.Status}'.";
        }
        return string.IsNullOrWhiteSpace(outcome.Error.AdditionalDetails)
            ? $"Codex turn failed: {outcome.Error.Message}"
            : $"Codex turn failed: {outcome.Error.Message} ({outcome.Error.AdditionalDetails})";
    }

    private static string BuildCodexMessageId(string turnId, string? phase, string text)
    {
        var normalizedPhase = string.IsNullOrWhiteSpace(phase) ? "none" : phase.Trim().ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return $"vino-codex-v1:{turnId}:{normalizedPhase}:{hash}";
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsCompletedStatus(string status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsInProgressStatus(string status) =>
        string.Equals(status, "inProgress", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalStatus(string status) =>
        IsCompletedStatus(status) ||
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan PositiveOrDefault(TimeSpan value, TimeSpan fallback) =>
        value > TimeSpan.Zero ? value : fallback;

    private async Task InterruptActiveTurnAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_activeTurns.TryGetValue(sessionId, out var active))
        {
            return;
        }
        try
        {
            await _codex.InterruptTurnAsync(active.ThreadId, active.TurnId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not interrupt active turn for paused session {SessionId}.", sessionId);
        }
    }


    /// <summary>
    /// True when this turn ended by putting a card on screen that waits for the user — a proposed
    /// approval, an asked question, or a proposed goal. The card's own timestamp scopes it to THIS
    /// turn (its Proposed/Asked time is at or after the turn started), so a card left pending from an
    /// earlier turn cannot mask a genuinely empty later turn.
    /// </summary>
    private static bool SessionIssuedAwaitCardDuring(SessionRecord session, DateTimeOffset since) =>
        AskCardIsAskedSince(session.AskCard, since) ||
        ApprovalCardIsProposedSince(session.ApprovalCard, since) ||
        GoalCardIsProposedSince(session.GoalCard, since);

    private static bool AskCardIsAskedSince(string? json, DateTimeOffset since)
    {
        var card = TryDeserializeCard<AskCard>(json);
        return card is not null &&
            string.Equals(card.Status, "asking", StringComparison.OrdinalIgnoreCase) &&
            card.AskedAt is { } asked && asked >= since;
    }

    private static bool ApprovalCardIsProposedSince(string? json, DateTimeOffset since)
    {
        var card = TryDeserializeCard<ApprovalCard>(json);
        return card is not null &&
            string.Equals(card.Status, "proposing", StringComparison.OrdinalIgnoreCase) &&
            card.ProposedAt is { } proposed && proposed >= since;
    }

    private static bool GoalCardIsProposedSince(string? json, DateTimeOffset since)
    {
        var card = TryDeserializeCard<GoalCard>(json);
        return card is not null &&
            string.Equals(card.Status, "proposing", StringComparison.OrdinalIgnoreCase) &&
            card.ProposedAt is { } proposed && proposed >= since;
    }

    private static T? TryDeserializeCard<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json, GoalJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ActiveTurn(string ThreadId, string TurnId, DateTimeOffset StartedAt);

    private sealed record RecoveredChatMessage(string Role, string Content);

    private sealed record TurnCompletionSignal(string Status, CodexTurnError? Error);

    private sealed record TurnOutcome(string Status, CodexTurnError? Error)
    {
        public static TurnOutcome FromNotification(TurnCompletionSignal completion) =>
            new(completion.Status, completion.Error);

        public static TurnOutcome FromSnapshot(CodexTurnReadResult snapshot) =>
            new(snapshot.Status, snapshot.Error);
    }

    private sealed class CodexTurnRecoveryException(string message, Exception innerException)
        : InvalidOperationException(message, innerException);

    private sealed class SessionPauseGate
    {
        private readonly object _gate = new();
        private TaskCompletionSource? _resume;
        private bool _paused;
        private int _waiters;

        public bool IsPaused
        {
            get
            {
                lock (_gate)
                {
                    return _paused;
                }
            }
        }

        public int WaiterCount => Volatile.Read(ref _waiters);

        public void Pause()
        {
            lock (_gate)
            {
                if (_paused)
                {
                    return;
                }
                _paused = true;
                _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Resume()
        {
            TaskCompletionSource? resume;
            lock (_gate)
            {
                if (!_paused)
                {
                    return;
                }
                _paused = false;
                resume = _resume;
                _resume = null;
            }
            resume?.TrySetResult();
        }

        public async Task WaitUntilResumedAsync(CancellationToken cancellationToken)
        {
            Task? wait;
            lock (_gate)
            {
                wait = _paused ? _resume?.Task : null;
            }
            if (wait is null)
            {
                return;
            }
            Interlocked.Increment(ref _waiters);
            try
            {
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _waiters);
            }
        }
    }
}

public sealed class SessionPausedException(Guid sessionId)
    : InvalidOperationException($"Session {sessionId:D} is paused.");
