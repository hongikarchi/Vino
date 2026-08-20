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
  rescan: "Rescan",
  referencesHeading: "References",
  bakesHeading: "Bakes",
  loadingDataFlow: "Reading references and bakes…",
  writerHoldsDocument: "A writer session holds the document; retry shortly.",
  noReferences: "This definition references no Rhino objects.",
  noBakes: "No stamped bakes from this definition yet.",
  noFamily: "(no family)",
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
  rescan: "재스캔",
  referencesHeading: "참조",
  bakesHeading: "베이크",
  loadingDataFlow: "참조·베이크 읽는 중…",
  writerHoldsDocument: "작성 세션이 문서를 점유 중입니다 — 잠시 후 다시 시도하세요.",
  noReferences: "이 정의는 Rhino 객체를 참조하지 않습니다.",
  noBakes: "이 정의에서 스탬프된 베이크가 아직 없습니다.",
  noFamily: "(패밀리 없음)",
};

// Parameterized strings (counts change Korean word order, so plain keys cannot express them).
export const fmt = {
  asOfRevision: (revision: number | string): string =>
    current === "ko" ? `r${revision} 기준` : `as of r${revision}`,
  brokenReferencesAlert: (count: number): string =>
    current === "ko"
      ? `깨진 참조 ${count}건: 정의가 더 이상 존재하지 않는 Rhino 객체를 가리키고 있어, 해당 컴포넌트는 오류 없이 빈 데이터를 내보냅니다.`
      : `${count} broken reference${count === 1 ? "" : "s"}: a definition points at Rhino objects that no longer exist, so those components emit empty data with no error.`,
  brokenCountSuffix: (count: number): string =>
    current === "ko" ? ` · 깨짐 ${count}` : ` · ${count} broken`,
  objectCount: (count: number): string =>
    current === "ko" ? `객체 ${count}개` : `${count} object${count === 1 ? "" : "s"}`,
  foreignBakes: (count: number): string =>
    current === "ko"
      ? `추적된 베이크 ${count}건은 다른 정의 또는 키가 바뀐 정의 소속입니다 (Save As는 문서 키를 바꿉니다 — 다시 베이크하면 재귀속됩니다).`
      : `${count} tracked bake${count === 1 ? "" : "s"} belong to other or re-keyed definitions (a Save As re-keys the document; re-bake to re-attribute).`,
  unattributedBakes: (count: number): string =>
    current === "ko"
      ? `추적된 베이크 ${count}건은 출처 스탬핑 도입 이전의 것입니다 (미귀속 — 다시 베이크하면 귀속됩니다).`
      : `${count} tracked bake${count === 1 ? "" : "s"} predate provenance stamping (unattributed — re-bake to attribute).`,
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
