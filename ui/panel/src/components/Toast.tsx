import { Icon } from "./Icons";
import type { CompletionToast } from "../hooks/useSessionCompletion";
import { t } from "../i18n";

// Fixed bottom-right stack of session-completion toasts. Success toasts read as a
// quiet "done"; attention toasts reuse the conflict-card red vocabulary; waiting
// toasts (a pending card needs an answer) reuse the ask-card accent blue. Clicking
// a toast body opens its session; the × dismisses it.
export function ToastStack({
  toasts,
  onDismiss,
  onOpen,
}: {
  toasts: CompletionToast[];
  onDismiss(id: string): void;
  onOpen(sessionId: string): void;
}) {
  if (toasts.length === 0) return null;
  return (
    <div className="toast-stack" aria-live="polite">
      {toasts.map((toast) => (
        <div
          key={toast.id}
          className={`toast toast-${toast.kind}`}
          role={toast.kind === "attention" ? "alert" : "status"}
        >
          <button
            type="button"
            className="toast-body"
            onClick={() => onOpen(toast.sessionId)}
            title={t("toastOpenSession")}
          >
            <span className="toast-icon">
              {toast.kind === "attention" ? (
                <Icon name="warning" />
              ) : toast.kind === "waiting" ? (
                <Icon name="question" />
              ) : (
                <span className="toast-check">✓</span>
              )}
            </span>
            <span className="toast-text">
              <strong className="toast-title">{toast.title}</strong>
              <span className="toast-kind">
                {toast.kind === "attention"
                  ? t("toastNeedsAttention")
                  : toast.kind === "waiting"
                    ? t("toastNeedsInput")
                    : t("toastFinished")}
              </span>
              {toast.detail ? <span className="toast-detail">{toast.detail}</span> : null}
            </span>
          </button>
          <button
            type="button"
            className="toast-dismiss"
            aria-label="Dismiss notification"
            onClick={() => onDismiss(toast.id)}
          >
            ×
          </button>
        </div>
      ))}
    </div>
  );
}
