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
  jumpToLatest: "Latest",
  claudeWindow: "limit",
  historyCaption: "History",
  historyTooltip: "Restore the canvas to a past verified state (positions, wires, values, captured script sources)",
  historyLoading: "Loading…",
  historyNone: "No revisions yet — they appear as verified jobs commit.",
  restoreHere: "Restore",
  restoreHereTooltip: "Put the canvas back to the state BEFORE this job — one guarded change; anything you edited by hand since then blocks instead of being overwritten",
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
  backendLabel: "Engine",
  signInAnyBody: "Sign in to at least one engine below - the panel unlocks as soon as one is ready.",
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
  // Phase 2: archive browser, graph tooltips, remaining chrome.
  archiveSubtitle: "Read-only archive of every Vino project on this machine",
  closeEsc: "Close (Esc)",
  close: "Close",
  loadingArchive: "Loading the archive…",
  couldNotLoadArchive: "Could not load the archive",
  noArchiveData: "No Vino project data was found on this machine.",
  badgeCurrent: "current",
  badgeUnavailable: "unavailable",
  projectUnreadable: "This project's data could not be read. It may be open in another Rhino instance or damaged.",
  noSessionsRecorded: "No sessions were recorded for this project.",
  deletedLabel: "Deleted",
  selectASession: "Select a session",
  selectASessionHint: "Pick a project on the left, then a session, to read what it did.",
  suffixDeleted: " · deleted",
  suffixCurrent: " · current",
  suffixReadOnly: " · read-only",
  restoreToActiveTitle: "Restore this session to the active list",
  workingEllipsis: "Working…",
  restore: "Restore",
  purgeTitle: "Permanently delete this session and its transcript",
  deleteForever: "Delete forever",
  liveInProject: "Live in this project",
  importTitle:
    "Create a new session in the current project seeded with this conversation. Its component ids and geometry are stale and will be re-discovered before any change.",
  importing: "Importing…",
  importButton: "Import into current project",
  couldNotImport: "Could not import this session into the current project.",
  couldNotLoadTranscript: "Could not load the transcript",
  loadingTranscript: "Loading the transcript…",
  noMessages: "This session has no recorded messages.",
  roleYou: "You",
  roleSystem: "System",
  rescan: "Rescan",
  referencesHeading: "References",
  bakesHeading: "Bakes",
  loadingDataFlow: "Reading references and bakes…",
  writerHoldsDocument: "A writer session holds the document; retry shortly.",
  noReferences: "This definition references no Rhino objects.",
  noBakes: "No stamped bakes from this definition yet.",
  noFamily: "(no family)",
  // Phase 3: session canvas, chat pane leftovers, data view, selection/focus chips, cards.
  // Session canvas.
  dragToReorder: "Drag vertically to change priority.",
  brokerPaused: "Single-writer broker — paused",
  brokerIdle: "Single-writer broker — idle",
  executing: "Executing",
  idleWaitingForJobs: "Idle — waiting for jobs",
  panHintRight: "More to the right — drag to pan, double-click to fit everything.",
  panHintBelow: "More below — drag to pan, double-click to fit everything.",
  canvasEmptyHint: "Create one with the + Session button",
  // Chat pane.
  connectionHeading: "Connection",
  solutionLabel: "Solution",
  chatEmptyHint: "Choose a workstream to view its context and send instructions.",
  attachmentReadFailed: "Could not read an attachment.",
  clickToRename: "Click to rename",
  resumePausedTitle: "Resume this paused session",
  resume: "Resume",
  resuming: "Resuming…",
  restoreView: "Restore view",
  restoreViewTitle: "Restore every object the focus chips hid or locked",
  deleteSessionTitle: "Delete session (recoverable from Deleted)",
  sendingEllipsis: "Sending…",
  collapseWorkLog: "Collapse the work log",
  expandTurnLog: "Expand the work log behind this reply",
  expandWorkLog: "Expand the work log",
  hideEarlierSteps: "Hide earlier steps",
  effortTooltip: "Reasoning effort for this session (used directly; clamped to the model's range).",
  permissionTooltip:
    "Session permission. Review = inspect only · Standard = destructive work asks first · Full-auto = grants are auto-issued (every one is logged).",
  modelPinTooltip: "Pin a Codex model for this session, or Auto to use the catalog default. Effort is set separately.",
  targetTooltip:
    "Bind this session's writes to one Grasshopper document. Unbound sessions must pick a document before submitting changes.",
  autoDefault: "Auto (default)",
  unbound: "Unbound",
  effectiveModelTitle: "Effective model and reasoning",
  routingPending: "Routing pending",
  pausedPlaceholder: "Session is paused — resume it to continue",
  fullAutoChip: "full-auto mode",
  fullAutoChipTitle: "Running without approval cards — every auto-issued grant is logged.",
  haltResumeFailed: "The resume request failed — check the connection and try again.",
  haltedForRecovery: "Halted for recovery",
  showMore: "Show more",
  collapse: "Collapse",
  haltResumeTitle: "Clear the halt and run the session again",
  standingChip: "Auto-approving ×",
  standingChipTitle:
    "The 'keep allowing this kind' consent is on, so destructive work is auto-approved without a card. Click to release it.",
  goalCardRenderError: "The goal card could not be displayed.",
  approvalCardRenderError: "The approval card could not be displayed.",
  askCardRenderError: "The question card could not be displayed.",
  composerAria: "Message Vino",
  attachFilesAria: "Attach files",
  attachTitle:
    "Attach files — images, text, Markdown, JSON, CSV, PDF (no count or size limit). Paste or drop also works.",
  sendAria: "Send instruction",
  issuesAria: "Issues",
  remainingTokensAria: "Remaining codex tokens",
  vinoWorkingAria: "Vino is working",
  themeToggleAria: "Toggle light or dark theme",
  // Goal card + shelf.
  goalCardAria: "Goal confirmation",
  goalHeading: "Goal",
  goalUnderstood: "Here's what I understood — is this right?",
  goalScored: "Scored",
  rejected: "Rejected",
  goalRunning: "Running",
  goalWaiting: "Waiting",
  goalCriteriaLabel: "Success looks like",
  goalAssumptionsLabel: "Assumptions made",
  goalOutOfScopeLabel: "Not in this pass",
  goalApprove: "Proceed as is",
  goalShowOptionTitle: "Show the objects this option points at in the viewport",
  goalCancelEdit: "Cancel editing",
  goalEditApprove: "Edit & approve",
  goalNo: "No",
  goalDismissTitle: "Close the settled goal card — the record stays in the conversation",
  goalDismiss: "Clear goal",
  // Approval card.
  approvalCardAria: "Change approval",
  approvalHeadingAnswered: "Approval result",
  approvalHeadingAsk: "Approve this change?",
  approvalGranted: "Granted",
  approvalExpired: "Grant expired",
  cardDismissTitle: "Close this card — the record stays in the conversation",
  approvalCloseAria: "Close the approval card",
  approvalExpiryNote:
    "The approval key's 15-minute window has passed. If the work is still needed, ask for it again.",
  colorPolicyAria: "Color application",
  colorLabel: "Color",
  colorRecolor: "Paint with material colors",
  colorKeep: "Keep current colors (labels only)",
  colorPreset: "Color preset",
  schemeElementLabel: "Element",
  schemeMaterialLabel: "Material",
  schemeMembersSummary: "Show target layers",
  layerHasCustomTitle: "This layer already has its own color",
  layerHasCustom: "Has custom color",
  layerNameUnchanged: "Names are not changed",
  choicesAria: "Which one to keep",
  itemShowTitle: "Show this item's objects",
  zoomChip: "Zoom",
  roleLabel: "Role",
  impactLabel: "Change",
  targetViewTitle: "Show this target in the Rhino viewport",
  rejectReasonPlaceholder: "Reason for refusing (optional)",
  approveNeedsItemTitle: "Select at least one item to fix",
  approveAndAllow: "Approve + keep allowing",
  approveAndAllowTitle:
    "Grants this approval and, until released, auto-approves the same kind of destructive work in this session without a card.",
  refuse: "Don't do this",
  // Ask card.
  askCardAria: "Question",
  askAnswered: "Answered",
  askHeading: "Confirmation needed",
  askCloseAria: "Close the question card",
  noteLabel: "Note",
  askRecommended: "Recommended",
  askNotePlaceholder: "Note (optional) — Ctrl+Enter accepts the recommended option",
  askNoteAria: "Note (optional)",
  // Selection rail / focus chips.
  railAria: "This message's targets",
  focusModeSelectTitle: "Click: select + zoom only",
  focusModeIsolateTitle: "Click: hide everything else (isolate)",
  notFound: "Not found",
  ghSkipOtherDoc: "canvas is showing another definition — view unchanged",
  ghSkipEditorClosed: "the Grasshopper window is closed",
  ghSkipNoBounds: "no known position — zoom skipped",
  ghSkipZoom: "zoom skipped",
  componentMissing: "component missing",
  notFramed: "not framed",
  // Data view.
  arrowRefsTitle: "Rhino objects referenced by Grasshopper",
  arrowBakesTitle: "Objects baked back into Rhino",
  selectAllGroupTitle:
    "Select and zoom all existing objects in this group, and frame the referencing parameter in Grasshopper",
  selectObjectTitle:
    "Select and zoom this object in Rhino, and frame the referencing parameter in Grasshopper",
  bakeGroupZoomTitle: "Select and zoom this bake group in Rhino",
  bakeGroupFrameSuffix: ", and frame its baking component in Grasshopper",
  missingObjectDeleted: "missing — referenced object was deleted",
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
  jumpToLatest: "최신으로",
  claudeWindow: "한도",
  historyCaption: "히스토리",
  historyTooltip: "검증된 과거 상태로 캔버스를 되돌립니다 (좌표·와이어·값·캐처된 스크립트 소스)",
  historyLoading: "불러오는 중…",
  historyNone: "아직 리비전이 없습니다 — 검증된 작업이 커밋되면 생깁니다.",
  restoreHere: "복원",
  restoreHereTooltip: "이 작업 직전 상태로 되돌립니다 — 보호된 단일 변경; 그 사이 손으로 고친 것은 덮어쓰지 않고 차단됩니다",
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
  backendLabel: "엔진",
  signInAnyBody: "아래 엔진 중 하나에만 로그인하면 패널이 바로 열립니다.",
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
  archiveSubtitle: "이 컴퓨터의 모든 Vino 프로젝트를 읽기 전용으로 보관",
  closeEsc: "닫기 (Esc)",
  close: "닫기",
  loadingArchive: "보관함을 불러오는 중…",
  couldNotLoadArchive: "보관함을 불러오지 못했습니다",
  noArchiveData: "이 컴퓨터에서 Vino 프로젝트 데이터를 찾지 못했습니다.",
  badgeCurrent: "현재",
  badgeUnavailable: "사용 불가",
  projectUnreadable: "이 프로젝트의 데이터를 읽을 수 없습니다. 다른 Rhino 인스턴스에서 열려 있거나 손상됐을 수 있습니다.",
  noSessionsRecorded: "이 프로젝트에 기록된 세션이 없습니다.",
  deletedLabel: "삭제됨",
  selectASession: "세션을 선택하세요",
  selectASessionHint: "왼쪽에서 프로젝트를, 그다음 세션을 고르면 무엇을 했는지 볼 수 있습니다.",
  suffixDeleted: " · 삭제됨",
  suffixCurrent: " · 현재",
  suffixReadOnly: " · 읽기 전용",
  restoreToActiveTitle: "이 세션을 활성 목록으로 복원",
  workingEllipsis: "처리 중…",
  restore: "복원",
  purgeTitle: "이 세션과 대화 기록을 영구 삭제",
  deleteForever: "영구 삭제",
  liveInProject: "이 프로젝트의 활성 세션",
  importTitle:
    "현재 프로젝트에 이 대화를 시드로 새 세션을 만듭니다. 컴포넌트 id와 기하는 낡은 값이라 변경 전에 재탐색됩니다.",
  importing: "가져오는 중…",
  importButton: "현재 프로젝트로 가져오기",
  couldNotImport: "이 세션을 현재 프로젝트로 가져오지 못했습니다.",
  couldNotLoadTranscript: "대화 기록을 불러오지 못했습니다",
  loadingTranscript: "대화 기록을 불러오는 중…",
  noMessages: "이 세션에는 기록된 메시지가 없습니다.",
  roleYou: "나",
  roleSystem: "시스템",
  rescan: "재스캔",
  referencesHeading: "참조",
  bakesHeading: "베이크",
  loadingDataFlow: "참조·베이크 읽는 중…",
  writerHoldsDocument: "작성 세션이 문서를 점유 중입니다 — 잠시 후 다시 시도하세요.",
  noReferences: "이 정의는 Rhino 객체를 참조하지 않습니다.",
  noBakes: "이 정의에서 스탬프된 베이크가 아직 없습니다.",
  noFamily: "(패밀리 없음)",
  dragToReorder: "세로로 드래그하면 우선순위가 바뀝니다.",
  brokerPaused: "단일 작성자 브로커 — 일시정지",
  brokerIdle: "단일 작성자 브로커 — 유휴",
  executing: "실행 중",
  idleWaitingForJobs: "유휴 — 작업 대기 중",
  panHintRight: "오른쪽에 더 있습니다 — 드래그로 이동, 더블클릭으로 전체 보기.",
  panHintBelow: "아래에 더 있습니다 — 드래그로 이동, 더블클릭으로 전체 보기.",
  canvasEmptyHint: "+ 세션 버튼으로 새 세션을 만드세요",
  connectionHeading: "연결",
  solutionLabel: "해결",
  chatEmptyHint: "워크스트림을 고르면 컨텍스트를 확인하고 지시를 보낼 수 있습니다.",
  attachmentReadFailed: "첨부 파일을 읽지 못했습니다.",
  clickToRename: "클릭하면 이름을 바꿀 수 있습니다",
  resumePausedTitle: "일시정지된 이 세션을 재개합니다",
  resume: "재개",
  resuming: "재개 중…",
  restoreView: "보기 복구",
  restoreViewTitle: "포커스 칩이 숨긴/잠근 객체를 전부 복구",
  deleteSessionTitle: "세션 삭제 (삭제됨에서 복구 가능)",
  sendingEllipsis: "전송 중…",
  collapseWorkLog: "작업 로그 접기",
  expandTurnLog: "이 답변까지의 작업 로그 펼치기",
  expandWorkLog: "작업 로그 펼치기",
  hideEarlierSteps: "이전 단계 숨기기",
  effortTooltip: "이 세션의 추론 강도 (그대로 사용되며, 모델의 범위로 클램프됩니다).",
  permissionTooltip:
    "세션 권한. Review = 점검만 · Standard = 파괴적 작업은 먼저 물어봄 · Full-auto = 승인이 자동 발급됨 (전부 기록).",
  modelPinTooltip: "이 세션에 사용할 Codex 모델을 고정하거나, Auto로 카탈로그 기본값을 씁니다. 추론 강도는 별도로 설정합니다.",
  targetTooltip:
    "이 세션의 쓰기를 Grasshopper 문서 하나에 바인딩합니다. 바인딩 없는 세션은 변경 제출 전에 문서를 골라야 합니다.",
  autoDefault: "Auto (기본값)",
  unbound: "바인딩 없음",
  effectiveModelTitle: "실제 적용되는 모델과 추론 강도",
  routingPending: "라우팅 대기 중",
  pausedPlaceholder: "세션이 일시정지됨 — 계속하려면 재개하세요",
  fullAutoChip: "full-auto 모드",
  fullAutoChipTitle: "승인 카드 없이 자동 진행 중입니다 — 자동 발급된 승인은 전부 기록됩니다.",
  haltResumeFailed: "재개 요청이 실패했습니다 — 연결을 확인하고 다시 시도해 주세요.",
  haltedForRecovery: "복구 필요로 정지됨",
  showMore: "더 보기",
  collapse: "접기",
  haltResumeTitle: "정지 상태를 해제하고 세션을 다시 실행합니다",
  standingChip: "자동 승인 중 ×",
  standingChipTitle:
    "'같은 종류 계속 허용' 동의가 켜져 있어 파괴적 작업이 카드 없이 자동 승인됩니다. 클릭하면 해제됩니다.",
  goalCardRenderError: "목표 카드를 표시할 수 없습니다.",
  approvalCardRenderError: "승인 카드를 표시할 수 없습니다.",
  askCardRenderError: "질문 카드를 표시할 수 없습니다.",
  composerAria: "Vino에게 메시지",
  attachFilesAria: "파일 첨부",
  attachTitle:
    "파일 첨부 — 이미지, 텍스트, Markdown, JSON, CSV, PDF (개수·용량 제한 없음). 붙여넣기나 드롭도 됩니다.",
  sendAria: "지시 보내기",
  issuesAria: "문제",
  remainingTokensAria: "남은 codex 토큰",
  vinoWorkingAria: "Vino 작업 중",
  themeToggleAria: "라이트/다크 테마 전환",
  goalCardAria: "목표 확인",
  goalHeading: "목표",
  goalUnderstood: "이렇게 이해했습니다 — 맞나요?",
  goalScored: "채점됨",
  rejected: "거절됨",
  goalRunning: "진행 중",
  goalWaiting: "대기 중",
  goalCriteriaLabel: "이러면 성공",
  goalAssumptionsLabel: "이렇게 가정했습니다",
  goalOutOfScopeLabel: "이번엔 안 합니다",
  goalApprove: "이대로 진행",
  goalShowOptionTitle: "이 선택지가 가리키는 객체를 뷰포트에서 보기",
  goalCancelEdit: "편집 취소",
  goalEditApprove: "고쳐서 승인",
  goalNo: "아니요",
  goalDismissTitle: "정리된 목표 카드를 닫습니다 — 기록은 대화에 남습니다",
  goalDismiss: "목표 해제",
  approvalCardAria: "변경 승인",
  approvalHeadingAnswered: "승인 결과",
  approvalHeadingAsk: "이 변경을 승인하시겠어요?",
  approvalGranted: "승인됨",
  approvalExpired: "승인 만료됨",
  cardDismissTitle: "이 카드를 닫습니다 — 기록은 대화에 남습니다",
  approvalCloseAria: "승인 카드 닫기",
  approvalExpiryNote:
    "승인 키의 유효시간(15분)이 지났습니다. 같은 작업이 여전히 필요하면 다시 요청하도록 말해 주세요.",
  colorPolicyAria: "색 적용",
  colorLabel: "색",
  colorRecolor: "재료 색으로 칠하기",
  colorKeep: "기존 색 유지 (라벨만)",
  colorPreset: "색 프리셋",
  schemeElementLabel: "요소",
  schemeMaterialLabel: "재료",
  schemeMembersSummary: "대상 레이어 보기",
  layerHasCustomTitle: "이 레이어는 이미 색이 지정되어 있습니다",
  layerHasCustom: "기존 색 있음",
  layerNameUnchanged: "이름은 바뀌지 않음",
  choicesAria: "어느 것을 남길까요",
  itemShowTitle: "이 항목의 객체를 보기",
  zoomChip: "확대",
  roleLabel: "역할",
  impactLabel: "변경",
  targetViewTitle: "이 대상을 Rhino 뷰포트에서 보기",
  rejectReasonPlaceholder: "거절 사유 (선택)",
  approveNeedsItemTitle: "고칠 항목을 하나 이상 선택하세요",
  approveAndAllow: "승인 + 계속 허용",
  approveAndAllowTitle:
    "이번 승인과 함께, 이 세션의 같은 종류 파괴적 작업은 카드 없이 자동 승인됩니다 (해제 전까지).",
  refuse: "하지 마세요",
  askCardAria: "질문",
  askAnswered: "답변함",
  askHeading: "확인이 필요합니다",
  askCloseAria: "질문 카드 닫기",
  noteLabel: "메모",
  askRecommended: "추천",
  askNotePlaceholder: "메모 (선택) — Ctrl+Enter로 추천 항목 승인",
  askNoteAria: "메모 (선택)",
  railAria: "이 메시지의 대상",
  focusModeSelectTitle: "클릭: 선택+줌만",
  focusModeIsolateTitle: "클릭: 나머지 숨기고 보기 (isolate)",
  notFound: "찾을 수 없음",
  ghSkipOtherDoc: "캔버스가 다른 정의를 보고 있어 화면은 그대로",
  ghSkipEditorClosed: "Grasshopper 창이 닫혀 있음",
  ghSkipNoBounds: "위치를 알 수 없어 줌 생략",
  ghSkipZoom: "줌 생략",
  componentMissing: "컴포넌트 없음",
  notFramed: "프레이밍 생략",
  arrowRefsTitle: "Grasshopper가 참조하는 Rhino 객체",
  arrowBakesTitle: "Rhino로 다시 베이크된 객체",
  selectAllGroupTitle:
    "이 그룹의 남아 있는 객체를 전부 선택·줌하고, Grasshopper에서 참조 파라미터를 프레이밍합니다",
  selectObjectTitle:
    "이 객체를 Rhino에서 선택·줌하고, Grasshopper에서 참조 파라미터를 프레이밍합니다",
  bakeGroupZoomTitle: "이 베이크 그룹을 Rhino에서 선택·줌",
  bakeGroupFrameSuffix: ", 그리고 베이크한 컴포넌트를 Grasshopper에서 프레이밍",
  missingObjectDeleted: "없음 — 참조된 객체가 삭제됨",
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
  relativeTime: (minutes: number): string => {
    const ko = current === "ko";
    if (minutes < 1) return ko ? "방금 전" : "just now";
    if (minutes < 60) return ko ? `${minutes}분 전` : `${minutes}m ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 24) return ko ? `${hours}시간 전` : `${hours}h ago`;
    const days = Math.round(hours / 24);
    if (days < 30) return ko ? `${days}일 전` : `${days}d ago`;
    const months = Math.round(days / 30);
    if (months < 12) return ko ? `${months}개월 전` : `${months}mo ago`;
    const years = Math.round(months / 12);
    return ko ? `${years}년 전` : `${years}y ago`;
  },
  sessionCount: (n: number): string => (current === "ko" ? `세션 ${n}개` : `${n} session${n === 1 ? "" : "s"}`),
  messageCountMeta: (n: number): string => (current === "ko" ? `메시지 ${n}` : `${n} msg`),
  confirmPurge: (name: string): string =>
    current === "ko"
      ? `"${name}"을(를) 영구 삭제할까요? 되돌릴 수 없습니다.`
      : `Permanently delete "${name}"? This cannot be undone.`,
  haltedTooltip: (message: string): string =>
    current === "ko" ? `복구 필요로 정지됨 — ${message}` : `Halted for recovery — ${message}`,
  // Session-canvas node tooltip lines. Values are server/session data; only the prefixes localize.
  tipNow: (activity: string): string => (current === "ko" ? `현재: ${activity}` : `Now: ${activity}`),
  tipJob: (title: string, phase: string): string =>
    current === "ko" ? `작업: ${title} — ${phase}` : `Job: ${title} — ${phase}`,
  tipModel: (model: string, reasoning?: string | null): string =>
    current === "ko"
      ? `모델: ${model}${reasoning ? ` (${reasoning})` : ""}`
      : `Model: ${model}${reasoning ? ` (${reasoning})` : ""}`,
  tipPinnedModel: (model: string): string => (current === "ko" ? `고정: ${model}` : `Pinned: ${model}`),
  tipBackend: (backend: string): string => (current === "ko" ? `백엔드: ${backend}` : `Backend: ${backend}`),
  installCliButton: (label: string): string =>
    current === "ko" ? `${label} CLI 설치 + 로그인` : `Install ${label} CLI & sign in`,
  signInCliButton: (label: string): string =>
    current === "ko" ? `${label} 로그인` : `Sign in to ${label}`,
  tipTarget: (doc: string): string => (current === "ko" ? `대상: ${doc}` : `Target: ${doc}`),
  tipRouting: (reason: string): string => (current === "ko" ? `라우팅: ${reason}` : `Routing: ${reason}`),
  brokerExecutingFor: (title?: string | null): string =>
    current === "ko"
      ? `단일 작성자 브로커 — ${title ?? "세션"} 작업 실행 중`
      : `Single-writer broker — executing for ${title ?? "a session"}`,
  // Usage status line tooltips (token counts arrive pre-compacted, e.g. "128k").
  ctxTooltip: (used: string, total: string, percent: number): string =>
    current === "ko"
      ? `컨텍스트: ${total} 중 ${used} 토큰 사용 (${percent}%)`
      : `Context: ${used} of ${total} tokens used (${percent}%)`,
  claudeLimitStatus: (status: string): string =>
    current === "ko" ? `구독 한도 상태: ${status}` : `Subscription window status: ${status}`,
  claudeOverage: (status: string): string =>
    current === "ko" ? `초과 사용: ${status}` : `Overage: ${status}`,
  resetsTooltip: (when: string): string =>
    current === "ko" ? `한도 창 리셋: ${when}` : `Window resets: ${when}`,
  resetShort: (time: string): string => (current === "ko" ? `${time} 리셋` : `resets ${time}`),
  rewindOutcome: (moved: number, wires: number, values: number, sources: number, notRestored: number): string =>
    current === "ko"
      ? `복원 완료 — 이동 ${moved} · 와이어 ${wires} · 값 ${values} · 소스 ${sources}` +
        (notRestored > 0 ? ` · 복원 불가 ${notRestored}건(소스 미보관)` : "")
      : `Restored — moved ${moved} · wires ${wires} · values ${values} · sources ${sources}` +
        (notRestored > 0 ? ` · ${notRestored} not restorable (no captured source)` : ""),
  sessionTotalTokens: (total: string): string =>
    current === "ko" ? `세션 누적 ${total} 토큰` : `Session total: ${total} tokens`,
  windowResetFull: (label: string, time: string): string =>
    current === "ko"
      ? `${label} 윈도우가 ${time}에 리셋됨 — 다시 가득 찼습니다.`
      : `${label} window reset ${time} — full again.`,
  windowUsed: (label: string, percent: number, resetsAt?: string): string =>
    current === "ko"
      ? `${label} 윈도우: ${percent}% 사용${resetsAt ? ` · ${resetsAt} 리셋` : ""}`
      : `${label} window: ${percent}% used${resetsAt ? ` · resets ${resetsAt}` : ""}`,
  asOfLastTurn: (time: string): string =>
    current === "ko" ? `마지막 턴 기준 (${time})` : `As of the last turn (${time})`,
  issuesChip: (total: number): string =>
    current === "ko"
      ? `문제 ${total}건 — 클릭하면 상세 정보`
      : `${total} issue${total === 1 ? "" : "s"} — click for details`,
  // Attachments.
  attachmentUnsupported: (name: string): string =>
    current === "ko"
      ? `"${name}"은(는) 지원되지 않는 형식입니다 (이미지, 텍스트, Markdown, JSON, CSV, PDF).`
      : `"${name}" is not a supported type (images, text, Markdown, JSON, CSV, PDF).`,
  attachmentEmpty: (name: string): string =>
    current === "ko" ? `"${name}"이(가) 비어 있습니다.` : `"${name}" is empty.`,
  attachmentReadNamed: (name: string): string =>
    current === "ko" ? `"${name}"을(를) 읽지 못했습니다.` : `Could not read "${name}".`,
  removeAttachment: (name: string): string => (current === "ko" ? `${name} 제거` : `Remove ${name}`),
  // Halt banner / badge.
  haltBadgeTitle: (jobId: string, message: string): string =>
    current === "ko" ? `작업 ${jobId} — ${message}` : `Job ${jobId} — ${message}`,
  haltJobLabel: (jobId: string): string => (current === "ko" ? `작업 ${jobId}` : `Job ${jobId}`),
  haltJobTitle: (jobId: string): string =>
    current === "ko" ? `정지시킨 작업: ${jobId}` : `Job that halted the session: ${jobId}`,
  // Work log.
  stepCount: (n: number): string => (current === "ko" ? `${n}단계` : `${n} step${n === 1 ? "" : "s"}`),
  earlierSteps: (n: number): string =>
    current === "ko" ? `+${n} 이전 단계` : `+${n} earlier step${n === 1 ? "" : "s"}`,
  missingDocument: (id: string): string =>
    current === "ko" ? `없어진 문서 (${id})` : `Missing document (${id})`,
  confirmDeleteSession: (title: string): string =>
    current === "ko"
      ? `"${title}" 세션을 삭제할까요? 삭제됨에서 복구할 수 있습니다.`
      : `Delete session "${title}"? You can restore it from Deleted.`,
  // Data view focus notes.
  selectedNote: (n: number): string => (current === "ko" ? `${n}개 선택됨` : `Selected ${n}`),
  missingSuffix: (n: number): string => (current === "ko" ? ` · ${n}개 없음` : ` · ${n} missing`),
  selectionFailed: (message: string): string =>
    current === "ko" ? `선택 실패: ${message}` : `Selection failed: ${message}`,
  framedCount: (n: number): string => (current === "ko" ? `${n}개 프레이밍` : `framed ${n}`),
  selectAllCount: (n: number): string => (current === "ko" ? `${n}개 모두 선택` : `Select all ${n}`),
  zoomsFirstOf: (shown: number, total: number): string =>
    current === "ko" ? ` — ${total}개 중 처음 ${shown}개만 줌` : ` — zooms first ${shown} of ${total}`,
  // Selection rail (domain is the untranslated product noun "Rhino" / "GH").
  railPinnedTitle: (domain: string, n: number): string =>
    current === "ko"
      ? `${domain} ${n}개가 이 메시지에 고정됨 — 클릭하면 고정 해제 (숫자를 클릭하면 다시 보여줍니다)`
      : `${domain} ${n} pinned to this message — click to unpin (click the count to reveal them again)`,
  railLiveTitle: (domain: string, n: number): string =>
    current === "ko"
      ? `${domain}에서 ${n}개 선택됨 — 클릭하면 이 메시지에 고정`
      : `${n} selected in ${domain} — click to pin to this message`,
  railEmptyTitle: (domain: string): string =>
    current === "ko" ? `${domain}에서 선택된 것이 없습니다` : `Nothing selected in ${domain}`,
  railRevealTitle: (domain: string): string =>
    current === "ko" ? `고정된 ${domain} 대상을 다시 보여주기` : `Show the pinned ${domain} targets again`,
  railRevealAria: (domain: string): string =>
    current === "ko" ? `고정된 ${domain} 대상 보기` : `Reveal the pinned ${domain} targets`,
  // Focus / GH-focus / alt chips.
  focusChipTitle: (n: number): string =>
    current === "ko" ? `${n}개 객체를 뷰포트에서 확인` : `Show ${n} object${n === 1 ? "" : "s"} in the viewport`,
  ghChipTitle: (n: number): string =>
    current === "ko"
      ? `${n}개 컴포넌트를 Grasshopper 캔버스에서 확인`
      : `Show ${n} component${n === 1 ? "" : "s"} on the Grasshopper canvas`,
  ghNotFoundMissing: (n: number): string =>
    current === "ko" ? `찾을 수 없음 — ${n}개가 이 정의에 없습니다` : `Not found — ${n} not in this definition`,
  countSelected: (n: number): string => (current === "ko" ? `${n} 선택` : `${n} selected`),
  countGone: (n: number): string => (current === "ko" ? `${n} 사라짐` : `${n} gone`),
  countHidden: (n: number): string => (current === "ko" ? `${n} 숨김` : `${n} hidden`),
  countLocked: (n: number): string => (current === "ko" ? `${n} 잠금` : `${n} locked`),
  altPreviewTitle: (label: string, n: number): string =>
    current === "ko"
      ? `대안 "${label}"의 미리보기 ${n}개 객체만 뷰포트에 표시`
      : `Show only the ${n} preview object${n === 1 ? "" : "s"} of alternative "${label}" in the viewport`,
  altShowTitle: (altId: string): string =>
    current === "ko" ? `대안 "${altId}"을 뷰포트에서 보기` : `Show alternative "${altId}" in the viewport`,
  componentCount: (count: number): string =>
    current === "ko" ? `컴포넌트 ${count}개` : `${count} component${count === 1 ? "" : "s"}`,
  // Approval card.
  rejectedReasonLine: (reason: string): string =>
    current === "ko" ? `거절 사유: ${reason}` : `Rejected because: ${reason}`,
  underPathAll: (path: string): string =>
    current === "ko" ? ` (${path} 아래 전체)` : ` (everything under ${path})`,
  layerCount: (n: number): string => (current === "ko" ? `레이어 ${n}개` : `${n} layer${n === 1 ? "" : "s"}`),
  currentColorTitle: (hex: string): string => (current === "ko" ? `현재 색 ${hex}` : `Current color ${hex}`),
  proposedColorTitle: (hex: string): string => (current === "ko" ? `제안 색 ${hex}` : `Proposed color ${hex}`),
  approveSelected: (n: number): string =>
    current === "ko" ? `선택한 ${n}개 승인` : `Approve ${n} selected`,
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

// Server ApiError codes → display text. The record layer stays English + coded; the panel
// renders BY CODE at display time and never shows the code itself. Two tiers:
//  - FIXED codes fully replace the server sentence (its English text is static, nothing lost);
//  - PREFIXED codes keep the server detail behind a localized headline, because their message
//    varies and carries real information (exception text, component names).
// Unknown codes return null and the caller falls back to the server's English message.
const API_ERRORS_FIXED: Record<string, { en: string; ko: string }> = {
  unknown_backend: {
    en: "That engine is not available in this build.",
    ko: "이 빌드에서 사용할 수 없는 엔진입니다.",
  },
  model_backend_mismatch: {
    en: "That model belongs to a different engine - this session's engine is fixed.",
    ko: "다른 엔진의 모델입니다 - 세션의 엔진은 생성 시 고정됩니다.",
  },
  session_paused: {
    en: "The session is paused — resume it and try again.",
    ko: "세션이 일시정지 상태입니다 — 재개한 뒤 다시 시도하세요.",
  },
  order_version_conflict: {
    en: "The session order changed elsewhere — try again.",
    ko: "세션 순서가 다른 곳에서 바뀌었습니다 — 다시 시도해 주세요.",
  },
  bridge_timeout: {
    en: "Rhino did not answer in time.",
    ko: "Rhino가 시간 안에 응답하지 않았습니다.",
  },
  session_not_found: {
    en: "That session no longer exists.",
    ko: "세션이 더 이상 존재하지 않습니다.",
  },
  nothing_approved: {
    en: "Approving requires at least one item.",
    ko: "승인하려면 항목을 하나 이상 선택하세요.",
  },
  approval_card_absent: { en: "There is no approval card to answer.", ko: "답할 승인 카드가 없습니다." },
  approval_card_unreadable: {
    en: "The stored approval card could not be read.",
    ko: "저장된 승인 카드를 읽지 못했습니다.",
  },
  approval_card_answered: { en: "This approval was already answered.", ko: "이미 답변된 승인입니다." },
  approval_card_pending: {
    en: "The approval card is still waiting for an answer.",
    ko: "승인 카드가 아직 답변을 기다리고 있습니다.",
  },
  ask_card_absent: { en: "There is no question to answer.", ko: "답할 질문이 없습니다." },
  ask_card_unreadable: { en: "The stored question could not be read.", ko: "저장된 질문을 읽지 못했습니다." },
  ask_card_answered: { en: "This question was already answered.", ko: "이미 답변된 질문입니다." },
  ask_card_pending: {
    en: "The question is still waiting for an answer.",
    ko: "질문이 아직 답변을 기다리고 있습니다.",
  },
  ask_option_unknown: {
    en: "That option is no longer valid — pick one from the card.",
    ko: "해당 선택지는 더 이상 유효하지 않습니다 — 카드의 선택지 중에서 골라 주세요.",
  },
  goal_card_absent: { en: "There is no goal card to answer.", ko: "답할 목표 카드가 없습니다." },
  goal_card_unreadable: { en: "The stored goal card could not be read.", ko: "저장된 목표 카드를 읽지 못했습니다." },
  goal_card_answered: { en: "This goal was already settled.", ko: "이미 정리된 목표입니다." },
  goal_card_pending: {
    en: "The goal card is still waiting for an answer.",
    ko: "목표 카드가 아직 답변을 기다리고 있습니다.",
  },
  loopback_required: {
    en: "Vino only accepts connections from this machine.",
    ko: "Vino는 이 컴퓨터에서의 연결만 받습니다.",
  },
  origin_rejected: {
    en: "This page is not allowed to talk to the Vino runtime.",
    ko: "이 페이지는 Vino 런타임에 접근할 수 없습니다.",
  },
};
const API_ERRORS_PREFIXED: Record<string, { en: string; ko: string }> = {
  invalid_request: { en: "Invalid request", ko: "잘못된 요청" },
  invalid_state: { en: "Not possible right now", ko: "지금은 할 수 없는 작업" },
  not_found: { en: "Not found", ko: "대상을 찾을 수 없음" },
  bridge_error: { en: "Rhino bridge error", ko: "Rhino 브리지 오류" },
  canvas_focus_target: { en: "Canvas focus failed", ko: "캔버스 포커스 실패" },
};

export function apiErrorText(code: string | null | undefined, serverMessage: string | null): string | null {
  if (!code) return null;
  const fixed = API_ERRORS_FIXED[code];
  if (fixed) return current === "ko" ? fixed.ko : fixed.en;
  const prefixed = API_ERRORS_PREFIXED[code];
  if (prefixed) {
    const head = current === "ko" ? prefixed.ko : prefixed.en;
    return serverMessage ? `${head} — ${serverMessage}` : head;
  }
  return null;
}
