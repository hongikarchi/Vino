# T3 벤치 인수인계 — 2026-08-20

**받는 세션에게**: 이 문서는 T3 벤치를 돌리는 세션을 위한 체크리스트다. 08-19~20에 하네스가 크게 바뀌었고(마찰 제거 A트랙), 이번 T3는 **비교 벤치이자 그 변경들의 첫 라이브 게이트를 겸한다**. 벤치 절차 자체는 기존 계획대로 하되, 아래 확인 항목을 관찰·기록하라.

## 0. 전제 (시작 전 확인)
- **재설치 불필요**: HEAD `c746f8e` 기준 전체 A트랙 빌드가 이미 설치됨(08-20 11:48, `packages\8.0\Vino\0.1.0-alpha.7`, 패널은 기존 dist — 서버만 변경이라 무관). 다시 빌드·설치하지 마라. 단 시작 전 `git log --oneline -1`이 c746f8e 이후인데 서버 코드를 새로 커밋했다면 그때만 재빌드(`build-package.ps1 -SkipPanelBuild` — `npm ci`는 vite 잠금으로 깨져 있음, panel 빌드 금지).
- Rhino가 하나도 안 떠 있는지 확인(dev-loop는 떠 있으면 시작 거부).
- codex 쿼터 리셋: 13:35.

## 1. 이번 빌드에 들어간 변경 (벤치가 검증하는 것)
| 변경 | 기대 관찰 | 회귀 신호(즉시 보고) |
|---|---|---|
| **사적 검산**: 세션별 스크래치 cwd(`runtime\workspace\<sessionId>`) + sandbox `workspace-write` + 네트워크 | 모델이 제출 전 스크래치에서 스크립트 실행(폴더에 파일 생김), 코드 실패율↓ | 셸 명령 일제 실패 **"os error 2"** = Windows sandbox 헬퍼 갭 → workspace-write가 깨진 것. 폴백 = 커밋 823a9c1 revert |
| **A2 auto-fill**(845c66e): read/wire/execute-only/복구행 auto는 거절 대신 채움 | `fp.auto-no-baseline`/`fp.auto-drifted` Blocked가 사실상 0; problem log에 `kind=auto-fill` 레코드 | auto-fill 직후 사용자 수동 편집이 덮인 정황(캐너리: Source/Io/Value 무행 거절은 유지되어야 함) |
| **A3 지문**(c017201): RuntimeMessages 제외 + ledger 1회 재기준 | own execute 후 "drifted" 거절 소멸; 첫 잡에서 script 행들이 조용히 재기준 | 배포 직후 script 자원 stale 폭주(재기준 마이그레이션 실패 신호) |
| **A4**(c746f8e): cost 거부→`execute_cost_advisory`, `out` 흡수(`console_output_absorbed`), liveness 거절에 `Ready-made approval target` | cost/keyword로 Failed 0; 해당 진단이 커밋 메시지에 등장 | advisory만 남기고 진짜 UI 프리즈가 발생하면 즉시 보고(측정게이트·워치독이 못 잡은 것) |

## 2. 벤치 중 체크리스트
1. **각 셀 시작 전**: `runtime\workspace\` 아래 세션 폴더 생성 확인 (1줄 ls).
2. **Vino arm 진행 중**: 잡이 Blocked되면 메시지 클래스 기록 — `gptino:auto declined`가 나오면 어떤 분기인지(무행/타세션/드리프트) 그대로 복사. 이번 빌드에서 무행-wire/execute 거절은 **나오면 안 된다**.
3. **RecoveryRequired 발생 시**: 다음 잡이 no-baseline으로 막히는지(안 막혀야 정상 — RR 경로 ledger 기록이 신규).
4. **셀 종료 후**: run 디렉터리 경로와 `live-jobs.db`(+`-wal`,`-shm`) 위치를 결과에 남겨라 — 다른 세션이 제약별 실패 재분류(전후 비교)를 사후 분석한다.
5. **스크래치 행동 샘플 1회**(선택, 셀 사이 여유 있을 때): Vino 세션에 "제출 전에 스크래치에서 python으로 개수 검산하고 결과를 보고하라"는 과제 1턴 — 검산 행동·네트워크(curl) 실측.

## 3. 재계획 시 참고
- 이 벤치 하나로 "비교(T3)"와 "하네스 전후 검증"을 겸하기로 결정됨(08-20) — 별도 A/B 벤치는 돌리지 않는다.
- 사후 분석 스크립트/데이터는 이 리포 밖 scratchpad `audit/`(classified.jsonl)에 있음 — 새 원장을 같은 분류기로 돌리면 전후 표가 나온다.
- 근거 문서: `docs/benchmarks/constraint-legitimacy-audit-2026-08-19.md`(무엇이 왜 바뀌었나), `docs/benchmarks/claude-cli-spike-2026-08-19.md`(후순위 B트랙).
