// UI-label language. The 한/영 toggle has always steered Vino's PROSE; this extends it to the
// panel's own chrome. Vocabulary that must stay English regardless — model names (GPT-5.6 Sol),
// reasoning-effort level names (Extra High), permission level names, file names, and the
// product nouns Codex/Rhino/Grasshopper — is deliberately NOT keyed here.
export type UiLanguage = "ko" | "en";

const EN = {
  tabModel: "Model",
  tabData: "Data",
  graph: "Graph",
  newSession: "+ Session",
  pastSessions: "Past sessions",
  pastSessionsTitle:
    "Browse and restore past sessions — every project on this machine, plus this project's deleted sessions",
  effort: "Effort",
  permission: "Permission",
  model: "Model",
  target: "Target",
  composerPlaceholder: "Describe what you want — a modeling change, a document check-up, a cleanup…",
  stopEdit: "Stop & edit",
  stopEditTitle: "Stop the current work and pull your message back to edit it",
  ctrlEnterToSend: "Ctrl ↵ to send",
  deleteSession: "Delete",
};

const KO: typeof EN = {
  tabModel: "모델",
  tabData: "데이터",
  graph: "그래프",
  newSession: "+ 세션",
  pastSessions: "지난 세션",
  pastSessionsTitle: "지난 세션 탐색·복원 — 이 컴퓨터의 모든 프로젝트와, 이 프로젝트에서 삭제된 세션까지",
  effort: "추론 강도",
  permission: "권한",
  model: "모델",
  target: "대상",
  composerPlaceholder: "원하는 작업을 설명하세요 — 모델링 수정, 문서 점검, 정리…",
  stopEdit: "중지하고 수정",
  stopEditTitle: "진행 중인 작업을 멈추고, 보낸 메시지를 입력창으로 되찾아 수정합니다",
  ctrlEnterToSend: "Ctrl ↵ 전송",
  deleteSession: "삭제",
};

// Module-level current language: App stamps it from the runtime on every render, and a language
// change re-renders the whole tree from App, so children always read the fresh value.
let current: UiLanguage = "en";

export function setUiLanguage(language: string | null | undefined): void {
  current = language === "ko" ? "ko" : "en";
}

export function t<K extends keyof typeof EN>(key: K): string {
  return (current === "ko" ? KO : EN)[key];
}
