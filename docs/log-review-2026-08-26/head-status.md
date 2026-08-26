# HEAD 대조 — 이 문제가 지금 우리 코드에도 남아 있나

대상: `docs/log-review-2026-08-26/issues.md` A01–A20 · B01–B14 **34건**(A99는 기타 묶음이라 제외). 기준: repo HEAD `4f59d97` (2026-08-26 15:28 +0900).

## 1. 왜 다시 봤나

패키지 버전 문자열 `0.1.0-alpha.7`은 고정된 채 그 뒤의 빌드만 계속 재배포된다 — 설치본 DLL(`%APPDATA%\McNeel\Rhinoceros\packages\8.0\Vino\0.1.0-alpha.7\net8.0\`)은 2026-08-26 08:10 자이고, HEAD는 그보다 7시간 뒤인 `4f59d97`(2026-08-26 15:28)이다. 그래서 '알파7 로그에서 보였다'는 그 자체로 '지금도 있다'의 증거가 되지 못한다 — 08-16에 마지막으로 보인 증상이 그 사이 ~60개 커밋 중 하나로 이미 닫혔을 수 있다. 34건을 전부 HEAD 소스로 되짚고, `.log-mine`에서 brand=="Vino"로 마지막 관측일을 재계산해 대조했다. 결과: **완전히 닫힌 것 0건, 일부만 닫힌 것 10건(수정 커밋 18개 특정), 손대지 않은 것 24건**. 그리고 **11건은 관련 수정·완화 커밋보다 뒤에 다시 관측**됐으며, **12건은 설치본 빌드(08-26 08:10) 이후인 08-26 로그에도 살아 있다**. 라벨을 갈아끼우는 대신 얻은 것은 '어느 커밋이 무엇을 실제로 닫았고, 그 수정이 어느 갈래를 비켜갔는가'의 목록이다.

## 2. 요약

| 판정 | 건수 | P0 | P1 | P2 |
|---|---:|---:|---:|---:|
| **present** | 24 | 4 | 14 | 6 |
| **partial** | 10 | 0 | 7 | 3 |
| **fixed** | 0 | 0 | 0 | 0 |
| **cannot-tell** | 0 | 0 | 0 | 0 |
| 합계 | 34 | 4 | 21 | 9 |

- **P0 4건(A19·A20·B05·B06) 전부 `present`** — 하나도 닫히지 않았다. P1 21건 중 14 present / 7 partial, P2 9건 중 6 present / 3 partial.
- 회귀·미커버 플래그 11건: A11, A02, B01, A04, A08, A09, B02, B03, B08, B13, B14
- 설치본 빌드 이후(08-26)까지 관측 12건: A11, A01, A02, A10, A18, B07, A04, A03, B10, A07, A17, B09
- 신뢰도 medium 4건(B01·B11·B12·B14) — 잔여 원인의 상당 부분이 모델 행동이라 소스만으로는 부분 판정.

## 3. 전수 판정

| ID | 제목 | 심각도 | HEAD 판정 | 신뢰 | 마지막 Vino 관측 | 회귀 | HEAD 근거 | 수정 커밋 |
|---|---|---|---|---|---|---|---|---|
| A19 | Compact 경합·failed 세션 재개 불가 | P0 | **present** | high | 2026-08-24 |  | src/Vino.AgentHost/Runtime/SessionOrchestrator.cs:520 — `await CompactThreadIfNearLimitAsync(sessionId, client, threadId!, cancellationToken)` 의 반환값 … |  |
| A20 | auto-tidy가 요청 밖 캔버스를 재배치 | P0 | **present** | high | 2026-08-24 |  | src/Vino.AgentHost/Runtime/CanvasLayout.cs:72,144 — `var scope = ExpandToClusters(seedIds, byId, undirected);` + ExpandToClusters가 무방향 인접을 BFS로 전부 흡수… |  |
| B05 | 승인 없이 캔버스 파손·원복 불가 | P0 | **present** | high | 2026-08-24 |  | assets/instructions/house-rules.md:330-334 — "The canvas tidies itself AUTOMATICALLY after your turn: the server lays the whole connected dataflow cl… |  |
| B06 | 45s 타임아웃 → 상태 불명 정지 | P0 | **present** | high | 미상 |  | src/Vino.AgentHost/Runtime/LiveDocumentBackend.cs:78 — `private static readonly TimeSpan BridgeRequestTimeout = TimeSpan.FromSeconds(45);` 단일 고정 상수. … |  |
| A01 | 수용 술어 오경보가 실패의 67.5% | P1 | **present** | high | 2026-08-26 |  | src/Vino.AgentHost/Runtime/LiveDocumentBackend.cs:3112-3121 — verifying 단계 진입 직후 `CaptureSnapshotAsync(force:true)` → 곧바로 `CollectComponentOutputsAsy… |  |
| A02 | 빈 출력에도 'Verified and committed' | P1 | **present** | high | 2026-08-26 | ⚠ | src/Vino.AgentHost/Runtime/LiveDocumentBackend.ChangeSetValidation.cs:236 — `if (kind is not (OperationKind.CreateComponent or OperationKind.ReplaceC… |  |
| A03 | 제출 계약을 서버 거절로만 알 수 있다 | P1 | **present** | high | 2026-08-26 |  | src/Vino.AgentHost/Runtime/LiveDocumentBackend.OperationValidation.cs:198-212 — `foreach (var property in required) { ... throw new InvalidOperationE… |  |
| A04 | gptino:auto가 첫 쓰기마다 거절 | P1 | **partial** | high | 2026-08-26 | ⚠ | src/Vino.AgentHost/Runtime/LiveDocumentBackend.FingerprintRebase.cs:150-154 — 거절 문구가 HEAD에 그대로 존재: "this session has not written it, so there is no b… | `845c66e` (08-20) |
| A05 | 툴 결과 40K 절단, 리줌 핸들 없음 | P1 | **present** | high | 2026-08-24 |  | src/Vino.AgentHost/Runtime/LiveDocumentBackend.cs:561 — `var used = Utf8JsonLength(response, options) + Utf8JsonLength(inspectionsNode, options);` : … |  |
| A09 | 카드로 끝난 턴 = 어시스턴트 응답 소실 | P1 | **partial** | high | 2026-08-24 | ⚠ | src/Vino.AgentHost/Runtime/SessionOrchestrator.cs:1982-1990 `awaitingUserCard`(SessionIssuedAwaitCardDuring:2479) + `awaitingCaptureDelivery`(af6b016… | `af6b016` (08-24) |
| A10 | 컴파일 안 한 소스에 'Verified' | P1 | **present** | high | 2026-08-26 |  | src/Vino.Grasshopper/GrasshopperPythonFoundationAdapter.cs:190 — 소스 쓰기 경로는 `component.ExpireSolution(recompute: false);` 로 끝난다(주석: "Never recompute t… |  |
| A11 | RR 래치가 확정적 실패도 하드 정지 | P1 | **partial** | high | 2026-08-26 | ⚠ | FIXED: src/Vino.Rhino/RhinoSceneFoundationAdapter.cs:3683-3689 — `if (request.Visible is false && index == document.Layers.CurrentLayerIndex) throw R… | `285068a` (08-24) |
| A12 | C# 컴파일 오류를 'python_error'로 라벨 | P1 | **present** | high | 2026-08-24 |  | src/Vino.ScriptAdapter/ScriptBridgeOperationHandler.cs:241 `$"python_{message.Level.ToString().ToLowerInvariant()}"` — 진단 코드가 컴포넌트 언어와 무관하게 python_ 하… |  |
| A13 | exec에 crypto 없는데 계약은 UUID 요구 | P1 | **present** | high | 2026-08-24 |  | src/Vino.AgentHost/Codex/DynamicToolSpecs.cs:757 — `changeSetId = Uuid(),` (projectId/sessionId/dependencies 동일), :893 `private static object Uuid() … |  |
| A14 | 브로커 solve가 volatile을 못 채움 | P1 | **present** | high | 2026-08-24 |  | src/Vino.Grasshopper/GrasshopperPythonFoundationAdapter.cs:669-679 — `if (request.ExpireUpstream …) foreach (var source in ghComponent.Params.Input.S… |  |
| A16 | 직전에 만든 객체가 pre-write 스냅샷에 없음 | P1 | **present** | high | 2026-08-24 |  | src/Vino.AgentHost/Runtime/LiveDocumentBackend.cs:6810-6814 PreflightWireEndpoint — 스냅샷에 없고 같은 ChangeSet이 만들지도 않으면 그대로 거부, 문구·판정 08-14 이후 무변경(git log… |  |
| A18 | Claude 백엔드 결함 4종 | P1 | **present** | high | 2026-08-26 |  | src/Vino.AgentHost/Mcp/VinoMcpEndpoint.cs:127-136 — tools/list가 name·description·inputSchema만 반환. `_meta["anthropic/maxResultSizeChars"]` 선언 없음 |  |
| B01 | 완료 선언 직후 사용자 교정 | P1 | **partial** | medium | 2026-08-24 | ⚠ | src/Vino.AgentHost/Runtime/SessionOrchestrator.cs:650-659 — 시각 검수 발동 조건에 `PermissionModes.IsFullAuto(latest.PermissionMode)`가 들어 있다: 유인(attended) 세션은… | `795bf8a` (08-18) + `fabfeb7` (08-20) + `2d5cce2` (08-21) + `4c037e1` (08-21) |
| B02 | 캔버스 정리 = 거의 항상 재작업 | P1 | **present** | high | 2026-08-24 | ⚠ | src/Vino.AgentHost/Runtime/LiveDocumentBackend.cs:1926 — `var audit = CanvasLayoutAudit.Measure(snapshot.Canvas, moves.Keys.ToArray());` 는 move 적용 '전… |  |
| B03 | 카드 왕복세 — 포괄 승인 무시 | P1 | **partial** | high | 2026-08-24 | ⚠ | src/Vino.AgentHost/Runtime/StandingApprovals.cs:11-21 — 세션 전역 상시 승인이 존재("Scope is the whole session (not per operation kind)"); src/Vino.AgentHost/Co… | `cc47b79` (08-14) + `99832a3` (08-18) |
| B04 | recompute 떠넘기기 + Solver 오진 | P1 | **present** | high | 2026-08-24 |  | assets/instructions/house-rules.md (415줄) — solver/recompute 언급은 26·175·188·193·219-220줄뿐이며 전부 무관(스크립트 작성·recomputeDocument 비용). 빈 출력을 사용자 GH 상태로 귀속하… |  |
| B07 | 도구 부재 — 사용자가 직접 해야 함 | P1 | **partial** | high | 2026-08-26 |  | 미해결(a) — src/Vino.AgentHost/Codex/DynamicToolSpecs.cs:806-815 kind enum에 Value List 항목 쓰기 없음; LiveDocumentBackend.OperationValidation.cs:68 `Operatio… | `fa5a3cc` (07-27) + `55aee1d` (07-23) |
| B08 | 턴 결과가 통째로 사라짐 | P1 | **partial** | high | 2026-08-24 | ⚠ | src/Vino.AgentHost/Runtime/SessionOrchestrator.cs:1982-1990 — `awaitingUserCard`/`awaitingCaptureDelivery`가 카드로 끝난 턴·캡처 파킹을 정상 종료로 처리(오탐 경로 수정). 그러나 … | `646e439` (08-11) + `af6b016` (08-24) |
| B09 | 세션↔GH 문서 바인딩 상실 | P1 | **present** | high | 2026-08-26 |  | src/Vino.AgentHost/Runtime/LiveDocumentBackend.cs:7262-7305 `ResolveSessionTargetState`/`ResolveTargetStateByDocKey` — GH 문서가 2개 이상이고 바인딩이 null이면 "… … |  |
| B11 | 합의·지정 입력을 무시하고 재해석 | P1 | **present** | medium | 2026-08-24 |  | src/Vino.AgentHost/Runtime/SessionOrchestrator.cs:1188-1234 `ComposeGoalBlock` — 매 턴 주입되는 것은 goal 카드 1장의 Objective/Criteria/OutOfScope/ChosenOption뿐.… |  |
| A06 | 툴 결과 봉투 shape가 제각각 | P2 | **present** | high | 2026-08-24 |  | src/Vino.AgentHost/Codex/CodexAppServerClient.cs:1635 `public static DynamicToolResult Fail(string message) => new(false, message);` — 성공만 JsonSerial… |  |
| A07 | 10초 넘는 툴 호출이 셀로 분리됨 | P2 | **present** | high | 2026-08-26 |  | src/Vino.AgentHost/Runtime/LiveDocumentBackend.cs:83-84 — `SubmitWaitDeadline = TimeSpan.FromSeconds(25); SubmitWaitCap = TimeSpan.FromSeconds(15);` … |  |
| A08 | 자기 solve가 자기 베이스라인을 깬다 | P2 | **partial** | high | 2026-08-24 | ⚠ | src/Vino.ScriptAdapter/PythonComponentFingerprint.cs:27 — `var authored = state with { RuntimeMessages = Array.Empty<ComponentRuntimeMessage>() };` R… | `c017201` (08-20) |
| A15 | AgentHost 수명이 Rhino에 묶임 | P2 | **present** | high | 2026-08-24 |  | src/Vino.AgentHost/Hosting/ParentProcessMonitor.cs:29-36 `Process.GetProcessById` ArgumentException → LogWarning + `_lifetime.StopApplication()` — 기동… |  |
| A17 | full-auto 통지를 오류 채널로 반환 | P2 | **partial** | high | 2026-08-26 |  | src/Vino.AgentHost/Runtime/FullAutoContinuation.cs:23,54-61 `MaxNudgesPerThread = 3` + TryConsumeNudge의 AddOrUpdate 카운터 — 스레드 수명 전체 3회 상한(카드 넛지 카운터는 … | `4376d85` (08-18) + `af6b016` (08-24) |
| B10 | 요청 없는 소켓 증식·회수 불가 | P2 | **present** | high | 2026-08-26 |  | src/Vino.AgentHost/Runtime/LiveDocumentBackend.cs:4873-4914 PreflightPythonSchemas — `if (requestedInputs < component.Inputs.Count \|\| requestedOutp… |  |
| B12 | 과장·포장된 설명 | P2 | **present** | medium | 2026-08-25 |  | assets/instructions/house-rules.md:46,47-51,52 — 반(反)날조 규칙은 있음("never … claim a count no tool reported", scannedObjects 0 처리, 측정값·단위 그대로 보고). 그러나 서술의… |  |
| B13 | 디버그 scaffold를 치우지 않음 | P2 | **present** | high | 2026-08-24 | ⚠ | assets/instructions/house-rules.md:335-339 "Separate SCAFFOLD from PRODUCT …" — 규범은 HEAD에 출하돼 있음(4c037e1, 2026-08-21). 그러나 이를 강제하는 서버 게이트·수락 술어는 없음 |  |
| B14 | 오래 걸려 사용자가 확인·포기 | P2 | **partial** | medium | 2026-08-24 | ⚠ | src/Vino.AgentHost/Runtime/LiveDocumentBackend.cs:297 `private const int SnapshotReadByteCap = 256 * 1024;`(599/624/651에서 강제) 및 :7975 `ProjectedDiagn… | `7d9252b` (08-21) |

> 회귀(⚠) = 관련 수정·완화 커밋이 출하된 **뒤에** 같은 증상이 다시 관측된 항목. 수정 커밋 칸이 빈 A02·B02·B13은 규범·감사 같은 완화만 있었고 그마저 통하지 않았다는 뜻이다. B06은 로그 시그니처가 브릿지 타임아웃 코드로 남지 않아 마지막 관측일 미상.

## 4. 고쳐진 것 — partial 10건

`fixed` **0건**. 아래 10건은 지목된 하위 결함 중 일부만 닫혔다.

**A04 gptino:auto가 첫 쓰기마다 거절** — 845c66e 2026-08-20 Audit recommendations 1-3: named stale conflicts, safe auto-fills, recovered baselines (원장 3층 시딩 자체는 76ac6d2 / 2026-08-08부터 존재)

- 마지막 Vino 관측 **2026-08-26**. 845c66e(08-20)로 read/wire/execute/recovered 갈래는 auto-fill로 흡수(113건이 그 증거). 08-26 잔여 3건은 '이 세션이 이 문서에서 쓴 적 없는 리소스' 갈래이고, 근인 서술('커밋이 원장을 안 시딩한다')은 HEAD에서 반증됨.

**A09 카드로 끝난 턴 = 어시스턴트 응답 소실** — af6b016 2026-08-24 08:54 +0900 Defect batch 1: capture-park is a settled ending, budgets re-arm on progress

- 마지막 Vino 관측 **2026-08-24**. af6b016(08-24 08:54 KST)은 capture-park 분기만 정착시킴. 08-24 04:22Z·05:50Z 2건은 full-auto 자동해소 카드 갈래로 커밋이 덮지 않은 경로.

**A11 RR 래치가 확정적 실패도 하드 정지** — 285068a 2026-08-24 Defect batch 2: layer visibility on Rhino's real semantics, canvas capture fails clean

- 마지막 Vino 관측 **2026-08-26**. 285068a(08-24)가 레이어 가시성 갈래를 쓰기 전 거절로 닫음. 남은 read-back 불일치·브릿지 타임아웃은 미커버이고, 마지막 관측 08-26은 그 미커버 갈래 — 회귀가 아니라 미완 수정.

**B01 완료 선언 직후 사용자 교정** — 795bf8a 2026-08-18 rhino_view_capture: the viewport as model feedback (preview Tier 3, v22) / fabfeb7 2026-08-20 Full-auto quality loop: capture-delivery budget + fresh-eyes visual review / 2d5cce2 2026-08-21 Close the capture-inspect loop everywhere: mode-free delivery, view-resilient review / 4c037e1 2026-08-21 General quality pair: scaffold-vs-product rule + checklist-driven visual review

- 마지막 Vino 관측 **2026-08-24**. 795bf8a~4c037e1(08-18~08-21)로 캡처 툴과 시각 검수가 들어옴. 그러나 검수는 full-auto 한정이라 유인 세션은 무방비 — 08-24 교정 사례는 그 미커버 경로.

**B03 카드 왕복세 — 포괄 승인 무시** — cc47b79 2026-08-14 Permission ladder: review / standard / full-auto + standing consent (보강 99832a3 2026-08-18 Auto-granted approvals persist as granted cards)

- 마지막 Vino 관측 **2026-08-24**. cc47b79(08-14)+99832a3(08-18)로 세션 전역 상시 승인은 실재(이슈의 "승격 경로 없음" 주장은 반증). 산문 승인 번역·카드 발행 예산은 미수정이라 08-24 카드 왕복이 그 뒤에 재발.

**B07 도구 부재 — 사용자가 직접 해야 함** — fa5a3cc 2026-07-27 Reliability round: reference-op schema fixes, semantic predicates, delete/resolver/UI (+ 55aee1d 2026-07-23 Grasshopper canvas selection context — the deferred roadmap item, wired end to end)

- 마지막 Vino 관측 **2026-08-26**. fa5a3cc(07-27)가 컴포넌트 이동 미반영을, 55aee1d(07-23)가 선택 컨텍스트 주입을 닫음(이동 불평 이후 0건). 나머지 3종(Value List 항목 쓰기·script 소스 페이징·샌드박스 루트)은 미수정 → 08-26 재관측은 미커버 갈래.

**B08 턴 결과가 통째로 사라짐** — 646e439 2026-08-11 Stop treating a card-ending turn as a lost response; af6b016 2026-08-24 Defect batch 1: capture-park is a settled ending

- 마지막 Vino 관측 **2026-08-24**. 646e439(08-11)·af6b016(08-24)로 카드/캡처 파킹 오탐은 닫혔고 재수집 폴백도 실재. 압축 경합(A19)은 미수정 → 08-24 4건은 그 미커버 갈래.

**A08 자기 solve가 자기 베이스라인을 깬다** — c017201 2026-08-20 Script fingerprint hashes authored state only; one-time ledger re-baseline

- 마지막 Vino 관측 **2026-08-24**. c017201(08-20)이 RuntimeMessages·레이아웃 접힘을 제거. 08-24 잔여 9잡은 그룹 멤버십 접힘·배치 내 self-stale로, 정확히 c017201이 손대지 않은 갈래 → 미완 수정.

**A17 full-auto 통지를 오류 채널로 반환** — 4376d85 2026-08-18 11:57 +0900 Full-auto continuation nudge: parked auto-resolved turns get a follow-up (+ af6b016 2026-08-24 캡처 예산 분리)

- 마지막 Vino 관측 **2026-08-26**. 4376d85(08-18)가 스레드당 넛지 3회 상한을 넣어 폭주는 닫힘. 오류 채널(Success=false) 사용은 미수정이고 MCP가 그대로 isError로 뒤집어 Claude 백엔드에도 전파 — 08-26 관측.

**B14 오래 걸려 사용자가 확인·포기** — 7d9252b 2026-08-21 snapshot_read v3: meta-first orientation, id-addressed detail, capped responses (보강 139e45a diagnostics 50행 캡, 9ec0ed1 inspect_outputs opt-in)

- 마지막 Vino 관측 **2026-08-24**. 7d9252b·139e45a·9ec0ed1(08-21~)로 페이로드 폭주는 캡으로 닫힘. effort 기본 xhigh(K116)와 카드·recompute 왕복은 그대로 → 08-24에 >300s 29턴·최장 5,713s.

요약: 10건 전부 '관측일이 수정 이전이라 당연한 것'이 아니다 — 모두 수정 커밋보다 **뒤에** 재관측됐고, 그 재관측은 예외 없이 해당 커밋이 덮지 않은 갈래에서 나왔다. 고쳐진 갈래가 되살아난 진짜 회귀는 0건.

## 5. 판정 불가

`cannot-tell` **0건**. 34건 모두 HEAD 소스에서 기제를 확인하거나 반증할 수 있었다. 다만 아래 4건은 판정은 섰으되 잔여 원인 일부가 소스 밖(모델 행동·GH 런타임·codex CLI)에 있어 신뢰도를 medium으로 낮췄다.

| ID | 소스로 확정한 것 | 소스 밖에 남은 것 | 무엇이 있으면 끝나나 |
|---|---|---|---|
| B01 | 유인 세션엔 완료 선언 전 시각 확인 강제 단계 없음(SessionOrchestrator.cs:650-659 full-auto 게이트) | 모델이 자발적으로 캡처를 볼지 | 유인 세션에도 시각 검수를 켠 뒤 '완료 선언 → 다음 턴 교정' 쌍 발생률 A/B |
| B11 | 결정 원장·상류 provenance 게이트 부재 | 합의를 잊는 것이 컨텍스트 문제인지 지시 문제인지 | 결정 원장을 턴 입력에 주입한 상태에서 동일 과제 재현 |
| B12 | 과장·비중을 규율하는 문장이 house-rules.md에 0건 | 규범을 넣으면 실제로 줄어드는지 | 비중 규범 추가 후 '포장했다' 사용자 시그널 빈도 비교 |
| B14 | effort 기본 xhigh 하드코딩 4곳 미변경(K116) | 지연의 어느 몫이 추론이고 어느 몫이 왕복인지 | effort medium/xhigh A/B + 턴당 툴콜 왕복 수 동시 계측 |

추가로 A14('왜 GH가 volatile을 못 채우나'), A18-(2)(3중 제출 근인), A07·A13의 codex 하네스 쪽 부수 증상은 리포 밖이라 소스로는 '수정이 들어온 적 없다'까지만 확정했다. 라이브 GH 계측(NewSolution 전후 DataCount)과 codex CLI 측 로그가 있어야 근인이 닫힌다.

## 6. 결론

Top 10(A20·A19·B06·A01·A02·A14·A05·A04·A03·A13) 중 지금도 살아 있는 것은 **10건 전부** — 9건은 손댄 흔적이 없는 `present`이고, A04만 `partial`(auto-fill 갈래는 닫혔으나 거절 경로는 08-26에도 발화). P0 4건도 하나도 닫히지 않았다.
