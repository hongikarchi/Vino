# 저녁 세션 — 2026-08-11 (자율 진행)

사용자 퇴근 전 지시: ① 즉효 3종(bake 줌·알림 컬러·goal 해제), ② 소켓 replacement 방식,
③ rewire 중 solver 일시정지 검토·구현, ④ 컨텍스트 자동 compact, ⑤ 라이노 라이브 테스트 포함.
Fable 주간 사용량 50% 상한 준수.

## 구현 완료 (커밋 순)

### `a95bca0` — W2 후속: 스위트 그린 복구
661a5e8(resultOutput 필수화)이 자기 테스트만 추가하고 기존 테스트 29건 + 지침 asset 미러 2건을
깨뜨린 채 남겨져 있었다(브랜치 잔존 결함). asset(payload-guide/house-rules)을 컴파일 폴백에 정확히
동기화하고, 7개 테스트 파일의 canvas.create 페이로드에 `resultOutput:null`을 삽입. 599/599 그린.

### `cfe6f75` — 컨텍스트 자동 compact (B3)
- **프로토콜 확인**: codex 0.147.0 app-server에 `thread/compact/start {threadId}` RPC +
  `thread/compacted {threadId,turnId}` 알림 + `contextCompaction` 스레드 아이템이 실재
  (generate-json-schema로 스키마 검증). config에 `model_auto_compact_token_limit`도 존재.
- **사전(proactive)**: 기존 스레드 turn 시작 전, 마지막 관측 컨텍스트 사용률 ≥
  `ContextCompactThresholdPercent`(기본 80, `--context-compact-threshold`, 0=off)이면 compact 요청
  → 완료 신호를 90s 한도로 대기(스레드당 5분 쿨다운, 실패해도 turn은 진행).
- **사후(reactive)**: 컨텍스트 초과 문구로 실패한 turn은 **확정된** compact 후 같은 게이트 안에서
  1회 재시도(미확정이면 재시도 안 함 — 동일 실패 반복 방지).
- **가시화**: thread/compacted·contextCompaction 아이템 양쪽에서 waiter 해소 + 30s dedup으로
  시스템 노트 1건("Codex compacted this session's context…").
- 테스트 2건(임계치 사전 compact / 오버플로 compact-재시도-복구), fake에 CompactThreadAsync 추가.

### `28dbc33` — 패널 3종 (Wave A)
- **bake 줌**: DataView bake 그룹이 references와 같은 버튼 패턴으로 `POST /focus` 호출
  (objectIds는 이미 클라이언트에 있었음). 50개 캡은 "zooms first N of M"으로 명시.
- **알림 3분류**: `CompletionKind`에 `waiting` 추가 — 대기 카드(goal/approval `proposing`,
  ask `asking`)를 든 세션 완료는 파란 "Needs your input" 토스트(물음표 아이콘, `--accent`),
  캔버스 unread dot도 동일 분류. 진짜 blocked만 빨강 유지. OS 알림 제목 분기.
- **goal 해제**: `dismissGoalCard`(DELETE /goal) 클라이언트+mock+useRuntime 액션, 확정/거절/
  채점된 카드에 "목표 해제" 버튼(approval dismiss와 동일 배선). W5-c 완성.
- 패널 66 테스트(+7) 그린, tsc+vite 빌드 클린. 데모모드(?demo=1)로 세 기능 모두 재현 가능.

### `c4a9d03` — 소켓 replacement + rewire 배치 solve (B1+B2, 프로토콜 v18)
- **replaceComponentIo → python.replaceSchema**: 어댑터 단일 원자 op —
  동일 타입 신규 컴포넌트(같은 pivot·nickName, 모델이 고른 newComponentId) → 소스 복사/설정 →
  소켓을 선언 스키마로 재구축(신규 컴포넌트라 제거 합법; 관리 콘솔 'out' 보존) →
  원본 연결을 (socketMap 매핑 포함) 같은 이름 소켓으로 재배선 → 원본 삭제 → **solve 1회**.
  원본은 최종 삭제 전까지 무변경이라 그 이전 실패는 전부 mutation_rolled_back(검증된 롤백).
- 서버: kind 추가(owner/bridge/필수 args + resultOutput required-nullable), **ChangeSet 단독 op
  규칙**, writeSet은 교체 대상 1건만(신규는 스냅샷 diff가 자동 Direct 원장), live-foreign 대상은
  삭제와 동일 3분기 가드, resultOutput 술어는 newComponentId에 부착.
- **deferSolve (B2)**: 실행기가 canvas.setWire 중 "뒤에 solve 나르는 op가 있는" 것 전부에
  server-owned `deferSolve:true` 주입 → N-wire 재배선이 NewSolution 1회. 지연 wire도
  ExpireSolution은 수행(stale 없음). 모델이 쓴 값은 무조건 덮어씀(마지막 wire의 solve 억제로
  W2 빈-출력 클래스 재발 방지). 전역 EnableSolutions는 건드리지 않음(solver-off 잔존 리스크 0).
- 지침: payload guide·house rules에 replaceComponentIo 경로 + "수동 author→rewire→delete-orphans는
  다중 컴포넌트 재구축 전용" 예외 규범, asset 미러 동기화(parity 그린). W11 커버리지 게이트
  (DynamicToolSchemaCoverage)가 kind enum 누락을 실제로 잡아냄 → 보강.
- 테스트: replace 술어 2건, deferSolve 주입 3건. 솔루션 전체 773 그린.

## 라이브 게이트 결과 (DevLoop rhino-live ×4 + SDK 프로브 ×6)

### 환경 결함 2건 발견·수정 (내 코드와 무관, 이것부터 안 잡으면 어떤 게이트도 못 돎)

1. **codex 0.147 업데이트 갭**: `~/.codex/.sandbox-bin/`에 codex.exe 0.147만 복사되고 0.147이
   요구하는 `codex-code-mode-host.exe`가 누락(8/10 17:42 갱신분) → 이 복사본으로 실행되는 모든
   **dynamic tool 호출이 os error 2로 전멸**. npm vendor bin의 동일 버전 exe를 복사해 해소.
   **운영 AgentHost는 npm을 우선하므로 일상 사용은 무사** — LiveE2E만 sandbox-bin을 우선하는
   구식 리졸버였고, AgentHost와 같은 순서(sandbox-bin 최후순위)로 정렬함.
2. **LiveE2E 등록 레이스**: 브리지는 Rhino 플러그인 로드 시점에 붙는데 GH(서드파티 플러그인
   포함)는 수십 초 늦게 뜸 — 바운드 문서 검사가 단발 read라 "not the owned copy"로 오탐.
   120s 폴링 + 관용적 필드 read로 수정(회귀 하네스의 등록 폴링과 같은 원리).

### 본 게이트 판정

- **v18 패키지 정상**: yak 설치, 플러그인 로드, 패널 기동, 브리지 v18 핸드셰이크 전부 통과.
- **W2-a 미지수 해소 (핵심 성과)**: 운영 빌드 실측 — 모델이 슬라이더·CPython 컴포넌트 생성,
  소스+소켓, 타이핑까지 4잡 커밋(단, "output(s) 'Cylinder' empty" 정보 노트), **배선 잡과 execute
  잡은 출력이 계속 비어 OutputCountInRange/GeometryClosed 술어가 정확히 차단**(Failed).
  → ① 빈-출력 결함은 dev 하네스 한정이 아니라 **운영에도 실재**. ② 검출(661a5e8)은 설계대로
  작동 — 43.6% 거짓-초록 클래스가 이제 빨간 실패로 표면화된다. ③ 따라서 W2의 남은 일은
  detection이 아니라 **solve 완결 수정**이고, 그 전까지 detection-on 빌드는 producing 작업을
  막는다(아침의 "설치서 제외" 결정이 옳았음을 라이브로 재확인).
- **SDK 프로브(문맥 격리)**: RunPythonScript(UI 스레드, 명령 문맥)에서 어댑터와 동일한
  `AddObject(update:true)`만으로 슬라이더 volatile이 **즉시 채워짐**(docEnabled=true,
  EnableSolutions=true, 이후 모든 레버에서 count=1 유지). → 결함은 GH API가 아니라
  **브로커 디스패치 문맥에서만** 발생: 용의자는 `RhinoApp.InvokeOnUiThread`(명령 밖 실행 문맥)
  또는 stale GH_Document 인스턴스 불일치(gh-open-crash 계열). W2 수정 타깃이 이 둘로 좁혀짐.
- **replaceComponentIo / deferSolve**: cylinder가 W2 결함에 막혀 E2E가 그 지점을 통과 못 함 →
  라이브 종단 검증은 W2 solve 수정 후로 이연(단위 773 그린, deferSolve는 배선 잡에서 실제
  주입·실행됨, 부작용 관찰 없음).
- **설치 상태**: 게이트 후 **안전 subset(28ba18b)으로 복원 완료**(dll 검증: compact RPC·
  replaceSchema 부재 확인). v18 전환은 `gptino-fallback-28ba18b` 워크트리 삭제 전에
  `artifacts/dev-loop/<최신>/package/yak/GPTino/*.yak` 설치 한 번이면 됨. 주의: yak 재설치가
  패키지 폴더 안의 net8.0.bak-* 백업 2개를 지웠음(폴백 워크트리가 대체 복원 수단).

## 남은 일 / 이연

- **W2 solve 완결 수정이 최우선**: 프로브가 좁힌 두 용의자(InvokeOnUiThread 문맥 vs stale
  doc 인스턴스)를 브로커 경로 계측으로 판별 → 수정 → rhino-live 재게이트 → 그때 v18 설치 판단.
- replaceComponentIo 라이브 종단 + deferSolve 타이밍 측정은 W2 수정 뒤 같은 게이트에서.
- 권한 모드(auto/ask)·preview 시각 검증(Tier 3)은 사용자 결정 대기.
- W6 백업 재설계, W1-b/c/d, W9 등 로드맵 잔여는 그대로.
