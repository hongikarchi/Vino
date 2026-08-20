# 제약 합당성 감사 — 2026-08-19

**질문**: ChangeSet / fingerprint / predicate 등 Vino의 제약이 "실제 사고를 막았는가(TP)" 아니면 "제약 자체가 실패를 만들었는가(FP/자초)". 
**데이터**: 17개 프로젝트 `live-jobs.db` 전수 2,203잡(2026-07-21~08-13), 비커밋 383(17.4%). 잡별 ChangeSet·오퍼레이션 페이로드·동일 세션 전후 잡·타 세션 교차·resource-ledger.db·서버 코드를 대조. 5개 판정자 + 2개 회의론자 교차검증(양쪽 수치 ±2 이내 일치). 원시: scratchpad `audit/` (classified.jsonl, 워크플로 wf_72d87cc6).

## 한 줄 결론
**제약의 목표는 합당하다(모든 게이트에 실제 사고 사례 존재). 비용은 컸지만 그 대부분은 이미 수정됐고, 남은 비용은 "서버가 자기 장부를 못 믿거나(ledger 갭) 지문이 너무 휘발성(런타임 메시지 포함)"인 데서 온다 — 제약을 없앨 근거는 없고, 부담을 서버로 옮길 구체 항목만 남았다.**

## 전수 분류 (비커밋 383)
| 클래스 | 건수 | 비중 | TP / FP / 자초 / 불명 | 재시도 후 즉시 커밋 |
|---|---|---|---|---|
| fp.auto-no-baseline (auto 거절: 이 세션이 안 씀) | 114 | 29.8% | **1 / 94 / 19 / 0** | 114/114 재제출, 110건이 에러에 찍힌 지문 그대로 전사, 중앙 9초 |
| code.runtime/compile + keyword + schema | 88 | 23.0% | 61 / 11 / 16 / 0 | 코드 55/64 같은 컴포넌트에서 해결 |
| fp.stale-concrete (구체 지문 불일치) | 57 | 14.9% | **8 / 9 / 35 / 5** | 45/57 즉시 재제출 성공 |
| fp.auto-drifted (auto 거절: 드리프트) | 30 | 7.8% | 1 / 26 / 0 / 3 | 30/30 맹목 재전사(26건 바이트 동일 페이로드) |
| 소형 게이트 5종 (liveness·cost·missing-target·declared-predicate·expect-absent) | 64 | 16.7% | 11 / 19 / 32 / 2 | — |
| rr.timeout/bridge·기타 | 30 | 7.8% | (제약 아님) | — |

**fingerprint 계열 합계 201건 = 비커밋의 52%, TP는 10건(5%).** 단, 시간축이 결정적:

| 주차 | 잡 | fingerprint 실패 | 전체 대비 | 비커밋 대비 |
|---|---|---|---|---|
| 07-20 | 194 | 24 | 12.4% | 42% |
| 07-27 | 687 | 86 | 12.5% | 72% |
| 08-03 | 436 | 52 | 11.9% | 72% |
| **08-10** | 886 | 45 | **5.1%** | 34% |

08-08 ledger 수정(76ac6d2)·08-06 adapter CAS→Failed(cc74d30)·08-07 consumer-first 삭제(e7aa9ca) 이후 **no-baseline 잔여 0.9%**, stale-concrete 잔여 9건(그중 4~5 TP, RecoveryRequired 0). **즉 "실패 1위" 통계는 수정 전 데이터였다.**

## 클래스별 판정과 권고

### 1. fp.auto-no-baseline — 94/114 FP: 서버가 자기 세션의 쓰기를 잊음
- 83건: 수정 전 ledger가 Source/Io/Value 쓰기를 기록 안 함. 6건: 07-30 재시작으로 인메모리 ledger 소실. 3건: RecoveryRequired 경로 쓰기가 ledger 미기록(**현존 갭**). 나머지 자초 19 = 프로토콜 위반(미기록 자원에 auto).
- 결정적 관찰(회의론자): 거절 메시지가 **현재 지문을 그대로 찍어주고** 모델은 9초 뒤 그걸 전사해 바이트 동일 페이로드를 재제출 → 이 분기는 **보호 효과 0, 순수 지연**. 유일한 TP(e99e2070)도 실제로는 schema.append-only가 막음.
- **권고**: `ResolveAutoExpectations`의 no-baseline 분기에서 거절 대신 **라이브 지문으로 채움**(문제 로그에 기록). 특히 Wire/Group(지문=id 해시, 드리프트 불가)·executePython 전용·readSet 전용은 무조건. 타 세션 소유/드리프트 분기는 유지(d1884d03 사고 보호는 거기). + RecoveryRequired 경로에서도 쓰기가 실제 랜딩했으면 ledger 기록.

### 2. fp.stale-concrete — TP 8: **유지**, 자초 35는 대부분 이미 수정됨
- TP 사례: 타 세션이 2분 전 배선한 컴포넌트 삭제(ce0856bc), 3세션이 4분 안에 같은 25개 이동(64bab01a — 세션이 물러섬), 사용자 슬라이더 드래그(dc63f550), 사용자 수동 그룹 편집(d5a3609e). **사람/타세션 편집을 잡는 유일한 증거가 ledger==live 비교**라 완화 불가.
- 자초 35 중 20건 = "own setSource 뒤 execute" 체인 → 76ac6d2 이후 자동 rebase됨. RecoveryRequired 7건 전부 cc74d30/e7aa9ca 이전.
- **권고(남은 것만)**: ① Stale 메시지에 **자원 kind+id 명시**(LiveDocumentBackend.cs:2443 — 익명 해시 4~7개 나열이 재시도 실패 유발) ② `PythonComponentFingerprint`에서 **RuntimeMessages 제외**(own execute/upstream 재solve가 Value 행을 stale로 만듦; 배포 시 ledger 일괄 재스탬프 필요) ③ `snapshot_read`에 `canvas:<guid>` 스코프 추가 또는 Read-origin baseline 기록(모델이 0000 delete를 "지문 API"로 쓰는 현상 제거). **하지 말 것**: parent-ownership rebase, 생성 컴포넌트 auto-delete — 둘 다 사람 편집 덮어쓰기 재도입.

### 3. fp.auto-drifted — 26/30 FP: "드리프트"의 정체는 서버 자신
- 20건: 지문에 포함된 **런타임 메시지**가 upstream 재solve·형제 세션 execute로 움직임(사람 편집 아님). 3건: 자기 delete로 그룹 멤버 줄어든 걸 ledger가 모름. 2건: RR 경로 쓰기 미기록. TP 1(그마저 35초 뒤 맹목 덮어씀).
- **권고**: 위 2-②와 동일(RuntimeMessages 제외) + 그룹 행 멤버십 갱신 + RR 경로 ledger 기록. 이 셋이면 26건 중 ~24 제거.

### 4. code/schema 88 — TP 61: 검증이 제 역할을 함, **사적 검산이 잡을 몫이 큼**
- 컴파일·이름·오버로드·캐스트 오류의 다수는 모델이 제출 전 스크래치에서 돌려봤으면 잡힘(08-19 workspace-write 변경의 근거). GH 런타임 데이터 필요한 것만 라이브 전용.
- keyword 10/10 = 콘솔 소켓 `out`을 라이브 목록에서 베껴 선언 → **서버가 auto-preserve/rename 흡수** 권고(preflight에서 `out`→console 처리, 스냅샷에 `out`을 평 소켓으로 노출 중단).
- append-only 12: 같은 세션이 방금 만든 컴포넌트의 기본 소켓 x,y/a를 치우려다 거절(자초 16) — 세션 생성 컴포넌트의 **첫 schema 쓰기는 교체 허용** 검토.

### 5. 소형 게이트 (64)
- **liveness 18**: TP 1(타 세션이 승인 받고 배선한 컴포넌트 삭제 시도). FP 10은 이미 수정(70f7f9b/cc974c2). 남은 결함: 문서 재오픈 시 docKey 회전으로 ledger 행 고아화(Save As용 `RemapDocKeyAsync`를 reopen에도) + 거절 시 **approval_request 타깃을 메시지에 동봉**(모델이 산문으로 "승인됨" 주장 3회).
- **cost-preflight 10**: **9/10 FP** — 키워드 추정기가 22000mm/5900mm 치수 슬라이더를 "개수"로 오인, "established" 플래그가 슬라이더 변경마다 리셋돼 권장 절차(저해상도→상향)가 통과 불가. 게다가 슬라이더 setValue는 GH 자동재계산으로 같은 solve를 게이트 밖에서 일으킴 → **거부→경고로 강등 + 정수 소형범위 슬라이더만 + 측정 테이블 기반 established**.
- **missing-target 21**: TP 7(외부 삭제 원자적 차단). 9건은 소켓 id 탐색 프로브 → createComponent 결과에 소켓 id 반환 / setWire에 소켓 이름 허용.
- **declared-predicate 9**: 6건이 writeSet에 없는 컴포넌트의 predicate를 검사 안 하고 쓰기 랜딩 후 fail-closed → 제출 시 거부 또는 자동 inspect + "ops는 전부 적용됨, predicate만 실패" 명시.
- **expectation-absent 6**: TP(중복 생성 방지) 유지, 동일 wire 재연결은 멱등 성공 처리.

## 사용자 질문에 대한 직답
- **제약이 over-engineering인가?** 아니오. 게이트마다 실제 사고(타세션 배선 삭제, 사용자 드래그 덮어쓰기, 빈 출력 초록 커밋, 외부 삭제 대상 쓰기)가 있다. 
- **추론 예산 낭비의 진범**: 제약의 *존재*가 아니라 ①ledger가 자기 세션 쓰기를 잊던 결함(수정됨) ②지문에 런타임 메시지가 섞여 휘발(미수정) ③에러에 정답은 있는데 자원명이 없음·모델이 산문으로 우회 시도. 전부 **서버 쪽 수정 항목**.
- **fingerprint가 지금도 문제인가?** 08-10 주 기준 전체 잡의 5.1%(수정 전 12%)·잔여는 대부분 ②. 위 권고 3건(no-baseline 채움, RuntimeMessages 제외+재스탬프, RR 경로 ledger)이면 ~1% 이하 예상.

## 우선순위 (구현 비용 ↑ 순)
1. Stale 메시지에 자원 kind+id (1줄)
2. no-baseline → 라이브 채움 (FingerprintRebase.cs 분기 1곳 + 문제 로그)
3. RR 경로 ledger 기록 (catch 블록에 after-snapshot 조건부)
4. PythonComponentFingerprint에서 RuntimeMessages 제외 + 배포 시 ledger 재스탬프 마이그레이션
5. cost-preflight 거부→경고, 추정기 정수/소형범위 한정
6. keyword `out` 서버 흡수, createComponent 소켓 id 반환, docKey reopen 리맵, liveness 거절에 approval 타깃 동봉
