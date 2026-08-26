# 기능 경로 무결성 감사 — 2026-08-11

"버그 찾기"가 아니라 **"우리가 만든 기능이 실제로 끝까지 배선·발화되는가"**를 묻는 감사.
선언된 모든 기능 표면(툴·엔드포인트·계약 필드·플래그·predicate·지침)을 5단 경로로 추적하고,
**실사용 1,138잡 로그와 대조**해 "이론상 되는 것"과 "실제로 한 번이라도 발화한 것"을 갈랐다.
Opus 6방향(계약 필드 1건은 재실행). 코드는 수정하지 않았다.

**5단 경로**: 선언 → 배선(핸들러) → 발화 조건(트리거/규범/호출부) → 결과 도달 → 테스트.
**분류**: WIRED / ORPHAN(선언됐으나 트리거 없음) / HALF_WIRED(발화하나 결과 미도달) / STALE(선언·문서·구현 불일치) / UNTESTED / BROKEN.

---

## 왜 이게 안전 문제인가

기능이 "코드에 있다"는 것과 "실제로 발화한다"는 것은 다르다. 발화 안 하는 기능은 팀이 **"이 기능 있으니 안전"이라고 착각**하게 만들어 오히려 위험하다. 이번 감사가 찾은 건 대부분 이 종류다 — 승인 게이트가 있다고 믿는데 카드가 안 떠서 파괴적 작업이 그냥 통과하는 식.

---

## 헤드라인 안전 발견 (실사용 로그로 정량화)

### 1. 커밋의 43.6%가 "빈 출력인데 초록 커밋" — 안전 술어가 있는데 안 붙는다

**실측: 커밋된 920잡 중 401잡(43.6%)이 커밋 메시지에 `output(s) … empty`를 달고도 초록 커밋됐다.**
(예: 잡 `642b0c81` — `console_log`/`posts`/`railing`/`fence`/`planes` 5개 출력 전부 빈 채 committed.)

- `OutputCountInRange`(빈 출력 방지 술어)는 **구현·단위테스트·스키마 노출·검증까지 전부 존재**한다
  (`Changes.cs:110`, `Verification.cs:164`, `DynamicToolSpecs.cs:853`).
- 그런데 **기본 부착 집합(`ApplyDefaultPredicates`, `ChangeSetValidation.cs:157`)에 없다.** create→objectExists,
  wire→wireExists, 나머지 전부 runtimeErrorAbsent만 붙는다.
- 출력을 내는 스크립트 계열 write(`executePython` 390건 + `updatePythonSource` 351건 = 741건)가 전부
  runtimeErrorAbsent 하나로만 검증된다 → "실행은 됐으나 아무것도 안 나온" 상태가 검증완료로 통과.
- 실사용에서 `OutputCountInRange`는 1,138잡 중 **7건**만 쓰였다(모델이 자발 선언할 때만).
- `DescribeCommitQuality`(`Verification.cs:357`)가 빈 출력을 **감지는 하는데** "정보성, 절대 상태 변경 금지"라
  아무것도 안 막는다. 감지하고 통과시키는 구조가 이 결함을 완성한다.
- 게다가 서버 실패 메시지와 house-rules는 "acceptancePredicates를 []로 비우면 표준셋 부착"이라 안내한다 —
  세맨틱 술어를 붙였다 실패하면 []로 되돌리라는 유인이 **죽은 출력을 초록으로 만드는 역설**을 실현한다.

이것이 08-10 라이브의 R4(죽은 출력 green commit)의 뿌리이고, **43.6% 규모로 실사용에서 실증됐다.**

### 2. 승인 게이트가 실사용에서 한 번도 발화·검증되지 않았다

- `approval_request` → `approvalGrantId` 경로는 코드상 완전(WIRED)하다.
- 그러나 **1,138잡 전부 approvalGrantId가 null.** 파괴적 쓰기가 grant 없이 통과했고, 사용자는 카드 버튼 대신
  "승인"/"4개 항목 모두 승인"을 **프로즈로 타이핑**했다.
- 이 프로젝트가 순수 GH 저작(rhino.* 파괴 op 0건)이라 승인 경로를 밟지 않은 탓이다. 즉 **"승인 게이트가
  있으니 안전"이 프로덕션 데이터로는 입증 불가** 상태 — 실전에서 end-to-end로 작동하는지 우리가 모른다.

### 3. ask_user — 클릭 카드가 있는데 규범이 없어 안 뜬다 (사용자가 스크린샷으로 지적한 그것)

- 툴·디스패치·엔드포인트 전부 배선됨. 그러나 **house-rules에 규범 0줄**이고, 오히려 경쟁 규범들이
  프로즈 질문을 강제한다("ASK BEFORE SOLVING … ask which are intended cantilevers" `:93`,
  "talk it through" `:110`). → 발화 트리거가 규범층에서 역방향으로 억제됨.
- `ComposeAskBlock`이 **부재**해서(goal/approval엔 있음) ask 답변이 매 턴 재주입되지 않고 1회 전달에만
  의존 → paused 세션이면 유실되는데 UI는 204(성공)로 표시.

---

## 기능 경로 무결성 원장 (표면별 요약)

### 툴 (23개 선언, 전원 디스패치 존재 — 진짜 갭은 "발화층")

| 툴 | 상태 | 근거 |
|---|---|---|
| change_submit / artifact_* / snapshot_read / component_catalog / connectWire류 | WIRED | 실사용 활발(change_submit 1,138잡) |
| goal_propose | WIRED | 실발화(confirmed 카드 2세션), 배달 프로즈 3건 실측 |
| memory_append | WIRED(규범 0) | house-rules 규범 없이도 발화 — 규범 없어도 되는 반례이나 발화가 모델 재량에 방치 |
| **ask_user** | **HALF_WIRED** | 규범 0줄 + ComposeAskBlock 부재. 실사용 0건 (P1) |
| **approval_request** | **WIRED이나 실사용 0** | grant 경로 완전하나 1,138잡 전부 grant 없음 (P1) |
| goal_score / structural_solve / layer_scheme_draft | UNTESTED-in-prod | 경로 완전하나 선행단계 다음으로 실무 미진행(각 0건) (P2) |
| recovery_resume / structural_extract / rhino_audit / rhino_layers / data_flow_read / inspect_outputs / arrange_layout / skill_read / data_read / rhino_list / job_status | WIRED | 대부분 정상 |

### predicate (enum 15종 / 스키마 12 / 실평가 6 / 자동부착 5)

| predicate | 상태 | 실사용 |
|---|---|---|
| runtimeErrorAbsent | WIRED | 796 제출 / 655 평가(25 실패 검출) — 유일하게 실효적인 기본 게이트 |
| wireExists/Absent, objectExists/Absent | WIRED | 자동 부착, 전건 pass |
| **OutputCountInRange** | **HALF_WIRED** | 구현·테스트 다 있으나 기본 미부착 → 7건만. 43.6% 빈출력의 방벽인데 안 붙음 (P1) |
| areaInRange/volumeInRange/geometryClosed/dataTreeBranchCountInRange/boundingBoxInRange | ORPHAN | 구현·단위테스트·스키마노출 완비, **실사용 0건**(태어나자마자 고아) (P2) |
| fingerprintEquals | ORPHAN | 노출되나 house-rules가 사용 금지 → 노출·규범 상충 (P2) |
| OutputEquals / BoundingBoxEquals / Custom | STALE | enum엔 있으나 Verify fail-closed + 검증 거부 + 스키마 미노출(3중 사문) (P2) |

### 플래그·옵션 (설정 파사드가 핵심 문제)

| 항목 | 상태 | 근거 |
|---|---|---|
| **GoalTokenBudget** | **ORPHAN** | 항상 null. `AgentHostArguments.Parse`에 바인딩 없음 — 주석은 "(config)"라 거짓 (P1) |
| CodexTurn* 4옵션(PollInterval/ReadTimeout/…) | ORPHAN | 파싱 코드 없음, 기본값(2s/10s/3/2)에서 못 바꿈 (P2) |
| **goal_enabled** | **HALF_WIRED** | native goal 하나만 게이트(최초 턴 1회). UI 토글 제거됨 → 신규 세션은 켤 방법 없음 (P1) |
| SetGoalEnabledAsync | ORPHAN | 호출부 0건 (P2) |
| ModelProfile/TaskClass enum | STALE | 항상 Standard(적응형 라우터 제거됨), UI엔 계속 투영 (P2) |
| MaxParallelTurns / model_profile(effort) / settings | WIRED | 정상 (대조군) |

### 계약 필드 (types.ts ↔ ApiModels ↔ Contracts)

| 필드 | 상태 | 근거 |
|---|---|---|
| /selection/current의 docId | ORPHAN(비대칭) | 서버가 데이터(`CurrentSelectionDocId`)를 갖고 SSE엔 실어보내는데 핀 엔드포인트는 안 방출 → 핀한 GH 선택이 문서 스코프 상실 (P1) |
| PinnedSelection.docId | ORPHAN(양쪽 부재) | ApiModels·types.ts 둘 다 없음. 다중 GH 문서서 핀 컴포넌트 소속 정의 불명 (P1) |
| goalEnabled | STALE/ORPHAN | 투영은 되나 입력 생산자·패널 소비자 0. 실DB: goal_enabled=1인 1세션은 구버전 클라 화석 (P1) |
| proposedAt/confirmedAt/askedAt/answeredAt | STALE(비대칭) | 서버가 채우고 **실DB에 실제 저장**되는데 types.ts에 필드 미선언 → 패널이 절대 못 읽음(데이터를 버림) (P2/INFO) |
| grantExpiresAt / framed / skipReason / askCard / approval domain·layerRow | WIRED | 생산·소비 3점 교차 확인 |
| RuntimeStatus / HostStateResponse 레코드 | ORPHAN(정의만) | **인스턴스화 0** — 서버가 상태를 계약 레코드가 아니라 익명 객체로 방출. C# 컴파일러조차 계약 형태를 못 지킴(드리프트의 근원) (INFO) |

### 엔드포인트 (계약 드리프트 — 공유 타입/코드젠 없음)

| 항목 | 상태 | 근거 |
|---|---|---|
| PUT /approval·/ask·/goal — 카드 답변 배달 | HALF_WIRED | `DeliverCardAnswerAsync` bool을 버려 paused 세션서 답변 유실 + 204 반환 (P1) |
| PUT /approval — layerScheme 분기 | HALF_WIRED | continuation 호출 자체가 빠짐 → 에이전트 정지 (P1) |
| goal 토글 (client.ts:49) | ORPHAN | 고아 JSDoc + 토글 엔드포인트 부재 (P2) |
| DELETE /ask, DELETE /goal | 부재 | approval만 dismiss 가능(라이프사이클 비대칭) (P2) |
| POST /runtime/stop-current, GET /layers, GET /messages | ORPHAN | 서버 라우트에 패널 호출부 없음 (P2/INFO) |
| POST /resume | WIRED | 계약(halt만 해제)과 구현 일치 |
| retract-last | UNTESTED | 테스트 0건 + 큐 대기 턴 인터럽트 사각 (P2) |

### OperationKind

- 실사용 14종 발화(connectWire 1035 / executePython 390 / updatePythonSource 351 / createComponent 300 …).
- **Rhino-scene·레이어큐레이션 19종은 이 프로젝트(1,138잡)에서 0건** — 최근 출하한 레이어 큐레이션
  (purge/moveObjectsToLayer/deleteRhinoLayer/…)이 대량 실사용 프로젝트에서도 한 번도 안 밟혔다.
  결함이 아니라 **스코프 공백**이나, 실전 회귀 신호가 0이라 미검증 위험(INFO).

---

## 시스템적 뿌리 — 여섯 표면이 모두 한 곳을 가리킨다

**빌드가 강제하는 건 "선언 ↔ 스키마" 패리티 한 겹뿐이다.** `DynamicToolSchemaCoverageTests`는
OperationKind/PredicateKind enum이 스키마에 노출되는지, recovery_resume·intent·approval 필드가 있는지만 본다.

강제하지 **않는** 것:
1. 각 툴에 "언제 부르라"는 **발화 규범**이 있는가 → ask_user·memory_append가 규범 0줄로 통과.
2. 각 카드류 툴에 **결과 재주입 렌더러**(Compose*Block)가 있는가 → ComposeAskBlock 부재로 "발화하나 결과 미도달".
3. 각 옵션이 **설정 경로**(Parse 바인딩)를 갖는가 → GoalTokenBudget·CodexTurn*가 "설정 가능한 척하는 죽은 knob".
4. 각 엔드포인트가 **클라이언트 호출부**를 갖는가 → 고아 라우트 3개 + 고아 JSDoc.
5. 각 predicate/op가 **실제로 밟히는 테스트**를 갖는가 → 세맨틱 술어 6종이 코드·스키마에 다 있어도 실사용 0건인 걸 아무도 못 잡음.
6. 카드 답변 배달 **실패가 표면화**되는가 → bool을 버려 유실이 204로 숨음.
7. 계약이 **컴파일러로라도 강제**되는가 → 서버가 상태를 계약 레코드가 아니라 익명 객체로 방출해
   `RuntimeStatus`/`HostStateResponse` 레코드가 정의만 되고 인스턴스화 0. types.ts↔ApiModels 동기화를
   지키는 게 사람 손(수기 types.ts + 수기 mock.ts)밖에 없음 → 서버 전용 타임스탬프가 계약을 안 건드리고 쌓임.

**결론: 기능은 "스키마 등재 + 디스패치" 2단만 있으면 CI를 통과한다. 그래서 ORPHAN/HALF_WIRED가 정상 통과하고,
ask_user 같은 "배선됐으나 발화 안 됨"이 계속 재생산된다.**

---

## 근본 처방 — "5단 머지 게이트"

기능은 스키마 등재로 끝나지 않는다. **선언 + 규범(트리거) + 디스패치 + 결과 렌더 + 테스트** 5단이 모두 붙어야
머지 가능하도록 빌드가 강제해야 한다. 구체 장치(전부 저비용 커버리지 테스트):

1. **툴↔규범 커버리지 테스트**: `DynamicToolSpecs.Create()`의 모든 툴 이름을 뽑아
   `HouseRules.Text ∪ PayloadGuide`에서 검색, "규범 언급 0"인 툴이 있으면 실패. 순수 읽기툴
   (snapshot_read/job_status/artifact_* 등)은 명시적 화이트리스트로 면제(면제 목록 자체가 리뷰 지점).
   → **ask_user·memory_append 재발을 막는 핵심 한 테스트.**
2. **카드↔ComposeBlock 테스트**: 카드류 툴(ask/goal/approval)마다 대응 `Compose*Block` 렌더러 존재를 강제.
   → "발화하나 결과 미도달"을 컴파일 게이트로 승격.
3. **predicate 4자 동기화 표**: 각 PredicateKind → [스키마노출·검증지원·기본부착여부·≥1 실경로테스트].
   예약/미구현 kind(OutputEquals 등)는 enum에서 분리하거나 attribute로 표식 → 계약 표면 = 실지원.
4. **옵션↔Parse 커버리지**: `AgentHostOptions`의 모든 프로퍼티가 `AgentHostArguments.Parse`로 설정 가능한지,
   아니면 상수로 명시됐는지 강제. → GoalTokenBudget·CodexTurn* 파사드 제거.
5. **엔드포인트↔클라이언트 커버리지**: 모든 /api 라우트가 client.ts 호출부를 갖거나 비-패널 화이트리스트
   (/health, /dev/*)에 있는지 강제. → 고아 라우트 + client.ts:49 제거.
6. **카드 답변 배달 실패 표면화**: `DeliverCardAnswerAsync` bool을 버리지 말 것(아키텍처 불변식).
7. **출력 내는 write는 의미 술어 기본 부착**: executePython/updatePythonSource 등에 관대한 outputCountInRange를
   자동 부착(아키텍처 불변식). → 43.6% 빈출력 초록통과 해소.

---

## 기존 수정 계획과의 연결

이 감사는 새 결함을 무더기로 만든 게 아니라, `docs/fix-plan-2026-08-11.md`의 여러 항목이 **한 뿌리의 증상**임을 보여준다:

- **W2(죽은 출력)** = 이 감사의 #1(43.6%, OutputCountInRange 미부착). 처방 6·7이 근본책.
- **W9(ask_user 프로즈)** = 이 감사의 #3(규범 0줄 + ComposeAskBlock 부재). 처방 1·2가 근본책.
- **W4/W5(카드·goal 정합성)** = 카드 답변 bool 유실, DELETE 비대칭, goal_enabled 고아. 처방 2·4·6.

→ 수정 계획에 **W11(기능 경로 무결성 하네스 = 5단 머지 게이트)** 를 추가한다. W11은 개별 결함을 고치는 게
아니라 **그 결함들이 다시 태어나지 못하게 막는 장치**다. 개별 수정(W2·W9·W4)을 하면서 그 자리에 해당
커버리지 테스트를 함께 심으면, 이후 새 기능이 자동으로 5단을 갖추게 된다.

---

## 방법 주기

- HEAD 5620cef, 읽기 전용. 리포·사용자 원본·라이브 DB 원본 미개봉.
- 실사용 대조는 457FDB8091063B0D(열린 프로젝트, 1,138잡)의 live-jobs.db/runtime.db/problem-log.jsonl을
  scratchpad 사본(.db+wal+shm)으로 복사해 node:sqlite readOnly로 판독.
- 정량 핵심: 커밋 920잡 중 401잡(43.6%) 빈출력 초록커밋 / approvalGrantId 0건 / predicate 6종만 발화 /
  세맨틱 술어 5종 0건 / 옵션 파사드 5종.
- 한계: 이 프로젝트가 GH 저작 전용이라 rhino.* 파괴 op·레이어 큐레이션·structural_solve 경로의 실발화는
  이 데이터로 확증 불가(스코프 공백) — 별도 실사용 프로젝트나 라이브 픽스처로 밟아봐야 한다.
