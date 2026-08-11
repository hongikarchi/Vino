namespace GPTino.AgentHost.Api;

public static class SessionStates
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Paused = "paused";
    public const string Failed = "failed";
}

public sealed record SessionRecord(
    Guid Id,
    string Name,
    string ModelProfile,
    string? Model,
    string State,
    int Order,
    string? CodexThreadId,
    string? CurrentTask,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? GrasshopperDoc = null,
    // The goal card as opaque JSON (GoalCard shape below), or null before the agent has framed
    // one. The store persists it; the agent proposes it and the user confirms it.
    string? GoalCard = null,
    // A pending/answered approval card as opaque JSON (ApprovalCard shape), or null.
    string? ApprovalCard = null,
    // A pending/answered ask card as opaque JSON (AskCard shape), or null.
    string? AskCard = null);

/// <summary>
/// What the agent understood the user to be asking for, framed BEFORE the work starts so the
/// user can correct it cheaply: the objective in one line, the criteria that will decide whether
/// it worked, the assumptions the agent had to make, and what it is deliberately leaving out.
/// The options are the user's structured replies (approve / narrow / correct …), each optionally
/// carrying Rhino object ids so choosing one can also show what it means in the viewport.
/// Lifecycle: proposing -> confirmed -> scored (or rejected).
/// </summary>
public sealed record GoalCard(
    string Status,
    string Objective,
    IReadOnlyList<string> Criteria,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> OutOfScope,
    IReadOnlyList<GoalOption>? Options = null,
    string? ChosenOption = null,
    IReadOnlyList<GoalCriterionScore>? Scores = null,
    DateTimeOffset? ProposedAt = null,
    DateTimeOffset? ConfirmedAt = null);

public sealed record GoalOption(string Id, string Label, string? Detail = null, IReadOnlyList<Guid>? ObjectIds = null);

/// <summary>One criterion's verdict. Evidence must quote a job/predicate result, never a claim.</summary>
public sealed record GoalCriterionScore(string Criterion, bool Passed, string Evidence);

public sealed record ChatMessage(
    long Id,
    Guid SessionId,
    string Role,
    string Content,
    string? Phase,
    DateTimeOffset CreatedAt);

public sealed record CreateSessionRequest(
    string Name,
    // Reasoning-effort level (low|medium|high|xhigh|max|ultra); field name kept as ModelProfile for
    // wire back-compat. Default xhigh. No adaptive routing — the value is used directly (clamped to
    // the model's supported efforts at turn time).
    string ModelProfile = "xhigh",
    string? Model = null,
    string? GrasshopperDoc = null);

/// <summary>
/// Rebinds a session to one Grasshopper document (a durable docKey) or clears the binding (null =
/// default document resolution: the only registered document when exactly one is open).
/// </summary>
public sealed record SetSessionTargetRequest(string? GrasshopperDoc = null);

/// <summary>One (objectId, fingerprint) pair the approval card displayed.</summary>
/// <summary>
/// A destructive fix the agent wants to make to geometry the USER owns. The broker refuses those
/// by default, so the agent lists exactly what it would touch and the user picks. Each item is
/// pinned to the fingerprint that was audited: if the object moved since, the grant no longer
/// matches it and the fix fails instead of hitting something else (approve-what-you-saw).
/// Lifecycle: proposing -> granted (carries grantId) or rejected.
/// </summary>
public sealed record ApprovalCard(
    string Status,
    string Summary,
    IReadOnlyList<ApprovalItem> Items,
    string? GrantId = null,
    IReadOnlyList<string>? ApprovedItemIds = null,
    DateTimeOffset? ProposedAt = null,
    // The option the user picked per item (item id -> chosen label), for the items whose fix the
    // machine must NOT decide. Persisted because it IS the answer: without it the next turn carries
    // a grant covering both near-duplicates and no word on which one the user meant to lose.
    IReadOnlyDictionary<string, string>? Choices = null,
    // Card kind marker ("layerSemantics" for layer-curation proposal tables); null = the classic
    // destructive-fix card. SHARED CONTRACT with the panel, absent on legacy cards.
    string? Kind = null,
    // Layer cards only: the colour convention the proposed colours were computed under, plus the
    // alternatives. Picking a different one re-derives every granted row's colour server-side and
    // persists the choice for later scans.
    ApprovalPresetChoice? Preset = null,
    // When the minted grant stops being accepted. The grant itself lives in process memory with a
    // short TTL while the card is a durable row, so without this the panel showed "승인됨" over a
    // key that was already dead — and the user only found out when the next write was refused.
    // Null on legacy cards and on cards that were never granted. Appended (not inserted) so any
    // positional construction of this record keeps compiling.
    DateTimeOffset? GrantExpiresAt = null,
    // Why the user said no, when they said no. Kept because a refusal is an answer the agent has
    // to hear: without it a rejected card was pure history — ComposeApprovalBlock rendered only
    // granted cards — so the next turn proposed the same thing again.
    string? RejectedReason = null,
    // Layer cards only: "recolor" (default) or "keep". "keep" writes labels and leaves every
    // colour alone — the answer for a document whose colours are already deliberate, which no
    // per-row default could express, because unticking a row skips its LABEL too.
    string? ColorPolicy = null);

/// <summary>
/// A plain question the agent needs answered before it can continue, with the answers as buttons.
///
/// <para>
/// The approval card only exists for one thing: lifting the broker's refusal on the user's own
/// geometry, which needs (objectId, fingerprint) pairs. Most of what actually stopped a session was
/// not that — it was "shall I replace 03B in parallel and verify before removing the old one?".
/// With no card for it the agent wrote the question as prose and stopped, and prose cannot be
/// clicked, so every one of those turns cost the user a typed reply.
/// </para>
/// <para>
/// Lifecycle: asking -> answered. The chosen option is delivered to the agent as a turn, so
/// pressing a button and typing the sentence are the same act.
/// </para>
/// </summary>
public sealed record AskCard(
    string Status,
    string Question,
    IReadOnlyList<AskOption> Options,
    // Why it has to ask at all, in one line — shown under the question so the user is not guessing
    // what is at stake.
    string? Because = null,
    string? ChosenOptionId = null,
    // Free text the user added when answering. Optional by design: an answer must stay one click.
    string? Note = null,
    DateTimeOffset? AskedAt = null,
    DateTimeOffset? AnsweredAt = null);

/// <param name="Recommended">
/// The agent's own recommendation. Exactly one option may carry it; the panel makes that the
/// default so Ctrl+Enter can answer without reading every line.
/// </param>
public sealed record AskOption(string Id, string Label, string? Detail = null, bool Recommended = false);

/// <summary>The user's answer to an ask card.</summary>
public sealed record AnswerAskRequest(string OptionId, string? Note = null);

/// <summary>The active colour preset and the alternatives the card offers (layer cards only).</summary>
public sealed record ApprovalPresetChoice(
    string Selected,
    IReadOnlyList<ApprovalPresetOption> Options);

public sealed record ApprovalPresetOption(string Id, string Label);

/// <summary>
/// One reviewable fix. Choices exist for findings where the machine must not decide — which of two
/// near-duplicates to keep is the user's call, because design-option stacks are intentional.
/// </summary>
public sealed record ApprovalItem(
    string Id,
    string Label,
    string? Measure,
    IReadOnlyList<ApprovalGrantItem> Targets,
    IReadOnlyList<string>? Choices = null,
    // Layer-curation cards only: the SERVER-synthesized proposal row (matcher + palette output).
    // Model-authored values for these fields are ignored at request time — confidence and colors
    // must never be model self-report. Null on every other card kind.
    ApprovalLayerRow? LayerRow = null,
    // layerScheme cards only: one proposed naming rule for a GROUP of layers. Unlike LayerRow this
    // IS the model's proposal — naming a group is the judgement we ask it for — but the members and
    // the material family are validated against the document and the palette before the card
    // renders, and nothing is written until the user approves the row.
    ApprovalSchemeRow? SchemeRow = null);

/// <summary>
/// A proposed scheme rule for one observed group, on the two independent axes. Element is the
/// user's own vocabulary (free text — their office names things their way); Material must be a
/// palette family, because colour is derived from it. UnderPath scopes the material to a layer
/// branch, which is how a file usually declares what its contents are made of.
/// </summary>
public sealed record ApprovalSchemeRow(
    string GroupKey,
    string GroupKind,
    IReadOnlyList<string> Members,
    string? Element = null,
    string? Material = null,
    string? UnderPath = null,
    string? Evidence = null);

/// <summary>
/// One server-computed layer-curation proposal row (SHARED CONTRACT with the panel). Canonical and
/// Material are empty strings for triage rows (no deterministic rule matched — the model offers
/// family candidates through the existing Choices channel and the user decides). ProposedArgbColor
/// equals CurrentArgbColor when no color change is proposed. FocusObjectIds are TOP-LEVEL sample
/// objects on the layer — the ◎ viewport focus needs selectable objects, a layer GUID is not one.
/// </summary>
public sealed record ApprovalLayerRow(
    string FullPath,
    string Canonical,
    string Material,
    string Confidence,
    string Evidence,
    int CurrentArgbColor,
    int ProposedArgbColor,
    bool PreChecked,
    IReadOnlyList<Guid>? FocusObjectIds = null,
    // The layer already carries a colour somebody appears to have chosen. A MARKER, not a veto:
    // un-checking every such row left nothing pre-checked on a real document (where most layers
    // are coloured), which turned a bulk approve into thirty manual ticks — the work this feature
    // removes. The card-level colour policy answers that; this only flags what to look at.
    bool CustomColour = false);

/// <summary>The user's answer: which items to grant, plus any per-item choice they made.</summary>
public sealed record AnswerApprovalRequest(
    string Status,
    IReadOnlyList<string>? ApprovedItemIds = null,
    IReadOnlyDictionary<string, string>? Choices = null,
    // Layer cards: the colour preset the user picked on the card. Persisted to the project table
    // and used to re-derive the granted rows' colours before the agent's next turn reads them.
    string? Preset = null,
    // Optional free text attached to a refusal ("이건 두고 저것만"). Delivered to the agent with
    // the refusal so a "no, because…" does not have to be retyped as a chat message.
    string? Reason = null,
    // Layer cards: "recolor" or "keep". "keep" applies labels only, leaving every colour alone.
    string? ColorPolicy = null);

/// <summary>
/// One (objectId, fingerprint) target the approval card displays. Label/Role/Impact are optional
/// model-authored display strings (SHARED CONTRACT with the panel): label = short name, role =
/// what the component does in the definition, impact = what changes if it is deleted / what
/// replaces it. Absent on legacy cards — they keep deserializing (null) and the panel simply
/// omits those lines. Grant minting still binds ONLY (ObjectId, Fingerprint).
/// </summary>
public sealed record ApprovalGrantItem(
    Guid ObjectId,
    string Fingerprint,
    string? Label = null,
    string? Role = null,
    string? Impact = null,
    // Which world this id lives in: "rhino" (a document object) or "grasshopper" (a canvas
    // component). The panel needs it to point the right viewport at the target — the zoom control
    // used to be hardwired to the Grasshopper canvas, so a card about Rhino meshes answered with
    // "no Grasshopper definition is open". Absent means rhino: every card observed in practice is
    // Rhino-side, and offering the Rhino viewport for a canvas id merely reports "not found",
    // whereas the reverse produced an error blob printed next to the row.
    string? Domain = null);

/// <summary>Panel viewport focus: mode is select | isolate | lock | restore.</summary>
public sealed record FocusRequest(IReadOnlyList<Guid>? ObjectIds, string? Mode, bool? Zoom);

/// <summary>
/// Panel canvas focus: select + frame the given Grasshopper components (no mode; view-only).
/// </summary>
/// <param name="DocId">
/// Which Grasshopper document the ids belong to (a docKey). REQUIRED once more than one
/// definition is open: without it the host falls back to the first-registered document, and a chip
/// pointing at a component in the second definition silently selects nothing there while clearing
/// the innocent document's selection. Null keeps the single-document behaviour.
/// </param>
public sealed record CanvasFocusEndpointRequest(
    IReadOnlyList<Guid>? ObjectIds,
    bool? Zoom,
    string? DocId = null);

/// <summary>Prose language for GPTino's answers: "ko" or "en" (anything else reads as "en").</summary>
public sealed record LanguageSetting(string Language);

public sealed record ReorderSessionsRequest(
    IReadOnlyList<Guid> OrderedSessionIds,
    long OrderVersion);

public sealed record SendMessageRequest(
    string Content,
    string? ClientMessageId = null,
    IReadOnlyList<IncomingAttachment>? Attachments = null,
    // A selection the user pinned in the composer at send time (like an attachment). When present it
    // is the AUTHORITATIVE operand for this turn — the objects to act on — not the live-selection
    // discovery hint. Lets the user pin, then keep working in Rhino/GH without holding the selection.
    PinnedSelection? PinnedSelection = null);

/// <summary>A file the panel attached to a message, carried as Base64 over the loopback API.</summary>
public sealed record IncomingAttachment(string FileName, string MediaType, string DataBase64);

/// <summary>
/// A user-pinned selection snapshot: exactly the Rhino objects and/or Grasshopper components the
/// message targets. Ids fix WHICH objects; fingerprints are still resolved live before any write.
/// </summary>
public sealed record PinnedSelection(
    IReadOnlyList<Guid>? RhinoObjectIds = null,
    IReadOnlyList<PinnedGrasshopperObject>? GrasshopperObjects = null);

public sealed record PinnedGrasshopperObject(Guid Id, string? Label = null);

public sealed record SetPausedRequest(bool Paused);

public sealed record SetModelRequest(string ModelProfile, string? Model = null);

/// <summary>Rename a session; the name becomes its display title.</summary>
public sealed record RenameSessionRequest(string Name);

/// <summary>
/// The user's answer to a proposed goal card. Status is "confirmed" or "rejected"; the optional
/// edits let the user correct the objective/criteria before approving (approve-what-you-saw:
/// whatever comes back is what the agent is held to).
/// </summary>
public sealed record SetGoalRequest(
    string Status,
    string? ChosenOption = null,
    string? Objective = null,
    IReadOnlyList<string>? Criteria = null);

public sealed record RuntimeStatus(
    string State,
    Guid ProjectId,
    string? RhinoPath,
    string? GrasshopperPath,
    bool BridgeConnected,
    string? WriterSessionId,
    int QueueLength,
    DateTimeOffset StartedAt);

public sealed record HostStateResponse(
    RuntimeStatus Runtime,
    IReadOnlyList<SessionRecord> Sessions,
    long OrderVersion);

public sealed record ModelView(
    string Id,
    string Model,
    string DisplayName,
    string Description,
    bool IsDefault,
    IReadOnlyList<string> ReasoningEfforts);

public sealed record AcceptedTurn(Guid SessionId, long MessageId, string State);

public sealed record ArchivedSession(
    Guid Id,
    string Name,
    string State,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    bool Deleted);

public sealed record ArchivedProject(
    string Fingerprint,
    string? ProjectName,
    string? RhinoFile,
    string? GrasshopperFile,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastActivityAt,
    int SessionCount,
    bool Current,
    bool Available,
    IReadOnlyList<ArchivedSession> Sessions);

public sealed record ArchivedMessage(
    long Id,
    string Role,
    string Content,
    string? Phase,
    DateTimeOffset CreatedAt);

public sealed record ApiError(string Code, string Message);
