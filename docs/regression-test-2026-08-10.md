# 회귀 재현 테스트 — 2026-08-10 (HEAD 5620cef)

실사용 로그에서 "수행 자체가 실패했던 케이스"를 전수 채굴하고, 같은 파일·같은 명령으로 HEAD 빌드에서
재현해 해결 여부를 판정한 라운드. **진단 전용 — 이 라운드에서 코드는 수정하지 않았다.**

## 방법

- **채굴**: 활성 프로젝트(457FDB, 260729 심의 모델링) 1,112잡 + 타 실사용 프로젝트 428잡 +
  problem-log + 사용자 메시지 전수 분석 → 재현 대상 케이스 R1~R11 추출.
- **코드 검증**: docs/issue-triage-2026-08-10.md 4-C의 수정 25건 + 최신 3커밋(07389a0/70f7f9b/5620cef)을
  HEAD에서 정적 검증(서버/어댑터/패널 3방향).
- **라이브 재현**: HEAD 풀 빌드(.NET 738 + 패널 59 테스트 그린) → 설치(robocopy /MIR) →
  사용자가 백업한 `example/` 세트(260729 심의 모델링.3dm 447MB + 브릿지 패널링.gh + 구조 분석.gh + 선정리.gh)를
  dev-mode로 열어 재생. 실사용 프로젝트의 context(rules.md/MEMORY.md/language)도 복제해 지시 환경 동일화.
- **규모**: 세션 3, 사용자 턴 20, 잡 39, 판정 71건(PASS 23 / PARTIAL 15 / FAIL 33 / INCONCLUSIVE 1),
  스크린샷 60+장. 총 소요 약 3시간.
- **스크린샷**: GH 캔버스·Vino 패널·Rhino 뷰포트 모두 창 열거+CopyFromScreen으로 캡처 가능함을
  확립(최소화/가림/흰화면 가드 포함). GH 캔버스 창은 1개라 문서별 캡처는 전환 후 촬영.

주의: 로그 분석 결과 **기존 설치본은 08-08이 아니라 08-10 16:26 빌드**였다(사용자 재설치).
16:30의 "AgentHost restart" 배너 = 그 재설치이며, 진행 중이던 승인 턴 2건(14:43/14:44)을 삼켰다(R8의 정체).

## 케이스별 판정 총괄

| 케이스 | 원 증상 (로그) | 이번 재현 결과 | 판정 |
|---|---|---|---|
| **R1** 배선절단 12연속 거부 | foreign-consumer disconnect 12회 거부, 사용자 수동 삭제로 종결 | 재발 없음. 거부 2건(append-only 1, 지문 1) 모두 1왕복 자력 복구 | ✅ 해결 |
| **R2** 승인이 브로커 미도달 | 전 로그에서 approval_card 저장 0건, 채팅 "승인" 4회 전부 증발 | **관측 사상 최초로 approval_card 저장·렌더·소비 완주.** approval_request 자발 호출 → 버튼 승인 → 서버 자동 턴 배달 → 24초 커밋. 거절도 인지 + MEMORY 학습 | ✅ 해결 |
| **R3** 45s 예산 → 프리즈 | 45s 초과 → RecoveryRequired, Rhino 6.7분 프리즈 | 재현됨 + **근본원인 발견: 무거운 solve가 아니라 GH 모달 브레이크포인트**(아래 P0-2) | ❌ 잔존(오진된 채) |
| **R4** 죽은 출력 green commit / 수동 Recompute 강요 | 하루 7회 recompute 요청, 빈 출력이 초록 커밋 | **두 문서 모두 100% 재발.** 원인 재규정: 브로커 생성 컴포넌트는 volatile data가 초기화되지 않음(슬라이더조차 값 6.0·출력 0). solver 토글 가설은 기각. 라이브 데이터 파괴도 관찰(PanelsOut 1120→0) | ❌ 잔존(악화 확인) |
| **R5** 해상도 게이트 루프 | 4회 반복 차단, 사용자 수동 개입으로만 탈출 | 게이트 발화 자체는 정확(1회, 9ms, 사전 거부). 그러나 **탈출이 원리적으로 불가** — established 분기가 죽은 코드(P0-6) | ❌ 잔존(재규정) |
| **R6** 정리 품질 | "정렬도 안 맞고 위치도 이상한데" | 모델 손배치는 규범에 근접(우측 엣지 정렬 달성, 단 이웃 그룹과 119×88px 겹침). **서버 arrange_layout은 문서를 악화**(최장 wire 2942→6290px)시키고 그것을 already-tidy라 부름. 감사는 이동 전 좌표를 측정(P0-4 실증) | ⚠️ 부분 |
| **R7** assistant 응답 소실 | 오늘 3건, goal 카드 턴과 상관 | **결정론 규명: 카드(goal/ask/approval) 호출이 턴의 마지막 툴이면 100% 소실**(6/6). 카드 뒤에 다른 툴이 오면 정상(3/3) | ❌ 잔존(조건 특정) |
| **R8** 재시작이 턴 삼킴 | 승인 턴 2건 105분 무응답 후 배너만 | 원인 확정: 16:26 사용자 재설치. 이번 런에서는 인위 재현 생략 | 📌 원인 규명 |
| **R9** C# 컴파일 반복 실수 | 'out' 예약어, object 언박싱 | 사전 거부 정상 작동(write 이전 차단). 이번 런 재발 1건도 1왕복 복구 | ✅ 허용 수준 |
| **R10** 원장 기록 누락 | 방금 만든 컴포넌트에 "has not written it" | **DB로 확증**: RecoveryRequired 경로로 생성된 컴포넌트는 원장에 생성 행이 없음(정상 5행 vs 1행) | ❌ 잔존 |
| **R11** 환각 GUID | 존재하지 않는 GUID 제출 | 재발 없음(T0에서 GUID 10/10 실재, 그룹 컨테이너 정확 제외) | ✅ 해결 |
| **H** zoom 오배송 (GH 문서 다중) | 칩 클릭 → 엉뚱한 문서 + `undefined 선택` | 4 서브케이스 전부 명세대로: otherDocumentShown 스킵(오배송·선택해제 0) / 같은 문서 framed=true 줌 / docId 생략 400+등록목록 / missing 200+missingCount | ✅ 해결 |
| **E2** 문서 신원 오염 | projectName ".model.3dm" | 오염 없음(projectName=review-model). GH 백업(definition.gh)도 실제 생성 확인 | ✅ 해결 |

## 신규 발견 결함 (이번 라운드에서 처음 드러난 것)

### P0

1. **ask 카드에 답하면 세션이 영구 사망한다.**
   카드가 뜬 턴은 완료 신호를 내지 않아 세션이 `working`에 고정되고, 그 상태에서 `PUT /ask`로 답하면
   `queued`에 영구히 갇힌다. resume 무효, retract-last로 idle 복귀해도 새 메시지가 다시 queued에서 멈춤 —
   **그 세션과 대화 컨텍스트를 통째로 버려야 한다.** S2가 이렇게 죽었고 S3에서 독립 재현.
   (T1의 S1은 카드 대기 중 상태가 blocked였기에 정상 배달 — 상태 의존적.)
   리포에 같은 증상의 흔적 주석 존재: `SessionOrchestrator.cs:199-202` "left the session hung in 'queued'".

2. **"45s 예산 초과"의 실체는 Vino 자신이 유발하는 Grasshopper 모달 브레이크포인트다.**
   GH Button(`a8b97322-2d53-…`) 생성 시 어댑터가 `GH_Param<T>.TypeName`을 읽는 순간 GH가
   "InstantiateT() cannot be called … Cannot create an instance of an interface(IGH_Goo)" 모달을 띄우고,
   그 모달이 브리지를 무한 블록한다(Rhino는 전 구간 Responding=True, HTTP 25ms — OS 프리즈 아님).
   사람이 닫기 전까지 474초, 읽기 전용 snapshot조차 45s 타임아웃. **남은 Button 때문에 이후 모든 canvas
   연산(읽기 포함)마다 모달이 재발**(1잡에 14개)해 문서가 사용 불가로 오염된다. 45s 메시지의
   "무거운 solve를 줄여라"는 오진을 사용자·모델 모두에게 주입한다.
   후보 지점: `GrasshopperCanvasFoundationAdapter`의 TypeName 접근 방어 + 모달 감지 계층.

3. **브로커가 만든 컴포넌트는 volatile data가 초기화되지 않는다 (R4의 진짜 원인).**
   신규 Number Slider조차 값 6.0이 박혀 있는데 출력 DataCount=0. `EnsureSolverEnabled` /
   `EnableSolutions=true` 수정은 이 증상을 건드리지 못한다(재활성화 보고 0건, EnableSolutions는
   앱 전역 static이라 "파일에 꺼진 채 저장" 가설 자체가 성립 불가). 여기에 실행 기본 수용 술어가
   `runtimeErrorAbsent` 하나뿐이라 빈 출력이 6번 초록 커밋됐고, T1에서는 **살아있는 문서 데이터가
   실제로 파괴**됐다(d2050004 PanelsOut 1120→0, 비스크립트 상류는 생존 → 원래 빈 문서 아님).
   모델이 `OutputCountInRange` 술어를 스스로 붙이면 정확히 잡히는데, 서버 실패 메시지는 오히려
   "acceptancePredicates를 비우라"고 지시한다.

4. **CanvasLayoutAudit이 정리 '후'가 아니라 '전' 좌표를 측정한다** (`LiveDocumentBackend.cs:1211`).
   라이브 실증: 같은 시드로 arrange 2연속 호출 — 1차 보고 longWires **3** vs 실제 커밋된 배치 재계산 **34**
   (2차 already-tidy 분기가 문서 전체를 재며 34를 보고). 툴 설명의 "committed arrangement에서 서버 측정"은
   현재 사실이 아니다. 덤으로 **arrange가 실제로 만든 배치는 문서를 악화**시켰다(공유 파라미터를 소비처에서
   6,290px 밖으로; rules.md의 "소비처 바로 왼쪽" 규범과 정반대) — 그리고 그 상태가 서버 레이아웃의 고정점이라
   2차 호출은 already-tidy를 선언한다.

5. **카드로 끝나는 턴은 100% assistant 응답이 소실된다 (R7의 결정론).**
   goal/ask/approval 호출이 턴의 마지막 툴이면 6/6에서 "Codex reported completion, but Vino could not
   recover an assistant response" system/error가 카드 바로 위에 빨간 배너로 렌더된다. 카드 뒤에
   inspect 등 다른 툴이 이어지면 정상(3/3). 정상 흐름(카드 발행)이 매번 오류처럼 보인다.

6. **첫 실행 해상도 게이트의 established 분기는 도달 불가한 죽은 코드다 (R5의 진짜 원인).**
   `PreflightExecuteCost`는 `ValueFingerprint` 유무로 상한 10,000 vs 2,000,000을 가르는데,
   `valueJson`은 `GH_NumberSlider`에만 생성된다(`GrasshopperCanvasFoundationAdapter.cs:1145,1168`).
   라이브 두 문서 237객체 전수: ValueFingerprint 보유 = 전부 슬라이더, 스크립트는 100% null.
   → 스크립트 컴포넌트의 실효 상한은 영구 10,000이고, 02B(1,408,000 elements)는 **Vino로는
   최대 해상도 실행이 영구히 불가능**하다. 사용자가 겪은 건 "루프"가 아니라 구조적 불가였다.

### P1

- **goalEnabled 플래그가 역동작**: false인 S1은 사용자 턴 4/4에서 goal 카드 발화(GUID 지정 삭제 한 줄에도),
  true인 S2/S3는 0회. 요청 3건 처리에 카드 응답 8회(goal 3 + ask 1 + approval 2 + 재개 2)가 든 왕복 세금의 주범.
- **RecoveryRequired의 3중 거짓 보고**: 잡은 "Applied: none/Unknown outcome"이라 했지만 Button은 실제
  커밋됐고(181→182), 원장에는 생성 행이 없으며, 후속 턴에서 에이전트는 stale 스냅샷을 근거로 사용자에게
  "생성되지 않았다"고 단정했다(거짓 음성). 세 관측점이 전부 다른 답을 냈고 틀린 쪽이 사용자에게 전달됐다.
- **halt 래치가 실질 차단을 못 한다**: RecoveryRequired 89초 만에 에이전트가 `recovery_resume`으로 자기 해제.
  브리지가 여전히 막힌 상태에서 새 사용자 메시지 POST가 202로 접수됨.
- **447MB 백업의 UI 스톨 실측 12~15초**: change_submit 43콜이 이봉분포(30콜 ≤444ms / 13콜 6~15s),
  느린 13콜 합 162.9s = tool-handling 총량의 96%. 20초 스로틀마다 재발, 크기 상한·Modified 게이트 없음.
  백업 무한 누적(이미 893MB, GH 문서 수만큼 동일 모델 중복)도 코드 검증에서 확인.
- **수동 Recompute가 프로젝트 MEMORY.md에 규범으로 굳어 있다**: 파이프라인(P0-3)을 고쳐도
  에이전트는 학습된 메모리 때문에 계속 사람에게 Recompute를 시킬 것. 수정 배포 시 메모리 정리 필요.
- **거절/ask 답변은 세션이 paused면 영구 유실**(코드 검증: `DeliverCardAnswerAsync` 반환값을 세 호출부 모두
  무시, 거절은 재전달 블록이 없음). 이번 라이브에서는 blocked 상태라 통과 — 상태 의존적 지뢰.
- **arrange_layout 툴 설명의 "opt-out 프로젝트에서는 완전히 비활성"은 거짓**: post-turn 자동 tidy만 막히고
  (그건 정상 동작 확인 ✅), 모델이 직접 부르면 옵트아웃 프로젝트에서도 실행·커밋된다.
- **모델이 감사 지표를 날조**: 존재하지 않는 필드명(columnCrowding.overlapPairs 등)으로 지표를 코드펜스
  제시했고 그중 overlapPairs:0은 실측(119×88px 겹침)과 모순.

### P2 (UI/UX 소결함, 라이브 관찰)

- 카드가 사용자 답을 기다리는 동안 세션 status가 `working` → 패널이 스피너+WORKING 표시(사람 대기인데).
- 채팅 마크다운 코드펜스 미해석(``` 문자 그대로 노출), 잡 결과 raw JSON(지문 16진수 벽)이 대화창에 덤프.
- 토스트가 카드·오류를 안 알리고 마지막 assistant 메시지만 고집.
- ask 카드 헤더 "답변함" 이중 렌더. 답변된 ask 카드 dismiss 경로 없음(승인 카드엔 있음).
- 확정된 goal이 하단바에 "대기|취소"로 고착(턴이 비정상 종료되면 confirmed에서 영원히 멈춤).
- `nothingFound` 경로가 `canvas.Refresh()`에 도달하지 못해 해제된 선택이 화면에 계속 칠해져 보임.
- `GET /selection/current`에 docId가 없고, 실제로 보고 있는 문서와 다른 문서의 선택을 7분+ 반환
  (최근 갱신·비어있지 않은 GH 선택 우선 규칙이 "사용자가 보는 문서"와 무관) — 다중 문서에서 고정(pin)이
  엉뚱한 정의를 잡을 수 있음.
- `/dev/*` 관측 엔드포인트와 `/runtime`의 revision이 기본 문서(doc1)만 봄 — 다중 문서 테스트 하네스 갭.

## 4축 평가 요약

**속도** — 병목은 여전히 모델 추론(턴의 91.3%). 턴 평균 98s / p50 29.1s / 최대 436s.
Vino 자체 처리에서 유일하게 큰 비용은 447MB 백업 스톨(12~15s × 20s 스로틀)이며, 카드 UX가
요청당 턴 수를 2배로 만든다(카드 낀 요청 = 카드 턴 + 재개 턴). Blocked→즉시 재성공 왕복 세금은
2쌍(+6s/+7s)으로 과거(42% 지배)보다 크게 줄었다.

**의도 부합** — 환각 0(T0 GUID 전수 실재), 구조 술어(ObjectExists/WireExists 등) 6/6 정확.
그러나 실행 결과 검증이 사실상 없고(P0-3/5), T1a는 명령의 절반(기존 wire 제거)이 미수행,
T6은 거짓 음성 보고. "브로커 보고 ≠ 문서 실제"가 이번 라운드의 관통 주제다.

**안전성** — 크래시 0, R1/R2류 교착 0. 남은 리스크는 P0-1(세션 사망), P0-2(모달 웨지+문서 오염),
데이터 파괴(P0-3), halt 래치 약화. 447MB 실무 파일에서 3시간 연속 구동 자체는 안정적이었다.

**UI/UX** — 승인 카드 흐름(제안→체크박스→승인/거절→자동 재개→사유 보존→메모리 학습)은 이번 라운드
최대 성과로, 설계 의도대로 완주한다. zoom docId도 명세대로. 반면 카드 턴의 빨간 오류 배너(P0-5),
WORKING 오표시, goal 카드 남발(P1 역동작)이 그 성과를 체감상 깎아먹는다.

## 스크린샷 기록 (질문에 대한 답)

**가능하다.** 창 열거 + CopyFromScreen 기법으로 GH 캔버스/Vino 패널/Rhino 뷰포트를 자동 캡처했고
(최소화 -32000 가드, 가림/흰화면 판정, 촬영 후 육안 교차확인 절차 포함), 이번 런에서 60+장을 기록했다.
대표: 승인 카드 렌더(t1b-approvalcard-panel), ask 카드(t2-s2-askcard2-panel), 모달 브레이크포인트
(t6-breakpoint-dialog), zoom 전후(t5-04/05), 정리 전후(t4a-before/after, t4b-after-arrange).
제약: GH 캔버스 창은 1개(제목이 활성 정의를 따름) → 문서별 캡처는 전환 후 촬영. 플로팅 패널은 Rhino
비활성 시 숨겨지므로 캡처 전 SetForegroundWindow 필요.

## 증거물 위치

- 런 디렉터리: `artifacts/dev-loop/20260810T190637Z-verify/` (staged 파일, runtime, histories git)
- 판정 원장: `…/evidence/results.jsonl` (71건) + `…/evidence/final-jobs-summary.md`
- 스크린샷: `…/evidence/shots/` (60+장, *-capture.json에 판정 메타)
- DB 사본: `…/evidence/final-db/` (live-jobs 39행 / runtime.db / resource-ledger 143행 / problem-log 209줄 / authoring-latency 185콜)
- 정찰(채굴+코드 검증) 원본: Claude 세션 scratchpad `recon-agent0~5.json`

## 테스트 인프라 노트 (다음 라운드를 위해)

- `artifacts/verify-2026-08-10/launch.ps1`·`checks-http.ps1`은 UTF-8 BOM이 없어 PS 5.1이 ANSI로 읽음 —
  한글 경로가 깨져 즉사. BOM 사본을 만들어 실행했다(리포 원본은 미수정, 수정 권장).
- SQLite 사본은 `.db`+`-wal`+`-shm` 3종 세트로 복사해야 한다(WAL 미체크포인트 시 0행으로 보임).
- `/dev/snapshot` 응답은 BOM으로 시작(JSON.parse 전 제거 필요).
- 카드 응답(PUT approval/ask/goal)은 서버가 스스로 턴을 배달한다 — 추가 재촉 메시지를 보내면 턴이 이중으로 돈다.
- GH 문서 전환은 메뉴바 우측 문서 라벨(≈1080,45) 클릭 → 썸네일 드롭다운.
- dev-latency.ps1은 `loop-state.json`을 요구(run 디렉터리에 셰임 생성으로 해결).
