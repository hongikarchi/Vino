# 기지(旣知) 발견 대장 — 2026-08-26

**목적**: Stage 1(W2 dedup)이 "이미 찾았고/이미 고친" 문제를 재보고하지 않도록 하는 **기지 필터**.
선행 문서·메모리·커밋 로그(07-21~08-26)를 전수 대조해 **문제 1건 = 1행**으로 정리했다.

**사용법**
1. W1 finder가 낸 `(category, signature)`를 이 표의 **증상 시그니처** 열과 대조한다.
2. 매칭되고 상태가 `수정됨`이면 → **재보고 금지**, 단 "회귀 인지법" 열의 신호가 alpha.7 로그에
   실재하면 **회귀(regressed)로 승격**해 보고한다.
3. 상태가 `열림/이연/부분`이면 → **필터하지 말 것**. 이들은 alpha.7 로그에 **나오는 게 정상**이며,
   빈도·심각도만 갱신한다(§끝 목록 참조).
4. 상태가 `반증`이면 → 같은 가설이 다시 나와도 **그 가설로는 보고 금지**(정정된 기전으로만).

**버전 경계**: `0.1.0-alpha.7` = 2026-08-14(33bfa01) 이후. 그 이전 로그의 수정된 항목은 기대되는
"과거 데이터"이므로 카운트에서 pre-alpha7로 분리한다.

**상태 표기**: `수정<커밋,날짜>` / `부분` / `열림` / `이연` / `반증` / `설계`(by design)

---

## 1. 계약·스키마 마찰

| ID | 이름 | 증상 시그니처 (로그) | 최초 식별 | 상태 | 수정 범위 | 회귀 인지법 (alpha.7 로그) |
|---|---|---|---|---|---|---|
| K001 | createComponent `resultOutput` 문서 누락 | 잡 거부 메시지 `Operation '...' is missing required argument 'resultOutput'`; 모델이 operation-contract.md 페이로드를 그대로 씀 | 08-12 convergence-audit C1 | 수정 a507445, 08-12 | `docs/operation-contract.md`, payload-guide | `resultOutput` missing 거부가 다시 다발 |
| K002 | mapped op 3종 strict preflight 누락 | `canvas.referenceRhinoObjects`/`python.replaceSchema`/`rhino.ensureLayer`가 submit 통과 후 어댑터에서 unmapped-member로 사망 → Failed/RecoveryRequired | 08-12 C2 | 수정 a507445(v19), 08-12 | `LiveDocumentBackend.OperationValidation.cs` | 위 3 op에서 "unknown member"/어댑터 역직렬화 실패 |
| K003 | `newComponentId` GUID 형식 미검사 | replaceSchema의 newComponentId 오형식이 preflight를 통과 | 08-12 C2 정정 | 수정 a507445, 08-12 | GuidArguments 검사 | replaceSchema에서 GUID 파싱 실패가 dispatch 이후 발생 |
| K004 | ensureRhinoLayer `argbColor` 계약 3중 충돌 | argbColor 생략 → STJ 기본값 0 대입 → 기존 레이어 색이 ARGB 0(투명 검정)으로 덮임 | 08-12 C3 | 수정 a507445(v19 nullable), 08-12 | `IRhinoSceneAdapter`, `RhinoSceneFoundationAdapter.cs:2482` | ensureLayer 후 레이어 색이 검정/투명으로 변한 사용자 보고 |
| K005 | "모든 Rhino mutation = rhinoObject" 요약 충돌 | submit 거부 `resource mismatch` (purge=table, layer op=rhinoLayer인데 요약을 따름) | 08-12 C4 | 수정 a507445, 08-12 | payload-guide.md:33 / DynamicToolSpecs 폴백 | purge/layer op에서 resource mismatch 재발 |
| K006 | absent-create 허용 목록 복제본 불일치 | 오류 메시지가 reference/ensure를 허용 목록에서 누락 → 모델이 잘못된 remediation | 08-12 C5 | 수정 a507445, 08-12 | ChangeSetValidation 오류 문구 | 오류 문구에 reference/ensure 누락 |
| K007 | setGroup create 기본 predicate 누락 | 신규 group이 `objectExists` 없이 `runtimeErrorAbsent`만으로 커밋 | 08-12 C6 | 수정 a507445, 08-12 | `ApplyDefaultPredicates` | setGroup 커밋에 objectExists 부재 |
| K008 | moveObjectsToLayer approval 규범 누락 | 서버·어댑터는 승인 요구하는데 payload-guide 요약엔 없음 → 승인 없이 제출→거부 | 08-12 C7 | 수정 a507445, 08-12 | payload-guide.md:42 | move에서 "requires approval" 거부 반복 |
| K009 | referenceRhinoObjects `autoUpstream` 숨은 옵션 | 문서엔 없는데 실제 배치에 사용; 코드 주석이 거짓; explicit pivot+autoUpstream이 reference만 submit 통과 후 어댑터 사망 | 08-12 C8 (검증 시 악화) | 열림 | `CanvasAutoPlacement.cs:372-395` | reference create에서 pivot 관련 어댑터 예외 |
| K010 | operation-contract predicate "6종" stale | 문서는 6종, 실제 스키마/검증기는 12종 | 08-12 C9 / W2-c | 부분 | `docs/operation-contract.md:180` | 모델이 존재하는 predicate를 "미지원"으로 회피 |
| K011 | 서버 전용 카드 timestamp가 TS 계약에 없음 | Goal/Ask의 `proposedAt`/`askedAt`이 DB엔 있고 패널은 못 읽음 | 08-11 capability-integrity | 이연 | `ApiModels.cs` ↔ `types.ts` | (UI 표시 부재 — 로그 신호 없음) |
| K012 | 콘솔 소켓 `out` append-only 함정 | Failed `schema append-only` / C# `'out' is a reserved keyword`; 모델이 라이브 목록의 `out`을 베껴 선언 | 07-24 로그코퍼스 · 08-19 감사(keyword 10/10 FP) | 수정 c746f8e(서버 흡수), 08-20 | `LiveDocumentBackend.cs:5052` `console_output_absorbed` | `out` 키워드 거절이 다시 등장(흡수 진단이 안 찍힘) |
| K013 | 세션 생성 컴포넌트의 첫 schema 쓰기 거절 | append-only 12건: 방금 만든 컴포넌트의 기본 소켓 x,y/a를 치우려다 거절 | 08-19 감사 §4 | 열림(권고만) | schema append-only 규칙 | `append-only` 거절이 같은 세션 생성 컴포넌트에서 발생 |
| K014 | replaceSchema 출력 소켓 position 0 거부 (F1) | `The Python component rejected an appended Output socket at position 0.` (C#·Python 양쪽) | 08-13 live-gate F1 | 수정 0d6aeb4, 08-13 | `AppendMissingParameters` → GH 파라미터 리스트 끝 앵커 | 같은 문구 재등장 |
| K015 | SDK-source 가드 오탐 on `source:null` (F2) | `C# sources must be Rhino 8 script-mode…`가 신선한 스캐폴드 자기 기본 템플릿을 거부 | 08-13 live-gate F2 | 수정 0d6aeb4, 08-13 | replaceSchema 가드 = 모델 제공 소스 한정 | source:null replaceSchema에서 같은 거부 |
| K016 | SDK-클래스 C# 소스 제출 | `GH_ScriptInstance`+`RunScript` 소스 → read-back 불일치 → RecoveryRequired 과대분류 | 08-07 bake-cleanup 사건 B | 수정 637187f, 08-07 | preflight + 어댑터 백스톱 + `mutation_rolled_back` 강등 | SDK 래퍼 소스가 write까지 도달 |
| K017 | 환각 컴포넌트 typeId | `component type not installed`; 니블 등차수열 GUID(예 `8b4c3d2e-…`), 인스턴스↔타입 혼동 | 08-07 사건 A | 수정 637187f, 08-07 | create typeId 카탈로그 preflight | `component type not installed`가 write 이후에 발생 |
| K018 | wire GUID 혼동 | `targetParameterId`에 objectId 삽입 → missing-target | 07-27 backlog | 수정(07-27 preflight 힌트) | preflight 힌트 | wire op에서 parameterId==objectId |
| K019 | 소켓 id 탐색 프로브 (missing-target 9건) | 존재하지 않는 소켓 id로 setWire → `missing target`; 모델이 id 탐색용 0000 delete를 씀 | 08-19 감사 §5 | 수정(createComponent가 `committed.sockets` 반환), 08-20 | change_submit 결과 | 소켓 id 추측 후 missing-target 왕복 |
| K020 | declared-predicate fail-closed (쓰기는 랜딩) | 잡 Failed인데 "ops는 전부 적용됨, predicate만 실패"라는 구분이 없음; writeSet 밖 컴포넌트 predicate | 08-19 감사 §5 | 열림 | Verify/제출 검증 | `pred.declared-unsatisfied` Failed에 적용 여부 미표기 |
| K021 | 계약 다중 서술(ChangeSet 3중 선언) | 같은 계약이 스키마·payload-guide·house-rules에 각각 → 드리프트 | 08-21 CRUD 감사 P2 | 이연 | 계약 단일소스화 | 계약 문구 불일치로 인한 거부 |
| K022 | payload 인자명 오류 반복 | 08-26 세션에서 페이로드 인자명 오류 10회; payload-guide가 `change_submit` description(16KB) 안에만 존재 | 08-26 claude-script-read-cap | 열림 | 지시문 배치 | `missing required argument`/`unknown member` 반복 |

## 2. fingerprint·동시성

| ID | 이름 | 증상 시그니처 (로그) | 최초 식별 | 상태 | 수정 범위 | 회귀 인지법 |
|---|---|---|---|---|---|---|
| K023 | fp.auto-no-baseline 거절 | `gptino:auto declined for {key}: this session has not written it, so there is no baseline…` + 9초 뒤 바이트 동일 재제출 | 07-27 backlog(42% 1위) · 08-19 감사(114건, 94 FP) | 수정 845c66e(auto-fill), 08-20 | `LiveDocumentBackend.FingerprintRebase.cs` | 같은 문구가 read/wire/execute-only에서 재등장 (auto-fill 레코드 없음) |
| K024 | fp.auto-drifted (RuntimeMessages 휘발) | `gptino:auto declined … another session wrote it after this session last did.` — 실제론 자기/상류 재solve | 08-19 감사(30건, 26 FP) | 수정 c017201(RuntimeMessages 제외+ledger 재기준), 08-20 | `PythonComponentFingerprint` | own execute 직후 drifted 거절 |
| K025 | fp.stale-concrete self-stale | `The fingerprint of {resource} changed after the base snapshot. Current fingerprint: … Resubmit with this value` | 07-27 A-1 | 수정 76ac6d2/자동 rebase, 08-08 | `ResolveSelfStaleRebase` | self-sequential 편집에서 stale 재발 |
| K026 | fp.stale-concrete 타세션/사용자 편집 (TP) | 위와 동일 문구, 단 원인이 사람/타세션 | 08-19 감사(TP 8) | 설계(완화 금지) | — | **정상 동작** — FP로 분류 금지 |
| K027 | RecoveryRequired 경로 쓰기가 ledger 미기록 | RR 이후 다음 잡이 no-baseline으로 막힘; 원장에 생성 행 없음(R10) | 08-10 R10 · 08-19 | 수정 845c66e, 08-20 | RR catch 블록 ledger 기록 | RR 직후 잡이 no-baseline Blocked |
| K028 | 재시작으로 인메모리 ledger 소실 | 07-30 재시작 후 no-baseline 6건 | 08-19 감사 | 수정 76ac6d2(영속), 08-08 | `resource-ledger.db` | AgentHost 재시작 직후 no-baseline 다발 |
| K029 | Stale 메시지에 자원명 부재 | 익명 해시 4~7개만 나열 → 재시도 실패 | 08-19 감사 권고1 | 수정 845c66e, 08-20 | `LiveDocumentBackend.cs:2443` | stale 메시지에 kind+id 없음 |
| K030 | group 멤버십 해시 드리프트 | 자기 delete로 그룹 멤버 감소 → auto 거절(그룹은 계속 거절 유지) | 08-19 감사 §3 | 부분(의도적 유지) | setGroup 지문 | setGroup auto 거절 빈발 |
| K031 | docKey reopen 회전 → ledger 행 고아 | 문서 재오픈 후 liveness/auto 거절, Save-As 거절 문구 | 08-19 감사 §5 | 열림(의도적 미구현) | `RemapDocKeyAsync` | 문서 재오픈 후 원장 미인식 |
| K032 | liveness 거절에 approval 타깃 부재 | 모델이 산문으로 "승인됨" 주장(3회) | 08-19 감사 §5 | 수정 c746f8e, 08-20 | `Ready-made approval target: objectId=…` | liveness 거절에 해당 문구 없음 |
| K033 | focus가 CAS fingerprint를 변경 | select/isolate가 Hide/Unlock으로 attributesJson 변경 → 이후 쓰기 fingerprint 충돌 | 08-13 focus 재설계 | 수정 a566c63(v20), 08-13 | `RhinoSceneFoundationAdapter` focus | 칩 클릭 직후 Rhino 객체 stale fingerprint |
| K034 | canvas.move 전 배치 all-or-nothing CAS | 컴포넌트 1개 layout fingerprint만 흔들려도 정리 전체 `precondition_refused` | 08-10 issue-triage F | 열림 | `canvas.move` 계약 | arrange 잡이 precondition_refused로 통째 실패 |
| K035 | 승인 grant가 구조 지문에 핀(1회·15분) | 승인과 소비 사이 wire 편집 1회로 grant 무효 | 08-10 issue-triage B-6 | 부분(standing consent cc47b79, 08-14) | `GrasshopperCanvasFoundationAdapter:1094` | grant 있는데 "승인 없음" 재요구 |
| K036 | 다중 세션 writer 직렬화 세금 | Blocked→즉시 재성공 왕복(2쌍 +6~7s) | 07-27~08-10 | 설계(허용 수준) | single-writer broker | 왕복 세금이 다시 지배적(>20%) |

## 3. 세션 생명주기

| ID | 이름 | 증상 시그니처 (로그) | 최초 식별 | 상태 | 수정 범위 | 회귀 인지법 |
|---|---|---|---|---|---|---|
| K037 | 카드로 끝나는 턴 = assistant 응답 소실 | system/error `Codex reported completion, but Vino could not recover an assistant response.` + 세션 Failed(blocked) — 카드가 마지막 툴이면 6/6 결정론 | 08-10 R7/P0-5 | 수정 646e439, 08-11 (라이브 CONFIRMED) | `SessionOrchestrator.cs:1994` 주변 | **동일 문구 재등장** — 08-22 품질트랙에서 ⓐ로 2회 관측(별건 K038-b) |
| K038 | ask 카드 답변 → 세션 queued 영구 사망 | 세션 `working` 고정, PUT /ask 후 `queued` 고착, resume·retract 무효 | 08-10 P0-1 | 수정 646e439+878ae34, 08-11 | 오케스트레이터 턴 종료 판정 | 세션이 queued에서 30분+ 정지 |
| K038b | "could not recover an assistant response"가 카드 아닌 정상 턴에서 발생 | 같은 문구 + 세션 blocked, 카드 무관(검수 수리 턴·비평 턴) ×2 | 08-22 vino-quality-track ⓐ | 열림(P1 후보) | 미조사 | 카드 없는 턴에서 해당 문구 |
| K039 | W1-b 활성 턴 취소 불가 | interrupt/stop-current/retract-last가 CTS를 취소하지 않아 게이트 잔류 | 08-11 fix-plan W1-b | 부분(`_activeTurns` 존재, 무진행 상한·게이트 타임아웃 미확인) | `SessionOrchestrator` | 턴이 무한 폴링, 다음 메시지가 queued |
| K040 | 무진행 상한/세션 게이트 타임아웃 부재 | `WaitForTurnOutcomeAsync`에 전체 타임아웃 없음(의도) + 무진행 감지 없음 | 08-11 W1-d | 열림 | 오케스트레이터 | 턴 duration 극단치(>1h) |
| K041 | paused 세션에서 카드 답변 유실 | PUT 204인데 모델에 미도달; `DeliverCardAnswerAsync` bool 폐기 | 08-10 P1 · 08-11 W4-b | 수정 7de3f99·73d7a83, 08-11~12 | DeliveryPending 필드 | 카드 답변 후 모델이 답을 모르는 채 재질문 |
| K042 | layer-scheme 승인 트랜잭션 순서 | 저장 실패에도 카드 granted settle → 재시도 409 → 회복 불가 | 08-12 convergence 1.4-1 (P0) | 수정 73d7a83, 08-12 | `Program.cs:541-554` | layerScheme 500 후 409 |
| K043 | layerScheme 카드 continuation 부재 | 승인해도 에이전트 정지(grant 없어 백업 채널도 불가) | 08-11 capability-integrity | 수정 878ae34, 08-11 | `Program.cs:532` | 레이어 승인 후 무응답 |
| K044 | 카드 재-PUT 이중 배달 | 같은 카드 재PUT 시 재-mint·재배달 | 08-11 W4-a (GPT 주장4) | 수정 878ae34(409 `ask_card_answered`), 08-11 | 카드 엔드포인트 | 같은 카드 답변이 2회 메시지로 |
| K045 | 세션당 승인 대기 카드 1개(조용한 덮어쓰기) | 새 카드가 이전 카드를 무고지 대체, 세션 목록에 대기 배지 없음 | 08-10 issue-triage B-7 | 열림 | `approval_card` 단일 컬럼 | 카드가 답변 없이 사라짐 |
| K046 | grant 수명 vs 카드 수명 불일치 | 만료·소비 후에도 죽은 grantId가 매 턴 주입 | 08-10 B-4 | 수정(GrantExpiresAt 저장·표시), 08-10 | `ApiModels`, `ApprovalCard.tsx` | 만료 grant 재사용 시도 |
| K047 | halt 래치가 실질 차단 못 함 | RecoveryRequired 89초 만에 `recovery_resume` 자기 해제, 브리지 막힌 채 새 메시지 202 | 08-10 P1 | 열림 | `ThrowIfSessionHalted` | RR 직후 자기 resume + 연속 실패 |
| K048 | 재시작 후 halted 복귀 미작동 | phase가 `recoveryrequired`(≠`-acknowledged`)인데 재기동 후 정상 제출 재개 | 08-10 D | 부분 | `DurableJobStore` 복원 조건 | `recoveryrequired-acknowledged` 행이 계속 0건 |
| K049 | 문제 배너 sticky (Blocked/Failed 포함) | 최신 잡이 RecoveryRequired/Blocked/Failed로 굳어 배너가 영구 | 08-10 E1 | 수정(ReadRecentProblems phase 존중 + 재개 버튼 ack), 08-10 | `LiveDocumentBackend.cs:1870` | 새 잡을 내도 배너 잔존 |
| K050 | 재설치/재시작이 진행 중 턴을 삼킴 | "AgentHost restart" 배너 + 승인 턴 2건 105분 무응답 | 08-10 R8 | 열림(원인 규명만) | 부트스트랩/턴 복원 | 배너 직전 턴이 응답 없이 소멸 |
| K051 | 컨텍스트 오버플로 → 영구 실패 루프 | generic Failed 후 같은 스레드 재-resume 반복 | 08-11 요청5 | 수정 cfe6f75(auto-compact 80%+1회 재시도), 08-11 | `SessionOrchestrator` | 컨텍스트 초과 문구 반복 실패 |
| K052 | 세션 전환 시 드래프트/첨부/고정 소실 | 사용자 불만 "쓰던 글이 사라짐"; `key={selected?.id}` 리마운트 | 08-10 C (12/12) | 수정 9d6fd1c(draftStore, 세션별), 08-10 | `ui/panel/src/draftStore.ts` | 드래프트 소실 재보고 |
| K053 | 첨부 영속 부재(File 객체 메모리 전용) | 붙여넣은 이미지 원본이 디스크에 없음 → 재시작 시 소실 | 08-10 C | 열림(IndexedDB 미구현) | 컴포저 첨부 | 첨부만 소실되는 보고 |
| K054 | 오리진 파편화(임의 포트) | localStorage/테마/탭이 Rhino 재시작마다 초기화, leveldb에 `127.0.0.1:<port>` 오리진 59개 | 08-10 C/I | 이연(사용자 결정 대기) | `Program.cs:25` 포트 바인딩 | 설정 초기화 반복 보고 |
| K055 | 패널 401 "세션이 만료되었습니다" | 패널 배너 문구 + `/api/v1/*` HTTP 401; 쿠키 `vino_runtime`이 포트로 격리되지 않아 다중 AgentHost가 충돌 | 08-10 I | 부분(401 표면화만; 자동 재부트스트랩 없음) | `client.ts:111`, `Program.cs:195` | 401 배너 + Retry 무효 |

## 4. 읽기 경로·용량

| ID | 이름 | 증상 시그니처 (로그) | 최초 식별 | 상태 | 수정 범위 | 회귀 인지법 |
|---|---|---|---|---|---|---|
| K056 | snapshot_read 무캡 | 컴포넌트당 2,196자 × 500 = 1.4MB 단일 결과; unchanged여도 전문 반환 | 08-21 CRUD 감사 P0 | 수정 7d9252b(v3 meta/index/id-지정, 256KiB 상한), 08-21 | `snapshot_read` | 단일 툴 결과 >256KiB |
| K057 | `resources[]` 이중 인코딩 | 결과 25~30%가 resources/canvas 중복 | 08-21 P0 | 수정 7d9252b(전면 삭제), 08-21 | 스냅샷 봉투 | resources 배열 재등장 |
| K058 | job diagnostics 무캡 | 모델 대면 진단이 수백 행 | 08-21 P2 | 수정 139e45a(50행 캡+`diagnosticsOmitted`), 08-24 | 잡 결과 투영 | 진단 50행 초과 |
| K059 | inspect_outputs mass properties 강제 | 모든 geometry 출력마다 Area/Volume 계산 | 07-27 #4 · 08-21 발견7 | 수정 9ec0ed1(opt-in `includeMassProperties`), 08-24 | inspect 스키마 | massProps가 항상 계산됨 |
| K060 | 코드모드 change_submit 결과 유실 | 모델이 잡 결과를 못 봄 → 같은 실패 반복 | 08-21 P1 | 수정 a6d89ef(PendingJobDigests → `<vino_job_results>`), 08-24 | 턴 입력 합성 | 실패 잡 후 모델이 결과를 모름 |
| K061 | `script:` 스코프 캡 면제 → Claude MCP spill | 결과 64,003자 → `tool-results/mcp-vino-snapshot_read-*.txt`로 spill, 모델은 `--tools ""`라 파일 못 엶 → 소스 0자 | 08-26 claude-script-read-cap | 열림 | `LiveDocumentBackend.cs:561`, `VinoMcpEndpoint.cs:127` | Claude 세션에서 spill 파일명 + 이후 사용자 붙여넣기 |
| K062 | 소스 부분 읽기 툴 전무 | 50K 소스를 매 수정마다 setSource 전문 재전송(~3.7분/회) | 08-26 | 열림 | `python.replaceBlock`은 쓰기 전용 | 동일 컴포넌트 setSource가 반복 전문 전송 |
| K063 | 상시 컨텍스트 97KB (툴 59.3KB) | change_submit description 단독 27.5KB, 저빈도 툴 14.6KB 상시 탑재 | 08-21 CRUD 감사 | 열림(P6) | `DynamicToolSpecs` | 컨텍스트 사용률 초기값 과대 |
| K064 | `python.inspect` JSON 이스케이프 과다 | `"`·`<` 1,463개가 `"` 등으로 팽창 | 08-26 | 열림 | JSON 인코더(UnsafeRelaxedJsonEscaping) | 소스 응답이 원문보다 30%+ 큼 |

## 5. 검증 오경보·검증 갭

| ID | 이름 | 증상 시그니처 (로그) | 최초 식별 | 상태 | 수정 범위 | 회귀 인지법 |
|---|---|---|---|---|---|---|
| K065 | 빈 출력 초록 커밋 43.6% | 커밋 메시지 `output(s) 'X' 'Y' empty` 를 달고도 Committed | 08-11 capability-integrity #1 | 수정 661a5e8(`resultOutput` 스키마 강제 → outputCountInRange 자동 주입), 08-11 | `ChangeSetValidation`, `DynamicToolSpecs` | producing 잡이 empty 노트 달고 Committed |
| K066 | 브로커 생성 컴포넌트 volatile 미초기화 | 신규 슬라이더 값 6.0인데 출력 DataCount=0; 라이브 데이터 파괴(PanelsOut 1120→0) | 08-10 R4/P0-3 | 부분(검출만 출하; solve 완결 미수정) | 용의자 `InvokeOnUiThread` 문맥 vs stale GH_Document | 신규 컴포넌트가 계속 빈 출력, 사용자 수동 Recompute 요구 |
| K067 | 잘못된 solver 토글 가설 | "EnableSolutions가 파일에 꺼진 채 저장됨" | 08-11 | **반증**(앱 전역 static) | `EnsureSolverEnabled` 잔존 | 이 가설로 재보고 금지 |
| K068 | "outputs empty WITH ISSUES" 오경보 | `Job {id} committed WITH ISSUES: … output(s) '…' empty` — setSource 직후 정상인데 경보 | 08-26 | 열림 | `LiveDocumentBackend.Verification.cs:401` | setSource/execute 직후 empty 노트가 정상 결과에 붙음 |
| K069 | "acceptancePredicates를 비우라" 역설 | 서버 실패 메시지·house-rules가 의미 술어를 떼라고 안내 → 죽은 출력이 초록 | 08-11 #1 | 부분(W2-c) | 실패 메시지 문구 | 실패 후 모델이 predicate를 비우고 재제출 |
| K070 | 세맨틱 predicate 5종 실사용 0 | area/volume/geometryClosed/branchCount/bbox 실사용 0건 | 08-11 | 부분(house-rules 규범 추가, 실경로 테스트 0) | predicate 4자 동기화(W11-c) | 여전히 0건 |
| K071 | 사문 predicate 3종 | `OutputEquals`/`BoundingBoxEquals`/`Custom`이 enum에 있으나 검증 거부 | 08-11 | 이연 | `Changes.cs:107,112,129` | 모델이 선언 → fail-closed 거부 |
| K072 | 첫 실행 해상도 게이트 탈출 불가 (R5) | 4회 반복 차단; `ValueFingerprint`가 슬라이더에만 생성돼 스크립트 상한 영구 10,000 | 08-10 R5/P0-6 | 수정 9d5dc63(측정 기반 게이트)+c746f8e(거부→advisory), 08-13~20 | `PreflightExecuteCost` | 스크립트가 10,000 상한에 반복 차단 |
| K073 | cost-preflight 9/10 오탐 | 22000mm/5900mm 치수 슬라이더를 "개수"로 오인해 execute 거부 | 08-19 감사 §5 | 수정 c746f8e(`execute_cost_advisory` 강등), 08-20 | 비용 추정기 | cost로 인한 Failed 재등장 |
| K074 | 측정 기반 예측 게이트 오차 | `predicted to take ~50.0s … over the 20s predicted-solve ceiling` — 같은 ChangeSet 내 슬라이더 변경은 미반영, 초선형 과소예측 | 08-13 W2 | 부분(문서화된 한계) | `component-measurements.db` | 예측 거부가 실제로는 짧은 solve였던 사례 |
| K075 | CanvasLayoutAudit이 이동 '전' 좌표 측정 | 1차 보고 longWires 3 vs 실제 34; 2차는 already-tidy 선언 | 08-10 P0-4 | 부분(08-21 4a5b466 감사 재작업) | `LiveDocumentBackend.cs:1211` 계열 | arrange 보고치와 재계산치 불일치 |
| K076 | arrange_layout이 문서를 악화 | 최장 wire 2942→6290px, 공유 파라미터를 소비처에서 6,290px 밖으로 | 08-10 R6 | 부분(ALAP·우측엣지·그룹 컨테이너는 08-10 수정) | `CanvasLayout.cs` | arrange 후 longWires 증가 |
| K077 | tidy 실패가 아무에게도 안 보임 | `wait:false` 투척 + 예외 삼킴 + arrange 잡이 last-terminal 추적서 제외 | 08-10 F | 열림 | `LiveDocumentBackend.cs:1319,1324,5757` | arrange 잡 Failed인데 세션엔 신호 없음 |
| K078 | 모델이 감사 지표를 날조 | 존재하지 않는 필드명(`columnCrowding.overlapPairs`)을 코드펜스로 제시, 실측과 모순 | 08-10 P1 | 열림(모델 행동) | 지시문 | 응답에 존재하지 않는 서버 필드명 |
| K079 | RecoveryRequired 3중 거짓 보고 | 잡 "Applied: none/Unknown outcome"인데 실제 커밋됨(181→182), 원장 무행, 에이전트는 "생성 안 됨" 단정 | 08-10 P1 | 부분(K027 원장 수정으로 완화) | RR 매니페스트 | RR 잡의 applied 목록과 실제 문서 불일치 |
| K080 | visual-review가 첫 사용자 질문을 "Session goal"로 씀 | 비평 프롬프트의 목표 문장이 엉뚱함 | 08-26 | 열림 | `VisualReviewState` | 시각 검수 목표 문구가 첫 질문 |
| K081 | 뷰포트 캡처 502 (브리지 경합) | 비평 라운드 중 Perspective 캡처 502 ×2 (Front은 성공) | 08-22 quality-track ⓑ | 열림 | `/dev/viewport-capture` | 캡처 502 |

## 6. 호스트·브릿지 안정성

| ID | 이름 | 증상 시그니처 (로그) | 최초 식별 | 상태 | 수정 범위 | 회귀 인지법 |
|---|---|---|---|---|---|---|
| K082 | GH 파일 열기 중 Rhino 즉사 | 크래시 덤프·WER·이벤트로그 전무 + 설정 flush 미수행 = 네이티브 fast-fail; 저작 중 File>Open | 08-07 gh-open-crash | 수정 537aaed(BridgeUiOperationScope + `ThrowIfDetached`), 08-07 | `VinoRuntimeHost`, `GrasshopperDocumentLiveness` | 저작 중 문서 전환 후 프로세스 소멸(로그 끊김) |
| K083 | `ThrowIfDetached` 커버리지 구멍 | `RemoveObject(…,true)`/`AddObject(update:true)` 무방비 | 08-10 D | 수정(08-10 확대) | `GrasshopperCanvasFoundationAdapter` | detach 이후 삭제 경로에서 크래시 |
| K084 | R3 동기 teardown 중첩 펌프 | `_targets` 비면 GH UI 스레드에서 `Monitor.Wait 2s` + AgentHost Kill/WaitForExit | 08-07 gh-open-crash R3 | 열림 | 런타임 호스트 teardown | 문서 전환 시 2초 프리즈 |
| K085 | R4 패널 250ms 워치독 P/Invoke | 중첩 펌프 안에서도 틱 | 08-07 R4 | 열림(저위험) | `VinoPanel` | — |
| K086 | GH 모달 브레이크포인트 (IGH_Goo) | `InstantiateT() cannot be called … Cannot create an instance of an interface(IGH_Goo)` 모달이 브리지 무한 블록(474s 실측), 이후 모든 canvas 연산마다 재발 | 08-10 P0-2 (45s 오진의 실체) | 부분(08-11 라이브 재현 실패; `GrasshopperCanvasFoundationAdapter.cs:1387` TypeName 미가드) | TypeName 접근 지점 | 읽기 전용 snapshot조차 45s 타임아웃, Rhino Responding=True |
| K087 | "45s 예산 = 무거운 solve" 오진 | 잡 메시지가 "무거운 solve를 줄여라"로 안내 | 07-24~08-10 | **반증**(K086이 실체) | 타임아웃 문구 | 이 프레임으로 재보고 금지 |
| K088 | 45s 브리지 예산 초과 → RecoveryRequired | 타임아웃 문구 + RR; solve는 UI 스레드에서 계속 → Rhino 프리즈 | 07-24 로그코퍼스(12/171) | 부분(워치독 30s·측정 게이트·advisory로 대부분 차단, D5=45s 유지) | `AgentHostOptions`, 워치독 | 45s 타임아웃 RR이 alpha.7에서 재발 |
| K089 | 무거운 C# 스크립트 UI 프리즈 | 런타임 메시지 `Error running script: Vino solve budget (30000 ms) exceeded - reduce the workload or split this stage.` | 08-13 W1 | 수정 4906221(서버 주입 워치독), 08-13 (라이브 PASS) | `CSharpWatchdogInjector.cs` | 워치독 없이 30s+ 점유(Python 경로는 미커버) |
| K090 | `setSchema`에 비용 게이트 부재 | 소켓 1개 추가가 무제한 다운스트림 solve 유발 → 45s 초과 | 08-10 D-⓷ | 수정(08-10 4-C), 08-10 | `LiveDocumentBackend` preflight | setSchema에서 타임아웃 |
| K091 | 자기 저작 와이어도 절단 불가 | 절단 가드 4연속 거부 → 재구축 교착(중복 배선 잔존) | 08-10 D-⓵ | 수정(`IsSelfAuthoredWire`), 08-10 | `LiveDocumentBackend.cs:4338` | 자기 세션 wire 삭제 거부 |
| K092 | 447MB 백업 UI 스톨 + 무한 누적 | change_submit 43콜 중 13콜 6~15s(합 162.9s = tool-handling 96%); BackupRoot 893MB | 08-10 P1 | 열림(Modified 게이트는 **반증** — 대형 씬은 로드 직후 Modified=true) | `VinoDocumentBackup.cs:103` | execute마다 10s+ 스톨, backups 폴더 증가 |
| K093 | 캔버스 편집엔 pre-execute 백업 없음 | `BeforeExecute`가 스크립트 execute 경로에서만 발화 | 08-11 세션 정정 | 열림 | `GrasshopperPythonFoundationAdapter` | 컴포넌트 추가/삭제 후 백업 부재 |
| K094 | GH 정의 백업이 항상 실패 | `manifest.json`의 `"grasshopperBackup": null` 3/3; `SaveQuiet(".definition.gh.tmp")`가 확장자 디스패치로 false | 08-10 §0-2 | 수정(`.definition.tmp.gh`), 08-10 | `VinoDocumentBackup` | manifest에 grasshopperBackup null |
| K095 | 백업이 문서 신원 오염 (`.model.3dm`) | `project.json`의 `"projectName": ".model.3dm"`, `rhinoFile`이 backups 경로; 유령 프로젝트 폴더 생성 | 08-10 §0-1 | 수정 48961f1(Write3dmFile+BackupRoot 가드), 08-10 | `VinoDocumentBackup`, `VinoPlugIn`, `VinoBackupPaths` | projectName이 `.model.3dm` |
| K096 | 백업 실패 모달 (`SuppressAllInput` 무효) | Rhino 모달 "Failed to save … The temporary file could not be renamed." → UI 스레드 정지 | 08-10 §0-1 | 부분(`SuppressDialogBoxes` 추가) | 백업 경로 | 저장 실패 모달 보고 |
| K097 | stale `.3dm.rhl` → read-only 저장 실패 | 사용자 "read-only/임시로 열림"; `<이름>.3dm.rhl` 잔존 | 07-24 · 08-18 postmortem | 수정(bench kill을 PID 스코프로), 08-18 | `scripts/bench-run.ps1`, `bench-round.ps1` | 벤치 후 사용자 모델이 잠김 |
| K098 | codex 스레드 cwd가 프로젝트 폴더 | 자식 프로세스 핸들이 `.3dm` temp rename 차단 | 07-24 | 수정(`ResolveThreadWorkspaceDirectory`) | cwd 이동 | 저장 실패 + cwd가 모델 폴더 |
| K099 | 하드 크래시(coreclr ACCESS_VIOLATION) | 잡 DB에 흔적 없음(프로세스 사망); `%LOCALAPPDATA%\CrashDumps` 덤프, 폴트 모듈 coreclr.dll | 07-30 stability-defenses | 열림(Layer 4 샌드박스 이연) | 미수정 | host.log가 중간에 끊기고 재기동 |
| K100 | GH solve 중단 불가 | 시작된 solve는 외부 중단 불가(단일 UI 스레드) | 07-30 | 설계(자기 throw만이 탈출) | — | — |
| K101 | 단일 DLL 핫스왑 → AgentHost 크래시 루프 | `FileNotFoundException` (Vino.Core 버전 불일치) → 패널 영구 "waiting" | 08-11 세션 | 수정(절차: publish 전량 배포) | 배포 규범 | 설치 후 패널 waiting + 호스트 재시작 반복 |
| K102 | codex `.sandbox-bin` 갭 | 모든 dynamic tool이 `os error 2` (codex-code-mode-host.exe 부재) | 08-11 | 수정 a239777(resolver 순서 정렬) | `CodexExecutableResolver`, LiveE2E | dynamic tool 일제 `os error 2` |
| K103 | Codex resolver 4중 우선순위 충돌 | DevLoop/smoke만 sandbox 최우선 | 08-12 §4 | 수정 58a1036, 08-12 | DevLoop/smoke | 하네스와 운영이 다른 codex 버전 |
| K104 | 크래시 다이얼로그 + scratch RhinoCommon | 프로세스 트리에서 크래시 대화상자, 스크래치 검산이 RhinoCommon 로드 | 08-21 | 수정 5276cbc, 08-21 | 스크래치 실행 환경 | 스크래치에서 Rhino 어셈블리 로드 |
| K105 | `/dev/viewport-capture` 봉투 vs canvas-capture PNG 비일관 | 캡처가 PNG가 아니라 JSON 봉투 | 08-22 ⓒ | 열림(소품 정리) | dev 엔드포인트 | 캡처 소비자가 디코드 실패 |
| K106 | `nothingFound` 경로가 `canvas.Refresh` 미도달 | 해제된 선택이 화면에 계속 칠해짐 | 08-10 P2 | 열림 | canvas focus | focus 후 잔상 |

## 7. 백엔드 특이 (codex vs claude)

| ID | 이름 | 증상 시그니처 (로그) | 최초 식별 | 상태 | 수정 범위 | 회귀 인지법 |
|---|---|---|---|---|---|---|
| K107 | Claude MCP 결과 캡 25K 토큰 spill | 툴 결과가 `tool-results/mcp-vino-<tool>-*.txt`로 spill; `MAX_MCP_OUTPUT_TOKENS` 기본 25,000 | 08-26 | 열림 | `VinoMcpEndpoint.cs:127-136` (`_meta["anthropic/maxResultSizeChars"]` 미선언) | Claude 세션에서 spill 파일 언급 |
| K108 | Claude 세션에 파일 복구 경로 없음 | `--tools ""` → `No such tool: Read/Bash`; spill 파일을 못 엶 | 08-26 | 열림 | `ClaudeCliSessionClient.cs:352` | "No such tool" + 사용자 붙여넣기로 우회 |
| K109 | "Codex turn failed" 라벨이 Claude 오류에 | Claude OAuth 만료인데 codex 문구 | 08-26 | 열림 | 오류 라벨 합성 | backend=claude 세션에 codex 문구 |
| K110 | `--setting-sources ""`가 CLAUDE.md도 차단 | 지시문 미주입 | 08-24 구현 중 | 수정(`project` 스코프), 08-24 | `ClaudeCliSessionClient` | Claude 세션이 house-rules를 모름 |
| K111 | 스모크 READY 파서 `Substring(13)` | 리네임 후 모든 스모크 실패 | 08-25 | 수정 ea9b6e3, 08-25 | `scripts/smoke-*.ps1` | 스모크가 READY 파싱 실패 |
| K112 | `POST /resume`은 recovery-halt 전용 | 언파즈 시도 → 409 | 08-25 | 수정(문서·하네스: PUT pause) | 하네스 | resume 409 반복 |
| K113 | Claude `rate_limit_event`에 usedPercent 없음 | claudeLimits가 codex 미터형이 아님 | 08-25 | 설계(원본 투영) | `RuntimeStateProjector` | Claude 세션 usage 미터 공백 |
| K114 | Claude에 effort 대응 없음 | effort 파라미터 무시 | 08-19 스파이크 | 설계 | 모델 카탈로그 | — |
| K115 | robocopy `/E`가 stale 번들 누적 | 설치 폴더에 08-20·08-24 해시 번들 잔존 | 08-26 | 수정(`/MIR` + `/XF *.yak`), 08-26 | 설치 절차 | 설치본에 번들 2개 이상 |
| K116 | effort 기본 `xhigh` 하드코딩 3곳 | 07-24 A/B에서 medium이 동일 성공률·20~35% 빠름인데 미반영 | 08-13 meta-audit | 열림 | `ApiModels.cs:75`, `SessionStore.cs:208/220`, `ModelSelector.cs:48` | 전 세션 effort=xhigh |
| K117 | `serviceTier='fast'` 하드코딩 | turn/start 고정 → 하위 플랜 거부 리스크 | 08-13 meta-audit | 열림 | Codex 턴 시작 | 계정 플랜 관련 turn 실패 |

## 8. UX·의도 불일치

| ID | 이름 | 증상 시그니처 (로그) | 최초 식별 | 상태 | 수정 범위 | 회귀 인지법 |
|---|---|---|---|---|---|---|
| K118 | 승인 버튼을 눌러도 턴이 재개되지 않음 | 사용자가 "승인했어. 진행해줘."를 타이핑; PUT /approval이 저장만 | 08-10 B-1 | 수정(`ResumeAfterApprovalAsync`), 08-10 | `SessionOrchestrator`, `Program.cs` | 승인 후 사용자가 재촉 메시지를 침 |
| K119 | 거절이 모델에 영원히 미도달 | `ComposeApprovalBlock`이 granted만 렌더 → 다음 턴에 같은 시도 반복 | 08-10 B-2 | 수정(거절도 렌더), 08-10 | `SessionOrchestrator.cs:613` | 거절 후 동일 op 재시도 |
| K120 | approval_card 저장 0건 (프로즈 승인 요구) | 1,138잡 전부 `approvalGrantId` null; 채팅에 "승인"/"4개 항목 모두 승인" 타이핑 | 08-11 capability #2 | 수정(house-rules 규범 + 08-10 라이브 완주 확인) | house-rules, 디스패처 | approvalGrantId가 계속 null인데 파괴 op 진행 |
| K121 | ask_user 규범 0줄 → 프로즈 질문 | 턴이 툴 없이 물음표로 끝남("4,000 mm 기준으로 진행해도 될까요?"); `house-rules.md`에 `ask_user` 언급 **0건(HEAD 확인)** | 08-11 피드백1 / W9 | **열림** (ComposeAskBlock은 08-12 구현됨) | `assets/instructions/house-rules.md` | 카드 없이 질문으로 끝나는 턴 |
| K122 | 과잉 질문(자기 제안에 확인 요청) | goal 확정·"쭉 진행" 이후에도 "진행해도 될까요?" | 08-11 W9-b | 열림 | 지시문 | 비파괴 제안에 확인 요청 |
| K123 | goal 카드 거짓 "진행 중" 배지 | `status==="confirmed"`만 보고 영구 표시 | 08-10 G | 수정(세션 실행 상태 반영), 08-10 | `GoalCard.tsx` | idle 세션에 "진행 중" |
| K124 | goal 확정이 턴을 시작 안 함 | PUT /goal이 저장+SSE만 | 08-10 G | 수정, 08-10 | `Program.cs:668` | 목표 확정 후 무동작 |
| K125 | goal 카드 종료 경로 부재 | scored 카드 0건, confirmed에 영구 고착 | 08-10 G | 수정 28dbc33(`DELETE /goal` + 해제 버튼), 08-11 | 패널·서버 | confirmed 카드 누적 |
| K126 | native codex goal 이원화 / goalEnabled 고아 | 플래그가 카드 goal을 게이트하지 않음(최초 턴 1회 native만) | 08-11 W5 | 수정 867a4e5(native goal 삭제), 08-11 | 오케스트레이터 | goalEnabled 관련 혼선 |
| K127 | goal 카드 남발 = 왕복 세금 | 요청 3건에 카드 응답 8회 | 08-10 P1 | 부분 | goal 게이트 정책 | 사용자 턴마다 goal 카드 |
| K128 | zoom 오배송 (docId 부재) | 칩 클릭 → 다른 GH 문서로 전달 → 선택 0 + **무고한 문서의 선택 해제** | 08-10 H | 수정(`/canvas/focus`에 docId, 미등록 400), 08-10 | `ApiModels`, `Program.cs`, `client.ts` | 칩 클릭 후 다른 문서 선택 해제 |
| K129 | `undefined 선택` 칩 문구 | 서버 `{result:{…}}` 봉투를 패널이 최상위에서 읽음 | 08-10 H | 수정(client 언랩 + `framed`/`skipReason`), 08-10 | `client.ts`, 칩 | 칩 문구에 `undefined` |
| K130 | 환각 instanceId GUID (ghfocus 마커) | `a10c0001-3d5d-4aa0-8a01-000000000001` 류 패턴 GUID | 08-10 H | 수정(재발 없음, 08-10 R11 10/10 실재) | house-rules, `messageMarkers.ts` | 등차/패턴 GUID가 마커에 |
| K131 | Rhino 상태 칩 부재 / GH 칩 오도 | 헤더에 Rhino만 브랜드마크 배경색; GH 파란색=경로만 앎 | 08-10 E3 | 수정(StatusChip + 툴팁), 08-10 | `App.tsx` | — |
| K132 | 컴포저 높이 30px 점프 / 폭 15.98px 흔들림 | 선택 0↔n에서 스트립 마운트/언마운트; `scrollbar-gutter` 미설정 | 08-10 A (L11/L12) | 수정(항상 렌더 레일 + `scrollbar-gutter: stable`), 08-10 | `SelectionRail.tsx`, `styles.css` | 폭·높이 흔들림 재보고 |
| K133 | 첨부 칩이 핀과 다른 줄 | `.attachment-strip`이 `.selection-rail` 아래 별도 flex 행 | 08-11 피드백2 / W10 | 수정(단일 컨텍스트 행, `styles.css:1340`) | 패널 CSS | 첨부가 다시 별도 줄 |
| K134 | Rhino/GH 고정이 통째 덮어쓰기 | `pinned` 슬롯 1개, 해제도 전부-아니면-전무 | 08-10 A | 수정(독립 고정), 08-10 | `SelectionRail.tsx` | 한쪽 고정이 다른 쪽을 지움 |
| K135 | 핀/선택에 docId 없음 | 다중 GH 문서에서 다른 문서 선택을 고정 | 08-11 W7-a | 수정 609ad11(`PinnedSelection.docId`), 08-11 | `types.ts`, `ApiModels` | 핀이 다른 정의를 잡음 |
| K136 | `GET /selection/current`가 보고 있지 않은 문서 반환 | 7분+ 지난 다른 문서 선택 반환 | 08-10 P2 | 부분 | 선택 컨텍스트 소스 | 선택 컨텍스트가 화면과 불일치 |
| K137 | "지금 선택한 거 인식 가능?" 반복 질문 | 사용자 메시지 패턴 | 07-27 backlog | 부분(레일·docId로 완화) | 선택 컨텍스트 | 같은 질문 재발 |
| K138 | bake 클릭 zoom 부재 → 결과 유실 | Data탭 bake/reference 클릭이 GH를 프레이밍 안 함; 이후 `void`로 FocusResult 폐기(unhandled rejection) | 08-11 요청2 · 08-12 감사 | 수정 28dbc33 + a741c81 + 58a1036, 08-11~13 (라이브 PASS) | `DataView.tsx`, `App.tsx:547` | bake 클릭 후 아무 문구도 안 뜸 |
| K139 | 알림 컬러 오분류 | 대기 카드가 빨강 "NEEDS ATTENTION"으로 표시 | 08-11 요청1 | 수정 28dbc33(waiting=파랑), 08-11 | `useSessionCompletion.ts` | 대기 카드가 빨강 |
| K140 | 카드 대기 중 세션 status=`working` | 사람 대기인데 스피너+WORKING | 08-10 P2 | 부분(알림은 waiting 분류) | 상태 투영 | 카드 대기 세션이 working |
| K141 | auto-tidy가 프로젝트 rules.md를 무시 | "Do not use auto-tidy layout"인데 109개 재배치 커밋(3회) | 08-10 F | 수정(서버 후크가 rules.md 재조회), 08-10 | `ProjectContextStore`, `LiveDocumentBackend` | opt-out 프로젝트에서 auto-tidy 커밋 |
| K142 | arrange_layout 툴 설명의 "opt-out서 완전 비활성"은 거짓 | 모델이 직접 부르면 opt-out 프로젝트도 실행·커밋 | 08-10 P1 | 열림 | `DynamicToolSpecs.cs:348` | opt-out 프로젝트에 모델 발화 arrange |
| K143 | 소스 노드 0열 쏠림 / 그룹 오분류 / 중심 정렬 | 109개 중 46개 0열; `GH_Group`이 일반 노드(폭 ≈1900px) | 08-10 F (L9/L10) | 수정(ALAP·그룹 컨테이너·우측 엣지), 08-10 | `CanvasLayout.cs`, `CanvasLayoutAudit.cs` | 0열 쏠림 재발 |
| K144 | 캔버스 재배치 미반영 | 이동 후 화면 갱신 없음(수동 nudge 필요) | 07-27 backlog(최다 좌절) | 수정(`ExpireLayout`+`Invalidate`), 07-27 | `MoveObjectsCoreAsync` | 이동 후 화면 미갱신 보고 |
| K145 | 선택 geometry를 파라미터로 재생성 | 사용자 곡선을 참조 대신 재작성 | 07-27 backlog | 수정(`canvas.referenceRhinoObjects` op + 지시문) | 어댑터·지시문 | 선택 객체를 재생성하는 페이로드 |
| K146 | 개구부 무시 / py→C# tree flatten | 지시문만 추가, 라이브 미검증 | 07-27 backlog | 부분(지시문) | house-rules | 개구부에 패널 생성, 데이터트리 평탄화 |
| K147 | 수동 Recompute가 프로젝트 MEMORY.md에 규범화 | 에이전트가 사용자에게 Recompute를 시킴 | 08-10 P1 / W2-d | 열림(메모리 정리 필요) | 프로젝트 context MEMORY.md | "Solution → Recompute 해주세요" |
| K148 | 채팅 코드펜스 미해석 / raw JSON 덤프 | 코드펜스 기호가 그대로 노출, 지문 16진수 벽이 대화창에 덤프 | 08-10 P2 | 열림 | 패널 렌더 | 대화에 raw JSON |
| K149 | ask 카드 "답변함" 이중 배지 / dismiss 부재 | approval엔 있고 ask엔 없음 | 08-10 P2 · 08-12 1.4-4 | 수정 58a1036(dismiss 배선), 08-12 | `AskCard.tsx`, client | 답변된 ask 카드 잔존 |
| K150 | 카드 JSON parse 실패가 조용히 null | ErrorBoundary에도 안 감; 사용자 신호 0 | 08-12 1.4-5 | 열림 | `ChatPane.tsx:612` | 카드가 이유 없이 안 보임 |
| K151 | ErrorBoundary 고착 | failed state reset 없음 + identity key 없음 → in-place 카드 교체 시 fallback 영구 | 08-12 W7-f | 열림 | `ErrorBoundary.tsx:19-40` | 카드 자리에 오류 UI 고착 |
| K152 | 승인 카드 확대 버튼 도메인 무시 | GH id를 Rhino `/focus`로 전송 | 08-11 W7-g | 수정 58a1036 계열, 08-12 | `ApprovalCard.tsx:179` | GH 항목 확대가 Rhino로 |
| K153 | focus 실패를 "의도"로 보고 | 실패 후에도 "Restore view" 표시 | 08-11 W7-h | 수정 a566c63(v20 실측 보고·ownerToken), 08-13 | `useFocusTarget.ts` | 실패한 focus에 restore 제공 |
| K154 | focus가 read로 위장한 live write | Read 리스 하에서 Hide/Lock 영구 변이, `changed:false` | 08-12 §Focus | 수정 a566c63(모드별 access, v20), 08-13 | `RhinoSceneFoundationAdapter:3243` | select 모드가 문서를 변경 |
| K155 | W7-c 원상태 미기록 | select 모드에서도 무조건 Show/Unlock, 스택에 미기록 → 영구 해제 | 08-12 검증(악화) | 수정 a566c63, 08-13 | focus 스택 | focus 후 숨김/잠금이 안 돌아옴 |
| K156 | 대량 승인 카드 UX (>50) | 3,000건 규모 승인 시 카드가 무용 | 08-13 결정 | 이연 | 승인 카드 | 항목 수백 개 카드 |
| K157 | 권한 모드 부재 (삭제·wire 상시 차단 불만) | 사용자 불만 "승인이 자꾸 안 됨" | 08-11 요청3 | 수정 cc47b79(review/standard/fullAuto + standing consent), 08-14 | 권한 사다리 | 표준 모드에서 카드 폭증 |
| K158 | 시각 검증(스크린샷) 부재 | 캡처 op 0건, 툴 결과 텍스트 전용 | 08-10 F | 수정 795bf8a(`rhino_view_capture`, v22), 08-18 | 브리지 op + localImage | 시각 검수 없이 형상 오류 통과 |
| K159 | 사적 검산(스크래치 실행) 부재 | 컴파일·이름·캐스트 오류를 제출 후에야 발견 | 08-19 감사 §4 | 수정 823a9c1(workspace-write scratch + network), 08-19 (T3 PASS) | codex sandbox 정책 | `os error 2` 셸 실패 = workspace-write 파손 |
| K160 | 웹 접근 부재 | 참조 자료 조회 불가 | 08-19 | 수정 15439c8(codex native web_search), 08-20 | 세션 설정 | — |
| K161 | 자기 model/effort 보고 불가 | "codex status" 정보 미노출 | 07-27 backlog | 열림 | 상태 투영 | 모델이 자기 설정을 모름 |

## 9. 기타 (하네스·관측·배포·위생)

| ID | 이름 | 증상 시그니처 | 최초 식별 | 상태 | 수정 범위 | 회귀 인지법 |
|---|---|---|---|---|---|---|
| K162 | 예외 스택 미기록 / host.log 부재 | 콘솔 출력이 부모에서 버려짐, Message만 기록 | 08-13 meta-audit | 수정 33bfa01(host 파일 로거·job-exception 스택), 08-14 | `Vino.AgentHost` 로깅 | 예외에 스택 없음 |
| K163 | 로그에 버전/프로토콜 스탬프 없음 | problem-log 레코드 식별 불가 | 08-13 | 수정 33bfa01(v=0.1.0-alpha.7·protocol=21), 08-14 | `ProblemLog` | 스탬프 없는 레코드 |
| K164 | export/버그리포트 기능 없음 | 사용자가 로그를 낼 방법 없음 | 08-13 | 열림 | — | — |
| K165 | 원격 수집 인프라 전무 | 옵트인 텔레메트리 미구현 | 07-27 #7 | 이연 | — | — |
| K166 | 카드 엔드포인트 통합테스트 0 / HTTP route harness 0 | `tests/` 전체에 WebApplicationFactory 0 | 08-11 W8 · 08-12 §1.3 | 부분 | 테스트 하네스 | 회귀가 유닛에 안 잡힘 |
| K167 | 5단 머지 게이트 미구현 (W11-c/d/e) | CI가 "선언↔스키마" 2단만 강제 → ORPHAN/HALF_WIRED 통과 | 08-11 capability-integrity | 이연 | `DynamicToolSchemaCoverageTests` | 새 기능이 규범/렌더러 없이 머지 |
| K168 | 고아 라우트 `POST /runtime/stop-current` | 코드·테스트 caller 0 (**HEAD 확인**) | 08-11 · 08-12 | 열림 | `Program.cs:1258` | — |
| K169 | dead code 18건 sweep | ProblemDossier·SessionOrderBook·ProjectHomeLayout 등 production caller 0 | 08-12 §2 | 이연 | 다수 | — |
| K170 | 옵션 파사드 (CodexTurn 4옵션) | `Parse`에 바인딩 없음 = "설정 가능한 척하는 죽은 knob" | 08-11 · 08-12 | 이연 | `AgentHostArguments.cs` | 옵션 지정이 무시됨 |
| K171 | SSRF 갭 (이미지 URL fetch) | redirect 자동 허용 + 리터럴 IP만 검사 | 08-11 W7-e | 수정 d9357de, 08-11 (CGNAT/IPv6 잔여는 부분) | `ImageUrlAttachmentFetcher.cs` | 사설망 주소로의 fetch |
| K172 | 첨부 동시 인코딩 메모리 상주 | `Promise.all(encodeAttachment)` | 08-11 W7-j | 수정 6770038(순차), 08-11 | 컴포저 | 대용량 첨부 시 패널 정지 |
| K173 | 삭제 시 draft 유실 | 서버 삭제 성공 전 `clearDraft` | 08-11 W7-i | 수정 6770038, 08-11 | `ChatPane` | 삭제 실패 후 드래프트 소실 |
| K174 | `.gitignore` 확장자 변형 구멍 | `*.3dm`이 `.3dmbak`/`.3dm.rhl`을 안 막음 (작업트리 노출 실사례) | 08-11 W7-k | 수정 2d8cd9d, 08-11 | `.gitignore` | 바이너리/락 파일 트래킹 |
| K175 | mutation/refetch 실패 결합 + 상태 역행 | GET 실패 시 UI 롤백; 폴링에 in-flight guard·monotonic seq 없음 | 08-11 W7-d | 부분(retractLast 잔존) | `useRuntime.ts:303` | 쓰기 성공 후 UI가 옛 상태로 |
| K176 | 벤치 하네스 결함군 | 무필터 Rhino kill / idle 12s 미확인 / prep turn 레이스 / 단일 샘플 liveness / PS 5.1 배열 언롤 / BOM 없는 .ps1 | 08-15~08-21 | 수정(다수: e139e06·8a6f7b4·4010583·5f25511·a9d4998) | `scripts/bench-*.ps1` | 벤치 셀이 사용자 Rhino를 죽임 |
| K177 | dev-wave가 ask 카드 pending을 PASS로 판정 (F3) | 턴이 `idle`+assistant 0인데 `gate: PASS` | 08-13 live-gate F3 | 수정 786a26c, 08-13 | `dev-wave.ps1` | 게이트 PASS인데 실제로는 질문 대기 |
| K178 | bake_manager 보조 입력 비옵션 (F4) | `Input parameter layer failed to collect data`, 출력 빈 채 런타임 오류 0 | 08-13 live-gate F4 | 수정 786a26c(payload-guide `optional` + 스킬 헤더), 08-13 | 스킬 | 같은 문구 |
| K179 | 스킬/툴 실사용 0 스코프 공백 | Rhino-scene·레이어 큐레이션 19종이 1,138잡서 0건; `bakeGeometry` typed op 실사용 0 | 08-11 · 08-21 | 열림(정보) | — | 여전히 0건 |
| K180 | 한/영 토글 라이브 미검증 | 배선 완결, 검증 기록 없음 | 08-13 meta-audit | 열림 | `ProjectContextStore` | 응답 언어 불일치 |
| K181 | 릴리스 차단 결정 5건 | 서명·태그·매니페스트·지원 이메일·타머신 설치 | 08-13~14 | 이연 | `docs/release-checklist.md` | — |

---

## 08-26 시점 **열림/이연** 목록 — 이들은 alpha.7 로그에 **나오는 것이 정상**

W2 필터가 이 항목들을 "기지"라고 지워서는 안 된다. 발견되면 **빈도·심각도·최신 증거만 갱신**한다.

**P0/P1 후보 (사용자 체감·안전)**
- K066 브로커 생성 컴포넌트 volatile 미초기화(solve 완결) — 검출만 출하, 근본 미수정
- K068 "outputs empty WITH ISSUES" 오경보 (08-26 실측)
- K038b 카드 무관 턴의 `could not recover an assistant response` + 세션 blocked (08-22 ×2)
- K086 GH 모달 브레이크포인트 — TypeName 미가드 잔존(`:1387`)
- K092/K093 447MB 백업 스톨·무한 누적 / 캔버스 편집 백업 갭 (Modified 게이트는 반증됨)
- K047/K048 halt 래치 자기 해제 · 재시작 후 halted 복귀
- K107/K108 Claude MCP 25K 캡 spill + 복구 경로 부재 (이번 라운드의 계기)
- K061/K062 `script:` 스코프 캡 면제 · 소스 부분 읽기 툴 전무
- K121/K122 ask_user 규범 0줄(HEAD 확인) → 프로즈 질문·과잉 질문
- K099 하드 크래시(coreclr) — 로그에 흔적 없음이 특징

**P2 (마찰·위생)**
- K009 autoUpstream 숨은 옵션 · K010 predicate 문서 stale · K011 카드 timestamp 비대칭
- K013 세션 생성 컴포넌트 첫 schema 쓰기 거절 · K020 declared-predicate 적용여부 미표기
- K021 계약 다중 서술 · K022 payload 인자명 오류 · K063 상시 컨텍스트 97KB · K064 JSON 이스케이프
- K031 docKey reopen 리맵(의도적 미구현) · K034 canvas.move all-or-nothing · K035 grant 지문 핀
- K040 무진행 상한 부재 · K045 카드 단일 슬롯 · K050 재설치가 턴 삼킴 · K053 첨부 영속
- K054 오리진 파편화(결정 대기) · K055 패널 401 자동 회복
- K069 predicate 비우기 역설 · K070/K071 세맨틱·사문 predicate · K074 예측 게이트 오차
- K075/K076/K077 레이아웃 감사 타이밍·품질·실패 은폐 · K078 지표 날조 · K079 RR 3중 보고
- K080 visual-review 목표 문구 · K081 캡처 502 · K105 캡처 봉투 비일관 · K106 Refresh 미도달
- K084/K085 teardown 중첩 펌프·패널 워치독 · K109 Claude 오류 라벨
- K116 effort xhigh 기본 · K117 serviceTier fast · K127 goal 카드 남발 · K136/K137 선택 컨텍스트
- K142 arrange opt-out 설명 거짓 · K147 수동 Recompute 메모리 규범 · K148 raw JSON 덤프
- K150/K151 카드 parse 침묵·ErrorBoundary 고착 · K156 대량 승인 UX · K161 자기 설정 보고
- K164/K165 로그 export·텔레메트리 · K166/K167 테스트 하네스·5단 게이트
- K168 고아 라우트 · K169 dead code · K170 옵션 파사드 · K175 refetch 결합 · K179 스코프 공백
- K180 한/영 검증 · K181 릴리스 결정

**반증된 가설 (같은 프레임으로 재보고 금지)**
- K067 "EnableSolutions가 파일에 꺼진 채 저장" — 앱 전역 static이라 불성립
- K087 "45s = 무거운 solve" — 실체는 GH 모달(K086) 또는 예산 초과 자체
- `FileWriteOptions.UpdateDocumentPath`가 문서를 개명한다 — 기본값 false, `doc.Path` 불변(K095의 진짜 기전은 `EndSaveDocument` 핸들러)
- "브로커가 예외를 무조건 Failed 분류"(GPT 주장10) · "dotnet build가 stale 패널 포함"(주장23) — 오분석
- "resume에 dynamicTools 미탑재로 approval_request 미가시" — 모델 환각
- "Vino가 모델 옆에 임시 파일을 만들어 read-only 유발" — 반증(K097이 실체)
