# 수렴 단계 전수 감사 — 2026-08-12

감사 결과, 현재 HEAD 2cb3cc54fa0f에는 “선언만 있고 디스패치가 없는” dynamic tool/op은 없다. 대신 완성도를 해치는 핵심은 다음 네 부류였다.

- 신규 확정 결함: layer-scheme 승인 트랜잭션 순서, 3개 op의 strict preflight 누락, ensureRhinoLayer 계약 불일치, focus를 read로 위장한 live write, bake zoom 결과 유실
- 알려진 결함의 부분수정 잔존: 카드 답변 bool 유실, ask dismiss server-only, W7-c/d/e/f/h
- production dead/future-only 구조: 파일 가족 4개, 죽은 멤버·상태·라우팅 계층, 불필요한 인터페이스
- 실제로 갈라진 중복 판단: operation metadata, Codex 실행파일 resolver, focus restore ownership

코드·문서 수정, 빌드, 테스트, 프로세스/라이브 실행 없이 정적 감사만 수행했다.

> **검증 상태 (2026-08-12, Claude 교차검증):** 본 감사의 CONFIRMED 주장 중 실행 가능성이 높은 약 40건(카드 lifecycle 6, 계약 C1~C8, dead code 18, 패널/focus/resolver 8)을 코드와 전수 대조했다. **반박 0건 — 감사 판정 전량 유지.** 정정 2건(C2 GUID 절반, focus 경합 직렬화)과 실제가 감사보다 더 나쁜 4건은 본문에 `[검증 …]` 주석으로 표시했고, 종합·우선순위는 말미 "검증 부록" 참조.

## 감사 기준과 범위

기준선은 docs/capability-integrity-2026-08-11.md:186의 5620cef, 현재는 2cb3cc54fa0f다.

요청에 적힌 “fix-plan의 Phase 3”은 실제로는 docs/roadmap-2026-08-11.md:103에 있다. 해당 Phase 3의 W7-c~k, 고아·파사드 청소, W11-c/d/e를 감사 대상으로 해석했다.

검색 범위:

- tracked 파일 419개 전부
- C# production 139개, test 106개
- panel ts/tsx 41개 및 main.tsx 기준 import graph
- Program.cs의 route 48개 전부
- OperationKind 34개, dynamic tool 23개 전부
- assets, scripts, tools, 비-archive docs의 exact-name/route/op 역참조
- rg가 없어 git grep, git ls-files, Select-String 사용
- bin/, obj/, dist/, node_modules/, artifacts/, .references/는 생성물/vendor라 caller 판정에서 제외

판정 의미:

- CONFIRMED: 현재 저장소 안에서 정적으로 끊김·불일치·무참조를 입증
- SUSPECT: 외부 ABI, reflection, 실제 Rhino/WebView 상태가 필요
- LIVE_REQUIRED: 정적 연결 여부까지만 판단, 실제 종단 결과는 등급을 올리지 않음
- “테스트 0”은 그 자체로 기능 결함 판정이 아님

---

# 1. 발화하지 않거나 반만 연결된 기능

## 1.1 Dynamic tool 23개 전수

공통 선언·스키마는 src/GPTino.AgentHost/Codex/DynamicToolSpecs.cs:60, exact dispatch는 src/GPTino.AgentHost/Codex/DynamicToolDispatcher.cs:179다.

G는 전용 카드가 아니라 generic tool result가 모델로 반환되고 activity log에 렌더된다는 뜻이다.

| Tool | 선언 / dispatch | 규범 | 렌더·client | 직접 dispatcher test | 판정 |
|---|---:|---|---|---|---|
| snapshot_read | 69 / 181 | ✓ | G | 있음 | WIRED |
| component_catalog | 87 / 183 | ✓ | G | 있음 | WIRED |
| rhino_list | 101 / 185 | ✓ | G | 있음 | WIRED |
| rhino_audit | 121 / 191 | ✓ | G | 있음 | WIRED |
| structural_extract | 166 / 195 | ✓ | G | 있음 | WIRED |
| structural_solve | 192 / 197 | ✓ | G | 있음 | WIRED |
| layer_scheme_draft | 245 / 193 | ✓ | ApprovalCard | 있음 | WIRED, 답변 결함 별도 |
| rhino_layers | 260 / 199 | ✓ | G | 0 | WIRED/UNTESTED |
| data_flow_read | 275 / 189 | ✓ | G | 0 | WIRED/UNTESTED |
| inspect_outputs | 291 / 187 | ✓ | G | 0 | WIRED/UNTESTED |
| artifact_read | 308 / 201 | ✓ | G | 있음 | WIRED |
| artifact_write | 318 / 202 | ✓ | G | 있음 | WIRED |
| change_submit | 334 / 203 | ✓ | G/job | 있음 | WIRED |
| arrange_layout | 364 / 204 | ✓ | G/job | 있음 | WIRED |
| job_status | 393 / 205 | ✓ | G | 0 | WIRED/UNTESTED |
| recovery_resume | 409 / 207 | ✓ | G | 있음 | WIRED |
| skill_read | 429 / 209 | ✓ | G | 0 | WIRED/UNTESTED |
| goal_propose | 444 / 211 | ✓ | GoalCard | 0 | WIRED/UNTESTED |
| ask_user | 506 / 215 | △ | AskCard | 0 | SUSPECT/LIVE_REQUIRED |
| approval_request | 548 / 217 | ✓ | ApprovalCard | 있음 | WIRED |
| goal_score | 671 / 213 | ✓ | GoalCard | 0 | WIRED/UNTESTED |
| memory_append | 702 / 210 | tool 설명만 | G | 0 | WIRED/UNTESTED |
| data_read | 718 / 219 | ✓ | G | 0 | WIRED/UNTESTED |

발견:

- [KNOWN/PARTIAL·SUSPECT] ask_user는 여전히 house-rules.md/payload-guide.md exact 언급이 0이고 경쟁 prose 지시가 assets/instructions/house-rules.md:93에 있다. 다만 tool description 자체가 카드 사용을 강제하므로 “현재도 절대 발화하지 않는다”고 확정할 수 없다. 기준선 docs/capability-integrity-2026-08-11.md:48-54,67.
- [RESOLVED] 과거 부재했던 ComposeAsk 재주입은 src/GPTino.AgentHost/Runtime/SessionOrchestrator.cs:814-877에 구현됐다.
- [NEW·INFO] activity summary switch가 카드 관련 5종을 누락한다. layer_scheme_draft, goal_propose, ask_user, approval_request, goal_score는 work log에 raw snake_case로 표시된다: DynamicToolDispatcher.cs:313-338.
- 직접 dispatcher test 0인 10개는 기능 결함으로 승격하지 않았다. W11-a/b 계열 하네스 공백은 그대로다: docs/fix-plan-2026-08-11.md:264-279.

## 1.2 OperationKind 34개 전수

src/GPTino.Contracts/Changes.cs:13-60의 34종 중:

- 모델 노출 30종은 schema → 규범 → bridge mapping → handler까지 모두 존재
- Rename, SetSolverState, UpdateRhinoLayer, DocumentGlobal 4종은 의도적 schema-hidden/fail-closed 예약값

30종 목록:

Read, MoveComponent, ConnectWire, DisconnectWire, SetValue, UpdatePythonSource, SetComponentIo, ReplaceComponentIo, ConvertSocket, CreateComponent, DeleteComponent, SetLayout, CreateRhinoObject, ModifyRhinoObject, DeleteRhinoObject, BakeGeometry, UpdateRhinoAttributes, SetGroup, ExecutePython, ReadRuntimeMessages, CreateRhinoPrimitive, TransformRhinoObject, ReferenceRhinoObjects, FixRhinoEndpointPair, PurgeTableEntries, MoveObjectsToLayer, UpdateRhinoLayerProperties, DeleteRhinoLayer, SaveRhinoLayerState, EnsureRhinoLayer.

공통 표면:

- schema: DynamicToolSpecs.cs:785-809
- 규범: assets/instructions/payload-guide.md:4-28, docs/operation-contract.md:133-157
- mapping: LiveDocumentBackend.OperationValidation.cs:63-99
- handlers: Canvas CanvasSceneBridgeOperationHandlers.cs:24-36, Script ScriptBridgeOperationHandler.cs:26-32, Rhino CanvasSceneBridgeOperationHandlers.cs:204-224
- 결과: 공통 change_submit/job result 경로

exact bridge-op 테스트 문자열이 0인 것은 다음 6개다.

- python.runtimeMessages
- rhino.inspect
- rhino.purgeTableEntries
- rhino.moveObjectsToLayer
- rhino.deleteLayer
- rhino.layerState

이는 UNTESTED/LIVE_REQUIRED이며, 기준선 docs/capability-integrity-2026-08-11.md:117-122 범위와 겹친다.

v18 ReplaceComponentIo와 server-owned deferSolve는 정적으로는 전 표면이 연결됐다. 다만 docs/session-2026-08-11-evening.md:83-89에 따르면 라이브 종단은 W2에 막혔고 설치 DLL도 safe subset으로 되돌린 상태가 마지막 기록이다. 현재 설치 상태는 LIVE_REQUIRED다.

## 1.3 Endpoint 48개 전수

src/GPTino.AgentHost/Program.cs:255 이하의 Map* 48개를 모두 역추적했다.

| 분류 | 수 | 결과 |
|---|---:|---|
| root panel bootstrap | 2 | 둘 다 caller 있음 |
| panel client/useRuntime/App caller | 33 | 전부 연결 |
| Terminal/scripts/LiveE2E caller | 11 | 전부 연결 |
| repo code caller 0 | 2 | ORPHAN |

연결된 root 2개:

- POST /panel/bootstrap Program.cs:255
- GET /panel Program.cs:275

panel 경로 33개:

- runtime/events :320,323
- data-flow/focus/canvas-focus/selection/language :339,354,371,397,430,433
- approval PUT/DELETE :445,769
- ask PUT :700
- goal PUT/DELETE :952,819
- resume :845
- session create/order/pause/retract/target/title/model :860,876,888,900,910,921,932
- delete/deleted/restore/purge :1019,1036,1041,1054
- messages POST/terminal :1077,1084
- runtime pause/login/models :1096,1111,1121
- archive 3종 :1124,1127,1135

panel 외 caller 11개:

- GET /layers :346 → scripts/gate-layer-curation.ps1:93,159,198
- GET messages :1069 → Terminal, smoke, LiveE2E
- health :1156
- dev 8개 :1172,1181,1191,1214,1231,1241,1258,1262

고아 2개:

1. [KNOWN→CURRENT ORPHAN·CONFIRMED] DELETE /sessions/{id}/ask, Program.cs:794-814

   서버 route만 생겼다. client.ts, mock, useRuntime, AskCard에는 dismiss가 0이다. 기준선 capability-integrity:112의 “route 부재”가 “server-only”로 이동했을 뿐이다.

2. [KNOWN·CONFIRMED] POST /runtime/stop-current, Program.cs:1104-1109

   README 외 코드·테스트 caller 0이다. underlying StopCurrentAsync도 이 route 한 곳만 호출한다. 기준선 capability-integrity:113.

GET /layers, GET messages는 기준선과 달리 현재는 전역 고아가 아니다.

실제 HTTP route integration harness(WebApplicationFactory/TestServer)는 저장소 전체 0이다. 따라서 caller 존재와 별개로 48개 실제 HTTP 종단은 LIVE_REQUIRED다. W8 fix-plan:214-221 그대로다.

## 1.4 카드 lifecycle 결함

세 카드 모두 생산→projection→parse/render→PUT answer까지는 연결된다.

- Goal: DynamicToolDispatcher.cs:813-857 → RuntimeStateProjector.cs:77 → ChatPane.tsx:614-621,1122-1135 → client.ts:377-385
- Approval: DynamicToolDispatcher.cs:1410-1420 → projector :78 → ChatPane.tsx:623-630,1137-1153 → client :272-283
- Ask: DynamicToolDispatcher.cs:1210-1250 → projector :81 → ChatPane.tsx:632-639,1155-1164 → client :287-291

하지만 다음이 남았다.

1. [NEW·CONFIRMED] layer-scheme 저장 실패가 카드를 먼저 settle함

   Program.cs:541-553에서 TryWriteScheme 결과를 받은 뒤 성공 여부를 확인하기 전에 card를 granted로 저장·publish한다. 실패 시 500이지만 재시도는 이미 answered라 Program.cs:468-475에서 409다. 규칙은 저장되지 않고 카드만 완료되는 회복 불가 상태다.

   [검증 CONFIRMED — 직접 확인] `stored`를 :541에서 받아놓고 :542-548에서 카드 granted 저장·publish가 먼저, `!stored` 검사는 :549-554로 뒤다. **P0 권고** — 수정은 순서 교환으로 단순하다.

2. [KNOWN/PARTIAL·CONFIRMED] layer-scheme 승인 delivery bool 유실

   Program.cs:555-563이 DeliverCardAnswerAsync bool을 버린다. layer scheme에는 grant가 없어 ComposeApprovalBlock도 SessionOrchestrator.cs:740-743에서 거르므로 paused/turn-start 실패 시 답이 영구 유실된다. 기준선 capability-integrity:109-110, W11-f fix-plan:279.

   [검증 CONFIRMED+정밀화] scheme 규칙 자체는 TryWriteScheme으로 이미 저장되며 유실되는 건 에이전트 전달 메시지뿐이다. SubmitMessageAsync는 AppendMessageOnceAsync 전에 SessionPausedException을 던지므로(SessionOrchestrator.cs:155-158) 실패한 delivery는 아무것도 영속하지 않는다. grant가 있는 승인 경로(Program.cs:693)도 bool을 버리지만 그쪽은 ComposeApprovalBlock 백업이 있다. 거절(:491-495)·ask(:756-759) 분기는 bool을 처리해 DeliveryPending을 세우는 것과 대조적.

3. [KNOWN/PARTIAL·CONFIRMED] rejected goal delivery bool 유실

   Program.cs:997-1004가 bool을 버리고, ComposeGoalBlock은 confirmed만 허용한다(SessionOrchestrator.cs:678-680). 즉 거절 답변의 즉시 delivery 실패는 복구되지 않는다.

   [검증 CONFIRMED·악화] GoalCard 레코드에는 DeliveryPending 필드 자체가 없다(ApiModels.cs:40-50; Approval :123·Ask :154에만 존재). ClearPendingCardDeliveriesAsync도 Ask+Approval만 다룬다(SessionOrchestrator.cs:837-853). goal에는 복구 메커니즘이 아예 없으므로 수정은 필드 추가부터다.

4. [KNOWN→ORPHAN·CONFIRMED] answered AskCard dismiss 없음

   DELETE route는 생겼지만 client/UI가 없다. answered 카드가 계속 렌더된다.

5. [NEW·SUSPECT] 카드 JSON parse 실패가 조용히 null 처리됨

   ChatPane.tsx:612-639에서 parse 실패를 null로 바꿔 ErrorBoundary에도 도달하지 않는다. 실제 corrupt/version-skew 카드 존재 여부는 LIVE_REQUIRED다.

   [검증 CONFIRMED] :612-613 주석이 의도된 drop임을 밝히지만("malformed card is dropped rather than crashing the stream"), 사용자 가시 신호가 전혀 없다는 사실은 그대로다.

## 1.5 Panel component 27개 전수

tracked production TSX 선언 27개 모두 import/render caller가 있다. orphan component는 0이다.

- App.tsx: StatusChip:36, NewSessionPopover:76, App:138
- ChatPane.tsx: UsageMeter:230, UsageStatusLine:248, ProblemIndicator:298, HaltBanner:377, ChatPane:425
- SessionCanvas.tsx: Wire:95, SessionNode:122, OrchestratorNode:188, DocNode:229, SessionCanvas:275
- 파일 component: AltChip, ApprovalCard, ArchiveBrowser, AskCard, DataView, ErrorBoundary, FocusChip, GhFocusChip, GoalCard, Icon, NoGrasshopper, SelectionRail, StatusBadge, ToastStack

직접 component render test는 ApprovalCard 하나뿐이다(approvalCard.test.tsx:97-159). 나머지 26개는 UNTESTED, 고아는 아니다.

최신 v18 패널 3종:

- [NEW·HALF_WIRED/CONFIRMED] bake zoom: endpoint/client unwrap은 연결됐지만 DataView.tsx:14-16,260-269가 callback을 void로 만들고, App.tsx:540-548도 Promise/result를 버린다. 실패, selectedCount, missingCount가 전혀 표시되지 않는다.

  [검증 CONFIRMED+정밀화] client.ts:308-313 unwrap과 useRuntime.ts:211-212 전달은 정상이고, 유실 지점은 정확히 두 곳이다: App.tsx:547의 `void actions.focusObjects(...)`가 FocusResult와 rejection을 모두 버리고(실패는 unhandled promise rejection이 됨), components/DataView.tsx:16의 prop 타입이 void다. 채팅 chip 경로는 같은 FocusResult로 "N 선택 · N 사라짐"을 이미 렌더하므로(useFocusTarget) 수정 시 재사용 가능.
- waiting notification: static WIRED, OS WebView 알림은 LIVE_REQUIRED.
- goal dismiss: route/client/mock/useRuntime/GoalCard까지 static WIRED, HTTP/render 종단은 LIVE_REQUIRED.

---

# 2. 죽은 코드·옵션·파일·asset

## 2.1 Production-dead 파일·타입 가족

| 판정 | 위치 | 검색 근거 | 권고·영향 |
|---|---|---|---|
| CONFIRMED·NEW | src/GPTino.Contracts/ProblemDossier.cs:3-28, src/GPTino.Core/IdempotencyStore.cs:6-63 | production caller 0, store는 tests만 | 삭제. runtime idempotency는 별도 durable string-key 경로 |
| CONFIRMED·NEW | src/GPTino.Core/SessionOrderBook.cs:8-164, Sessions.cs:21-40 | production 생성 0, tests만. 실 CAS는 SessionStore | 삭제. SessionOrderSnapshot/live enum은 유지 |
| CONFIRMED·NEW | src/GPTino.History/ProjectHomeLayout.cs:7-78 | Resolve caller 0, fingerprint는 무관한 test만 | 삭제. 현재 data-root/history 영향 없음 |
| CONFIRMED·NEW | DynamicToolDispatcher.cs:59-115 DisconnectedDocumentBackend | production DI/생성 0, test 1곳만 | production 삭제 또는 test fake로 이동 |
| CONFIRMED·KNOWN | ApiModels.cs:333-346 | RuntimeStatus/HostStateResponse 인스턴스화 0 | 삭제. 기준선 capability-integrity:103 |
| CONFIRMED·NEW | ModelRoutingException.cs:1-8, SessionOrchestrator.cs:350-361, EffectiveModelState.cs:37-46 | throw site 0. ModelSelector가 catalog 예외를 먼저 흡수 | exception/catch/failure projection 삭제·축소 |

## 2.2 호출·생산 0 멤버와 상태

| 판정 | 위치 | 근거 | 권고 |
|---|---|---|---|
| CONFIRMED·NEW | AuthoringLatencyTrace.cs:34-43 TryTurn | repo 전체 호출 0 | 삭제; turn-boundary 주석도 축소 |
| CONFIRMED·NEW | Changes.cs:203-208 JobExecutionResult.Succeeded | 읽기 0 | 삭제 |
| CONFIRMED·NEW | DocumentRegistrationLedger.cs:59-67 Outstanding | production 0, tests만 | 삭제 또는 test helper 이동 |
| CONFIRMED·NEW | DocumentRuntimeTargeting.cs:44-53 NextGeneration | production 0, tests만 | test helper로 이동 |
| CONFIRMED·NEW | MaterialPalette.cs:29-63,170-186 | VariantStopsL/VariantArgb production 0, tests만 | variant 일반화 축소 |
| CONFIRMED·NEW | Sessions.cs:3-14 | Drafting producer 0; WaitingForDependency, Completed는 scheduler 소비 분기만 | enum/scheduler arm 축소 |
| CONFIRMED·KNOWN | Routing.cs:3-24 | ModelProfile.Standard, TaskClass.StandardWrite만 생산 | 라우팅 잔재 평탄화 |
| CONFIRMED·KNOWN | Changes.cs:107,112,129 | OutputEquals, BoundingBoxEquals, Custom은 validation/verifier 모두 fail-closed | enum에서 분리 또는 legacy converter와 함께 축소 |
| SUSPECT·NEW | GptinoPanel.cs:73 DocumentSerial | repo 참조 0이나 public plugin ABI | 외부 소비 확인 뒤 삭제 |

명시적 test seam인 SeedResourceLedgerForTests, SeedTurnCreatedComponents, SimulateCompletionReassertForTestAsync는 dead로 올리지 않았다. Rhino/Grasshopper reflection discovery 타입도 제외했다.

[검증 18/18 CONFIRMED] 2.1·2.2·2.3 판정 전량 유지. 삭제 착수 시 주의 4건: ① SessionOrderSnapshot(Sessions.cs:16-19)은 production 사용 중(LiveDocumentBackend·SingleWriterBroker·ReadyWorkScheduler) — 감사의 유지 판단과 일치. ② AuthoringLatencyTrace 클래스는 TryToolCall(DynamicToolDispatcher.cs:225,233)로 살아있고 TryTurn만 죽음. ③ variantStopsL은 팔레트 JSON 문서에서 여전히 파싱되므로(MaterialPalette.cs:186) 코드 삭제 시 JSON 스키마 동반 정리 필요. ④ CodexTurn 4옵션은 production 코드 경로는 살아있고(항상 컴파일 기본값) 외부 설정 가능성만 부재 — dead code가 아니라 dead knob.

## 2.3 Operationally inert 옵션·파사드

1. [KNOWN·CONFIRMED] CodexTurn 4옵션

   AgentHostOptions.cs:34-40은 SessionOrchestrator.cs:92-95에서 사용되지만 AgentHostArguments.cs:45-67 Parse에 없다. 외부 knob을 약속하지 않는다면 상수/test 주입으로 축소해야 한다. 기준선 capability-integrity:88, W11-d.

2. [NEW·CONFIRMED] RuntimeIdentity와 --project-directory

   RuntimeIdentity AgentHostOptions.cs:99-104의 5필드 중 production에서 읽히는 것은 ProjectId 하나뿐이다(RuntimeStateProjector.cs:140). 이미 AgentHostOptions.ProjectId가 주입된다.

   ProjectDirectory는 AgentHostArguments.cs:22-24,48에서 parse하고 Program.cs:66-71의 죽은 identity 필드로 전달하는 것 외 production read가 0이다. --project-directory와 record 전체를 삭제·축소할 수 있다. 영향은 bootstrapper, Program, projector ctor, smoke/tests다.

기준선의 GoalTokenBudget, SetGoalEnabledAsync, goal_enabled는 현재 코드에서 제거되어 해소됐다.

## 2.4 Import/file/asset 결과

- panel production orphan source: 0
- unreachable 9개는 test 8개와 정상 ambient vite-env.d.ts
- C# semantic-dead 파일은 위 4가족과 동일
- reference-zero raster asset:
  - assets/icons/gptino-16.png
  - assets/icons/gptino-32.png
  - assets/icons/gptino-64.png
  - assets/icons/gptino-128.png

실제 소비는 24/48/256뿐이다. docs/brand/icon-brief.md:119-136도 나머지를 spare로 설명한다. 삭제 시 runtime/package 영향은 없고 수동 교체용 크기별 파일만 사라진다.

basename caller 0인 연구용 standalone script 8개는 직접 실행 entrypoint일 수 있어 SUSPECT로 유지했다:

add-torsion-warping.py, alt-solutions.py, audit-axes-quality.py, audit-axes.py, inspect-3dm.py, map-focus-targets.py, codex-goal-probe.ps1, w2-probe.py.

---

# 3. Operation 계약 5자 대조

공통 원천:

- S(server required): src/GPTino.AgentHost/Runtime/LiveDocumentBackend.OperationValidation.cs:121-188
- R(adapter records): ICanvasAdapter.cs:286-353, IScriptDocumentAdapter.cs:79-132, IRhinoSceneAdapter.cs:175-259,339-390,540-598
- D: docs/operation-contract.md:131-157
- A: assets/instructions/payload-guide.md:4-28
- F: src/GPTino.AgentHost/Codex/DynamicToolSpecs.cs:14-57

A와 F는 raw-string 들여쓰기까지 제거해 비교했으며 13,813자가 완전 동일하다. †는 server-owned 필드다.

| Kind | Bridge | S required | R | D | A | F | 판정 |
|---|---|---|---|---|---|---|---|
| Read | owner별 inspect | Canvas/Rhino objectId, Script componentId | 동일 | :133 ✓ | :28 ✓ | =A | ALIGNED |
| MoveComponent | canvas.move | operationId,pivots,expectedFingerprints | 동일 | :134 ✓ | :4 ✓ | =A | ALIGNED |
| SetLayout | canvas.move | 위와 동일 | 동일 | :134 ✓ | :4 ✓ | =A | ALIGNED |
| ConnectWire | canvas.setWire | operationId,wire,action,rejectCycles | 동일 + deferSolve† | :136 ✓ | :6 ✓ | =A | ALIGNED |
| DisconnectWire | canvas.setWire | 위와 동일 | 동일 + deferSolve† | :136 ✓ | :6 ✓ | =A | ALIGNED |
| SetValue | canvas.setNumberSlider | 7필드 | 동일 | :135 ✓ | :5 ✓ | =A | ALIGNED |
| UpdatePythonSource | python.setSource | 6필드 | 동일 | :141 ✓ | :11 ✓ | =A | ALIGNED |
| SetComponentIo | python.setSchema | operationId,componentId,inputs,outputs,preserveIncidentWires | 동일, nested superset | :142 ✓ | :12 ✓ | =A | ALIGNED |
| ReplaceComponentIo | python.replaceSchema | 6 required + source?,socketMap? | 동일 | :143 ✓ | :13 ✓ | =A | 필드 정합, strict 누락 |
| ConvertSocket | python.setTyping | 5필드 | 동일 | :144 ✓ | :14 ✓ | =A | ALIGNED |
| CreateComponent | canvas.create | operationId,objectId,componentTypeId,pivot,resultOutput | 동일 + nickName?; result nullable | :137 resultOutput 누락 | :7 ✓ | =A | MISMATCH |
| DeleteComponent | canvas.delete | operationId,objectId,expectedFingerprint | 동일 | :139 ✓ | :9 ✓ | =A | ALIGNED |
| CreateRhinoObject | rhino.upsert | 7필드, fingerprint null 허용 | 동일 + sourceDocKey†,approved† | :149 ✓ | :19 ✓ | =A | ALIGNED |
| BakeGeometry | rhino.upsert | 위 create 계약 | 동일 + server-owned | :149 ✓ | :40 ✓ | =A | ALIGNED |
| ModifyRhinoObject | rhino.upsert | 7필드, fingerprint non-null | 동일 + server-owned | :149 ✓ | :19 ✓ | =A | ALIGNED |
| UpdateRhinoAttributes | rhino.upsert | 위 modify 계약 | 동일 + server-owned | :149 ✓ | :19 ✓ | =A | ALIGNED |
| DeleteRhinoObject | rhino.delete | 3필드 | 동일 + approved† | :150 ✓ | :20 ✓ | =A | ALIGNED |
| SetGroup | canvas.setGroup | 5필드 | 동일 | :140 ✓ | :10 ✓ | =A | 필드 정합, predicate 불일치 |
| ExecutePython | python.execute | 4필드 | 동일 | :145 ✓ | :15 ✓ | =A | ALIGNED |
| ReadRuntimeMessages | python.runtimeMessages | componentId | 동일 | :146 ✓ | :16 ✓ | =A | ALIGNED |
| CreateRhinoPrimitive | rhino.createPrimitive | 4필드 + kind별 definition | 동일 + sourceDocKey† | :147 ✓ | :17 ✓ | =A | ALIGNED |
| TransformRhinoObject | rhino.transform | 4필드 | 동일 + approved† | :148 ✓ | :18 ✓ | =A | ALIGNED |
| ReferenceRhinoObjects | canvas.referenceRhinoObjects | 5필드 | 동일 + nickName? | :138 ✓ | :8 ✓ | =A | 필드 정합, strict 누락 |
| FixRhinoEndpointPair | rhino.fixEndpointPair | 8필드 | 동일 + approved† | :151 ✓ | :21 ✓ | =A | ALIGNED |
| PurgeTableEntries | rhino.purgeTableEntries | operationId,entries | 동일 | :153 ✓ | :23 ✓ | =A | ALIGNED |
| MoveObjectsToLayer | rhino.moveObjectsToLayer | operationId,items,targetLayerId | 동일 + approved† | :154 ✓ | :24 ✓ | =A | 필드 정합, approval 규범 누락 |
| UpdateRhinoLayerProperties | rhino.updateLayer | 3 required + 5 optional | 동일 | :155 ✓ | :25 ✓ | =A | ALIGNED |
| DeleteRhinoLayer | rhino.deleteLayer | 3필드 | 동일 | :156 ✓ | :26 ✓ | =A | ALIGNED |
| SaveRhinoLayerState | rhino.layerState | operationId,action,name | 동일 | :157 ✓ | :27 ✓ | =A | 필드 정합, 이름 불일치 |
| EnsureRhinoLayer | rhino.ensureLayer | operationId,layerId,fullPath | + non-null argbColor, parentLayerId? | :152 optional | :22 optional | =A | MISMATCH |

## 3.1 계약 불일치 상세

### C1. createComponent 문서 한 면만 구형

[NEW·CONFIRMED]

- S: LiveDocumentBackend.OperationValidation.cs:130-133
- R: ICanvasAdapter.cs:286-298
- A/F: payload-guide.md:7, DynamicToolSpecs.cs:21
- D만 누락: docs/operation-contract.md:137

문서의 exact payload를 따르면 resultOutput missing으로 submit 거부된다.

### C2. mapped op 3종 strict preflight 누락

[NEW·CONFIRMED]

ResolveBridgeOperation LiveDocumentBackend.OperationValidation.cs:63-99와 ValidateDeserializableArguments :245-427을 기계 대조한 결과 정확히 3종이 빠진다.

- canvas.referenceRhinoObjects
- python.replaceSchema
- rhino.ensureLayer

BridgeProtocol.cs:64가 UnmappedMemberHandling.Disallow이므로 unknown/ill-shaped argument가 submit을 통과한 뒤 bridge 실행에서 죽는다. python.replaceSchema는 GuidArguments:1293-1308에서도 누락돼 componentId/newComponentId GUID 형식 검사가 미뤄진다.

[검증 PARTIAL 정정] 누락 3종 집합은 mapped 27종 ↔ strict 24종 기계 재대조로 정확히 일치(스위치에 default 없음 → fall-through). 단 GUID 부분은 절반만 맞다: replaceSchema의 componentId는 RejectInterleavedPythonFingerprintSequences가 submit 시점에 D-format으로 검사한다(ChangeSetValidation.cs:777). 실제로 검사를 피하는 건 **newComponentId뿐**이다(BuildResultOutputPredicate는 파싱 실패 시 조용히 null 반환, ChangeSetValidation.cs:249-253).

최종 job state가 Failed인지 RecoveryRequired인지는 LIVE_REQUIRED다.

### C3. ensureRhinoLayer 세 계약 충돌

[NEW·CONFIRMED]

- server와 docs/assets는 argbColor를 optional로 봄
- record는 non-null int: IRhinoSceneAdapter.cs:567-572
- adapter는 항상 색을 대입: RhinoSceneFoundationAdapter.cs:2482-2488

생략 시 STJ 기본값 0이 들어가 기존/신규 layer 색을 덮는다. 실제 Rhino 표시만 LIVE_REQUIRED다.

[검증 CONFIRMED 전항] BridgeProtocol.JsonOptions가 RespectRequiredConstructorParameters를 설정하지 않아 생략 시 실제로 0이 대입되고, 기존 레이어를 clone한 뒤 무조건 `layer.Color = FromArgb(request.ArgbColor)`를 대입하므로(RhinoSceneFoundationAdapter.cs:2482-2487) 기존 색이 ARGB 0(투명 검정)으로 덮인다.

추가로 resource alignment의 ensure case는 검사 없이 return한다: OperationValidation.cs:1084-1088. 주석은 “path-derived id, adapter returns real id”라 하지만 실제 계약은 caller가 layerId를 선택하고 adapter도 이를 강제한다(RhinoSceneFoundationAdapter.cs:2489-2512).

### C4. “모든 Rhino mutation = rhinoObject” 요약 충돌

[NEW·CONFIRMED]

payload-guide.md:33/fallback DynamicToolSpecs.cs:47은 모든 Rhino mutation이 rhinoObject라고 요약한다. 같은 문서의 per-op 표는 purge=table, layer op=rhinoLayer, state=rhinoLayerTable이라고 올바르게 적는다(payload-guide.md:22-27). 요약을 따르면 submit resource mismatch가 난다.

### C5. absent-create 허용 목록 복제본 불일치

[NEW·CONFIRMED]

- 올바른 계약: operation-contract.md:17-22
- 올바른 per-op asset: payload-guide.md:8,22
- stale 요약: payload-guide.md:39가 reference/ensure 누락
- stale server 오류 메시지: ChangeSetValidation.cs:472-482
- 실제 구현은 둘 다 지원: ChangeSetValidation.cs:977-1024

기능보다 오류 메시지·규범이 좁아 잘못된 remediation을 유도한다.

### C6. setGroup create 기본 predicate 누락

[NEW·CONFIRMED]

D/A/F는 create면 objectExists가 기본이라고 명시한다(operation-contract.md:52-56, payload-guide.md:41). 하지만 구현 switch ChangeSetValidation.cs:157-173은 SetGroup을 누락한다. helper는 GrasshopperGroup을 지원한다(:261-269).

새 group은 objectExists 대신 runtimeErrorAbsent만 자동 부착된다. 실제 false-success 빈도는 LIVE_REQUIRED다.

### C7. moveObjectsToLayer approval 규범 누락

[NEW·CONFIRMED]

server approval injection에는 포함된다(LiveDocumentBackend.cs:599-607). adapter도 user-owned item마다 approval을 요구한다(RhinoSceneFoundationAdapter.cs:4001-4042). 그러나 payload-guide.md:42의 approval 요약에는 move가 없다.

### C8. referenceRhinoObjects.autoUpstream 숨은 옵션

[NEW·CONFIRMED 도달 / SUSPECT 의도]

문서에는 없지만 CanvasAutoPlacement.cs:56-66,368-395가 reference에도 autoUpstream을 읽고 dispatch 전에 제거한다(:431-444). 의도된 옵션인지 accidental reachability인지는 정적으로 확정할 수 없다. 유지한다면 문서화, 아니면 reference에서 축소해야 한다.

[검증 CONFIRMED·악화] 도달만 하는 게 아니라 실제로 읽혀 배치에 사용된다 — IsSentinelCreate가 canvas.referenceRhinoObjects를 수용한다(CanvasAutoPlacement.cs:374-375). 코드 주석 :372-373("referenceRhinoObjects has no autoUpstream")은 **거짓**이다. 추가 엣지: explicit pivot+autoUpstream 조합을 canvas.create는 거부하지만(OperationValidation.cs:461-467) reference에는 그 검사가 없어 submit을 통과한 뒤 adapter에서 unmapped-member(Disallow)로 죽는다.

### C9. predicate 문서가 여전히 6종이라고 주장

[KNOWN·CONFIRMED]

operation-contract.md:180-182는 6종 지원이라고 적지만 현재 schema/validation/verifier는 12종이다. W2-c fix-plan:114-115가 미완이다.

semantic 5종은 현재 house rules 규범이 추가돼 기준선의 “규범 부재 고아”는 부분 해소됐다. 다만 실제 dispatch/use test는 0이므로 UNTESTED/LIVE_REQUIRED다.

### C10. 서버 전용 카드 timestamp 잔존

[KNOWN/PARTIAL·CONFIRMED]

- server Goal: ApiModels.cs:49-50
- server Ask: ApiModels.cs:150-151
- TS Goal: types.ts:118-127에 없음
- TS Ask: types.ts:163-171에 없음

Approval proposedAt은 TS에 추가됐지만 Goal/Ask는 여전히 비대칭이다. 기준선 capability-integrity:101.

---

# 4. 중복·충돌 로직

| 판정 | 중복 판단 | 근거 | 권고·영향 |
|---|---|---|---|
| CONFIRMED | Operation metadata 8곳 이상 | enum, schema, mapping, required, strict, resource alignment, create/predicate, D/A/F. C1~C7이 실제 drift | 한 metadata 표로 축소. validation/schema/docs coverage 영향 |
| CONFIRMED | Codex executable resolver 4중 | production CodexExecutableResolver.cs:15-75, LiveE2E :370-420, DevLoop :1034-1080, smoke :83-161 | 공유/축소. DevLoop·smoke는 여전히 sandbox를 PATH보다 우선 |
| CONFIRMED | focus restore owner 2곳 | useFocusTarget.ts:67-75와 ChatPane.tsx:448-459, server stack은 doc당 하나 | owner를 하나로 축소. chip unmount가 다른 chip 상태 복원 가능 |
| SUSPECT drift risk | SDK C# source detector 2벌 | LiveDocumentBackend.cs:5007-5206, GrasshopperPythonFoundationAdapter.cs:939-1111; 현재 동일 | prewrite/backstop은 유지, 순수 parser만 공유 |
| SUSPECT drift risk | Script GUID·identifier·out 규칙 | executor :4176-4180,4083-4093,5595-5647 ↔ adapter :22-28,1170-1204,1229-1285 | 양 gate 유지, 상수/순수 판정만 축소 |
| SUSPECT drift risk | failure-code 문자열 | executor LiveDocumentBackend.cs:576-597; 각 adapter 상수 | BridgeContract 상수로 축소. drift 시 Failed/RecoveryRequired 분류 변동 |
| SUSPECT drift risk | wire resource ID 포맷 | OperationValidation.cs:918-947, ChangeSetValidation.cs:1004-1019, snapshot LiveDocumentBackend.cs:3648-3654 | formatter/parser 공유 또는 parity test |
| SUSPECT | GPTINO env scrub 4곳 | Bootstrapper, Codex client, Terminal, smoke | 경계별 backstop 유지 + 동일 corpus test |
| SUSPECT | projects root literal | AgentHostOptions.cs:73-76, ProjectArchiveReader.cs:35-43 | helper로 축소. drift 시 archive 목록 단절 |
| CONFIRMED intentional | asset + compiled fallback | parity test와 정적 비교 모두 exact | 유지. asset 손상 시 fallback 필요 |
| CONFIRMED low | LeafOf 구현 2개 | LayerNameAnalyzer.cs:219-224, LayerScheme.cs:208-213 | 작은 path helper로 축소; 영향 두 클래스뿐 |
| CONFIRMED | protocol version 문서 충돌 | 코드 v18 BridgeProtocol.cs:42-52, release-checklist.md:65,183은 v16→v17 | stale release 지시 삭제/수정 |

Codex resolver의 현재 충돌은 실질적이다. production/LiveE2E는 PATH/npm을 sandbox보다 우선하지만 DevLoop와 standalone smoke는 sandbox를 먼저 고른다. docs/session-2026-08-11-evening.md:59-63에 기록된 “구식 sandbox 우선” 결함을 LiveE2E만 고친 상태다.

[검증 CONFIRMED — resolver 4곳·우선순위 충돌] production CodexExecutableResolver.cs:30-76(sandbox 최후순위, 강등 사유 주석 :20-26), LiveE2E tools/GPTino.LiveE2E/Program.cs:370-421(정렬됨), DevLoop tools/GPTino.DevLoop/Program.cs:1042-1050(**sandbox 최우선**), smoke scripts/smoke-agenthost.ps1:145-152(**sandbox 최우선**). 추가 drift: PATH와 무관한 roaming 전역 npm 위치는 production만 안다.

[검증 보강 — focus restore 발화자는 3곳] useFocusTarget cleanup을 쓰는 chip 4종(FocusChip·ApprovalCard·GoalCard·AltChip) + ChatPane 자체 cleanup(ChatPane.tsx:452-460) + header "Restore view" 버튼(:956-966). 구체 interleave: chip A isolate → chip B isolate(서버 스택은 B로 교체) → A unmount 시 A의 스테일 isolatingRef가 restore를 쏴 B의 스택을 pop한다. FocusChip.tsx:9-11 주석("restore policy belongs to the owner")과 useFocusTarget:70-75의 실제 동작이 서로 모순.

## Focus read/write 계약 충돌

[NEW·CONFIRMED 계약 불일치 / LIVE_REQUIRED 경합]

- /focus는 Hide/Lock/Show/Unlock live write 수행: RhinoSceneFoundationAdapter.cs:3243-3301
- backend는 ReadBridgeQueryAsync 사용: LiveDocumentBackend.cs:720-730
- handler는 Read access와 changed:false 반환: CanvasSceneBridgeOperationHandlers.cs:286-304
- _focusStack은 plain Dictionary: RhinoSceneFoundationAdapter.cs:3226-3229

이는 AGENTS의 “reads may run concurrently against immutable snapshots; live writes through single-writer” 계약과 어긋난다. 실제 동시 손상은 LIVE_REQUIRED다.

[검증 확인 + 경합 강등] 계약 위반 본체(read lease 하에서 Hide/Lock 영구 변이·changed:false·fingerprint 미반영)는 확정이며, 핸들러 주석이 이를 알면서 정당화하고 있다(CanvasSceneBridgeOperationHandlers.cs:291-294 "Isolate/lock do write visibility attributes"). 단 _focusStack 경합은 현재 실행 불가: 플러그인이 모든 bridge op를 RhinoApp.InvokeOnUiThread로 마샬링하고(GptinoRuntimeHost.cs:995-1008 → RhinoUiThreadDispatcher.cs:22) focus 코어가 await 없이 동기 완료라 UI 펌프에서 직렬화된다. 디스패치 경로가 바뀌면 되살아나는 아키텍처 리스크로 유지, LIVE_REQUIRED에서는 제외.

---

# 5. 네이밍 감사

| 판정 | 이름 | 실제 동작 | 권고·영향 |
|---|---|---|---|
| CONFIRMED: 제거 대상 없음 | Wireify/Cordyceps | production occurrence는 attribution, legacy converter, MCP deny-list뿐 | 유지. converter 삭제 시 stored job 단절, attribution 삭제 시 라이선스 문제 |
| CONFIRMED | python.*, Python*, GrasshopperPythonFoundationAdapter | C#, CPython3, IronPython2 전부 처리 | wire alias는 호환성 때문에 유지, 내부 타입부터 Script*로 축소 |
| CONFIRMED | recomputeDocument | 항상 NewSolution; bool은 expireAllObjects 값 | 다음 계약 migration에서 expireAllObjects |
| CONFIRMED | setValue | Number Slider 전용 | 내부 canonical 명칭을 slider로 축소 |
| CONFIRMED | updateRhinoAttributes | rhino.upsert; geometryJson 필수이고 geometry도 Replace | wire alias 유지 시 attribute-only 아님을 명시 |
| CONFIRMED | saveRhinoLayerState | action은 save/restore/delete | 내부 MutateRhinoLayerState |
| CONFIRMED | deleteComponent | group box 등 generic GH object도 삭제 | operation-contract 설명 확장 또는 내부 generic 명칭 |
| CONFIRMED | MoveComponent/SetLayout | 동일 map/request/handler | 외부 값 유지, 내부 즉시 canonicalize |
| CONFIRMED | CreateRhinoObject/BakeGeometry | 동일 create-upsert | 외부 값 유지, 내부 canonicalize |
| CONFIRMED | ModifyRhinoObject/UpdateRhinoAttributes | 동일 modify-upsert | 외부 값 유지, 내부 canonicalize |
| CONFIRMED | UpdateRhinoLayer | reserved enum과 live UpdateRhinoLayerProperties/record UpdateRhinoLayerRequest가 충돌 | reserved/legacy 공간으로 격리 |
| CONFIRMED | RhinoBridge vs RhinoScene | 같은 owner를 contract 층별로 다르게 부름 | wire는 유지, 문서 용어 통일 |
| CONFIRMED·KNOWN | ModelProfile | API/UI에서는 reasoning effort, Contracts에서는 죽은 adaptive profile | stale enum/projection 삭제 |
| CONFIRMED | doc key | GrasshopperDoc, gh_doc, target_doc, doc_key, boundGrasshopperDocId | *DocKey로 정규화; runtime GrasshopperDocumentId GUID와 구분 |
| CONFIRMED | CanvasAutoPlacement | component뿐 아니라 reference parameter도 배치 | 이름 또는 설명 범위 확장 |

문서 네이밍/계약 drift:

- ui/panel/README.md:22-30은 삭제된 role, 존재하지 않는 /mode, 제거된 auto/fast/standard/deep, caller 0인 stop-current를 현행처럼 설명한다.
- README.md:4,16은 “one active document pair / one runtime per pair”라 적지만 현재 runtime은 한 Rhino에 여러 GH 문서를 등록한다.

---

# 6. Over-engineering 및 정리 권고

| 대상 | 판정 | 권고 | 삭제·축소 영향 |
|---|---|---|---|
| ProblemDossier + generic idempotency store | production 0 | 삭제 | 관련 tests/public ABI만 |
| SessionOrderBook + change/result 계약 | production 0, 실 CAS 별도 | 삭제 | tests와 dead contract만 |
| ProjectHomeLayout | 전체 facade caller 0 | 삭제 | 무관한 fingerprint test만 |
| DisconnectedDocumentBackend | test 1곳만 | 삭제/test fake 이동 | ModelEffort test와 stale portability docs |
| ISingleWriterBroker | 선언+구현 외 interface use 0 | interface 삭제 | concrete broker 영향 없음 |
| IReadyWorkScheduler | 구현 1, consumer 1, fake 0 | interface 삭제 | broker ctor/field만 |
| Terminal ITerminalApiClient, ITerminalView | 구현 1, consumer 1, fake 0 | 축소 | Terminal 3파일; test seam 의도가 있다면 유지 근거 필요 |
| ProjectContextStore : IThreadInstructionComposer | DI는 InstructionAssembler 등록 | 구현 표시만 삭제 | runtime 영향 없음 |
| adaptive routing 잔재 | 값이 항상 Standard/StandardWrite | 삭제·평탄화 | projector/types/mock/history suffix |
| RuntimeIdentity + project-directory | 한 필드만 중복 사용, 나머지 dead | 삭제·축소 | Program/projector/bootstrapper/smoke/tests |
| palette variant 체계 | production은 BaseArgb만 | 축소 | JSON variant 필드와 variant tests |
| operation metadata 8중 | 실제 drift 7건 | 단일 metadata로 축소 | validation/schema/docs 생성·검증 |
| Codex resolver 4중 | 현재 우선순위 충돌 | 공유 구현으로 축소 | DevLoop/LiveE2E/smoke |
| SDK/identifier backstops | 양쪽 safety gate는 의미 있음 | gate 유지, pure logic만 공유 | 한 gate 삭제 시 prewrite 또는 in-process 보호 상실 |
| asset/fallback 복제 | parity가 있고 설치 손상 복구 목적 | 유지 | 삭제 시 asset 누락 때 모델 계약 상실 |
| legacy owner converter | persisted compatibility 목적 | 유지 | 삭제 시 구 job deserialize 단절 |
| 외부 OperationKind aliases | persisted schema 영향 큼 | wire 유지, 내부 canonicalize | 즉시 삭제 시 resume/stored ChangeSet 호환성 영향 |
| reference-zero icon 4개 | package/runtime ref 0 | 삭제 | spare 크기 파일만 소실 |
| stale predicate 3종 | 성공 경로 없음 | legacy 처리 후 축소 | 구 JSON deserialize 정책 필요 |
| reserved op 4종 | 의도적 fail-closed | 유지 또는 별도 legacy enum | 즉시 삭제 시 persisted enum 호환성 검토 |

---

# W7-c~k 현재 판정

| 항목 | 현재 | 근거 |
|---|---|---|
| W7-c | KNOWN·UNRESOLVED·CONFIRMED | target을 Show/Unlock하지만 원 target 상태는 stack에 미기록: RhinoSceneFoundationAdapter.cs:3268-3301; restore :3322-3337. fix-plan:192-193 그대로. [검증·악화] plain select 모드에서도 무조건 Show/Unlock(:3276-3277)인데 이때는 스택에 아무것도 안 쌓여 원상복구 자체가 없음 — 숨김/잠금이던 객체가 조건 없이 영구히 풀림 |
| W7-d | KNOWN·PARTIAL·CONFIRMED | 일반 action/timestamp gate는 해소. retractLast만 write+GET 결합·ungated set useRuntime.ts:303-308; retry UUID도 매 호출 신규 :374-405, draftStore에 보존 없음 |
| W7-e | KNOWN·PARTIAL | DNS/redirect/magic-byte는 구현. 하지만 “any non-public” 계약과 달리 CGNAT, multicast/reserved, IPv6 unspecified/multicast 허용: ImageUrlAttachmentFetcher.cs:185-216. 실제 도달은 LIVE_REQUIRED |
| W7-f | KNOWN·PARTIAL + NEW RESIDUAL | ErrorBoundary는 추가됐지만 failed state reset 없음 ErrorBoundary.tsx:19-40; boundary 자체 identity key도 없어 새 카드로 바뀌어도 fallback 고착. [검증 CONFIRMED] ChatPane.tsx:1143·1158의 key는 CHILD 카드에 걸려 있어 failed boundary는 child를 렌더하지 않으므로 무효; 카드 slot이 null이 되거나 세션 전환(key={session.id}) 시에만 풀림 — 고착 케이스는 정확히 in-place 카드 교체 |
| W7-g | RESOLVED | item/detail 모두 domain 분기, render tests 있음 |
| W7-h | KNOWN·UNRESOLVED·CONFIRMED | hook은 실패를 catch/void, chips는 실측이 아니라 intent 보고. 추가로 hook과 ChatPane이 restore owner를 중복 소유 |
| W7-i | RESOLVED STATIC | delete 성공 뒤 draft clear ChatPane.tsx:973-981; UI test 0 |
| W7-j | PARTIAL/PLAN DIVERGENCE | 순차 인코딩 구현. 계획의 총합 경고는 없고 현재 client/server는 의도적으로 unlimited |
| W7-k | RESOLVED | .gitignore:24-31에 .3dmbak, .3dm.rhl, .rhl, .ghx 포함 |

추가 기준선 상태:

- W1-b/c/d는 그대로다. 활성 턴 CTS 없음, 카드 답변 전 interrupt 없음, session gate/무진행 timeout 없음. 실제 영구 hang은 LIVE_REQUIRED: fix-plan:89-95.
- W2는 최신 라이브 기록상 빈 출력 검출은 성공했지만 solve 완결은 미해결이다: session-evening:71-85.
- W3의 unsafe IGH_Param.TypeName 경로는 GrasshopperCanvasFoundationAdapter.cs:1387에 남았다.
- W11-c/d/e coverage gate도 미구현이다. 현재 schema coverage test는 선언↔schema만 검사한다.

## LIVE_REQUIRED로 남긴 항목

- ask_user가 현재 규범 조합에서 실제로 발화하는지
- 6개 exact bridge-op 및 v18 replace/deferSolve 종단
- 현재 설치 DLL의 protocol v18 여부
- 48 route의 실제 HTTP binding/serialization
- focus restore 경합의 실제 발생 빈도와 W7-c 실제 상태 손실 (동시 접근 자체는 검증에서 InvokeOnUiThread 직렬화가 확인돼 정적으로 강등)
- bake zoom의 실제 Rhino 동작과 OS/WebView notification
- SSRF redirect/DNS 실제 I/O
- ensureRhinoLayer의 Rhino 색 표시 및 strict 누락 후 job terminal state
- W2 solve 완결

GPT 주장 10·23, 거대 클래스 리팩터, 성능 추측, 새 기능 제안은 제외했다. 이번 감사에서는 코드 수정·빌드·테스트·라이브 실행을 하지 않았다.

---

# 검증 부록 — 2026-08-12 교차검증 (Claude)

본 감사의 CONFIRMED 주장 중 실행 가능성이 높은 약 40건을 병렬 검증 에이전트 4개(카드 lifecycle / 계약 C1~C8 / dead code 18건 / 패널·focus·resolver) + 직접 열람으로 코드와 전수 대조했다. 정적 검증만 수행, 코드 수정·빌드·라이브 실행 없음. 본문 해당 지점의 `[검증 …]` 주석이 상세다.

## 결과 요약

| 구분 | 건수 | 내용 |
|---|---:|---|
| 반박(REFUTED) | 0 | 없음 — 감사 판정 전량 유지 |
| 정정 | 2 | C2 GUID(componentId는 ChangeSetValidation.cs:777에서 검사, 실누락은 newComponentId뿐) · focus _focusStack 경합(InvokeOnUiThread 직렬화로 현재 비실행, 아키텍처 리스크로 강등) |
| 감사보다 악화 | 4 | GoalCard에 DeliveryPending 필드 자체 부재 · W7-c는 select 모드도 영구 Show/Unlock · C8은 코드 주석이 거짓 + reference만 pivot 검사 부재 · bake zoom 실패가 unhandled rejection |
| 정밀화 | 3 | bake zoom 유실 지점 2곳으로 축소(App.tsx:547 void + DataView prop) · focus restore 발화자 2곳→3곳 · layer-scheme 유실 범위(규칙은 저장됨, 에이전트 전달만 유실) |

## 우선순위 권고

1. **P0 — layer-scheme 승인 트랜잭션 순서(1.4-1)**: 저장 실패에도 카드가 granted로 settle, 재시도 409, 회복 불가. 수정은 순서 교환으로 단순.
2. **P1 — 카드 답변 유실 계열(1.4-2/3)**: goal은 DeliveryPending 필드 추가부터. layer-scheme granted 경로에도 거절 분기(:491-495)와 같은 DeliveryPending 처리 필요.
3. **P1 — C2 strict preflight 3종 + newComponentId GUID, C3 ensureRhinoLayer argbColor**(기존 레이어 색이 ARGB 0으로 덮이는 실사용 손상 경로).
4. **P2 — bake zoom 결과 표시**(유실 2곳, 채팅 chip의 FocusResult 렌더 패턴 재사용) · focus restore owner 단일화 · W7-c target 원상태 기록.
5. dead code 18건 삭제는 전량 안전. 단 2.2 말미 `[검증 18/18]` 주석의 주의 4건(SessionOrderSnapshot 생존, TryTurn만 삭제, variantStopsL JSON 스키마 동반 정리, CodexTurn은 dead knob) 준수.
