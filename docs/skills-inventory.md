# 스킬·결정론 툴 인벤토리와 테스트 세션 시나리오

작성 2026-08-14 (W4). 목적: "자주 쓰는 기능은 검증된 고정 코드로, LLM은 예외 보충만"이라는
방향에서 지금 무엇이 있고, 무엇이 없고, 다음 테스트 세션에서 무엇을 밟을지의 단일 목록.

## 1. 실행 계층 3종 (고정 코드가 사는 곳)

| 계층 | 실체 | 강제 방식 |
|---|---|---|
| A. 서버 결정론 툴 | rhino_audit(8 kind) · structural_extract/solve/loads/layout/size · layer_scheme_draft · rhino_layers | 서버/어댑터 코드가 직접 계산 — 모델은 호출만 ("Detection is server code") |
| B. verbatim 스킬 | assets/skills/*.py (bake_manager, structural_check) | house rules 규범으로 verbatim 배선 강제 — **기계적 강제는 아님** (규범 무시 라이브 선례 있음, ChangeSetValidation.cs의 FORCED 주석 참고) |
| C. 서버측 소스 재작성 | SourceDocKey/DeferSolve 주입, C# 워치독(vino:guard) 주입 | dispatch 시점에 서버가 모델 작성 페이로드를 재작성 |

B의 하드닝 아이디어(이연): `skill_instantiate` — 스킬 이름+파라미터만 받아 서버가 canonical
소스를 setSource로 주입. C 계층 인프라에 얹으면 저비용.

## 2. 스킬 파일 (assets/skills — 인덱스만 프롬프트 주입, 본문은 skill_read)

| 파일 | 상태 | 비고 |
|---|---|---|
| bake_manager.py | **완성·운영** | 레이어 ensure·family 멱등 재bake·GUID 보존 Replace·group/block·dry-run·undo·컴포넌트 zoom 스탬프 |
| structural_check.py | 완성·운영 | SLS 처짐 판정 payload, 판정 수식 재작성 금지 규약 |
| structural_viewer.py | 완성·운영 | 진단 뷰어 payload — results.json→심각도 컬러램프+변형 슬라이더, 색 매핑 재작성 금지 |
| structural_bake.py | 완성·운영 | 진단 bake payload — 판정색 축선을 Vino::Structural 밴드 레이어로, family replace·Toggle 발동 |
| gh-authoring.md | 운영 | well-known GUID 테이블·언어 정책 |
| gh-csharp-cookbook.md | 운영 | 기본 저작 언어 C# 스캐폴드·Parallel.For 안전규칙 |
| gh-paneling-cookbook.md | 운영 | isotrim UV·attractor·CreateOffsetBrep idiom |
| gh-pynite-cookbook.md | 운영 | 캔버스 내 PyNite 계약 (라이브 오라클 검증 0.004%) |
| structural-analysis.md | 운영 | 구조 도메인 WHAT-AND-WHY (쿡북과 짝) |

## 3. 사용자 요청 워크플로 6종 커버리지

| 워크플로 | 현재 | 다음 단계 |
|---|---|---|
| GH bake (+옵션 카드) | 스킬 완성 | 옵션(group/layer/update·overwrite) ask 카드 사전 질의 — **이연** |
| 구조 검토 (PyNite) | **호스트 툴로 완성** | 테스트 세션에서 gate-structural 재사용 |
| 레이어 정리 | **호스트 툴로 완성** (게이트 PASS 이력) | 테스트 세션에서 gate-layer-curation 재사용 |
| delete layer enhance | 없음 | 사용자의 Block Edit New 코드 인수 대기 → 정리 → 분류(A/B) → 게이트 |
| sketchup / cad export·import | 없음 | agent-facing 파일 export 표면 신설이 선결 (아키텍처 결정) — **이연** |
| D5 재질 정리 (mapping·object·layer 3검사) | 없음 | rhino_materials read 툴 + rhino_audit 신규 kind + rename 금지 서버 가드 — **이연** |

## 4. 다음 테스트 세션 시나리오 (실사용 검증 순서)

전제: Vino 패키지 설치본, dev-loop 픽스처 또는 사본 뜬 실무 파일.

1. **bake 왕복** — 정의 하나 만들고 bake → 같은 family 재bake(멱등: 객체 수 불변·GUID 보존)
   → group/layer 옵션 변형 → data 탭에서 bake 귀속·zoom 확인.
2. **구조 검토** — `scripts/gate-structural.ps1` 그대로 (자유단 ask-back → solve → 반력 평형); 커브 입력 워크플로우는 `scripts/gate-structural-curves.ps1` (`-SceneKind structural-curves`: 폴리라인 분해·역할별 단면·지점·G/Q 하중 ask-back → solve → 역할별 단면·KDS 계수·활하중이 결과 아티팩트에 실렸는지).
3. **레이어 정리** — `scripts/gate-layer-curation.ps1` 그대로 (부분 grant·preset·미해석 복합어 triage).
4. **권한 사다리** — `scripts/gate-permission.ps1` (review 무변경 / fullAuto 무카드+기록 /
   standing 2회차 무카드). ※ 2026-08-14 게이트 신설.
5. **한/영 토글** — 토글 후 다음 턴 응답 언어 전환(코드·툴명은 영어 유지) + 카드 답변 합성문 언어.
   배선은 완결이나 라이브 검증 기록이 없던 항목.
6. **기존 코드 인수 리허설** — 사용자 제공 코드 1건을 받아 분류(A/B)→정리→게이트 초안까지의
   절차를 실제로 한 번 밟기 (delete layer enhance가 첫 후보).

## 5. 로그가 이제 담는 것 (W4 로그 P0 이후)

- `host.log` (데이터 루트, 4MB 롤오버 1회): 호스트 ILogger 전체 + 예외 스택 — 이전에는 파일이 아예 없었음.
- `problem-log.jsonl`: 전 레코드에 `v`(패키지 버전)·`protocol`(BridgeProtocol.Version) 스탬프,
  Failed/RecoveryRequired에 `job-exception`(타입+스택) 레코드, fullAuto/standing엔 `auto-approval` 레코드.
- 남은 P1(이연): JobDiagnostics 영속, 진단 번들 export(+스크러빙), 성공 사례 replay 재현성.
