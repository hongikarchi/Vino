import type { GrasshopperObjectRef } from "./types";

/** One staged composer attachment; the bytes stay in the File until send encodes them. */
export interface PendingAttachment {
  id: string;
  file: File;
  fileName: string;
  mediaType: string;
  size: number;
}

/** What the user has half-written for one session but not sent yet. */
export interface SessionDraft {
  text: string;
  /** Pinned Rhino object ids, or null when Rhino is not pinned. Independent of the GH pin. */
  pinnedRhino: string[] | null;
  /** Pinned Grasshopper components, or null when GH is not pinned. */
  pinnedGh: GrasshopperObjectRef[] | null;
  /** Staged files. Memory only — a File cannot be serialized to localStorage. */
  attachments: PendingAttachment[];
}

const EMPTY: SessionDraft = { text: "", pinnedRhino: null, pinnedGh: null, attachments: [] };

/**
 * Per-session composer drafts, kept OUTSIDE the React tree.
 *
 * <para>
 * ChatPane is mounted with `key={session.id}`, so switching sessions destroys and rebuilds it —
 * deliberately, because its unmount cleanup is what restores an isolated Rhino document and what
 * resets per-session log expansion. The cost was that everything typed, attached or pinned died
 * with it: come back to a session and the composer was empty. Keeping the draft in a module-level
 * map (App never unmounts) makes the draft outlive the remount without touching that contract.
 * </para>
 *
 * <para>
 * Text and pins are additionally mirrored to localStorage so a panel reload keeps them. Attachments
 * are not: they are live `File` handles. They survive a session switch, not a reload — and pretending
 * otherwise (persisting a name with no bytes) would be worse than losing them.
 * </para>
 *
 * <para>
 * The mirror is keyed per session id but the AgentHost binds to a random port, so the browser
 * origin changes on every host restart and the mirror is effectively per-run. That is a known
 * limitation of the port-0 bind, not of this store.
 * </para>
 */
const drafts = new Map<string, SessionDraft>();

const storageKey = (sessionId: string) => `gptino.draft.${sessionId}`;

interface MirroredDraft {
  text?: string;
  pinnedRhino?: string[] | null;
  pinnedGh?: GrasshopperObjectRef[] | null;
}

function readMirror(sessionId: string): MirroredDraft | null {
  try {
    const raw = localStorage.getItem(storageKey(sessionId));
    return raw ? (JSON.parse(raw) as MirroredDraft) : null;
  } catch {
    // Unavailable or corrupt storage must never keep the composer from rendering.
    return null;
  }
}

function writeMirror(sessionId: string, draft: SessionDraft): void {
  try {
    if (!draft.text && !draft.pinnedRhino && !draft.pinnedGh) {
      localStorage.removeItem(storageKey(sessionId));
      return;
    }
    localStorage.setItem(
      storageKey(sessionId),
      JSON.stringify({
        text: draft.text,
        pinnedRhino: draft.pinnedRhino,
        pinnedGh: draft.pinnedGh,
      } satisfies MirroredDraft),
    );
  } catch {
    // Quota or a locked-down WebView: the in-memory draft still works for this run.
  }
}

export function readDraft(sessionId: string): SessionDraft {
  const held = drafts.get(sessionId);
  if (held) return held;
  const mirrored = readMirror(sessionId);
  const restored: SessionDraft = mirrored
    ? {
        text: mirrored.text ?? "",
        pinnedRhino: mirrored.pinnedRhino ?? null,
        pinnedGh: mirrored.pinnedGh ?? null,
        attachments: [],
      }
    : { ...EMPTY, attachments: [] };
  drafts.set(sessionId, restored);
  return restored;
}

export function writeDraft(sessionId: string, patch: Partial<SessionDraft>): SessionDraft {
  const next: SessionDraft = { ...readDraft(sessionId), ...patch };
  drafts.set(sessionId, next);
  writeMirror(sessionId, next);
  return next;
}

/** Called after a successful send, and when a session is deleted. */
export function clearDraft(sessionId: string): void {
  drafts.delete(sessionId);
  try {
    localStorage.removeItem(storageKey(sessionId));
  } catch {
    // Nothing to do: the in-memory copy is already gone.
  }
}
