# A트랙 사후 분석 (T3 벤치 원장) — 2026-08-20

**목적**: 08-19 감사에서 나온 마찰 수정(A2 auto-fill·A3 지문·A4 advisory/absorb)과 사적 검산(workspace-write+network)이 실전에서 작동했는지를, T3 벤치(11 run, HEAD c746f8e 빌드)의 잡 원장·문제 로그·스크래치 디렉터리로 검증. 분류기는 8-19 감사와 동일. 데이터: `artifacts/dev-loop/20260820T13*~15*`.

## 결과 — 실패 클래스 전후 비교
| 클래스 | 기준선(전수 2,203잡) | T3 (26잡, Vino arm) |
|---|---|---|
| fp.auto-no-baseline | 114 (비커밋의 29.8%) | **0** |
| fp.auto-drifted | 30 (7.8%) | **0** |
| fp.stale-concrete | 57 (14.9%) | **0** |
| code.csharp-keyword (`out` 에코) | 10 | **0** |
| gate.cost-preflight 거부 | 10 (9 오탐) | **0** (거부 자체가 제거됨) |
| gate.liveness | 18 | 0 |
| fp.expectation-absent | 6 | 1 (중복 wire 방지 — 의도된 가드) |
| code.runtime-or-compile | 64 | 1 (`RhinoMath` 미해결 — 진짜 코드 오류) |
| pred.declared-unsatisfied | 9 | 2 (모델 선언 predicate가 잡음 — 검증의 정상 작동) |
| **비커밋 합계** | 383/2203 = 17.4% | **4/26 = 15.4%**, 그중 제약-제조 실패 0 |

핵심: 남은 실패 4건이 전부 "진짜"(코드 오류 1, 검증 적중 2, 중복 방지 1)다. **하네스가 만들어내던 실패 클래스는 관측 0.**

## 새 메커니즘의 직접 증거
- **auto-fill 8건** (problem-log `kind=auto-fill`, 전부 read expectation): 이전 빌드였다면 각각 Blocked + ~9초 전사 재제출이었을 왕복 8회가 소리 없이 흡수됨. 26잡 표본에서 8회 = 실질 절감 큼.
- **사적 검산 실전 사용**: run 3b80dacb의 세션 스크래치에 모델이 스스로 만든 `scratch/stage1.cs`, `stage2.cs`, **`validate.py`**(파라미터 수치 검산 스크립트) — 지시문만으로 유도된 자발적 검산 행동.
- **workspace-write + 네트워크 실측 통과**: 인수인계 §2.5 스모크(`scratch_check.py`) 결과 `calculation=10`, **`http_status=200`**(https 요청 성공). "os error 2"(sandbox 헬퍼 갭 신호)는 잡 메시지·host.log 어디에도 0건.
- 회귀 신호 0: 잘못된 auto-fill(쓰기 계열) 0건 — 기록된 8건 전부 read, 캐너리(Source/Io/Value 무행 거절) 우회 흔적 없음.

## 한계
- 표본 26잡(기준선의 1.2%) — fingerprint 계열 0은 "기회 부족"일 수 있으나, 기준선 발생률(전체 잡의 ~9%)이면 26잡에서 기대 ~2.4건. auto-fill 8건이 흡수를 직접 증명하므로 방향성은 확정, 정밀 비율은 사용 누적 후 재측정.
- B/C arm(베이스라인 MCP)은 Vino 원장을 거의 안 쓰므로 이 표는 Vino arm 중심. 셀 승패 비교는 벤치 세션의 채점 결과가 정본.

## 판정
**A트랙 라이브 게이트 PASS.** 사적 검산·웹·auto-fill·지문 재기준·advisory/absorb 전부 실전에서 의도대로 동작, 회귀 신호 없음. 남은 후속: 사용 누적 후 fingerprint 계열 재측정, cost advisory가 실제 프리즈를 못 막는 사례가 나오는지 감시.
