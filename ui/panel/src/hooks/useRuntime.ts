import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  PanelSessionExpiredError,
  createApiClient,
  createMockApiClient,
  type GptinoApiClient,
} from "../api/client";
import { moveById, shiftById } from "../order";
import type {
  ApprovalAnswer,
  FocusMode,
  MessageAttachment,
  ModelInfo,
  ModelProfile,
  PinnedSelection,
  RuntimeState,
} from "../types";

type OptimisticUpdate = (current: RuntimeState) => RuntimeState;

const isPanelSessionExpired = (cause: unknown): boolean =>
  cause instanceof PanelSessionExpiredError;

export function useRuntime() {
  const clientRef = useRef<GptinoApiClient | null>(null);
  if (!clientRef.current) clientRef.current = createApiClient();
  const client = clientRef.current;

  const [runtime, setRuntime] = useState<RuntimeState | null>(null);
  // Server-truth snapshot, set ONLY at the three points a snapshot arrives from the
  // server (initial load, subscribe callback, post-action refetch) — never on an
  // optimistic write or a rollback. The completion detector consumes this instead of
  // `runtime` so a client-only optimistic status (e.g. "drafting") can never manufacture
  // a false completion edge.
  const [serverRuntime, setServerRuntime] = useState<RuntimeState | null>(null);
  const [models, setModels] = useState<ModelInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  // Per-action failures, keyed by the same action key as busyActions. Every failure used to
  // collapse into the single `error` string above, whose only surface was a 10px "✕ 1" chip at
  // the bottom of the composer — so approving, resuming, zooming and expiring all looked
  // identical to "nothing happened". A card can now render its own failure where it happened.
  const [actionErrors, setActionErrors] = useState<Record<string, string>>({});
  // A 401 is categorically different: nothing the user does in the panel can recover it, because
  // the cookie belongs to an AgentHost that is gone. It gets its own flag and its own banner
  // instead of scrolling past as one more transient error string.
  const [sessionExpired, setSessionExpired] = useState(false);
  const [busyActions, setBusyActions] = useState<Set<string>>(() => new Set());

  const clearActionError = useCallback((key: string) => {
    setActionErrors((current) => {
      if (!(key in current)) return current;
      const next = { ...current };
      delete next[key];
      return next;
    });
  }, []);

  const replaceClient = useCallback((next: GptinoApiClient) => {
    clientRef.current = next;
  }, []);

  // Monotonic gate for SERVER snapshots (initial load, SSE, post-action refetch). A snapshot strictly
  // OLDER than the last one applied is a late arrival — a poll or a post-action refetch that raced in
  // behind a newer SSE — and must not overwrite fresher state (the stale-"working"-after-"idle"
  // flicker). Same-or-newer always applies. Optimistic writes and rollbacks bypass this: they are
  // local, not server truth, and never touch serverRuntime or the applied watermark.
  const appliedAtRef = useRef(0);
  const applyServerRuntime = useCallback((next: RuntimeState) => {
    const seq = Date.parse(next.lastUpdatedAt);
    if (!Number.isNaN(seq)) {
      if (seq < appliedAtRef.current) return;
      appliedAtRef.current = seq;
    }
    setRuntime(next);
    setServerRuntime(next);
  }, []);

  useEffect(() => {
    let disposed = false;
    let unsubscribe: () => void = () => undefined;

    const connect = async (activeClient: GptinoApiClient) => {
      try {
        const initial = await activeClient.getRuntime();
        if (disposed) return;
        applyServerRuntime(initial);
        setLoading(false);
        setError(null);
        void activeClient
          .listModels()
          .then((catalog) => {
            if (!disposed) setModels(catalog);
          })
          .catch(() => {
            // The model catalog is optional UI sugar; routing still works without it.
          });
        unsubscribe = activeClient.subscribe(
          (next) => {
            if (!disposed) {
              applyServerRuntime(next);
            }
          },
          (subscriptionError) => {
            if (disposed) return;
            setError(subscriptionError.message);
            if (isPanelSessionExpired(subscriptionError)) setSessionExpired(true);
          },
        );
      } catch (initialError) {
        if (disposed) return;
        if (import.meta.env.DEV && !activeClient.demo) {
          const mock = createMockApiClient();
          replaceClient(mock);
          setError("AgentHost is unavailable — showing demo data.");
          await connect(mock);
          return;
        }
        setError(initialError instanceof Error ? initialError.message : "Unable to connect to GPTino");
        if (isPanelSessionExpired(initialError)) setSessionExpired(true);
        setLoading(false);
      }
    };

    void connect(clientRef.current!);
    return () => {
      disposed = true;
      unsubscribe();
    };
  }, [replaceClient, applyServerRuntime]);

  // Resolves true when the API call succeeded; callers that staged local state
  // (e.g. the composer draft) use the false result to restore it.
  const runAction = useCallback(
    async (key: string, optimistic: OptimisticUpdate | undefined, action: (client: GptinoApiClient) => Promise<void>): Promise<boolean> => {
      const before = runtime;
      if (optimistic) setRuntime((current) => (current ? optimistic(current) : current));
      setBusyActions((current) => new Set(current).add(key));
      setError(null);
      clearActionError(key);
      try {
        try {
          await action(clientRef.current!);
        } catch (actionError) {
          // The WRITE failed: revert the optimistic view. (serverRuntime is left untouched so a
          // failed action never emits a phantom completion edge.)
          if (before) setRuntime(before);
          const message = actionError instanceof Error ? actionError.message : "The GPTino action failed";
          setError(message);
          // Also attach it to the action, so the button the user pressed can say what happened.
          setActionErrors((current) => ({ ...current, [key]: message }));
          if (isPanelSessionExpired(actionError)) setSessionExpired(true);
          return false;
        }
        // The write landed. A failed REFRESH must NOT roll it back — that used to turn a successful
        // write into a phantom "it failed" whenever only the follow-up GET hiccuped. The next SSE or
        // poll brings the fresh state; the monotonic gate keeps it from regressing.
        try {
          applyServerRuntime(await clientRef.current!.getRuntime());
        } catch {
          // Refresh failed; leave the current view for the next server snapshot to correct.
        }
        return true;
      } finally {
        setBusyActions((current) => {
          const next = new Set(current);
          next.delete(key);
          return next;
        });
      }
    },
    [runtime, applyServerRuntime],
  );

  const reorder = useCallback(
    (sourceId: string, targetId: string) => {
      if (!runtime || sourceId === targetId) return;
      const sessions = moveById(runtime.sessions, sourceId, targetId);
      const request = {
        orderedSessionIds: sessions.map(({ id }) => id),
        orderVersion: runtime.orderVersion,
      };
      void runAction(
        "reorder",
        (current) => ({ ...current, sessions }),
        (activeClient) => activeClient.reorderSessions(request),
      );
    },
    [runAction, runtime],
  );

  const shift = useCallback(
    (sessionId: string, direction: -1 | 1) => {
      if (!runtime) return;
      const sessions = shiftById(runtime.sessions, sessionId, direction);
      if (sessions.every((session, index) => session.id === runtime.sessions[index]?.id)) return;
      const target = sessions.findIndex(({ id }) => id === sessionId);
      const displaced = runtime.sessions[target]?.id;
      if (displaced) reorder(sessionId, displaced);
    },
    [reorder, runtime],
  );

  // Archive reads never mutate runtime state, so they bypass runAction's
  // optimistic-update / refetch machinery. Stable identities on purpose: the
  // archive overlay keys its fetch effects on these, and they must not re-fire
  // every time a runtime event re-memoizes `actions`.
  const listArchive = useCallback(() => clientRef.current!.listArchive(), []);
  const listDeleted = useCallback(() => clientRef.current!.listDeletedSessions(), []);
  // Read-only like the archive callbacks: the data-flow drawer keys its fetch effect on this.
  const focusObjects = useCallback(
    (objectIds: string[], mode: FocusMode) => clientRef.current!.focusObjects(objectIds, mode),
    [],
  );
  const focusCanvasObjects = useCallback(
    (objectIds: string[], docId?: string | null) =>
      clientRef.current!.focusCanvasObjects(objectIds, docId),
    [],
  );
  const captureSelection = useCallback(() => clientRef.current!.getCurrentSelection(), []);

  const getDataFlowDetail = useCallback(
    (docId?: string | null) => clientRef.current!.getDataFlowDetail(docId),
    [],
  );

  // Prose language for GPTino's answers. Project-level (not per session) and applied when
  // the next thread starts/resumes, so the toggle reports optimistically and never blocks.
  const [language, setLanguageState] = useState("en");
  useEffect(() => {
    let disposed = false;
    clientRef.current
      ?.getLanguage()
      .then((value) => {
        if (!disposed) setLanguageState(value.language === "ko" ? "ko" : "en");
      })
      .catch(() => undefined);
    return () => {
      disposed = true;
    };
  }, []);
  const setLanguage = useCallback(async (next: string) => {
    setLanguageState(next === "ko" ? "ko" : "en");
    try {
      const applied = await clientRef.current!.setLanguage(next);
      setLanguageState(applied.language === "ko" ? "ko" : "en");
    } catch {
      // Leave the optimistic value; the next reload re-reads the server's answer.
    }
  }, []);
  const readArchiveMessages = useCallback(
    (fingerprint: string, sessionId: string, limit?: number) =>
      clientRef.current!.readArchiveMessages(fingerprint, sessionId, limit),
    [],
  );

  const updateSession = useCallback(
    (sessionId: string, update: (session: RuntimeState["sessions"][number]) => RuntimeState["sessions"][number]) =>
      (current: RuntimeState): RuntimeState => ({
        ...current,
        sessions: current.sessions.map((session) => (session.id === sessionId ? update(session) : session)),
      }),
    [],
  );

  const actions = useMemo(
    () => ({
      createSession(name: string, grasshopperDoc?: string) {
        return runAction("create-session", undefined, (activeClient) => activeClient.createSession(name, grasshopperDoc));
      },
      reorder,
      shift,
      setSessionTarget(sessionId: string, grasshopperDoc: string | null) {
        return runAction(
          `target:${sessionId}`,
          updateSession(sessionId, (session) => ({ ...session, boundGrasshopperDocId: grasshopperDoc })),
          (activeClient) => activeClient.setSessionTarget(sessionId, grasshopperDoc),
        );
      },
      // Clears a halted session's halt state. Deliberately NOT optimistic: the halt banner must
      // stay mounted while the POST is in flight (its busy "재개 중…" label and its inline failure
      // message live in that component instance), so the halt is cleared only by the post-action
      // refetch after the server's 204. On failure nothing was changed locally — the same banner
      // instance stays up and renders the error. Double-fire is prevented by the busy state.
      resumeHalt(sessionId: string) {
        return runAction(
          `halt:${sessionId}`,
          undefined,
          (activeClient) => activeClient.resumeHaltedSession(sessionId),
        );
      },
      pauseSession(sessionId: string, paused: boolean) {
        return runAction(
          `pause:${sessionId}`,
          updateSession(sessionId, (session) => ({
            ...session,
            paused,
            status: paused ? "paused" : "idle",
          })),
          (activeClient) => activeClient.setSessionPaused(sessionId, paused),
        );
      },
      async retractLast(sessionId: string): Promise<string | null> {
        const content = await clientRef.current!.retractLastMessage(sessionId);
        const next = await clientRef.current!.getRuntime();
        setRuntime(next);
        setServerRuntime(next);
        return content;
      },
      setModel(sessionId: string, modelProfile: ModelProfile, model?: string | null) {
        return runAction(
          `model:${sessionId}`,
          updateSession(sessionId, (session) => ({ ...session, modelProfile, pinnedModel: model ?? null })),
          (activeClient) => activeClient.setSessionModel(sessionId, modelProfile, model),
        );
      },
      renameSession(sessionId: string, name: string) {
        return runAction(
          `rename:${sessionId}`,
          updateSession(sessionId, (session) => ({ ...session, title: name })),
          (activeClient) => activeClient.renameSession(sessionId, name),
        );
      },
      // Granting mints a server-side grant bound to the ticked items; no optimistic patch, for the
      // same reason as goals — the server's answer is the only truthful one.
      answerApproval(
        sessionId: string,
        answer: ApprovalAnswer,
      ) {
        return runAction(
          `approval:${sessionId}`,
          undefined,
          (activeClient) => activeClient.answerApprovalCard(sessionId, answer),
        );
      },
      // Clearing an answered card is a plain server write; the next snapshot is the truth.
      dismissApproval(sessionId: string) {
        return runAction(
          `approval:${sessionId}`,
          undefined,
          (activeClient) => activeClient.dismissApprovalCard(sessionId),
        );
      },
      // Same contract for a settled goal card: DELETE, then the refetched snapshot drops the shelf.
      dismissGoal(sessionId: string) {
        return runAction(
          `goal:${sessionId}`,
          undefined,
          (activeClient) => activeClient.dismissGoalCard(sessionId),
        );
      },
      // The user's click on a question. No optimistic patch — the server delivers the answer as a
      // turn and the next snapshot is the truth.
      answerAsk(sessionId: string, optionId: string, note?: string) {
        return runAction(
          `ask:${sessionId}`,
          undefined,
          (activeClient) => activeClient.answerAskCard(sessionId, optionId, note),
        );
      },
      // Same contract as the other settled cards: DELETE, the next snapshot drops the card.
      dismissAsk(sessionId: string) {
        return runAction(
          `ask:${sessionId}`,
          undefined,
          (activeClient) => activeClient.dismissAskCard(sessionId),
        );
      },
      // The user's verdict on a proposed goal card. No optimistic patch: the server rewrites the
      // card (status + any edits) and the next SSE push is the truth — showing an approved card
      // before the server accepted it would be exactly the false-success this project refuses.
      answerGoal(
        sessionId: string,
        answer: { status: "confirmed" | "rejected"; chosenOption?: string; objective?: string; criteria?: string[] },
      ) {
        return runAction(
          `goal:${sessionId}`,
          undefined,
          (activeClient) => activeClient.answerGoalCard(sessionId, answer),
        );
      },
      async sendMessage(
        sessionId: string,
        content: string,
        attachments?: MessageAttachment[],
        pinnedSelection?: PinnedSelection,
      ) {
        const clientMessageId = crypto.randomUUID();
        const createdAt = new Date().toISOString();
        // The backend persists attachments as short "[Attached: name]" lines; the optimistic
        // message mirrors that so the transcript does not jump when the server row arrives.
        const attachmentLines = (attachments ?? []).map((attachment) => `[Attached: ${attachment.fileName}]`);
        const optimisticContent =
          attachmentLines.length === 0
            ? content
            : [content, ...attachmentLines].filter((line) => line.length > 0).join("\n");
        return runAction(
          `message:${sessionId}`,
          updateSession(sessionId, (session) => ({
            ...session,
            status: session.paused ? session.status : "drafting",
            messages: [
              ...session.messages,
              { id: clientMessageId, role: "user", content: optimisticContent, createdAt, pending: true },
            ],
          })),
          (activeClient) =>
            activeClient.sendMessage(sessionId, {
              content,
              clientMessageId,
              ...(attachments && attachments.length > 0 ? { attachments } : {}),
              ...(pinnedSelection ? { pinnedSelection } : {}),
            }),
        );
      },
      openTerminal(sessionId: string) {
        return runAction(
          `terminal:${sessionId}`,
          updateSession(sessionId, (session) => ({ ...session, terminalOpen: true })),
          (activeClient) => activeClient.openTerminal(sessionId),
        );
      },
      deleteSession(sessionId: string) {
        return runAction(
          `delete:${sessionId}`,
          // Optimistically drop it from the visible list; the refetch confirms.
          (current) => ({ ...current, sessions: current.sessions.filter((session) => session.id !== sessionId) }),
          (activeClient) => activeClient.deleteSession(sessionId),
        );
      },
      restoreSession(sessionId: string) {
        return runAction(
          `restore:${sessionId}`,
          undefined,
          (activeClient) => activeClient.restoreSession(sessionId),
        );
      },
      purgeSession(sessionId: string) {
        return runAction(
          `purge:${sessionId}`,
          undefined,
          (activeClient) => activeClient.purgeSession(sessionId),
        );
      },
      listDeleted,
      pauseRuntime(paused: boolean) {
        return runAction(
          "pause-runtime",
          (current) => ({ ...current, paused }),
          (activeClient) => activeClient.setRuntimePaused(paused),
        );
      },
      openLoginTerminal() {
        return runAction("login-terminal", undefined, (activeClient) => activeClient.openLoginTerminal());
      },
      listArchive,
      readArchiveMessages,
      focusObjects,
      focusCanvasObjects,
      captureSelection,
      setLanguage,
      getDataFlowDetail,
      // Import mutates runtime state (a new session appears), so — unlike the read-only archive
      // callbacks — it goes through runAction whose post-action getRuntime() pulls the new session in.
      importArchiveSession(fingerprint: string, sessionId: string) {
        return runAction(
          `import:${sessionId}`,
          undefined,
          (activeClient) => activeClient.importArchiveSession(fingerprint, sessionId),
        );
      },
    }),
    [focusObjects, focusCanvasObjects, captureSelection, getDataFlowDetail, listArchive, listDeleted, readArchiveMessages, reorder, runAction, setLanguage, shift, updateSession],
  );

  return {
    runtime,
    serverRuntime,
    models,
    loading,
    error,
    actionErrors,
    clearActionError,
    sessionExpired,
    demo: clientRef.current.demo,
    busyActions,
    language,
    actions,
  };
}
