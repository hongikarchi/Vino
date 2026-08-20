import type { SessionActivity, SessionStatus } from "./types";

import { t } from "./i18n";

/** The thinking-row activity phases (사용자 확정: 계획/스냅샷 읽기/작성/검증/정리 + 문제 수습). */
export type WorkPhase = "planning" | "reading" | "drafting" | "verifying" | "tidying" | "trouble";

/** Resolved per call so the label follows the 한/영 toggle. */
export function workPhaseLabel(phase: WorkPhase): string {
  switch (phase) {
    case "planning": return t("phasePlanning");
    case "reading": return t("phaseReading");
    case "drafting": return t("phaseDrafting");
    case "verifying": return t("phaseVerifying");
    case "tidying": return t("phaseTidying");
    default: return t("phaseTrouble");
  }
}

const READING_KINDS = new Set([
  "snapshot_read",
  "inspect_outputs",
  "rhino_list",
  "rhino_inspect",
  "component_catalog",
  "rhino_audit",
  "rhino_layers",
  "structural_extract",
  "data_read",
  "skill_read",
  "job_status",
]);
const DRAFTING_KINDS = new Set([
  "artifact_write",
  "artifact_read",
  "change_submit",
  "consolidate_stages",
  "structural_solve",
]);
const TIDYING_KINDS = new Set(["arrange_layout"]);

interface WorkPhaseSignals {
  status: SessionStatus;
  activity?: SessionActivity[];
  job?: { phase?: string | null } | null;
}

/**
 * Derives the mascot phase from signals the host already sends — no host change needed.
 * The activity feed records a tool call when it FINISHES, so outside of a running job this
 * reads as "what it just did", a half-beat late by design (host-side tool-START events are a
 * later wave). Job phases are live, so applying/verifying are exact. Null = not working.
 */
export function deriveWorkPhase(session: WorkPhaseSignals): WorkPhase | null {
  const { status } = session;
  if (status !== "working" && status !== "drafting" && status !== "verifying") {
    return null;
  }
  const jobPhase = session.job?.phase ?? null;
  // A job that is failing or recovering while the turn is still alive: the mascot goes red
  // and stops walking. Matched loosely because job.phase can carry display text, not an enum.
  if (jobPhase && /fail|recover/i.test(jobPhase)) {
    return "trouble";
  }
  if (status === "verifying" || jobPhase === "verifying") {
    return "verifying";
  }
  if (jobPhase === "applying" || jobPhase === "waiting" || jobPhase === "ready") {
    // A queued/executing ChangeSet is still the "writing it through" stage to the user.
    return "drafting";
  }
  if (status === "drafting") {
    // Optimistic just-sent state: whatever the activity feed holds is the PREVIOUS turn's tail,
    // so deriving from it would lie. The turn is starting — the model is planning.
    return "planning";
  }
  const activity = session.activity;
  const kind = activity && activity.length > 0 ? activity[activity.length - 1]?.kind : undefined;
  if (kind && TIDYING_KINDS.has(kind)) return "tidying";
  if (kind && DRAFTING_KINDS.has(kind)) return "drafting";
  if (kind && READING_KINDS.has(kind)) return "reading";
  // Working with no tool activity yet (or an unknown kind): the model is thinking.
  return "planning";
}
