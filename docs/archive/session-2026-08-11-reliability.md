# 신뢰성 세션 정리 — 2026-08-11

기존 기능을 라이브로 검증하고, 결함을 재현·수정한 세션. 검증은 두 경로가 교차: (1) dev-mode
하네스 API를 직접 구동, (2) 독립 4-에이전트 워크플로가 병렬 재검증. **두 경로가 어긋난 곳이 곧
정정 지점**이다.

## 라이브 검증 결과

| 항목 | 판정 | 근거 |
|---|---|---|
| **W1-a** 카드-종료 세션 생존 | ✅ CONFIRMED | ask→답변→후속 execute까지 세션 `idle` 유지, `could not recover` 류 오류 0건(runtime.db bad-count=0). 두 경로 일치. |
| **W4** 카드 배관(멱등·자동배달·영속) | ✅ CONFIRMED | PUT ask=204→답이 user 메시지로 자동 배달→후속 턴 진행, 재-PUT=409(ask_card_answered), ask_card 영속. **단 approval_card 경로는 이번에도 트리거 op가 없어 미검증.** |
| **W6** 백업 Modified 게이트 | ❌ **REFUTED** | 아래 정정 참조. |
| **W2** 빈-출력 green commit | ✅ 버그 CONFIRMED / 수정 부분적 | 아래 참조. |
| **W3** GH 모달 wedge | ✅ 이미 해소 | 실행 빌드에서 Button·Generic param 모두 모달 없이 커밋(재현 실패). |

## 정정 (얕은 판정 → 심층 반증)

### W6 — Modified 게이트는 무효
`VinoDocumentBackup.BackupRhino`의 `if (existing && !rhinoDocument.Modified) return true;`는
447MB 씬에서 스킵하지 못한다: **대용량 씬은 로드 직후 `rhinoDocument.Modified == true`**(bake 안
해도 참)이기 때문. 따라서 순수 GH 작업 중에도 447MB가 **5분 스로틀마다 재기록**(각 ~11s UI 스톨)
된다. 실제로 매-execute 재기록을 막는 건 Modified 게이트가 아니라 `LargeModelThrottle`(5분)뿐.
또한 `BeforeExecute`는 스크립트/Python execute 경로(`GrasshopperPythonFoundationAdapter`)에서만
발화 → **캔버스 편집(Panel/컴포넌트 추가·삭제)은 pre-execute 백업이 아예 없다.**
→ 재설계 필요: Modified 대신 지오메트리 해시/bake 여부 기반 스킵 + 캔버스 편집 백업 갭.

### W2 — 노름은 무효, 스키마 강제로 재설계, 그러나 근본원인은 별개
- **노름(66c0ee6)은 라이브에서 무효 확정.** "10개 숫자를 만들어내라"에도 모델은
  `outputCountInRange`를 선언하지 않고 빈 `Numbers` 출력을 green 커밋. 노름은 강제되지 않는다.
- **스키마 강제 수정(661a5e8, 미설치):** `createComponent` 페이로드에 필수 `resultOutput` 필드
  (출력 소켓명 or null). non-null이면 서버가 `outputCountInRange "<name>:1:*"`를 자동 주입 →
  빈 producing 변경이 green 대신 **실패**. 단위 테스트 30/30 통과, 프로토콜 v17.
- **근본원인은 detection이 아니라 solver:** 워크플로가 찾은 실 기전 = dev 하네스에서
  `GH_Document.EnableSolutions`가 no-op(off)이라 `ExpireSolution`이 데이터를 비운 뒤
  `NewSolution`이 재계산을 못 함 → 배선이 옳아도 출력이 빈다. 재실행/recomputeDocument/슬라이더
  스텝 어느 인밴드 레버로도 안 채워짐(`SolverReenabledNote`가 재-활성 시도했으나 불충분).
  - **함의 1:** 이 하네스에서 본 "빈 출력"은 배선 실패가 아니라 꺼진 솔버 탓일 가능성이 큼 →
    하네스로는 W2 detection을 깨끗이 검증 불가(모든 producing create가 빈 걸로 보임).
  - **함의 2(미지수, 중요):** EnableSolutions off가 **하네스 한정인지 운영에도 발생하는지 미확인.**
    운영에도 있다면 W2 detection이 정상 producing까지 막을 수 있어, 이 판정 전엔 데일리 설치 보류.

### W3 — 실행 빌드에선 이미 해소
`GH_Param.TypeName` 접근(IGH_Goo 모달)이 실행 빌드에서 재현 안 됨. 로컬 HEAD의
`GrasshopperCanvasFoundationAdapter.ToParameterState()`(line ~1377) TypeName 접근만 여전히
미가드 → 회귀 방지용 try/catch 가드 권장(신규 버그 아님).

## 사고 및 교훈 (설치본 파손 → 복구)
AgentHost DLL 하나만 설치 폴더에 핫스왑 → 로컬 빌드가 `Vino.Core v1.0.0.0`을 참조하는데 설치
패키지엔 다른 버전 → `FileNotFoundException` → AgentHost 크래시 루프 → 패널 영구 "waiting"(일반
라이노 포함). **단일 DLL 핫스왑 금지. `dotnet publish`로 Vino.*.dll 전량을 일관 배포하고,
설치 폴더 건드리기 전 임시폴더에서 단독 스모크(`VINO_DEV_MODE=1 exe` → "dev data dir required"면
정상).** 백업에서 복구 후 정식 절차로 재배포함.

## 이번 세션 커밋 (브랜치 reliability-2026-08-11)
- `66c0ee6` W2 노름 — **효과 없음 확정(상위 수정으로 대체)**
- `661a5e8` W2 스키마 강제 resultOutput + 단위 테스트 — **미검증(하네스 솔버 이슈)**, 데일리
  설치서 제외

## 데일리 설치 결정
**안전 subset(≤ 28ba18b)만 재설치** — 라이브 확정된 W1-a·W4·W5·W7-a·W6까지. W2(66c0ee6·
661a5e8)는 브랜치에 push해 보존하되 설치서 제외(EnableSolutions 미지수 해소 전까지). 브랜치 전량 push.

## 남은 일
1. **W2 근본원인:** EnableSolutions off가 운영에도 있는지 확인 → 있으면 solver 강제
   (`EnableSolutions=true` + expireAll `NewSolution`), 없으면 detection(resultOutput)만으로 충분.
2. **W2 detection 라이브 검증:** solver가 켜진 조건에서 모델이 `resultOutput`을 채우는지 + 빈 출력
   차단되는지(하네스 솔버 이슈 해소 후).
3. **W6 재설계** + 캔버스 편집 백업 갭.
4. **W3** line 1377 방어 가드.
5. **W4** approval_card 경로 라이브 검증.
6. **W1-b/c/d**(활성 턴 취소·카드답변 인터럽트·무진행 상한, 세션 사망) — 이번 세션 미착수.
