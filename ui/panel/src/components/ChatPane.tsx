import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ChangeEvent,
  type ClipboardEvent,
  type DragEvent,
  type KeyboardEvent,
  type SyntheticEvent,
} from "react";
import { clearDraft, readDraft, writeDraft, type PendingAttachment } from "../draftStore";
import { SelectionRail } from "./SelectionRail";
import { ErrorBoundary } from "./ErrorBoundary";
import type {
  ApprovalAnswer,
  ApprovalCard as ApprovalCardData,
  AskCard as AskCardData,
  CanvasFocusResult,
  GrasshopperObjectRef,
  ChatMessage,
  CodexLimits,
  CurrentSelection,
  FocusMode,
  FocusResult,
  PinnedSelection,
  GoalCard as GoalCardData,
  GptinoSession,
  GrasshopperDocInfo,
  MessageAttachment,
  ModelInfo,
  ModelProfile,
  RuntimeConflict,
  SessionActivity,
  SessionHalt,
  SessionUsage,
} from "../types";
import { Icon } from "./Icons";
import { StatusBadge } from "./StatusBadge";
import { FocusChip } from "./FocusChip";
import { GhFocusChip } from "./GhFocusChip";
import { AltChip } from "./AltChip";
import { GoalCard } from "./GoalCard";
import { ApprovalCard } from "./ApprovalCard";
import { AskCard } from "./AskCard";
import { parseMessageSegments } from "../messageMarkers";

interface ChatPaneProps {
  session: GptinoSession | undefined;
  conflicts: RuntimeConflict[];
  models: ModelInfo[];
  /** Account-scoped codex rate limits for the status line; null before the first turn reports them. */
  limits?: CodexLimits | null;
  /** Registered GH docs; the target selector renders when more than one exists OR the session carries a (possibly stale) binding. */
  grasshopperDocs?: GrasshopperDocInfo[] | null;
  busyActions: Set<string>;
  /** The latest runtime/connection error, or null. Surfaced as a compact problem chip by the ctx line. */
  error?: string | null;
  onModel(profile: ModelProfile): void;
  onPinModel(model: string | null): void;
  /** Rename this session (its display title). */
  onRename(title: string): void;
  /** Answer the agent's proposed goal card (approve, optionally edited, or reject). */
  onAnswerGoal(answer: {
    status: "confirmed" | "rejected";
    chosenOption?: string;
    objective?: string;
    criteria?: string[];
  }): void;
  /**
   * Per-action failure messages keyed like busyActions. A card renders its OWN failure so a
   * refused approval or a rejected resume is visible where it was pressed, not only as a
   * 10px chip under the composer.
   */
  actionErrors?: Record<string, string>;
  /** Grant (or refuse) the destructive fixes the agent listed on an approval card. */
  onAnswerApproval(answer: ApprovalAnswer): void;
  /** Clear an answered approval card. */
  onDismissApproval?(): void;
  /** Clear a settled goal card (confirmed/rejected/scored) off the shelf. */
  onDismissGoal?(): void;
  /** Answer the agent's clickable question. */
  onAnswerAsk?(optionId: string, note?: string): void;
  /** Clear an answered ask card. */
  onDismissAsk?(): void;
  /** Bind the session's writes to a GH doc (docKey) or unbind with null. */
  onTarget(grasshopperDoc: string | null): void;
  /** Resolves false when the send failed (the composer restores its draft). */
  onSend(content: string, attachments?: MessageAttachment[], pinnedSelection?: PinnedSelection): Promise<boolean | void> | void;
  /** The live Rhino/GH selection (streamed), used to show the composer's pin affordance and counts. */
  currentSelection?: CurrentSelection | null;
  /** Captures the COMPLETE current selection (not the 32-id SSE cap) to pin to the next message. */
  onCaptureSelection?(): Promise<PinnedSelection>;
  /** Resume a paused session (the composer is disabled while paused). */
  onResume(): void;
  /** Clear this session's halt state (POST /resume). Resolves false when the request failed. */
  onResumeHalt(): Promise<boolean>;
  /** Soft-delete this session (hidden from the list, recoverable from the trash). */
  onDelete(): Promise<boolean | void> | void;
  /** Stop the current turn and retract the last user message; resolves its text (or null) to edit. */
  onStopEdit(): Promise<string | null>;
  /**
   * Drive the Rhino viewport onto a set of objects (GPTino's focus-reference primitive:
   * [[focus:guids|label]] markers in assistant text render as chips that call this).
   * Optional — without it markers degrade to their plain-text labels.
   */
  onFocus?(objectIds: string[], mode: FocusMode, ownerToken?: string): Promise<FocusResult>;
  /**
   * Drive the Grasshopper canvas onto a set of components ([[ghfocus:guids|label]] markers render
   * as chips that call this). Optional — without it ghfocus markers degrade to plain-text labels.
   */
  onFocusCanvas?(objectIds: string[], docId?: string | null): Promise<CanvasFocusResult>;
  /**
   * Show a proposed alternative ([[alt:id|label]] markers). Optional — without it alt
   * markers degrade to their labels, exactly like focus markers without a viewport.
   */
  onSelectAlt?(altId: string): void;
}

// PendingAttachment now lives in draftStore: staged files have to outlive this component's
// remount, so its type belongs with the store that holds them.

// No count or size cap on attachments — the user decides what is worth attaching (large images
// cost tokens/context, which is their call). Only the type allowlist is enforced client-side.
const ALLOWED_MEDIA_TYPES = new Set([
  "image/png",
  "image/jpeg",
  "image/webp",
  "image/gif",
  "text/plain",
  "text/markdown",
  "application/json",
  "text/csv",
  "application/pdf",
]);
// Windows often reports no MIME type for text-ish extensions; map them before rejecting.
const EXTENSION_MEDIA_TYPES: Record<string, string> = {
  png: "image/png",
  jpg: "image/jpeg",
  jpeg: "image/jpeg",
  webp: "image/webp",
  gif: "image/gif",
  txt: "text/plain",
  md: "text/markdown",
  markdown: "text/markdown",
  json: "application/json",
  csv: "text/csv",
  pdf: "application/pdf",
};
const ATTACHMENT_ACCEPT = [...ALLOWED_MEDIA_TYPES, ".md", ".markdown", ".txt", ".json", ".csv"].join(",");

const resolveMediaType = (file: File): string | undefined => {
  if (ALLOWED_MEDIA_TYPES.has(file.type)) return file.type;
  const extension = file.name.split(".").pop()?.toLowerCase() ?? "";
  return EXTENSION_MEDIA_TYPES[extension];
};

const formatBytes = (bytes: number) =>
  bytes >= 1024 * 1024
    ? `${(bytes / (1024 * 1024)).toFixed(1)} MB`
    : bytes >= 1024
      ? `${Math.round(bytes / 1024)} KB`
      : `${bytes} B`;

const encodeAttachment = (item: PendingAttachment): Promise<MessageAttachment> =>
  new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error(`Could not read "${item.fileName}".`));
    reader.onload = () => {
      const result = String(reader.result ?? "");
      const comma = result.indexOf(",");
      resolve({
        fileName: item.fileName,
        mediaType: item.mediaType,
        dataBase64: comma >= 0 ? result.slice(comma + 1) : result,
      });
    };
    reader.readAsDataURL(item.file);
  });

type StreamItem =
  | { type: "message"; at: number; message: ChatMessage }
  | { type: "activity"; at: number; activity: SessionActivity };

// The stream is grouped into turns: the work log (activities) that precedes an assistant reply
// belongs to that reply and folds away under it once the reply lands — click the reply to expand
// the full log. Activities with no concluding reply yet are the LIVE log (rendered separately,
// windowed to the last few) while the session is working, or an orphan log block otherwise.
type StreamBlock =
  | { type: "message"; key: string; at: number; message: ChatMessage; log: SessionActivity[] }
  | { type: "log"; key: string; at: number; activities: SessionActivity[] };

// How many live activity rows stay on screen while working; older ones fold behind a "+N earlier".
const LIVE_LOG_VISIBLE = 3;

// Reasoning-effort levels, ascending. The slider offers whichever of these the chosen model advertises.
const EFFORT_ORDER: ModelProfile[] = ["low", "medium", "high", "xhigh", "max", "ultra"];
const EFFORT_LABELS: Record<ModelProfile, string> = {
  low: "Low",
  medium: "Medium",
  high: "High",
  xhigh: "Extra High",
  max: "Max",
  ultra: "Ultra",
};
const effortRank = (value: string): number => {
  const index = EFFORT_ORDER.indexOf(value as ModelProfile);
  return index < 0 ? EFFORT_ORDER.length : index;
};

const formatTime = (value: string) =>
  new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit" }).format(new Date(value));

const compactTokens = (value: number) =>
  value >= 1_000_000 ? `${(value / 1_000_000).toFixed(1)}M` : value >= 1_000 ? `${Math.round(value / 1_000)}k` : `${value}`;

// Window labels sort shortest first: "45m" < "5h" < "weekly" < "3d" < anything unparsed.
const windowRank = (label: string): number => {
  const match = /^(\d+(?:\.\d+)?)([mhd])$/.exec(label);
  if (match) {
    const value = Number(match[1]);
    return match[2] === "m" ? value : match[2] === "h" ? value * 60 : value * 1_440;
  }
  return label === "weekly" ? 10_080 : Number.MAX_SAFE_INTEGER;
};

const usageTone = (usedPercent: number) =>
  usedPercent >= 90 ? "critical" : usedPercent >= 70 ? "warn" : "";

// One fuel-gauge segment of the status line: the filled part is what REMAINS.
function UsageMeter({ label, usedPercent, title }: { label: string; usedPercent: number; title: string }) {
  const left = Math.max(0, Math.min(100, Math.round(100 - usedPercent)));
  return (
    <span className={`usage-meter ${usageTone(usedPercent)}`} title={title}>
      <b>{label}</b>
      <span className="meter-track">
        <span className="meter-fill" style={{ width: `${left}%` }} />
      </span>
      {left}%
    </span>
  );
}

// codex-CLI-style status line under the composer: the selected session's
// context left plus the account's provider windows (5h / weekly). Every figure
// shows what REMAINS; used% and reset times live in the tooltips. Renders
// nothing until the first turn reports usage. Null guards are null-inclusive
// (`!= null`) because the server serializes absent values as explicit nulls.
function UsageStatusLine({ usage, limits }: { usage?: SessionUsage; limits?: CodexLimits | null }) {
  const hasContext = usage?.contextWindow != null && usage.contextWindow > 0 && usage.contextUsedTokens != null;
  const contextUsedPercent = hasContext
    ? Math.min(100, (usage!.contextUsedTokens! / usage!.contextWindow!) * 100)
    : undefined;
  const windows = [...(limits?.windows ?? [])].sort((a, b) => windowRank(a.label) - windowRank(b.label));
  if (contextUsedPercent === undefined && windows.length === 0) return null;

  return (
    <span className="usage-line" aria-label="Remaining codex tokens">
      {contextUsedPercent !== undefined ? (
        <UsageMeter
          label="ctx"
          usedPercent={contextUsedPercent}
          title={[
            `Context: ${compactTokens(usage!.contextUsedTokens!)} of ${compactTokens(usage!.contextWindow!)} tokens used (${Math.round(contextUsedPercent)}%)`,
            usage!.totalTokens != null ? `Session total: ${compactTokens(usage!.totalTokens)} tokens` : null,
          ]
            .filter(Boolean)
            .join("\n")}
        />
      ) : null}
      {windows.map((window) => {
        // A reset time in the past means the provider window rolled over since
        // the last turn — report it as full instead of a stale used%.
        const reset = window.resetsAt ? Date.parse(window.resetsAt) : Number.NaN;
        const rolledOver = !Number.isNaN(reset) && reset < Date.now();
        return (
          <UsageMeter
            key={window.label}
            label={window.label}
            usedPercent={rolledOver ? 0 : window.usedPercent}
            title={[
              rolledOver
                ? `${window.label} window reset ${formatTime(window.resetsAt!)} — full again.`
                : `${window.label} window: ${Math.round(window.usedPercent)}% used${window.resetsAt ? ` · resets ${formatTime(window.resetsAt)}` : ""}`,
              limits?.updatedAt ? `As of the last turn (${formatTime(limits.updatedAt)})` : null,
            ]
              .filter(Boolean)
              .join("\n")}
          />
        );
      })}
    </span>
  );
}

// A VS-Code-status-bar-style problem indicator that lives next to the ctx meter: a compact
// ✕ (errors) / ! (conflicts) count that opens a popover with the details on click and vanishes
// entirely once nothing is wrong. Replaces the full-width error/conflict banners.
function ProblemIndicator({ error, conflicts }: { error?: string | null; conflicts: RuntimeConflict[] }) {
  const [open, setOpen] = useState(false);
  const errorCount = error ? 1 : 0;
  const total = errorCount + conflicts.length;
  // Auto-dismiss the popover the moment the underlying problems clear ("해결되면 사라지도록").
  useEffect(() => {
    if (total === 0) setOpen(false);
  }, [total]);
  if (total === 0) return null;
  return (
    <span className="problem-indicator">
      {open ? (
        <div className="problem-popover" role="dialog" aria-label="Issues">
          {error ? (
            <div className="problem-item error">
              <strong>Connection</strong>
              <p>{error}</p>
            </div>
          ) : null}
          {conflicts.map((conflict) => (
            <div className="problem-item warn" key={conflict.id}>
              <strong>{conflict.title}</strong>
              <p>{conflict.detail}</p>
              {conflict.resolution ? (
                <p className="problem-solution">
                  <b>Solution</b> — {conflict.resolution}
                </p>
              ) : null}
            </div>
          ))}
        </div>
      ) : null}
      <button
        type="button"
        className="problem-chip"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
        title={`${total} issue${total === 1 ? "" : "s"} — click for details`}
      >
        {errorCount > 0 ? <span className="problem-count error">✕ {errorCount}</span> : null}
        {conflicts.length > 0 ? <span className="problem-count warn">! {conflicts.length}</span> : null}
      </button>
    </span>
  );
}

// Past this length the halt message renders truncated with a 더 보기/접기 toggle, so a long
// recovery explanation never pushes the transcript below the fold.
const HALT_MESSAGE_PREVIEW = 160;

/** Exported for the pure-logic test harness (no DOM env in vitest). */
export const truncateHaltMessage = (message: string, limit = HALT_MESSAGE_PREVIEW): string =>
  message.length <= limit ? message : `${message.slice(0, limit - 1).trimEnd()}…`;

/** Inline error under the 재개 button when POST /resume fails (exported for the tests). */
export const HALT_RESUME_FAILED_MESSAGE =
  "재개 요청이 실패했습니다 — 연결을 확인하고 다시 시도해 주세요.";

/**
 * The 재개 click flow, exported for the pure-logic tests: busy-guarded (no double-fire even if
 * the disabled attribute is bypassed), clears the previous inline error before retrying, and
 * reports the failure message when the POST fails. The banner stays mounted throughout — the
 * halt itself is only cleared by the server-confirmed refetch (no optimistic clear), which is
 * what keeps this instance's busy label and failure state visible.
 */
export async function runHaltResume(
  busy: boolean,
  onResume: () => Promise<boolean>,
  setFailed: (message: string | null) => void,
): Promise<void> {
  if (busy) return;
  setFailed(null);
  const ok = await onResume();
  if (!ok) setFailed(HALT_RESUME_FAILED_MESSAGE);
}

// The halted-for-recovery callout: sibling of .blocked-callout in the stream, amber like the
// paused status (halt is recoverable — red is reserved for blocked/broken). Shows the reason
// (truncated with expand), the halting job id, and the 재개 button that POSTs /resume.
function HaltBanner({ halt, busy, onResume }: { halt: SessionHalt; busy: boolean; onResume(): Promise<boolean> }) {
  const [expanded, setExpanded] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  const long = halt.message.length > HALT_MESSAGE_PREVIEW;
  return (
    <div className="halt-callout" role="alert">
      <strong>
        <Icon name="warning" /> 복구 필요로 정지됨
      </strong>
      <p>
        {expanded || !long ? halt.message : truncateHaltMessage(halt.message)}
        {long ? (
          <button
            type="button"
            className="halt-expand"
            aria-expanded={expanded}
            onClick={() => setExpanded((value) => !value)}
          >
            {expanded ? "접기" : "더 보기"}
          </button>
        ) : null}
      </p>
      <div className="halt-meta">
        <span className="halt-job" title={`정지시킨 작업: ${halt.jobId}`}>
          Job {halt.jobId}
        </span>
        <time dateTime={halt.at}>{formatTime(halt.at)}</time>
        <button
          type="button"
          className="halt-resume"
          disabled={busy}
          title="정지 상태를 해제하고 세션을 다시 실행합니다"
          onClick={() => void runHaltResume(busy, onResume, setFailed)}
        >
          {busy ? "재개 중…" : "재개"}
        </button>
      </div>
      {failed ? (
        <p className="halt-error" role="alert">
          {failed}
        </p>
      ) : null}
    </div>
  );
}

const shortFile = (path: string) => path.split(/[\\/]/).pop() ?? path;

export function ChatPane({ session, conflicts, models, limits, grasshopperDocs, busyActions, error, actionErrors, currentSelection, onModel, onPinModel, onRename, onTarget, onSend, onCaptureSelection, onResume, onResumeHalt, onDelete, onStopEdit, onFocus, onFocusCanvas, onSelectAlt, onAnswerGoal, onAnswerApproval, onDismissApproval, onDismissGoal, onAnswerAsk, onDismissAsk }: ChatPaneProps) {
  // Draft state is SEEDED from the per-session store and written back on every change. This pane
  // is remounted by `key={session.id}` on every session switch (deliberately — its unmount
  // restores an isolated Rhino document), which used to take the half-written message, the staged
  // attachments and the pinned selection with it.
  const draftSessionId = session?.id ?? "none";
  const seed = readDraft(draftSessionId);
  const [draft, setDraftText] = useState(seed.text);
  const setDraft = useCallback(
    (value: string | ((current: string) => string)) => {
      setDraftText((current) => {
        const next = typeof value === "function" ? value(current) : value;
        writeDraft(draftSessionId, { text: next });
        return next;
      });
    },
    [draftSessionId],
  );
  // Inline session rename: the title becomes a text field on click, commits on Enter/blur.
  const [editingTitle, setEditingTitle] = useState(false);
  const [titleDraft, setTitleDraft] = useState("");
  // Which proposed alternative the user last asked to see, so the chips show what is on
  // screen right now (the preview itself lives wherever the owner renders it).
  const [activeAlt, setActiveAlt] = useState<string | null>(null);
  // True while a focus chip has left the document isolated/locked — drives the header's
  // "Restore view" button only. Each chip surface now owns its OWN isolation via an owner token
  // (useFocusTarget): its unmount cleanup restores what it owns and the server refuses a stale
  // token, so the pane-level duplicate cleanup that used to live here could pop another chip's
  // isolation and is gone. The header button stays tokenless: the user's explicit global restore.
  const [focusIsolating, setFocusIsolating] = useState(false);
  const [pending, setPendingState] = useState<PendingAttachment[]>(seed.attachments);
  const setPending = useCallback(
    (value: PendingAttachment[] | ((current: PendingAttachment[]) => PendingAttachment[])) => {
      setPendingState((current) => {
        const next = typeof value === "function" ? value(current) : value;
        writeDraft(draftSessionId, { attachments: next });
        return next;
      });
    },
    [draftSessionId],
  );
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const [dragging, setDragging] = useState(false);
  const [effortOpen, setEffortOpen] = useState(false);
  // Pinned selections, ONE PER DOMAIN. They were a single PinnedSelection slot, so a capture of
  // one domain silently replaced the other and unpinning cleared both — even though the wire
  // contract has always carried them as separate fields.
  const [pinnedRhino, setPinnedRhinoState] = useState<string[] | null>(seed.pinnedRhino);
  const [pinnedGh, setPinnedGhState] = useState<GrasshopperObjectRef[] | null>(seed.pinnedGh);
  const setPinnedRhino = useCallback(
    (value: string[] | null) => {
      setPinnedRhinoState(value);
      writeDraft(draftSessionId, { pinnedRhino: value });
    },
    [draftSessionId],
  );
  const setPinnedGh = useCallback(
    (value: GrasshopperObjectRef[] | null) => {
      setPinnedGhState(value);
      writeDraft(draftSessionId, { pinnedGh: value });
    },
    [draftSessionId],
  );
  // The docKey the GH pin was captured from, kept alongside pinnedGh so it reveals/sends against its
  // origin definition rather than whichever document the session is bound to now.
  const [pinnedGhDocId, setPinnedGhDocIdState] = useState<string | null>(seed.pinnedGhDocId);
  const setPinnedGhDocId = useCallback(
    (value: string | null) => {
      setPinnedGhDocIdState(value);
      writeDraft(draftSessionId, { pinnedGhDocId: value });
    },
    [draftSessionId],
  );
  const [pinning, setPinning] = useState(false);
  // The goal shelf's open/closed state is a reading preference, not session data: remembered
  // across sessions and reloads like the canvas collapse and the theme.
  const [goalShelfOpen, setGoalShelfOpen] = useState(() => {
    try {
      return localStorage.getItem("gptino.goalShelfOpen") === "1";
    } catch {
      return false;
    }
  });
  const handleGoalShelfToggle = (event: SyntheticEvent<HTMLDetailsElement>) => {
    const open = event.currentTarget.open;
    setGoalShelfOpen(open);
    try {
      localStorage.setItem("gptino.goalShelfOpen", open ? "1" : "0");
    } catch {
      // The toggle still works for this run.
    }
  };

  // "Is it running?" answered from the SESSION, not from the goal card's lifecycle field.
  const sessionRunning =
    session != null &&
    (session.status === "working" ||
      session.status === "queued" ||
      session.status === "verifying" ||
      session.status === "drafting");

  // Every canvas-side action this pane triggers is scoped to the session's own definition.
  // Without it the host resolves to the first-registered document, which is only the right one
  // by accident — with two .gh files open a chip selected nothing in the intended definition and
  // wiped the other one's selection instead.
  const canvasDocId = session?.boundGrasshopperDocId ?? null;
  const focusCanvasInSession = useCallback(
    (objectIds: string[]) =>
      onFocusCanvas
        ? onFocusCanvas(objectIds, canvasDocId)
        : Promise.reject(new Error("Canvas focus is not available.")),
    [onFocusCanvas, canvasDocId],
  );

  const liveRhinoCount = currentSelection?.rhinoObjectCount ?? 0;
  const liveGhCount = currentSelection?.grasshopperObjectCount ?? 0;

  // One domain at a time: capture the FULL current selection (the SSE counts are capped) and keep
  // only the half this chip owns, leaving the other pin exactly as it was.
  const togglePin = async (domain: "rhino" | "gh") => {
    if (pinning) return;
    const alreadyPinned = domain === "rhino" ? pinnedRhino !== null : pinnedGh !== null;
    if (alreadyPinned) {
      if (domain === "rhino") {
        setPinnedRhino(null);
      } else {
        setPinnedGh(null);
        setPinnedGhDocId(null);
      }
      return;
    }
    if (!onCaptureSelection) return;
    setPinning(true);
    try {
      const captured = await onCaptureSelection();
      if (domain === "rhino") {
        const rhino = captured.rhinoObjectIds ?? [];
        // Nothing selected at capture time (the live hint went stale between render and click).
        if (rhino.length > 0) setPinnedRhino(rhino);
      } else {
        const gh = captured.grasshopperObjects ?? [];
        if (gh.length > 0) {
          setPinnedGh(gh);
          // Remember which definition this selection came from — the pin resolves against it later.
          setPinnedGhDocId(captured.docId ?? null);
        }
      }
    } finally {
      setPinning(false);
    }
  };

  // Re-confirm what a pin holds: select+zoom the Rhino objects, or frame the GH components.
  const revealPinnedRhino = () => {
    if (pinnedRhino && pinnedRhino.length > 0 && onFocus) void onFocus(pinnedRhino, "select");
  };
  const revealPinnedGh = () => {
    if (pinnedGh && pinnedGh.length > 0 && onFocusCanvas) {
      // Reveal in the definition the pin came from, falling back to the session's doc for pins
      // captured before docId was tracked (or on a legacy server that does not send it).
      void onFocusCanvas(pinnedGh.map((item) => item.id), pinnedGhDocId ?? canvasDocId);
    }
  };

  // The slider offers the reasoning-effort levels advertised by the chosen model (pinned, else the
  // catalog default), sorted ascending. Falls back to the full ladder before the catalog loads.
  const catalogModel =
    (session && models.find((model) => model.model === session.pinnedModel)) ??
    models.find((model) => model.isDefault) ??
    models[0];
  const effortLevels: ModelProfile[] = ((catalogModel?.reasoningEfforts?.length
    ? catalogModel.reasoningEfforts
    : EFFORT_ORDER) as string[])
    .filter((level): level is ModelProfile => EFFORT_ORDER.includes(level as ModelProfile))
    .sort((a, b) => effortRank(a) - effortRank(b));
  const streamRef = useRef<HTMLDivElement>(null);
  const effortRef = useRef<HTMLDivElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const pasteCounter = useRef(0);
  const submitGate = useRef(false);

  // The card travels as raw JSON so the wire shape has exactly one owner; a malformed card is
  // dropped rather than crashing the stream.
  const goalCard = useMemo(() => {
    if (!session?.goalCard) return null;
    try {
      return JSON.parse(session.goalCard) as GoalCardData;
    } catch {
      return null;
    }
  }, [session?.goalCard]);

  const approvalCard = useMemo(() => {
    if (!session?.approvalCard) return null;
    try {
      return JSON.parse(session.approvalCard) as ApprovalCardData;
    } catch {
      return null;
    }
  }, [session?.approvalCard]);

  const askCard = useMemo(() => {
    if (!session?.askCard) return null;
    try {
      return JSON.parse(session.askCard) as AskCardData;
    } catch {
      return null;
    }
  }, [session?.askCard]);

  const sessionConflicts = useMemo(
    () => (session ? conflicts.filter((conflict) => conflict.sessionIds.includes(session.id)) : []),
    [conflicts, session],
  );

  // Which folded turn logs the user has opened (assistant message id, or `orphan-<at>`), plus the
  // live log's own toggle. Keyed by session via App's key={session.id}, so it resets on switch.
  const [openLogs, setOpenLogs] = useState<Set<string>>(() => new Set());
  const [liveLogOpen, setLiveLogOpen] = useState(false);
  const toggleLog = (key: string) =>
    setOpenLogs((current) => {
      const next = new Set(current);
      next.has(key) ? next.delete(key) : next.add(key);
      return next;
    });

  // Group the time-sorted stream into turns. Activities accumulate into a buffer that flushes onto
  // the next assistant reply (its folded log); a user/system message flushes the buffer as an
  // orphan log block first. The trailing buffer is the LIVE log while working, else a final orphan.
  const working = session?.status === "drafting" || session?.status === "working";
  const { blocks, liveActivities } = useMemo<{ blocks: StreamBlock[]; liveActivities: SessionActivity[] }>(() => {
    if (!session) return { blocks: [], liveActivities: [] };
    const items: StreamItem[] = session.messages.map((message) => ({
      type: "message",
      at: Date.parse(message.createdAt) || 0,
      message,
    }));
    for (const activity of session.activity ?? []) {
      items.push({ type: "activity", at: Date.parse(activity.at) || 0, activity });
    }
    items.sort((a, b) => a.at - b.at);

    const out: StreamBlock[] = [];
    let buffer: SessionActivity[] = [];
    const flushOrphan = () => {
      if (buffer.length === 0) return;
      const at = Date.parse(buffer[buffer.length - 1].at) || 0;
      out.push({ type: "log", key: `orphan-${at}-${buffer.length}`, at, activities: buffer });
      buffer = [];
    };
    for (const item of items) {
      if (item.type === "activity") {
        buffer.push(item.activity);
        continue;
      }
      if (item.message.role === "assistant") {
        out.push({ type: "message", key: `m-${item.message.id}`, at: item.at, message: item.message, log: buffer });
        buffer = [];
      } else {
        flushOrphan();
        out.push({ type: "message", key: `m-${item.message.id}`, at: item.at, message: item.message, log: [] });
      }
    }
    const isWorking = session.status === "drafting" || session.status === "working";
    if (!isWorking) {
      flushOrphan();
      return { blocks: out, liveActivities: [] };
    }
    return { blocks: out, liveActivities: buffer };
  }, [session]);

  // A single work-log row, reused by folded turn logs and the live window.
  const renderActivity = (activity: SessionActivity, key: string) => (
    <div
      className={`activity-row ${activity.ok ? "" : "failed"}`}
      key={key}
      title={`${activity.kind}${activity.durationMs > 0 ? ` · ${activity.durationMs}ms` : ""}`}
    >
      <span className="activity-dot" />
      {/* Ellipsized summaries stay recoverable: the text carries its own tooltip. */}
      <span className="activity-text" title={activity.summary}>{activity.summary}</span>
      <time dateTime={activity.at}>{formatTime(activity.at)}</time>
    </div>
  );

  // Switching sessions jumps straight to the newest message (an animated scroll
  // through the whole backlog is disorienting); new items in the same session
  // still glide down smoothly. The ref bookkeeping runs before the element
  // guard so empty-state renders (no stream div) still record the id — without
  // that, deleting and restoring the same session would smooth-scroll.
  const prevSessionId = useRef<string | undefined>(undefined);
  useEffect(() => {
    const switched = prevSessionId.current !== session?.id;
    prevSessionId.current = session?.id;
    const el = streamRef.current;
    if (!el) return;
    el.scrollTo({ top: el.scrollHeight, behavior: switched ? "auto" : "smooth" });
  }, [session?.id, blocks.length, liveActivities.length]);

  // Like a native <select>, the effort popover cannot outlive its session: it
  // closes whenever the selected session changes or disappears.
  useEffect(() => {
    setEffortOpen(false);
    // Pins are NOT cleared here any more. They used to be, to stop a pin captured in one session
    // being sent from another — but the pane is keyed by session id, so each session now keeps its
    // own draft (text, attachments and pins) in the draft store and none of them can cross over.
    // The safety property is preserved; the data loss it caused is not.
  }, [session?.id]);

  // The effort popover closes like the model dropdown: Esc or a press anywhere
  // outside the control dismisses it. Capture phase, because canvas nodes call
  // stopPropagation() on pointerdown — a bubble listener would never see those.
  useEffect(() => {
    if (!effortOpen) return;
    const handlePointerDown = (event: PointerEvent) => {
      const anchor = effortRef.current;
      // An unmounted anchor (empty state) can never contain the press — close.
      if (!anchor || (event.target instanceof Node && !anchor.contains(event.target))) {
        setEffortOpen(false);
      }
    };
    const handleKeyDown = (event: globalThis.KeyboardEvent) => {
      if (event.key === "Escape") setEffortOpen(false);
    };
    document.addEventListener("pointerdown", handlePointerDown, true);
    document.addEventListener("keydown", handleKeyDown);
    return () => {
      document.removeEventListener("pointerdown", handlePointerDown, true);
      document.removeEventListener("keydown", handleKeyDown);
    };
  }, [effortOpen]);

  if (!session) {
    return (
      <section className="chat-pane empty-state">
        <div className="empty-mark">G</div>
        <h2>Select a session</h2>
        <p>Choose a workstream to view its context and send instructions.</p>
      </section>
    );
  }

  const sending = busyActions.has(`message:${session.id}`);

  const addFiles = (incoming: File[]) => {
    if (incoming.length === 0) return;
    const next = [...pending];
    let error: string | null = null;
    for (const file of incoming) {
      const mediaType = resolveMediaType(file);
      if (!mediaType) {
        error = `"${file.name}" is not a supported type (images, text, Markdown, JSON, CSV, PDF).`;
        continue;
      }
      if (file.size === 0) {
        error = `"${file.name}" is empty.`;
        continue;
      }
      next.push({ id: crypto.randomUUID(), file, fileName: file.name, mediaType, size: file.size });
    }
    setPending(next);
    setAttachmentError(error);
  };

  const removeAttachment = (id: string) => {
    setPending((current) => current.filter((item) => item.id !== id));
    setAttachmentError(null);
  };

  const handleFileInput = (event: ChangeEvent<HTMLInputElement>) => {
    addFiles(Array.from(event.target.files ?? []));
    event.target.value = "";
  };

  const handlePaste = (event: ClipboardEvent<HTMLTextAreaElement>) => {
    const images = Array.from(event.clipboardData?.items ?? []).filter(
      (item) => item.kind === "file" && item.type.startsWith("image/"),
    );
    if (images.length === 0 || sending) return;
    event.preventDefault();
    const files: File[] = [];
    for (const item of images) {
      const file = item.getAsFile();
      if (!file) continue;
      pasteCounter.current += 1;
      files.push(new File([file], `pasted-${pasteCounter.current}.png`, { type: file.type || "image/png" }));
    }
    addFiles(files);
  };

  const handleDragOver = (event: DragEvent<HTMLDivElement>) => {
    if (sending || session.paused) return;
    event.preventDefault();
    setDragging(true);
  };

  const handleDrop = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    setDragging(false);
    if (sending || session.paused) return;
    addFiles(Array.from(event.dataTransfer?.files ?? []));
  };

  const submit = async () => {
    const content = draft.trim();
    if ((!content && pending.length === 0) || sending || submitGate.current) return;
    submitGate.current = true;
    try {
      // Snapshot what this submit sends: attachments pasted mid-encode stay
      // staged for the next send, and a failed send restores the draft.
      const toSend = [...pending];
      let attachments: MessageAttachment[] | undefined;
      if (toSend.length > 0) {
        try {
          // Encode one at a time, not Promise.all: a batch of large images otherwise holds every
          // base64 string (≈2.7× each in memory) at once, which could stall the WebView on big sends.
          const encoded: MessageAttachment[] = [];
          for (const item of toSend) {
            encoded.push(await encodeAttachment(item));
          }
          attachments = encoded;
        } catch (encodeError) {
          setAttachmentError(encodeError instanceof Error ? encodeError.message : "Could not read an attachment.");
          return;
        }
      }
      const savedDraft = draft;
      // Recombined only at send time: the two chips are independent state, the wire contract has
      // always been one object with two optional halves.
      const pinnedToSend: PinnedSelection | undefined =
        pinnedRhino || pinnedGh
          ? {
              ...(pinnedRhino && pinnedRhino.length > 0 ? { rhinoObjectIds: pinnedRhino } : {}),
              ...(pinnedGh && pinnedGh.length > 0
                ? { grasshopperObjects: pinnedGh, ...(pinnedGhDocId ? { docId: pinnedGhDocId } : {}) }
                : {}),
            }
          : undefined;
      setDraft("");
      setAttachmentError(null);
      const ok = await onSend(content, attachments, pinnedToSend);
      if (ok === false) {
        setDraft(savedDraft);
        return;
      }
      setPending((current) => current.filter((item) => !toSend.some((sent) => sent.id === item.id)));
      // Pinned selection is per-message, like attachments: clear it once the message is sent.
      setPinnedRhino(null);
      setPinnedGh(null);
      setPinnedGhDocId(null);
    } finally {
      submitGate.current = false;
    }
  };

  const handleComposerKey = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      void submit();
    }
  };

  return (
    <section className="chat-pane" aria-label={`${session.title} chat`}>
      <header className="chat-header">
        <div className="chat-title-block">
          <div className="chat-title-row">
            {editingTitle ? (
              <input
                className="chat-title-input"
                value={titleDraft}
                autoFocus
                aria-label="Session name"
                onChange={(event) => setTitleDraft(event.target.value)}
                onFocus={(event) => event.target.select()}
                onKeyDown={(event) => {
                  if (event.key === "Enter") {
                    event.preventDefault();
                    const next = titleDraft.trim();
                    if (next && next !== session.title) onRename(next);
                    setEditingTitle(false);
                  } else if (event.key === "Escape") {
                    setEditingTitle(false);
                  }
                }}
                onBlur={() => {
                  const next = titleDraft.trim();
                  if (next && next !== session.title) onRename(next);
                  setEditingTitle(false);
                }}
              />
            ) : (
              <h2
                className="chat-title"
                title="Click to rename"
                onClick={() => {
                  setTitleDraft(session.title);
                  setEditingTitle(true);
                }}
              >
                {session.title}
              </h2>
            )}
            <StatusBadge status={session.status} />
            {session.halt ? (
              <span
                className="halt-badge"
                title={`Job ${session.halt.jobId} — ${session.halt.message}`}
              >
                복구 필요로 정지됨
              </span>
            ) : null}
          </div>
        </div>
        {session.paused ? (
          <button
            type="button"
            className="chat-resume"
            title="Resume this paused session"
            disabled={busyActions.has(`pause:${session.id}`)}
            onClick={onResume}
          >
            Resume
          </button>
        ) : null}
        {focusIsolating ? (
          <button
            type="button"
            className="chat-resume"
            title="포커스 칩이 숨긴/잠근 객체를 전부 복구"
            onClick={() => {
              void onFocus?.([], "restore").then(() => setFocusIsolating(false));
            }}
          >
            Restore view
          </button>
        ) : null}
        <button
          type="button"
          className="chat-delete"
          title="Delete session (recoverable from Deleted)"
          disabled={busyActions.has(`delete:${session.id}`)}
          onClick={async () => {
            if (window.confirm(`Delete session "${session.title}"? You can restore it from Deleted.`)) {
              // Clear the draft only AFTER the delete succeeds. Clearing first lost the half-written
              // message (text, attachments, pins) whenever the delete — or its refetch — failed and
              // the session came back. A deleted session's orphaned draft is cleaned on next write.
              const ok = await onDelete();
              if (ok !== false) {
                clearDraft(session.id);
              }
            }
          }}
        >
          Delete
        </button>
      </header>

      <div className="chat-stream" ref={streamRef} aria-live="polite">
        {session.halt ? (
          <HaltBanner
            halt={session.halt}
            busy={busyActions.has(`halt:${session.id}`)}
            onResume={onResumeHalt}
          />
        ) : null}
        {session.status === "blocked" && sessionConflicts.length > 0 ? (
          <div className="blocked-callout" role="alert">
            {sessionConflicts.map((conflict) => (
              <div key={conflict.id}>
                <strong>
                  <Icon name="warning" /> {conflict.title}
                </strong>
                <p>{conflict.detail}</p>
                {conflict.resolution ? (
                  <p className="conflict-solution">
                    <b>Solution</b> — {conflict.resolution}
                  </p>
                ) : null}
              </div>
            ))}
          </div>
        ) : null}
        {blocks.map((block) =>
          block.type === "message" ? (
            <article
              className={`message message-${block.message.role} ${block.message.pending ? "pending" : ""}`}
              key={block.key}
            >
              <div className="message-author">
                <span>
                  {block.message.role === "assistant" ? "GPTino" : block.message.role === "system" ? "System" : "You"}
                </span>
                <time dateTime={block.message.createdAt}>{formatTime(block.message.createdAt)}</time>
              </div>
              <p>
                {parseMessageSegments(block.message.content).map((segment, index) => {
                  if (segment.kind === "text") return <span key={index}>{segment.text}</span>;
                  if (segment.kind === "alt") {
                    // A chip renders when SOMETHING can respond to it: id-carrying alts drive
                    // the viewport through onFocus; opaque alts need an external preview owner.
                    const altHasViewport = segment.objectIds !== undefined && onFocus !== undefined;
                    return onSelectAlt || altHasViewport ? (
                      <AltChip
                        key={index}
                        altId={segment.altId}
                        label={segment.label}
                        active={activeAlt === segment.altId}
                        objectIds={segment.objectIds}
                        onFocus={onFocus}
                        onIsolated={setFocusIsolating}
                        onSelect={(altId) => {
                          setActiveAlt(altId);
                          onSelectAlt?.(altId);
                        }}
                      />
                    ) : (
                      <span key={index}>{segment.label}</span>
                    );
                  }
                  if (segment.kind === "ghfocus") {
                    return onFocusCanvas ? (
                      <GhFocusChip
                        key={index}
                        objectIds={segment.objectIds}
                        label={segment.label}
                        docId={canvasDocId}
                        onFocusCanvas={onFocusCanvas}
                      />
                    ) : (
                      // No canvas focus wired in: degrade to the label, like focus without a viewport.
                      <span key={index}>{segment.label}</span>
                    );
                  }
                  return onFocus ? (
                    <FocusChip
                      key={index}
                      objectIds={segment.objectIds}
                      label={segment.label}
                      onFocus={onFocus}
                      onIsolated={setFocusIsolating}
                    />
                  ) : (
                    // No viewport in this context: the marker degrades to its label.
                    <span key={index}>{segment.label}</span>
                  );
                })}
              </p>
              {block.message.pending ? <span className="pending-label">Sending…</span> : null}
              {/* The turn's work log, folded away under the reply it produced. Click to expand. */}
              {block.log.length > 0 ? (
                <div className="turn-log">
                  <button
                    type="button"
                    className="turn-log-toggle"
                    aria-expanded={openLogs.has(block.key)}
                    onClick={() => toggleLog(block.key)}
                    title={openLogs.has(block.key) ? "작업 로그 접기" : "이 답변까지의 작업 로그 펼치기"}
                  >
                    <Icon name="chevron" className={`turn-log-caret ${openLogs.has(block.key) ? "open" : ""}`} width={12} height={12} />
                    {block.log.length} step{block.log.length === 1 ? "" : "s"}
                  </button>
                  {openLogs.has(block.key) ? (
                    <div className="turn-log-list">
                      {block.log.map((activity, index) => renderActivity(activity, `${block.key}-a${index}`))}
                    </div>
                  ) : null}
                </div>
              ) : null}
            </article>
          ) : (
            // An orphan log: work that produced no assistant reply before the next user/system turn.
            <div className="turn-log orphan" key={block.key}>
              <button
                type="button"
                className="turn-log-toggle"
                aria-expanded={openLogs.has(block.key)}
                onClick={() => toggleLog(block.key)}
                title={openLogs.has(block.key) ? "작업 로그 접기" : "작업 로그 펼치기"}
              >
                <Icon name="chevron" className={`turn-log-caret ${openLogs.has(block.key) ? "open" : ""}`} width={12} height={12} />
                {block.activities.length} step{block.activities.length === 1 ? "" : "s"}
              </button>
              {openLogs.has(block.key) ? (
                <div className="turn-log-list">
                  {block.activities.map((activity, index) => renderActivity(activity, `${block.key}-a${index}`))}
                </div>
              ) : null}
            </div>
          ),
        )}
        {goalCard && goalCard.status === "proposing" ? (
          // A PROPOSED goal is the live question, so it stays in the transcript where the user is
          // already looking. Once answered it becomes standing context and moves to the collapsed
          // shelf above the composer: it is long, it never changes, and leaving it inline pushed
          // the actual conversation off screen. Wrapped so a malformed card cannot blank the panel.
          <ErrorBoundary fallback={<div className="render-error" role="alert">목표 카드를 표시할 수 없습니다.</div>}>
            <GoalCard
              card={goalCard}
              busy={busyActions.has(`goal:${session.id}`)}
              failure={actionErrors?.[`goal:${session.id}`]}
              onAnswer={onAnswerGoal}
              onFocus={onFocus}
            />
          </ErrorBoundary>
        ) : null}
        {approvalCard ? (
          // Keyed by card identity: the session's card slot is REPLACED in place, so without a
          // remount the tick state (and the server's pre-checked defaults, which a lazy
          // initializer only reads once) would leak from the previous proposal onto the new one.
          <ErrorBoundary fallback={<div className="render-error" role="alert">승인 카드를 표시할 수 없습니다.</div>}>
            <ApprovalCard
              key={approvalCard.proposedAt ?? approvalCard.summary}
              card={approvalCard}
              busy={busyActions.has(`approval:${session.id}`)}
              failure={actionErrors?.[`approval:${session.id}`]}
              hasGrasshopper={(grasshopperDocs?.length ?? 0) > 0}
              onAnswer={onAnswerApproval}
              onDismiss={onDismissApproval}
              onFocus={onFocus}
              onFocusCanvas={onFocusCanvas ? focusCanvasInSession : undefined}
            />
          </ErrorBoundary>
        ) : null}
        {askCard && onAnswerAsk ? (
          <ErrorBoundary fallback={<div className="render-error" role="alert">질문 카드를 표시할 수 없습니다.</div>}>
            <AskCard
              key={`ask-${askCard.question}`}
              card={askCard}
              busy={busyActions.has(`ask:${session.id}`)}
              failure={actionErrors?.[`ask:${session.id}`]}
              onAnswer={onAnswerAsk}
              onDismiss={onDismissAsk}
            />
          </ErrorBoundary>
        ) : null}
        {/* The live work log: only the last few steps stay on screen; older ones fold behind a
            toggle so an active turn never floods the view. Folds into the reply once it lands. */}
        {liveActivities.length > 0 ? (
          <div className="live-log">
            {liveActivities.length > LIVE_LOG_VISIBLE ? (
              <button
                type="button"
                className="turn-log-toggle"
                aria-expanded={liveLogOpen}
                onClick={() => setLiveLogOpen((open) => !open)}
              >
                <Icon name="chevron" className={`turn-log-caret ${liveLogOpen ? "open" : ""}`} width={12} height={12} />
                {liveLogOpen
                  ? "Hide earlier steps"
                  : `+${liveActivities.length - LIVE_LOG_VISIBLE} earlier step${liveActivities.length - LIVE_LOG_VISIBLE === 1 ? "" : "s"}`}
              </button>
            ) : null}
            {(liveLogOpen ? liveActivities : liveActivities.slice(-LIVE_LOG_VISIBLE)).map((activity, index) =>
              renderActivity(activity, `live-${activity.at}-${index}`),
            )}
          </div>
        ) : null}
        {working ? (
          <div className="thinking-row" aria-label="GPTino is working">
            <span />
            <span />
            <span />
            <em>{session.status === "drafting" ? "Drafting a ChangeSet" : "Working"}</em>
            <button
              type="button"
              className="stop-edit-button"
              title="Stop the current work and pull your message back to edit it"
              onClick={async () => {
                const content = await onStopEdit();
                if (typeof content === "string") {
                  setDraft((current) => (current.trim().length > 0 ? current : content));
                }
              }}
            >
              Stop &amp; edit
            </button>
          </div>
        ) : null}
      </div>

      <div className="composer-wrap">
        {/* Standing goal: the FIRST row of the composer block, above the effort/model controls.
            It is the frame the whole conversation is held to, so it reads before the knobs —
            and it is long, unchanging reference material, so it stays collapsed and out of
            the transcript. The summary line always shows whether the session is actually
            running, which is the question the card itself could never answer. */}
        {goalCard && goalCard.status !== "proposing" ? (
          <details className="goal-shelf" open={goalShelfOpen} onToggle={handleGoalShelfToggle}>
            <summary className="goal-shelf-summary">
              <span className="goal-shelf-label">GOAL</span>
              <span className="goal-shelf-objective" title={goalCard.objective}>
                {goalCard.objective}
              </span>
              <span className={`goal-shelf-state${sessionRunning ? " running" : ""}`}>
                {goalCard.status === "scored"
                  ? "채점됨"
                  : goalCard.status === "rejected"
                    ? "거절됨"
                    : sessionRunning
                      ? "진행 중"
                      : "대기 중"}
              </span>
            </summary>
            <ErrorBoundary fallback={<div className="render-error" role="alert">목표 카드를 표시할 수 없습니다.</div>}>
              <GoalCard
                card={goalCard}
                busy={busyActions.has(`goal:${session.id}`)}
                failure={actionErrors?.[`goal:${session.id}`]}
                running={sessionRunning}
                onAnswer={onAnswerGoal}
                onDismiss={onDismissGoal}
                onFocus={onFocus}
              />
            </ErrorBoundary>
          </details>
        ) : null}

        <div className="control-strip">
          <div className="quality-control effort-control" ref={effortRef}>
            <button
              type="button"
              className="effort-toggle"
              onClick={() => setEffortOpen((open) => !open)}
              disabled={busyActions.has(`model:${session.id}`)}
              aria-expanded={effortOpen}
              title="Reasoning effort for this session (used directly; clamped to the model's range)."
            >
              <span className="effort-caption">Effort</span>
              <span className="effort-value">{EFFORT_LABELS[session.modelProfile] ?? session.modelProfile}</span>
            </button>
            {effortOpen ? (
              <div className="effort-slider">
                <input
                  type="range"
                  min={0}
                  max={Math.max(0, effortLevels.length - 1)}
                  step={1}
                  value={Math.max(0, effortLevels.indexOf(session.modelProfile))}
                  onChange={(event) => onModel(effortLevels[Number(event.target.value)] ?? session.modelProfile)}
                  disabled={busyActions.has(`model:${session.id}`)}
                  aria-label="Reasoning effort"
                />
                <div className="effort-ticks">
                  {effortLevels.map((level) => (
                    <span key={level} className={level === session.modelProfile ? "active" : ""}>
                      {EFFORT_LABELS[level] ?? level}
                    </span>
                  ))}
                </div>
              </div>
            ) : null}
          </div>
          {models.length > 0 ? (
            <div className="quality-control">
              <label htmlFor="model-pin">Model</label>
              <select
                id="model-pin"
                value={session.pinnedModel ?? ""}
                onChange={(event) => onPinModel(event.target.value || null)}
                disabled={busyActions.has(`model:${session.id}`)}
                title="Pin a Codex model for this session, or Auto to use the catalog default. Effort is set separately."
              >
                <option value="">Auto (default)</option>
                {models.map((model) => (
                  <option value={model.model} key={model.id} title={model.description}>
                    {model.displayName || model.model}
                  </option>
                ))}
              </select>
            </div>
          ) : null}
          {/* The Goal toggle is gone: goals are no longer a switch the user flips but a card the
              agent proposes and the user answers (rendered in the stream above). */}
          {(grasshopperDocs && grasshopperDocs.length > 1) || session.boundGrasshopperDocId != null ? (
            // Also rendered when the bound doc is no longer registered (even with 0-1 docs
            // left): the selector is the panel's only unbind path, so hiding it would strand
            // the session on a binding that fails every submit.
            <div className="quality-control">
              <label htmlFor="session-target">Target</label>
              <select
                id="session-target"
                value={session.boundGrasshopperDocId ?? ""}
                onChange={(event) => onTarget(event.target.value || null)}
                disabled={busyActions.has(`target:${session.id}`)}
                title="Bind this session's writes to one Grasshopper document. Unbound sessions must pick a document before submitting changes."
              >
                <option value="">Unbound</option>
                {(grasshopperDocs ?? []).map((doc) => (
                  <option value={doc.id} key={doc.id} title={doc.file}>
                    {shortFile(doc.file)}
                  </option>
                ))}
                {session.boundGrasshopperDocId != null &&
                !(grasshopperDocs ?? []).some((doc) => doc.id === session.boundGrasshopperDocId) ? (
                  // A disabled option carrying the stale value keeps the controlled select from
                  // rendering blank and names the broken binding; the user can switch to
                  // Unbound or a live document.
                  <option value={session.boundGrasshopperDocId} disabled>
                    Missing document ({session.boundGrasshopperDocId})
                  </option>
                ) : null}
              </select>
            </div>
          ) : null}
          <span
            className="effective-model"
            title={session.routingError ?? session.routingReason ?? "Effective model and reasoning"}
          >
            {session.effectiveModel ?? "Routing pending"}
            {session.reasoning ? ` / ${session.reasoning}` : ""}
            {session.effectiveProfile ? ` / ${session.effectiveProfile}` : ""}
          </span>
        </div>

        <div
          className={`composer ${dragging ? "dragging" : ""}`}
          onDragOver={handleDragOver}
          onDragLeave={() => setDragging(false)}
          onDrop={handleDrop}
        >
          {/* One always-present context row: what this message is about — the Rhino/GH pins AND any
              staged attachments together on one wrapping line. Always rendered (constant height) so the
              composer never grows or shrinks as selections/attachments come and go. Attachments used to
              sit on a separate row below the pins; the user asked for them in the same column. */}
          <div className="composer-rail">
            <SelectionRail
              liveRhinoCount={liveRhinoCount}
              liveGhCount={liveGhCount}
              pinnedRhinoCount={pinnedRhino?.length ?? null}
              pinnedGhCount={pinnedGh?.length ?? null}
              busy={pinning}
              disabled={sending || session.paused || !onCaptureSelection}
              onToggleRhino={() => void togglePin("rhino")}
              onToggleGh={() => void togglePin("gh")}
              onRevealRhino={revealPinnedRhino}
              onRevealGh={revealPinnedGh}
            />
            {pending.map((item) => (
              <span className="attachment-chip" key={item.id}>
                <span className="chip-name" title={item.fileName}>
                  {item.fileName}
                </span>
                <span className="chip-size">{formatBytes(item.size)}</span>
                <button
                  type="button"
                  className="chip-remove"
                  onClick={() => removeAttachment(item.id)}
                  disabled={sending}
                  aria-label={`Remove ${item.fileName}`}
                >
                  ×
                </button>
              </span>
            ))}
          </div>
          <textarea
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            onKeyDown={handleComposerKey}
            onPaste={handlePaste}
            placeholder={
              session.paused
                ? "Session is paused — resume it to continue"
                : "Describe what you want — a modeling change, a document check-up, a cleanup…"
            }
            aria-label="Message GPTino"
            rows={3}
            disabled={session.paused}
          />
          <input
            ref={fileInputRef}
            type="file"
            multiple
            hidden
            accept={ATTACHMENT_ACCEPT}
            onChange={handleFileInput}
            aria-hidden="true"
            tabIndex={-1}
          />
          <button
            type="button"
            className="attach-button"
            onClick={() => fileInputRef.current?.click()}
            disabled={sending || session.paused}
            aria-label="Attach files"
            title="Attach files — images, text, Markdown, JSON, CSV, PDF (no count or size limit). Paste or drop also works."
          >
            <Icon name="paperclip" />
          </button>
          <button
            type="button"
            className="send-button"
            onClick={() => void submit()}
            disabled={(!draft.trim() && pending.length === 0) || sending || session.paused}
            aria-label="Send instruction"
          >
            <Icon name="send" />
          </button>
        </div>
        {attachmentError ? (
          <div className="attachment-error" role="alert">
            {attachmentError}
          </div>
        ) : null}
        <div className="composer-hint">
          <div className="hint-keys">
            <span>Ctrl ↵ to send</span>
          </div>
          <span className="hint-status">
            <ProblemIndicator error={error} conflicts={sessionConflicts} />
            <UsageStatusLine usage={session.usage} limits={limits} />
          </span>
        </div>
      </div>
    </section>
  );
}
