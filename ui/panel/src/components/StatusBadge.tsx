import type { SessionStatus } from "../types";
import { t } from "../i18n";

// Resolved per render (not a module-level map) so the labels follow the 한/영 toggle.
const label = (status: SessionStatus): string => {
  switch (status) {
    case "working": return t("statusWorking");
    case "drafting": return t("statusDrafting");
    case "queued": return t("statusQueued");
    case "verifying": return t("statusVerifying");
    case "paused": return t("statusPaused");
    case "blocked": return t("statusBlocked");
    default: return t("statusIdle");
  }
};

export function StatusBadge({ status }: { status: SessionStatus }) {
  return (
    <span className={`status-badge status-${status}`}>
      <span className="status-dot" />
      {label(status)}
    </span>
  );
}
