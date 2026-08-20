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
  // Phase 1: badges, toasts, notifications, boot/login screens, global errors.
  statusWorking: "Working",
  statusDrafting: "Drafting",
  statusQueued: "Queued",
  statusVerifying: "Verifying",
  statusPaused: "Paused",
  statusBlocked: "Blocked",
  statusIdle: "Idle",
  toastNeedsAttention: "Needs attention",
  toastNeedsInput: "Needs your input",
  toastFinished: "Finished",
  toastOpenSession: "Open this session",
  attachingToRhino: "Attaching to Rhino",
  loadingRuntime: "Loading the active document runtime…",
  notConnected: "Vino is not connected",
  notConnectedHint: "Open a saved Rhino and Grasshopper file, then attach this panel.",
  retryConnection: "Retry connection",
  cliMissingTitle: "Codex CLI is not installed",
  signInTitle: "Sign in with ChatGPT",
  cliMissingBody:
    "Vino drives the OpenAI Codex CLI. The terminal installs it with npm (needs Node.js), then signs you in.",
  signInBody:
    "Vino needs a signed-in Codex CLI to run sessions. The terminal runs 'codex login' — finish the browser sign-in there.",
  installCodexButton: "Install Codex & log in",
  loginButton: "Log in with ChatGPT",
  loginUnlockHint: "This screen unlocks automatically once you're signed in.",
  themeToLight: "Switch to light mode",
  themeToDark: "Switch to dark mode",
  sessionExpiredBefore:
    "This panel does not hold the running Vino runtime's credential — every request is rejected. Close the panel in Rhino and reopen it with ",
  sessionExpiredAfter: ".",
  retry: "Retry",
  executorPaused: "Executor paused — active transaction will stop at its next safe boundary.",
  resumeAll: "Resume all",
  demoChip: "Demo",
  sessionNameLabel: "Session name",
  ghDocumentLabel: "Grasshopper document",
  createSession: "Create session",
  phasePlanning: "Planning the work",
  phaseReading: "Reading the canvas",
  phaseDrafting: "Writing the ChangeSet",
  phaseVerifying: "Verifying",
  phaseTidying: "Tidying the canvas",
  phaseTrouble: "Recovering from a problem",
  errorItemFallback: "This item could not be displayed.",
  agenthostUnavailableDemo: "AgentHost is unavailable — showing demo data.",
  unableToConnect: "Unable to connect to Vino",
  actionFailed: "The Vino action failed",
  panelTokenExpired:
    "The panel session expired (its token is not this runtime's). Close the panel and reopen it with VinoOpenPanel.",
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
  statusWorking: "작업 중",
  statusDrafting: "시작 중",
  statusQueued: "대기 중",
  statusVerifying: "검증 중",
  statusPaused: "일시정지",
  statusBlocked: "차단됨",
  statusIdle: "유휴",
  toastNeedsAttention: "확인 필요",
  toastNeedsInput: "입력 필요",
  toastFinished: "완료",
  toastOpenSession: "이 세션 열기",
  attachingToRhino: "Rhino에 연결 중",
  loadingRuntime: "활성 문서 런타임을 불러오는 중…",
  notConnected: "Vino가 연결되지 않았습니다",
  notConnectedHint: "저장된 Rhino·Grasshopper 파일을 연 뒤 이 패널을 다시 연결하세요.",
  retryConnection: "다시 연결",
  cliMissingTitle: "Codex CLI가 설치되어 있지 않습니다",
  signInTitle: "ChatGPT로 로그인하세요",
  cliMissingBody:
    "Vino는 OpenAI Codex CLI를 구동합니다. 터미널이 npm으로 설치(Node.js 필요)한 뒤 로그인까지 진행합니다.",
  signInBody:
    "세션을 돌리려면 로그인된 Codex CLI가 필요합니다. 터미널이 'codex login'을 실행하니 브라우저 로그인을 마쳐 주세요.",
  installCodexButton: "Codex 설치 후 로그인",
  loginButton: "ChatGPT로 로그인",
  loginUnlockHint: "로그인이 끝나면 이 화면은 자동으로 풀립니다.",
  themeToLight: "라이트 모드로 전환",
  themeToDark: "다크 모드로 전환",
  sessionExpiredBefore:
    "이 패널은 지금 실행 중인 Vino 런타임의 자격증명을 갖고 있지 않습니다 — 요청이 전부 거부됩니다. Rhino에서 패널을 닫고 ",
  sessionExpiredAfter: "로 다시 열어 주세요.",
  retry: "다시 시도",
  executorPaused: "실행기 일시정지 — 진행 중인 트랜잭션은 다음 안전 지점에서 멈춥니다.",
  resumeAll: "모두 재개",
  demoChip: "데모",
  sessionNameLabel: "세션 이름",
  ghDocumentLabel: "Grasshopper 문서",
  createSession: "세션 만들기",
  phasePlanning: "작업 계획 중",
  phaseReading: "캔버스 읽는 중",
  phaseDrafting: "ChangeSet 작성 중",
  phaseVerifying: "검증 중",
  phaseTidying: "캔버스 정리 중",
  phaseTrouble: "문제 수습 중",
  errorItemFallback: "이 항목을 표시할 수 없습니다.",
  agenthostUnavailableDemo: "AgentHost에 연결할 수 없어 데모 데이터를 표시합니다.",
  unableToConnect: "Vino에 연결할 수 없습니다",
  actionFailed: "Vino 동작이 실패했습니다",
  panelTokenExpired:
    "패널 세션이 만료됐습니다 (이 런타임의 토큰이 아닙니다). 패널을 닫았다가 VinoOpenPanel로 다시 열면 복구됩니다.",
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
  osNotifTitle: (sessionTitle: string, kind: "finished" | "input" | "attention"): string => {
    const suffix =
      current === "ko"
        ? kind === "finished" ? "완료" : kind === "input" ? "입력 필요" : "확인 필요"
        : kind === "finished" ? "finished" : kind === "input" ? "needs your input" : "needs attention";
    return `${sessionTitle} — ${suffix}`;
  },
  suggestedSessionName: (n: number): string => (current === "ko" ? `세션 ${n}` : `Session ${n}`),
  haltedTooltip: (message: string): string =>
    current === "ko" ? `복구 필요로 정지됨 — ${message}` : `Halted for recovery — ${message}`,
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
