import type {
  ArchiveMessage,
  ArchiveProject,
  DataFlowDetail,
  DeletedSession,
  MessageRequest,
  ModelInfo,
  ModelProfile,
  RuntimeState,
  SessionOrderRequest,
  FocusMode,
  FocusResult,
  CanvasFocusResult,
  PinnedSelection,
  ApprovalAnswer,
  PermissionMode,
} from "../types";
import { createMockApiClient } from "./mock";

export interface VinoApiClient {
  readonly demo: boolean;
  getRuntime(): Promise<RuntimeState>;
  subscribe(
    onState: (state: RuntimeState) => void,
    onError?: (error: Error) => void,
  ): () => void;
  listModels(): Promise<ModelInfo[]>;
  createSession(name: string, grasshopperDoc?: string): Promise<void>;
  /** On-demand Rhino<->GH data-flow detail for one GH doc (omit docId when only one is open). */
  focusObjects(objectIds: string[], mode: FocusMode, zoom?: boolean, ownerToken?: string): Promise<FocusResult>;
  /** Select + frame the given Grasshopper components on the GH canvas (the [[ghfocus:…]] chip). */
  focusCanvasObjects(objectIds: string[], docId?: string | null, zoom?: boolean): Promise<CanvasFocusResult>;
  /** The complete current Rhino/GH selection, for pinning it to a message (no 32-id SSE cap). */
  getCurrentSelection(): Promise<PinnedSelection>;
  /** Prose language for Vino's answers ("ko" | "en"); UI labels stay English either way. */
  getLanguage(): Promise<{ language: string }>;
  setLanguage(language: string): Promise<{ language: string }>;
  getDataFlowDetail(docId?: string | null): Promise<DataFlowDetail>;
  reorderSessions(request: SessionOrderRequest): Promise<void>;
  setSessionPaused(sessionId: string, paused: boolean): Promise<void>;
  /** Clear a session's halt state so it can run again. Idempotent (204 on a non-halted session too). */
  resumeHaltedSession(sessionId: string): Promise<void>;
  /** Stop the current turn and pull the last user message back for editing; returns its text. */
  retractLastMessage(sessionId: string): Promise<string | null>;
  /** Bind (docKey) or unbind (null) the GH document this session's writes target. */
  setSessionTarget(sessionId: string, grasshopperDoc: string | null): Promise<void>;
  setSessionModel(sessionId: string, modelProfile: ModelProfile, model?: string | null): Promise<void>;
  /** Set the session's permission level (review / standard / fullAuto). */
  setPermissionMode(sessionId: string, mode: PermissionMode): Promise<void>;
  /** Release the session's standing "같은 종류 계속 허용" consent without changing the mode. */
  releaseStandingApproval(sessionId: string): Promise<void>;
  /** Rename a session (its display title). */
  renameSession(sessionId: string, name: string): Promise<void>;
  /** Answer a proposed approval card: grant the ticked items (mints one bound grant) or reject. */
  answerApprovalCard(
    sessionId: string,
    answer: ApprovalAnswer,
  ): Promise<void>;
  /** Clear an ANSWERED approval card so it stops occupying the transcript. */
  dismissApprovalCard(sessionId: string): Promise<void>;
  /** Answer the agent's clickable question. The choice is delivered to the agent as a turn. */
  answerAskCard(sessionId: string, optionId: string, note?: string): Promise<void>;
  /** Clear an ANSWERED ask card so it stops occupying the transcript; the server 400s on a live question. */
  dismissAskCard(sessionId: string): Promise<void>;
  /** Answer a proposed goal card: approve (optionally edited) or reject. */
  answerGoalCard(
    sessionId: string,
    answer: { status: "confirmed" | "rejected"; chosenOption?: string; objective?: string; criteria?: string[] },
  ): Promise<void>;
  /** Clear a SETTLED goal card (confirmed/rejected/scored); the server 400s on a proposing one. */
  dismissGoalCard(sessionId: string): Promise<void>;
  sendMessage(sessionId: string, request: MessageRequest): Promise<void>;
  /** Soft-delete: hide from the active list, recoverable from the trash. */
  deleteSession(sessionId: string): Promise<void>;
  restoreSession(sessionId: string): Promise<void>;
  /** Permanent delete: removes the session and its transcript for good. */
  purgeSession(sessionId: string): Promise<void>;
  listDeletedSessions(): Promise<DeletedSession[]>;
  openTerminal(sessionId: string): Promise<void>;
  openLoginTerminal(): Promise<void>;
  setRuntimePaused(paused: boolean): Promise<void>;
  listArchive(): Promise<ArchiveProject[]>;
  readArchiveMessages(fingerprint: string, sessionId: string, limit?: number): Promise<ArchiveMessage[]>;
  /** Fork an archived session into the current project as a new live session (new thread, seeded context). */
  importArchiveSession(fingerprint: string, sessionId: string): Promise<void>;
}

const trimTrailingSlash = (value: string) => value.replace(/\/+$/, "");

function configuredApiBase(): string {
  const query = new URLSearchParams(window.location.search).get("apiBase");
  return trimTrailingSlash(query ?? window.__VINO__?.apiBase ?? "");
}

function demoRequested(): boolean {
  const query = new URLSearchParams(window.location.search);
  return query.get("demo") === "1" || window.__VINO__?.demo === true;
}

/**
 * A 401 from the loopback API. Its own type because it is the one failure the panel cannot
 * retry out of: the cookie was minted by an AgentHost that is no longer answering on this port
 * (Rhino restarted, or a second runtime took the cookie — it is scoped to 127.0.0.1 with no port
 * isolation). Nothing in the panel can re-mint it, so it needs a banner and a real instruction,
 * not one more transient error string. Note the label is inherited: there is no time-based
 * expiry on this path at all.
 */
export class PanelSessionExpiredError extends Error {
  constructor() {
    super(
      "패널 세션이 만료됐습니다 (이 런타임의 토큰이 아닙니다). 패널을 닫았다가 " +
        "VinoOpenPanel로 다시 열면 복구됩니다.",
    );
    this.name = "PanelSessionExpiredError";
  }
}

/**
 * Pulls the human sentence out of an ApiError body, tolerating a plain-text body (older routes,
 * proxies) by returning it unchanged. Never throws: a failure to parse an error must not replace
 * the error.
 */
function apiErrorMessage(body: string): string {
  const trimmed = body.trim();
  if (!trimmed.startsWith("{")) return trimmed;
  try {
    const parsed = JSON.parse(trimmed) as { message?: unknown };
    return typeof parsed.message === "string" && parsed.message.trim().length > 0
      ? parsed.message
      : trimmed;
  } catch {
    return trimmed;
  }
}

/** How every bridge read answers: the operation's own payload wrapped with its fingerprint. */
interface BridgeEnvelope<T> {
  result?: T;
  fingerprint?: string;
  diagnostics?: unknown[];
}

// Tolerates both shapes on purpose: an older AgentHost (a panel can outlive a host restart, and
// the mock client answers bare) returns the result unwrapped, and silently yielding undefined
// counts is exactly the failure this unwrap exists to end.
function unwrapBridge<T>(envelope: BridgeEnvelope<T> | T): T {
  if (envelope && typeof envelope === "object" && "result" in envelope) {
    const inner = (envelope as BridgeEnvelope<T>).result;
    if (inner !== undefined && inner !== null) return inner;
  }
  return envelope as T;
}

class HttpApiClient implements VinoApiClient {
  readonly demo = false;
  private readonly base: string;

  constructor(base: string) {
    this.base = `${base}/api/v1`;
  }

  private async request<T>(path: string, init?: RequestInit): Promise<T> {
    const response = await fetch(`${this.base}${path}`, {
      credentials: "same-origin",
      ...init,
      headers: {
        Accept: "application/json",
        ...(init?.body ? { "Content-Type": "application/json" } : {}),
        ...init?.headers,
      },
    });

    if (!response.ok) {
      // A 401 here is never something the user did wrong: the panel authenticates with a
      // cookie minted by a one-time bootstrap nonce, so the session goes stale whenever the
      // AgentHost it was minted for is gone (Rhino restarted, another instance took the
      // port). Raw server JSON told the user nothing actionable; name the fix instead.
      if (response.status === 401) {
        throw new PanelSessionExpiredError();
      }
      // Error bodies are ApiError JSON ({code, message}). Throwing the raw body put things like
      // {"code":"canvas_focus_target","message":"No Grasshopper definition is open…"} on screen
      // verbatim, next to the button the user pressed. Take the sentence, drop the envelope.
      const detail = await response.text();
      throw new Error(apiErrorMessage(detail) || `Vino API returned ${response.status}`);
    }

    if (response.status === 204 || response.headers.get("content-length") === "0") {
      return undefined as T;
    }

    return (await response.json()) as T;
  }

  getRuntime(): Promise<RuntimeState> {
    return this.request<RuntimeState>("/runtime");
  }

  listModels(): Promise<ModelInfo[]> {
    return this.request<ModelInfo[]>("/models");
  }

  subscribe(
    onState: (state: RuntimeState) => void,
    onError?: (error: Error) => void,
  ): () => void {
    let disposed = false;
    let pollingTimer: number | undefined;
    let events: EventSource | undefined;
    // In-flight guard: at a 1.5s interval a slow getRuntime could otherwise overlap itself, and two
    // responses landing out of order would deliver an older snapshot last. Skip a tick already running.
    let polling = false;

    const poll = async () => {
      if (polling) return;
      polling = true;
      try {
        onState(await this.getRuntime());
      } catch (error) {
        onError?.(error instanceof Error ? error : new Error("Runtime polling failed"));
      } finally {
        polling = false;
      }
    };

    const startPolling = () => {
      if (disposed || pollingTimer !== undefined) return;
      void poll();
      pollingTimer = window.setInterval(() => void poll(), 1_500);
    };

    if (typeof EventSource === "undefined") {
      startPolling();
    } else {
      events = new EventSource(`${this.base}/events`, { withCredentials: true });
      const handleState = (event: MessageEvent<string>) => {
        try {
          onState(JSON.parse(event.data) as RuntimeState);
        } catch {
          onError?.(new Error("Vino sent an invalid runtime event"));
        }
      };
      events.onmessage = handleState;
      events.addEventListener("state", handleState as EventListener);
      events.onerror = () => {
        events?.close();
        events = undefined;
        startPolling();
      };
    }

    return () => {
      disposed = true;
      events?.close();
      if (pollingTimer !== undefined) window.clearInterval(pollingTimer);
    };
  }

  reorderSessions(request: SessionOrderRequest): Promise<void> {
    return this.request("/sessions/order", {
      method: "PUT",
      body: JSON.stringify(request),
    });
  }

  createSession(name: string, grasshopperDoc?: string): Promise<void> {
    return this.request("/sessions", {
      method: "POST",
      body: JSON.stringify({
        name,
        // New sessions default to xhigh reasoning effort on the GPT-5.6-Sol model (see also mock.ts).
        modelProfile: "xhigh",
        model: "gpt-5.6-sol",
        ...(grasshopperDoc ? { grasshopperDoc } : {}),
      }),
    });
  }

  answerApprovalCard(
    sessionId: string,
    answer: ApprovalAnswer,
  ): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/approval`, {
      method: "PUT",
      body: JSON.stringify(answer),
    });
  }

  dismissApprovalCard(sessionId: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/approval`, { method: "DELETE" });
  }

  answerAskCard(sessionId: string, optionId: string, note?: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/ask`, {
      method: "PUT",
      body: JSON.stringify({ optionId, note }),
    });
  }

  dismissAskCard(sessionId: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/ask`, { method: "DELETE" });
  }

  getLanguage(): Promise<{ language: string }> {
    return this.request<{ language: string }>("/language");
  }

  setLanguage(language: string): Promise<{ language: string }> {
    return this.request<{ language: string }>("/language", {
      method: "POST",
      body: JSON.stringify({ language }),
    });
  }

  // Both focus endpoints answer with the bridge envelope {result, fingerprint, diagnostics}, not
  // the bare result. Reading the counts off the top level yielded `undefined` on every call, which
  // is why every focus chip said "undefined 선택" and why a missing component never produced its
  // "N 사라짐" warning (undefined > 0 is false). Unwrap once, here.
  focusObjects(objectIds: string[], mode: FocusMode, zoom = true, ownerToken?: string): Promise<FocusResult> {
    return this.request<BridgeEnvelope<FocusResult>>("/focus", {
      method: "POST",
      body: JSON.stringify({ objectIds, mode, zoom, ownerToken }),
    }).then(unwrapBridge);
  }

  focusCanvasObjects(objectIds: string[], docId?: string | null, zoom = true): Promise<CanvasFocusResult> {
    return this.request<BridgeEnvelope<CanvasFocusResult>>("/canvas/focus", {
      method: "POST",
      body: JSON.stringify({ objectIds, zoom, docId: docId ?? null }),
    }).then(unwrapBridge);
  }

  getCurrentSelection(): Promise<PinnedSelection> {
    return this.request<PinnedSelection>("/selection/current");
  }

  getDataFlowDetail(docId?: string | null): Promise<DataFlowDetail> {
    const query = docId ? `?doc=${encodeURIComponent(docId)}` : "";
    return this.request<DataFlowDetail>(`/data-flow${query}`);
  }

  setSessionPaused(sessionId: string, paused: boolean): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/pause`, {
      method: "PUT",
      body: JSON.stringify({ paused }),
    });
  }

  resumeHaltedSession(sessionId: string): Promise<void> {
    // Empty body on purpose (the contract is POST with no payload → 204); request() skips the
    // JSON Content-Type header when there is no body.
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/resume`, { method: "POST" });
  }

  async retractLastMessage(sessionId: string): Promise<string | null> {
    const result = await this.request<{ content: string | null }>(
      `/sessions/${encodeURIComponent(sessionId)}/retract-last`,
      { method: "POST" },
    );
    return result?.content ?? null;
  }

  setSessionTarget(sessionId: string, grasshopperDoc: string | null): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/target`, {
      method: "PUT",
      body: JSON.stringify({ grasshopperDoc }),
    });
  }

  setSessionModel(sessionId: string, modelProfile: ModelProfile, model?: string | null): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/model`, {
      method: "PUT",
      body: JSON.stringify({ modelProfile, model: model ?? null }),
    });
  }

  setPermissionMode(sessionId: string, mode: PermissionMode): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/permission`, {
      method: "PUT",
      body: JSON.stringify({ mode }),
    });
  }

  releaseStandingApproval(sessionId: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/permission/standing`, {
      method: "DELETE",
    });
  }

  renameSession(sessionId: string, name: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/title`, {
      method: "PUT",
      body: JSON.stringify({ name }),
    });
  }

  answerGoalCard(
    sessionId: string,
    answer: { status: "confirmed" | "rejected"; chosenOption?: string; objective?: string; criteria?: string[] },
  ): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/goal`, {
      method: "PUT",
      body: JSON.stringify(answer),
    });
  }

  dismissGoalCard(sessionId: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/goal`, { method: "DELETE" });
  }

  sendMessage(sessionId: string, request: MessageRequest): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/messages`, {
      method: "POST",
      body: JSON.stringify(request),
    });
  }

  deleteSession(sessionId: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}`, { method: "DELETE" });
  }

  restoreSession(sessionId: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/restore`, { method: "POST" });
  }

  purgeSession(sessionId: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/purge`, { method: "DELETE" });
  }

  listDeletedSessions(): Promise<DeletedSession[]> {
    return this.request<DeletedSession[]>("/sessions/deleted");
  }

  openTerminal(sessionId: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/terminal`, {
      method: "POST",
    });
  }

  openLoginTerminal(): Promise<void> {
    return this.request("/runtime/login-terminal", { method: "POST" });
  }

  setRuntimePaused(paused: boolean): Promise<void> {
    return this.request("/runtime/pause", {
      method: "PUT",
      body: JSON.stringify({ paused }),
    });
  }

  listArchive(): Promise<ArchiveProject[]> {
    return this.request<ArchiveProject[]>("/archive");
  }

  readArchiveMessages(fingerprint: string, sessionId: string, limit = 500): Promise<ArchiveMessage[]> {
    return this.request<ArchiveMessage[]>(
      `/archive/${encodeURIComponent(fingerprint)}/sessions/${encodeURIComponent(sessionId)}/messages?limit=${limit}`,
    );
  }

  importArchiveSession(fingerprint: string, sessionId: string): Promise<void> {
    return this.request(
      `/archive/${encodeURIComponent(fingerprint)}/sessions/${encodeURIComponent(sessionId)}/import`,
      { method: "POST" },
    );
  }
}

export function createApiClient(): VinoApiClient {
  if (demoRequested()) return createMockApiClient();
  return new HttpApiClient(configuredApiBase());
}

export { createMockApiClient };
