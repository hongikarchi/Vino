# 레이어 큐레이션 스펙 계획 (Material→Color + Name 정규화)

> 상태: 분석·계획 단계 — 구현 전. 본 문서는 구현 착수 전 사용자 확정을 위한 설계안이다.

---

## 1. 배경과 목표

**사용자가 원하는 것.**
1. "plaster 레이어면 그럴듯한 plaster 색"이 자동으로 — 재료 의미론에서 레이어 색을 결정하고, 유사 재료는 같은 색 패밀리 안에서 gradient로 구분.
2. 지저분한 실무 레이어 이름을 표준 테이블에 대해 정규화해 BIM(IFC-ish) 매핑이 가능한 모델로 — 1회 시맨틱 라벨링 후 영속화.

**레퍼런스 3종이 각각 증명하는 것.**

| 레퍼런스 | 증명하는 것 | 스펙에 가져올 것 |
|---|---|---|
| **MAT2LAY** (Food4Rhino, ~2k 다운로드) | 방향이 반대(layer→material)이고 의미론 제로인 단순 필러조차 수요가 있다 | (a) 레이어↔머티리얼 이름 동일성 관례 (b) "빈 곳만 채움" 비파괴 기본값 (c) 명시적 색 정책 enum |
| **OKLCH Layer Color Palette Picker** (GJKim, Python 스크립트) | Material Mode(스텐레스·나무·유리·철·콘크리트·석재·벽돌) = 재료→색 관례가 이미 팔레트로 존재하나, **레이어 하나씩 수동 클릭**이 한계. 작성자 스스로 "office standard를 그 위에 쌓으라"고 초대 | GPTino 기능 A = 이 수동 클릭의 자동화. hue=패밀리, L/C=변형이라는 OKLCH 구조 |
| **Reer /sync** | "1회 라벨링 후 영속화"가 씬 이해의 실체 (메모리: gptino-competitive-landscape.md) | 기능 B의 영속화 모델. 단, Reer는 생성 객체 short_id 유저스트링으로 라벨 지속 — 우리는 layer UserText로 동일 패턴 |

**차별점 (경쟁 조사 결론과 정합).** 라이브 배선은 커모디티화(McNeel MCP), per-layer IFC 지정도 VisualARQ/ggRhinoIFC가 이미 함. GPTino의 몫은 **신뢰성 계층**: 근거 있는 분류(어떤 토큰이 어떤 alias에 매칭됐는지), 서버 결정론 confidence 라벨, CAS로 고정된 원클릭 undo 적용, 표준 프로파일 출력(AIA/ISO/CALS). 두 기능은 별개가 아니라 **하나의 시맨틱 라벨 파이프라인**이다: 라벨 1회 → 색은 테이블에서, IFC 클래스는 테이블에서, 머티리얼 이름은 라벨에서.

---

## 2. 기능 A: Material→Layer 색상 시스템

### 2.1 OKLCH 팔레트 설계 (구체 수치)

원칙: **패밀리 = hue 고정, 변형 = L 스텝 (chroma는 bell-curve + gamut clamp)**. HSL이 아니라 OKLCH인 이유 — 동일 L 스텝이 지각적으로 균등하고 L/C를 바꿔도 hue가 드리프트하지 않아 공식으로 램프를 생성할 수 있다.

**시드 테이블 (기본 프리셋 `material-realistic` — OKLCH picker의 Material Mode 계열):**

| 재료 패밀리 | H (°) | C 범위 | 기준 L | 예 |
|---|---|---|---|---|
| concrete / stone | 70–90 | ≤0.03 | 0.65 | 근중성 회색 |
| plaster / gypsum | 80–90 | ≈0.02 | 0.90–0.93 | 따뜻한 오프화이트 |
| wood | 55–70 | 0.06–0.12 | 0.45–0.75 | tan→brown |
| brick / terracotta | 30–45 | 0.08–0.13 | 0.55 | |
| steel / metal | 240–260 | 0.01–0.04 | 0.55 | blue-grey |
| glass | 210–235 | 0.04–0.08 | 0.80–0.88 | 고명도 |
| vegetation | 140–150 | 0.10 | 0.60 | |
| insulation | 95–105 | 0.10 | 0.75 | 노랑 |

**패밀리 내 변형 램프**: 이산 L 스톱 `0.86 / 0.75 / 0.65 / 0.54 / 0.45` (11-stop 디자인 토큰 스케일에서 뷰포트 가독 대역만 절취). 연속 보간 금지 — 이산 스톱이어야 생성된 임의 두 색이 뷰포트에서 시각 분리됨. 예: `콘크리트::제자리치기` L=0.65, `콘크리트::PC` L=0.54, `콘크리트::무근` L=0.75, 전부 H=75 C=0.025.

**패밀리 간 분리 규칙**: 유사 L/C 대역에서 hue 시드 간격 ≥25–30° 유지. sRGB gamut 밖이면 **L·H 보존, C만 clamp**.

**대안 프리셋 `drafting-traditional`** (수기 도면 관례: yellow=timber, red=brick, dark blue=steel, green=concrete, purple=insulation). 두 관례는 충돌하므로(콘크리트: 회색 vs 녹색) **별도 선택 가능한 named table**이어야 하며, 이는 기능 B의 정규화 테이블 개념과 자연 결합: *시맨틱 라벨 1개 → 활성 convention table이 색 결정*.

### 2.2 재료 인식 — 신호 3원과 판단 경계

입력 신호 우선순위:
1. **레이어 이름 토큰** (plaster, conc, STL, 벽돌, 마감…) — 기능 B의 alias 테이블과 공유. 정규화된 이름은 곧 분류 정확도 향상.
2. **RenderMaterial 슬롯** — `Layer.RenderMaterial` 이름/타입이 있으면 강한 신호 (MAT2LAY가 증명한 이름 기반 interchange 관례).
3. **모델(LLM) 추론** — 위 둘이 실패한 잔여만. 모델은 재료 패밀리라는 **triage 판단**만 하고 색을 직접 지정하지 않는다.

**결정론 경계 (사용자 확정 원칙 "결정론적이면 코드로, 판단이면 프롬프트로"):**
- 모델: "이 레이어는 wood 패밀리 같다" — 제안만.
- 서버(AgentHost): 패밀리→OKLCH 좌표 계산, 램프 스텝 배정, OKLCH→sRGB 변환(Ottosson Oklab 레퍼런스 변환 ~20줄 C#, 의존성 불필요), gamut clamp — 전부 결정론. 정본 팔레트는 OKLCH로 단일 정의하고 **sRGB는 적용 시점에만 방출** (`System.Drawing.Color` / `ArgbColor` int).
- confidence는 서버가 계산: alias 테이블 정확 일치=**high**, prefix/패턴 일치=**medium**, 모델 추정=**low**. **모델 자기신고 금지.**

### 2.3 어느 색 슬롯을 건드리나

Rhino 레이어는 색 슬롯 3개가 독립: `Layer.Color`(뷰포트 표시), `Layer.PlotColor`(출력), `Layer.RenderMaterial`(렌더).

- **기본: `Layer.Color`만.** 기존 typed op `updateRhinoLayerProperties`의 `argbColor?`가 이미 지원 — **새 mutation op 불필요.**
- `RenderMaterial` 생성("라벨 이름으로 매칭 머티리얼 생성" — MAT2LAY 본연 기능)은 **명시 opt-in 별도 op**, 기존 할당 있으면 건너뜀(빈 곳만 채움). **[2026-08-07 사용자 확정]** 머티리얼 타입은 Rhino **Plaster 템플릿**(무광 diffuse) + **레이어 표시색과 동일한 색** — PBR 프리셋 불필요(§8 Q8 해소).
- **`PlotColor`는 절대 암묵 변경 금지** — 출력색은 흔히 의도적 흑백.

수동으로 색을 이미 지정한 흔적이 있는 레이어(예: GPTino 기본색·검정·흰색이 아닌 커스텀 색)는 카드에서 **opt-out 항목으로 표시**하고 기본 체크 해제 — "사람이 배치·수정한 영역은 기본 불가침".

---

## 3. 기능 B: Layer 이름 정규화 + BIM 매핑

### 3.1 표준 스키마 — standard-agnostic pivot

단일 표준 하드코딩 금지. 근거: 한국 실무 표준 채택률은 사실상 0 (AURIC 실태조사: 대형사 8곳 중 시도 1곳, 샘플 도면 22장 중 완전 준수 0장; KS F 1542 / 건설CALS는 **납품 시점 변환 대상**이지 일상 입력이 아님). 따라서:

- **정규 pivot 필드**: `{discipline, element(major), subElement(minor), presentation, status}` — ISO 13567(고정폭 필드), AIA/NCS(`A-WALL-FULL`), BS1192/Uniclass, KS F 1542(`AA-WALL`) 전부를 커버하는 교집합 구조.
- **출력 프로파일**: pivot → 선택된 프로파일로 렌더. 기본 표시는 AIA 스타일(가독성 최고), **CALS/KS F 1542 export 프로파일은 한국 시장 차별점** (관공서 납품 시 어차피 변환해야 하므로).
- **표준은 OUTPUT 타깃일 뿐, INPUT 기대치가 아니다.** 입력 매칭은 alias/synonym 테이블(`벽=WALL=W`, `기둥=COL`, `슬라브=SLAB`, `마감=FIN`…) + prefix/패턴 매칭 + LLM 분류.

실측 근거(구조회사 실파일, 70레이어): `SC/SG/SB` 부호 레이어, `"SC5 (Bracing)"` 같은 변형 마크 — **정확 일치만 하면 기본값 낙하**(구조 파이프라인에서 38부재 자중 +90% 사고 전례). prefix 매칭 필수, 행별 provenance(어떤 규칙이 매칭했나) 기록.

### 3.2 정규화 파이프라인

```
스캔 (rhino_layers + 신규 audit kind=layerSemantics)
  → 제안 테이블 생성 (원이름 → pivot → 정규 이름 / 재료 패밀리 / IfcClass / confidence / 매칭 근거)
  → ApprovalCard: 항목별 체크박스 + 모호 항목은 per-item 'choices' 라디오 + ◎ focus
  → 승인분만 일괄 적용
```

**v1 핵심 결정: 물리적 rename은 하지 않는다.** `updateRhinoLayerProperties`는 "presentation only — rename/re-parent are not available (they rewrite descendant paths)" (operation-contract.md:152). 자손 FullPath 재작성은 GH Geometry Pipeline 이름 필터를 "에러 없이 빈 출력"으로 깨뜨리고(curator-plan.md:220-223 확정), 구조 파이프라인 자체가 레이어 이름을 section mark로 쓰므로(layer FullPath = 부호) rename은 자기 발등을 찍는다. Raven의 nickname 강제 변경 반발 사례도 동일 교훈.

→ **v1 정규화 = 이름을 바꾸지 않는 영속 시맨틱 MAPPING.** 물리 rename은 v2, curator-plan이 명시한 대로 **영향 분석(경로 참조 스캔: GH 파이프라인·스크립트·블록 정의) + approval card + `saveRhinoLayerState` 자동 스냅샷** 뒤에서만.

### 3.3 영속화 (Reer /sync 방식) — 이중 저장

| 저장소 | 내용 | 이유 |
|---|---|---|
| **layer UserText** (`Layer.SetUserString`) — 네임스페이스 키: `gptino.material`, `gptino.ifcClass`, `gptino.canonical`, `gptino.labelSource`, `gptino.confidence` | 레이어별 라벨 결과 | .3dm에 영속, 협력사로 파일과 함께 이동, ggRhinoIFC가 공개 문서화한 선례와 동일. **레이어 fingerprint 해시 대상 밖**(id/FullPath/Parent/Color/Visible/Locked/RenderMaterialIndex/Linetype만) → 라벨링이 CAS 핀 무효화 안 함. UserDictionary는 File3dm 경유 유실 버그가 있으므로 금지 |
| **프로젝트 컨텍스트 폴더** (`%LocalAppData%\GPTino\projects\<hash>\context\layer-standard.json`) | alias 테이블·프로파일·회사 표준 | 사용자 확정: "컨텍스트 폴더는 LocalAppData, .gh 옆 아님". curator-plan Phase 5도 "스키마 정의는 rules.md와 같은 위치"로 확정. 사람이 직접 편집 가능, 스레드 시작마다 재로드 |
| **shipped 기본 테이블** (`assets/data/layers/material-palette.json`, `alias-seed-ko.json`) | 기본 재료→색 테이블, 한/영 alias 시드 | `sections-ks.json` 아키텍처 그대로: 테이블은 AgentHost 데이터, 매칭은 AgentHost, adapter는 기하 사실만 보고 |

**3-tier 승격 모델** (회사 배포 킬러기능 슬롯): 프로젝트 테이블 → 개인 → 회사 공유. 공유 진입은 **의도적 승격 행위로만** — 검증 통과 + 출처 메타데이터(누가/언제/어떤 검증). 자동 전파 금지(잘못된 테이블은 오류를 조용히 복제). 첫 회사 테이블 = 실제 구조회사 관례: `SC→기둥`, `SG→거더`, `SB→보`…

**[2026-08-07 사용자 확정]** **프로젝트 → 개인 승격은 채택** ("프로젝트->개인 단위로 올라가는거는 좋네"). **회사 계층은 이연** — "이후에 가능하면 하는걸로". 따라서 범위: v1 = 프로젝트 테이블 + shipped 시드, 개인 테이블 + 승격 제안은 반복 데이터가 쌓이는 대로 후속 phase, 회사 계층은 로드맵 밖(필요 시 재론). (승격 = 아래 계층 파일의 행을 윗 계층 파일로 복사하는 것 — 구조 자체는 유지)

### 3.4 IFC 클래스 매핑

- 어휘: 기존 툴들이 per-layer로 쓰는 **element-category 레벨** — `IfcWall, IfcSlab, IfcColumn, IfcBeam, IfcDoor, IfcWindow, IfcCovering, IfcStair, IfcRailing, IfcRoof, IfcFurniture`, 폴백 `IfcBuildingElementProxy`. VisualARQ·ggRhinoIFC의 UX와 정확히 일치하므로 GPTino 라벨이 기존 exporter에서 **즉시 실행 가능**.
- interop 출력(후순위): Revit 커넥터 스타일 CSV(`Revit_Category_And_Layer.csv` 형식), ggRhinoIFC 호환 layer UserText. 라이선스 클리어 경로(GeometryGymIFC MIT / IfcOpenShell LGPL 서브프로세스)와 클래스 컬럼 호환 유지.
- element 상위 레이어 트리는 ggRhinoIFC처럼 IFC 공간 구조(Site/Building/Storey) 예약 — v1은 라벨만, 강제 없음.

---

## 4. 아키텍처

### 4.1 Typed ops — 기존 스택 위에 최소 추가

주의: 어댑터 프로젝트는 리네임됨 — 인터페이스는 `src/GPTino.CanvasSceneAdapter/IRhinoSceneAdapter.cs`, 구현은 `src/GPTino.Rhino/RhinoSceneFoundationAdapter.cs`.

**mutation은 신규 0종** — 쓰기 경로는 전부 기존 op으로 커버(실제 70레이어 프로덕션 모델에서 라이브 게이트 통과 완료):
- `ensureRhinoLayer` — 생성 시 `ArgbColor` 필수(이미 강제), 중첩 경로 `Parent::Child` 지원
- `updateRhinoLayerProperties` — `argbColor?` + `expectedFingerprint` CAS
- `rhino.layerState` — 일괄 적용 전 `"GPTino: before-mat2lay"` 스냅샷 = 원클릭 revert
- `moveObjectsToLayer` — (스키마 재편 시) 항목별 CAS 배치

**신규는 read/compute 계열 2건:**
1. **audit kind `layerSemantics`** — curator-plan의 미구현 `layerSchema` 슬롯 계승. 기존 7종 audit(특히 `layerIntegrity`의 `layerNameHazard` — 공백/케이스 쌍둥이가 이미 정규화의 씨앗) 패턴 그대로: 레이어별 finding + fingerprint + proposedFix, 결과 fingerprint 해시. 신규 op 1건당 비용은 7개 레이어 관통(BridgeMessages → adapter interface → handler → RhinoSceneFoundationAdapter → LiveDocumentBackend validation → DynamicToolSpecs → operation-contract.md → tests) — curator-plan 실측치대로 예산 반영.
2. **layer UserText read/write** — 현재 layer-level `SetUserString`은 미사용. 라벨 영속화용 소형 op(또는 updateLayer 확장 필드). fingerprint 해시 대상 밖이므로 CAS 설계 변경 불필요.

### 4.2 승인 흐름

- 레이어 색/라벨은 **user geometry에 대한 파괴적 op이 아니므로 approval grant가 구조적으로 강제되지 않음** (grant 스코프는 rhinoObject write). 그러나 UX로는 기존 **`approval_request` → ApprovalCard(체크박스 + choices 라디오 + ◎ focus) → 턴 종료 → 다음 턴 grantId** 프로토콜을 그대로 재사용 — 레이어 테이블은 사용자 저작물이므로 default-deny 정신을 UI 레벨에서 유지.
- 서버가 `Approved`/`SourceDocKey`를 주입하고 모델 저작 값은 submit에서 거부하는 anti-spoof 그대로.
- 배치 규칙: ChangeSet당 ≤20 op(curator-plan 배칭 규칙), 전체를 `RhinoDoc.BeginUndoRecord/EndUndoRecord` 하나로 래핑(serial==0 재귀 케이스 처리), 배치 전 layerState 스냅샷 필수.

### 4.3 검증 / audit / 신뢰도

- confidence 라벨은 **서버 결정론 계산만**: 정확 일치=high / prefix·패턴=medium / 모델 추정=low. 정보로만 노출, **하드 게이트 지양**(미매핑 레이어가 있어도 차단 안 함 — "모델이-알-수-없는-검증기 재발명 위험").
- 적용 후 predicate 재검증: 서버가 `rhino_layers` 재관측으로 색 반영 확인(기존 updateLayer의 "요청 필드별 반영 검증" 패턴).
- **audit 정직성**: `scannedLayers 0`과 `findings 0`을 구분 표기 (실사용 모델 교훈: "0건 반환은 깨끗함과 구별되지 않아 더 위험").
- **스코프 갭 교훈 반영**: "레이어에 뭐가 있나"를 볼 때 `document.Objects` 열거만 하면 블록 정의 멤버가 0개로 보임(deleteLayer가 비가시 블록 기하를 파괴할 뻔한 라이브 게이트 전례). 기존 `EnumerateLayerOccupants`(블록 멤버 포함 ObjectCount, 실모델 2484→2477 검증 완료)를 반드시 경유.
- **fingerprint 오염 관리**: 대량 recolor는 레이어별 + 테이블 fingerprint를 전부 재작성 → 타 세션 ledger stale은 설계상 당연하나, ledger 갱신에 **귀속 마커**를 남겨 암묵 학습 신호("에이전트 후 사람 수정"=불만 라벨)가 오염되지 않게 — curator-plan이 moveObjectsToLayer에 처방한 그대로.

### 4.4 실패 모드와 라이브 게이트 계획

| 실패 모드 | 대응 |
|---|---|
| gamut 밖 OKLCH → 잘못된 sRGB | C-clamp 단위 테스트 + oklch.com 대조 픽스처 |
| 케이스 쌍둥이/공백 이름 → 매핑 충돌 | `layerNameHazard` finding을 같은 카드로 선행 배출 |
| 변형 마크 미매칭 → 기본값 낙하 | prefix 매칭 + confidence=medium 강등, 낙하 시 finding으로 노출 |
| ~~GH doc 없이 레이어 작업 불가~~ | **해소(2026-08-07)**: 브리지 행 근본 수정(`e3c3ec3`) 후 Rhino-only 타깃 무조건 등록이 현재 상태 — .gh 없이 동작. 소스의 PARKED 주석은 낡음(§8 Q6) |
| 대량 적용 중 사용자 편집 (TOCTOU) | 레이어별 CAS fingerprint 실패 항목만 skip-and-report |

**라이브 게이트**: "픽스처가 그 경로를 실제로 밟아야 통과" — 지저분한 한/영 혼용 이름 + 블록 전용 레이어 + 커스텀 색 레이어를 실제로 포함한 픽스처 씬에서 finding이 실제 발생해야 하고, 검증은 **독립 관측점 2개 이상 교차**(job 결과 vs `rhino_layers` 서버 재관측; 목록과 감사가 어긋나면 그 불일치가 버그). 최종 게이트는 사용자 실모델(33MB, 70레이어, InstanceReference 181)에서.

---

## 5. UX

- **신규 탭·버튼 없음.** 2026-08-05 사용자 확정: curator 탭/버튼 줄 삭제 — "버튼은 채팅으로 컨트롤한다는 정체성을 깨뜨림". 진입은 **채팅 발화**("레이어 정리해줘", "재료 색 입혀줘") → audit → 카드.
- **제안 테이블 UI = ApprovalCard 확장**: 행별 [현재 색 스와치 → 제안 색 스와치], [원이름 → 정규 라벨(이름은 안 바뀜 표기)], IfcClass, confidence 뱃지, 매칭 근거(어떤 토큰→어떤 alias), 체크박스, 모호 항목 choices 라디오, ◎ focus(뷰포트에서 해당 레이어 객체 하이라이트 — Reer의 뷰포트 주석 아이디어의 우리식 대응).
- **Data 탭 보조 뷰(선택)**: DataView.tsx 관용구 재사용(요약 칩, "as of r{N}" staleness 스탬프, Rescan, 접이식 행) — 라벨링 현황 열람용 읽기 전용. 데모 검증은 `?demo=1` mock.ts 픽스처 확장 + javascript_tool 측정(패널 UI 검증법 메모리).
- **되돌리기**: 3중 안전망 — (1) 배치 전 자동 layerState 스냅샷 + 실패/불만 시 원클릭 복원 카드, (2) BeginUndoRecord 래핑으로 Rhino Undo 1회, (3) grant는 1회 적용 소모(사용자 Undo를 replay로 뒤집을 수 없음).

---

## 6. 사용자 확정 원칙과의 정합성 체크리스트

| 확정 원칙 (출처) | 이 스펙의 준수 방식 |
|---|---|
| "결정론적이면 코드로, 판단이면 프롬프트로" (user-decisions) | 색 계산·confidence·매칭은 서버 결정론, 모델은 재료 패밀리 triage만 |
| "서버 결정론=high·**모델 자기신고 금지**·하드 게이트 지양" (competitive-landscape) | confidence 3단계 서버 산정, 정보로만 노출, 미매핑 차단 없음 |
| "비파괴만, nickname 변조 금지(Raven 교훈)" (roadmap-20260724) | v1 rename 없음 — 라벨은 메타데이터, 이름 불변 |
| "human-wins: 사람이 수정한 영역 기본 불가침" | 커스텀 색 레이어 opt-out 기본, 카드 승인 없이는 무변경 |
| provenance default-deny + approval grant (curator-exploration Phase 3) | approval_request→grantId 프로토콜 재사용, anti-spoof 유지 |
| "컨텍스트 폴더는 LocalAppData" (user-decisions) | 테이블 저장 위치 `%LocalAppData%\GPTino\projects\<hash>\context` |
| "탐지는 서버 audit, 모델은 triage" (curator-plan) | 신규 audit kind `layerSemantics`가 결정론 스캔 담당 |
| "거짓 성공의 구조적 반박 유지" | 적용 후 서버 재관측 predicate + scanned/findings 구분 |
| "mutate는 typed gptino_v1 op만" (house-rules:51-52) | 신규 쓰기 경로 없음, 기존 CAS op 조합 |
| 라이브 게이트 원칙 "경로를 실제로 밟는 픽스처 + 관측점 2개 교차" (live-gate-value) | §4.4 게이트 계획 |
| "회사 내부 배포 임박 — 사내 표준 등록이 킬러 기능" (competitive-landscape) | 3-tier 테이블 + 의도적 승격 + 출처 메타데이터, Phase 1부터 회사 테이블 수용 |
| 템플릿 자동 전파 금지 (roadmap-20260724) | 회사 공유는 승격 행위로만 |

---

## 7. 단계별 로드맵

**Phase 1 — 라벨링 + 색 (회사 배포 타이밍 목표, 최소 킬러기능).**
- audit kind `layerSemantics`(이름 토큰 + RenderMaterial 신호), shipped 테이블 2종(`material-palette.json` OKLCH 정본 + `alias-seed-ko.json` 한/영 시드), OKLCH→sRGB 변환기(C#, gamut clamp), layer UserText 라벨 영속화, ApprovalCard 제안 테이블, `updateRhinoLayerProperties` 배치 + layerState 스냅샷.
- 회사 표준 테이블은 프로젝트 컨텍스트 폴더의 JSON으로 직접 배치(승격 UI 없이 파일로 시작 — 사내 배포에 충분).
- 검증: 단위(OKLCH 변환·clamp·alias 매칭) → 픽스처 씬 라이브 게이트(지저분 이름+블록 레이어+커스텀 색, 관측점 2개 교차) → 사용자 실모델 33MB/70레이어 스모크 → `?demo=1` 패널 검증.

**Phase 2 — BIM 매핑 + 프로파일 출력.**
- pivot 스키마 + IfcClass 컬럼 확정 적용, CALS/KS F 1542·AIA 출력 프로파일, Revit 커넥터 CSV / ggRhinoIFC 호환 UserText 방출, RenderMaterial 생성 opt-in op(빈 곳만 채움), Data 탭 라벨 현황 뷰, 3-tier 승격 플로(출처 메타데이터).
- 검증: 방출 CSV/UserText를 실제 VisualARQ 또는 ggRhinoIFC로 왕복 확인(외부 소비자 = 독립 관측점), 실무 구조회사 파일에서 SC/SG/SB + 변형 마크 매칭률 실측.

**Phase 3 — 물리 rename (v2 확정 사항 이행) + 학습.**
- rename/re-parent op 신설 전 **영향 분석 엔진**(GH 이름 필터·경로 문자열 스크립트·블록 정의 참조 스캔) → 영향 리포트 카드 → 승인 → 스냅샷 → 적용. Rhino-only 타깃 언파킹(레이어 위생은 "캔버스 불필요 작업"의 대표 사례). alias 테이블 학습 루프(사용자 수정 → 프로젝트 테이블 자동 후보 → 승격 제안).
- 검증: GH 이름 필터가 실제로 걸린 픽스처에서 rename 후 파이프라인 출력이 비지 않음을 게이트로; 벤치마크 루프(dev-mode 실Rhino) 절차 재사용.

---

## 8. 미해결 질문 (사용자 결정 필요)

1. ~~**기본 색 프리셋**~~ **[2026-08-07 해소]** 사용자 답: OKLCH picker처럼 **프리셋 선택식**(material-realistic / drafting-traditional / 추후 확장) — 어느 하나를 강제하지 않고 사용자가 고른다. 최초 기본값만 material-realistic.
2. ~~**커스텀 색 판정 기준**~~ **[2026-08-07 해소]** 사용자 답: "변경하려면 승인 버튼을 누르게 하면 된다" — 모든 색 변경은 어차피 ApprovalCard를 거치므로 별도 판정 정책 불필요. 커스텀 색 추정 여부는 카드의 **기본 체크 상태**에만 반영(커스텀 색 의심 → 기본 체크 해제), 하드 정책 아님.
3. ~~**라벨 저장 이중화 범위**~~ **[2026-08-07 해소]** 사용자 승인: layer UserText(`gptino.*` 네임스페이스) + 컨텍스트 폴더 이중 저장 확정. 반출 파일에 `gptino.*` 키가 남는 것 허용 — 반출 전 일괄 제거 기능은 필요 시 후속 옵션.
4. ~~**pivot의 discipline 필드**~~ **[2026-08-07 해소]** 사용자 답: **유지**.
5. ~~**CALS 납품 프로파일**~~ **[2026-08-07 해소]** 사용자 답: **이연**.
6. ~~**Rhino-only 언파킹 시점**~~ **[2026-08-07 소멸]** 브리지 행의 근본 원인(수신 루프가 UI-thread 요청 완료까지 파이프를 안 읽어 생기는 상호 대기)이 커밋 `e3c3ec3`(bounded queue + 단일 워커, registration 응답은 인라인 유지)로 수정되어 라이브 게이트 통과(pair 등록 성공, /layers 74ms). **v1부터 .gh 없이 레이어 작업 가능.** 주의: `GptinoRuntimeHost.cs`의 PARKED 주석(±1807)과 "with zero GH docs nothing registers" 요약은 수정 이후 남은 **낡은 주석** — 정리 필요.
7. ~~**IfcClass 어휘 확장**~~ **[2026-08-07 해소]** 사용자 답: **IFC 전체 이연** — v1은 색+재료 라벨만. 라벨 스키마는 나중에 IfcClass 컬럼을 얹을 수 있는 형태만 유지(§3.4는 후순위 참고자료로 보존).
8. ~~**RenderMaterial 생성 시 PBR**~~ **[2026-08-07 해소]** 사용자 답: Rhino **Plaster 템플릿 + 레이어 표시색 동일 색**. PBR 없음 (§2.3 반영).

---

# 구현 계획 (2026-08-07 확정 스펙 기준)

> 본 계획의 모든 파일:라인 앵커는 HEAD `e7aa9ca` 기준 실측이다(`AuditCoreAsync` switch의 `case "layerIntegrity"` = RhinoSceneFoundationAdapter.cs:243, `LayerFingerprint` = :3838, ApprovalCard 체크 상태 초기화 = ApprovalCard.tsx:25 — 본 계획 작성 시점에 재검증 완료). 다른 세션이 활발히 커밋 중이므로 **각 웨이브 착수 직전 앵커 재확인이 절차의 일부**다.

## W0. 선행 정리 — 착수 전 확인 목록

**W0-1. 트리 상태 재확인 (매 웨이브 반복).**
- `git log --oneline -5` + `git status --short` 재실행. 현재 HEAD `e7aa9ca`, 트리는 `docs/layer-curation-plan.md`(untracked)만 제외하면 clean — 그러나 seam 3 리포트 시점에는 패널 파일들(ChatPane.tsx, types.ts, mock.ts, styles.css 등 + 신규 GhFocusChip.tsx)이 dirty였다가 이후 커밋된 것으로 보이므로, **W3 착수 직전 패널 파일 라인 앵커는 전부 재실측**할 것.
- 리네임 landed 확인: commit `7d01886`로 `GPTino.CordycepsAdapter` → `src/GPTino.CanvasSceneAdapter/` (IRhinoSceneAdapter.cs, CanvasSceneBridgeOperationHandlers.cs, DocumentBoundCanvasSceneAdapters.cs), 테스트는 `tests/GPTino.BridgeContract.Tests/RhinoSceneBridgeOperationHandlerTests.cs` + `CanvasBridgeOperationHandlerTests.cs`. 옛 경로를 인용한 문서·주석 발견 시 함께 수정.

**W0-2. 낡은 주석 정리 (스펙 §8 Q6 이행).**
- `src/GPTino.Rhino/GptinoRuntimeHost.cs:1802-1818` PARKED 주석("with zero GH docs nothing registers")은 바로 아래 `TryRegisterUnambiguousTargets`(:1837-1848)가 Rhino-only 타깃을 무조건 등록하는 현재 코드와 모순 — 주석만 현행화(pair 요건 재도입 금지, 브리지 행은 `e3c3ec3`에서 근본 수정됨).
- `src/GPTino.CanvasSceneAdapter/IRhinoSceneAdapter.cs:380-384` `RhinoAuditRequest` doc comment가 7종 중 4종만 나열 — W2에서 layerSemantics 추가 시 함께 현행화. **kind의 단일 진실은 RhinoSceneFoundationAdapter.cs:215-255의 switch**로 간주.

**W0-3. 설계 결정 확정 (코드 착수 전 문서화).**
- **UserText 키 casing**: 기존 객체 레벨 선례는 `GPTino.LogicalEntityId` 식 dotted-PascalCase(RhinoSceneFoundationAdapter.cs:24-29), 스펙은 소문자 `gptino.*`. → **스펙대로 소문자 `gptino.material` / `gptino.canonical` / `gptino.labelSource` / `gptino.confidence` 채택** (ggRhinoIFC류 외부 소비자 호환 + 스펙 §3.3 확정 문구 존중). 어댑터 상단에 `private const string` 4개로 선언, 기존 키는 건드리지 않음.
- **Undo 래핑 갭 명시 수용**: 스펙 §4.2의 "전체를 BeginUndoRecord 하나로 래핑"은 현재 구조상 불가(각 bridge op가 독립 요청, undo record는 per-op — RhinoSceneFoundationAdapter.cs:3309-3313). **v1 결정: N개 op = N개 Rhino Undo 스텝을 수용하고, 원클릭 revert는 배치 전 `rhino.layerState` 스냅샷(:3508-3540)이 담당.** 신규 배치 op는 만들지 않는다(mutation 0종 원칙 유지).
- **`ensureRhinoLayer`의 ArgbColor**: required-args(OperationValidation.cs:172)에 없어 생략 시 투명 검정(0)으로 생성됨 — 스펙 §4.1의 "이미 강제" 서술은 코드와 불일치. 큐레이션 코드 경로에서는 argbColor 항상 명시를 규칙화(required-args 추가는 선택 사항, 기존 테스트 LiveDocumentBackendTests.cs:938-947은 이미 전달하므로 추가해도 무해).
- **grant 의미론**: `rhino.updateLayer`는 ApprovableOperations(LiveDocumentBackend.cs:580-586)에 없고 ConsumeApprovalGrant(:510-537)는 RhinoObject write만 소모 — 레이어 카드의 grant는 **UX-only, 15분 만료로만 소멸**(스펙 §4.2와 정합). 계획서·카드 문구에서 "CAS-grant 구조 강제"를 주장하지 않는다.

**W0-4. 병렬 세션 충돌 지대 표시.**
- **최고 위험 쌍**: `assets/instructions/house-rules.md` ↔ `InstructionAssembler.cs:83-105`, `assets/instructions/payload-guide.md` ↔ `DynamicToolSpecs.DefaultPayloadGuide` — InstructionAssetParityTests.cs:14-20이 문자 단위 일치를 빌드 강제. 다른 세션도 이 파일들을 만진 이력이 있으므로 **한 커밋에 쌍으로, rebase 후 parity 테스트 즉시 재실행**.
- 인터페이스 변경(W2)은 `RhinoSceneBridgeOperationHandlerTests.cs:340-404`의 full-interface fake를 컴파일 깨뜨림 — 인터페이스+fake를 같은 커밋에.
- 카드 슬롯은 세션당 1개(ApiModels.cs:30-31) — 레이어 제안 카드가 진행 중인 다른 카드를 덮어씀. 시퀀싱 주의만, 코드 변경 없음.

---

## W1. 서버 결정론 코어 (Rhino 불필요, 순수 단위 테스트)

### 변경 파일

| 구분 | 파일 |
|---|---|
| 신규 | `src/GPTino.AgentHost/Hosting/OklchColor.cs` — Ottosson 레퍼런스 OKLCH→sRGB(≈20줄) + gamut C-clamp(L·H 보존) + `ToArgb()` (**0xFF alpha 강제 OR**) |
| 신규 | `src/GPTino.AgentHost/Hosting/MaterialPalette.cs` — 프리셋 테이블 로드·파싱, family→OKLCH 좌표, 이산 L 스톱 배정, ARGB 방출 |
| 신규 | `src/GPTino.AgentHost/Hosting/LayerAliasMatcher.cs` — 정확/prefix/패턴 매칭 + confidence 산정 + provenance(어떤 규칙이 매칭했나) |
| 신규 | `assets/data/layers/material-palette.json`, `assets/data/layers/alias-seed-ko.json` — csproj 변경 없음(`assets\data\**\*` glob이 output `data\**`로 자동 링크, GPTino.AgentHost.csproj:40-43) |
| 수정 | `src/GPTino.AgentHost/Hosting/ProjectContextStore.cs` — `LayerStandardPath` 프로퍼티(`RulesPath` :59 옆) + `EnsureScaffolded`(:93-115)에서 `WriteIfAbsent`(:202-208) 시드. **Compose(:117-140)에는 절대 포함 금지**(16 KiB 프로즈 캡, JSON이 중간 절단됨 — 매처가 제안 시점에 온디맨드 로드) |
| 수정 | `tests/GPTino.AgentHost.Tests/DataLibraryTests.cs:48-59` — layers/ 2파일 app-base 발견성 assert 추가 |
| 신규 | `tests/GPTino.AgentHost.Tests/OklchColorTests.cs`, `LayerAliasMatcherTests.cs`, `MaterialPaletteTests.cs` |

### 시드 테이블 스키마 (실제 필드)

`material-palette.json` — 정본은 OKLCH, sRGB는 적용 시점 방출:
```json
{
  "meta": { "description": "...", "colorSpace": "oklch", "verifiedAgainst": "oklch.com 2026-08-07", "sources": ["OKLCH Layer Color Palette Picker material mode"] },
  "variantStopsL": [0.86, 0.75, 0.65, 0.54, 0.45],
  "presets": [
    {
      "id": "material-realistic",
      "label": "재료 사실색",
      "default": true,
      "families": [
        { "family": "concrete", "hueDeg": 75, "chroma": 0.025, "baseL": 0.65 },
        { "family": "plaster",  "hueDeg": 85, "chroma": 0.02,  "baseL": 0.92 },
        { "family": "wood",     "hueDeg": 62, "chroma": 0.09,  "baseL": 0.55 },
        { "family": "brick",    "hueDeg": 38, "chroma": 0.10,  "baseL": 0.55 },
        { "family": "steel",    "hueDeg": 250,"chroma": 0.025, "baseL": 0.55 },
        { "family": "glass",    "hueDeg": 220,"chroma": 0.06,  "baseL": 0.84 },
        { "family": "vegetation","hueDeg": 145,"chroma": 0.10, "baseL": 0.60 },
        { "family": "insulation","hueDeg": 100,"chroma": 0.10, "baseL": 0.75 }
      ]
    },
    { "id": "drafting-traditional", "label": "수기 도면 관례", "default": false, "families": [ "…yellow=timber, red=brick, dark blue=steel, green=concrete, purple=insulation…" ] }
  ]
}
```

`alias-seed-ko.json` — confidence는 저장하지 않음(매칭 종류에서 런타임 산정):
```json
{
  "meta": { "description": "한/영 레이어 이름 alias 시드", "lang": ["ko", "en"] },
  "entries": [
    { "canonical": "WALL",   "material": "concrete", "aliases": ["벽", "WALL", "W", "벽체"], "prefixes": ["W-"], "patterns": [] },
    { "canonical": "COLUMN", "material": "concrete", "aliases": ["기둥", "COL"], "prefixes": ["SC"], "patterns": ["^SC\\d+"] },
    { "canonical": "GIRDER", "material": "steel",    "aliases": ["거더"], "prefixes": ["SG"], "patterns": ["^SG\\d+"] },
    { "canonical": "BEAM",   "material": "steel",    "aliases": ["보"],   "prefixes": ["SB"], "patterns": ["^SB\\d+"] },
    { "canonical": "SLAB",   "material": "concrete", "aliases": ["슬라브", "슬래브", "SLAB"], "prefixes": [], "patterns": [] },
    { "canonical": "FINISH", "material": "plaster",  "aliases": ["마감", "FIN"], "prefixes": [], "patterns": [] }
  ]
}
```
`material`은 material-palette의 family id를 참조. 프로젝트 `layer-standard.json`은 동일 `entries` 스키마 + `"preset"` 필드(선택된 프리셋 id, W4) — **프로젝트 항목이 shipped 시드보다 우선**(canonical 충돌 시 프로젝트 승리).

### 매칭·confidence 규칙 (LayerAliasMatcher)

1. 정규화: trim + case-fold + FullPath 마지막 세그먼트 사용.
2. **정확 일치**(aliases) → `high`. 3. **prefix/패턴 일치**(prefixes/patterns — `"SC5 (Bracing)"` → COLUMN 케이스, DynamicToolDispatcherTests.cs:254-258이 고정한 실모델 회귀와 동일 행동) → `medium`. 4. 미매칭 → null 반환(모델 triage 대상, 모델 판정 채택 시 `low`). 각 결과에 provenance 문자열("`벽` → WALL alias 정확 일치") 동봉.
3. 로드 경로: shipped = `DataLibrary.Read("layers/alias-seed-ko.json")` 후 `.content` JSON 파싱(DynamicToolDispatcher.cs:586-588의 이중 파싱 관용구; **경로 문자열은 상수 1곳** — sections-ks가 :587/:731 2곳 인라인인 실수 반복 금지), 프로젝트 = `ProjectContextStore.LayerStandardPath` 온디맨드 + IOException 시 shipped-only 폴백(ReadLanguage :72-84 방어 IO 패턴).

### 의존 순서
OklchColor → MaterialPalette(팔레트 JSON 의존) → LayerAliasMatcher(alias JSON 의존) → ProjectContextStore 확장. W2와 독립 — 병행 가능.

### DoD
- OKLCH→sRGB가 oklch.com 대조 픽스처 3개 이상과 채널당 ±1/255 이내 일치, gamut 밖 입력은 L·H 보존 C-clamp, 모든 방출 ARGB의 alpha=0xFF.
- 실제 shipped 파일 2종이 파서로 파싱되고, 프리셋 내 hue 시드 간격 ≥25° 규칙 자동 검증 통과.
- SC/SG/SB + `"SC5 (Bracing)"` prefix 매칭이 medium으로, `벽` 정확 매칭이 high로 산정.

### 검증
```powershell
dotnet test tests/GPTino.AgentHost.Tests/GPTino.AgentHost.Tests.csproj -c Release
```
(AgentHost.Tests는 net8.0 headless, RhinoCommon 무관 — GPTino.AgentHost.Tests.csproj:16 직접 참조 + InternalsVisibleTo GPTino.AgentHost.csproj:19. 단위 테스트는 **실제 shipped JSON**을 대상으로도 1개 이상 돌릴 것 — fake 카탈로그만 쓰면 스키마 드리프트를 못 잡음.)

---

## W2. audit kind `layerSemantics` + layer UserText r/w

### 신규 계약 shape

**audit 요청**: 기존 `RhinoAuditRequest(Kind, Tolerance?, BandFactor?, Limit=50)` 그대로, `kind="layerSemantics"`만 추가. **audit 응답**: `RhinoAuditFinding`에 additive-nullable 필드 1개 (EndIndices 선례 IRhinoSceneAdapter.cs:399-401):

```csharp
// IRhinoSceneAdapter.cs — RhinoAuditFinding에 추가 (bridge 직렬화 record, 필수 필드 금지)
RhinoLayerSemanticsFacts? LayerFacts = null

public sealed record RhinoLayerSemanticsFacts(
    string FullPath,
    int ArgbColor,
    string? RenderMaterialName,          // Layer.RenderMaterial?.Name — 신호 2
    IReadOnlyDictionary<string, string>? UserText,  // gptino.* prefix만
    int TopLevelObjectCount,
    int BlockMemberCount,                // EnumerateLayerOccupants 경유
    IReadOnlyList<Guid> SampleOccupantIds);  // ◎ focus용, cap 32
```

어댑터는 **사실만 보고**(이름/색/RenderMaterial/기존 라벨/점유) — alias·팔레트·confidence 해석은 전부 AgentHost(sections-ks 선례: DynamicToolDispatcher.cs:456-461 "matching happens HERE"). finding 배출 대상 = `gptino.material` 라벨이 없거나 stale한 레이어만(70레이어 실모델이 limit=100 캡 안에 들어오게), `scanned` = 방문한 전체 레이어 수(scanned 0 vs findings 0 구분 — house-rules.md:39 audit 정직성).

**UserText 쓰기**: 신규 op 없음 — `UpdateRhinoLayerRequest`(IRhinoSceneAdapter.cs:229-235)에 `IReadOnlyDictionary<string,string>? UserText = null` 추가. **읽기**: `RhinoLayerSummary`(IRhinoSceneAdapter.cs:211-222)에 `IReadOnlyDictionary<string,string>? UserText = null`(gptino. prefix 필터) 추가 → rhino_layers 툴과 GET /layers가 자동 수혜(관측점 2 확보).

### 변경 파일 전수 (seam 실측 그대로)

| 구분 | 파일:앵커 | 내용 |
|---|---|---|
| 수정 | `src/GPTino.Rhino/RhinoSceneFoundationAdapter.cs:215-255` | `case "layerSemantics":` 추가(:243 layerIntegrity 옆) + unknown-kind 오류 문자열(:252-255) 확장; 신규 `AuditLayerSemantics` private 메서드(AuditLayerIntegrity :1531-1669 구조 복제: `(findings, scanned, truncated)` 튜플, `EnumerateLayerOccupants` :568 경유, `LayerFingerprint` :3838, FindingId=`Hash("layerSemantics|{layer.Id:D}")[..16]`); 결과 fingerprint는 :257-259 공용 해시가 자동 처리 — 별도 해시 금지 |
| 수정 | 같은 파일 `UpdateLayerCoreAsync :3281-3370` | (a) :3293-3296 at-least-one-field 가드에 userText 추가, (b) :3316-3327 apply에 `layer.SetUserString` 루프, (c) :3328-3352 요청-필드별 재검증에 `GetUserString` 대조, (d) :3353-3360 **Changed 계산에 userTextChanged OR** — 이거 없으면 라벨링 배치 전체가 Changed:false로 보고됨(seam 2 실측) |
| 수정 | 같은 파일 `ListLayersCoreAsync :3241-3252` | summaries에 gptino.* UserText 채움 + 상단 :24-29 옆에 `gptino.*` 키 상수 4개 선언 |
| 수정 | `src/GPTino.CanvasSceneAdapter/IRhinoSceneAdapter.cs` | :229-235 요청 필드, :211-222 summary 필드, :391-401 finding 필드+신규 record, :380-384 doc comment 현행화 |
| 수정 | `src/GPTino.AgentHost/Codex/DynamicToolSpecs.cs:118-137, 146-151` | rhino_audit 설명 + kind enum에 "layerSemantics"; :35 updateRhinoLayerProperties 가이드에 `userText?` |
| 수정 | `assets/instructions/house-rules.md:36-37` + `src/GPTino.AgentHost/Hosting/InstructionAssembler.cs:84-85` | kind 목록 — **문자 단위 미러, 같은 커밋** |
| 수정 | `assets/instructions/payload-guide.md` + `DynamicToolSpecs.DefaultPayloadGuide` | userText 필드 문장 — 같은 커밋(parity 테스트) |
| 수정 | `src/GPTino.AgentHost/Runtime/LiveDocumentBackend.OperationValidation.cs:372-382` | '(it must change at least one of color, visible, locked)' 가드 문구·판정에 userText 추가 — 어댑터 :3293-3296과 쌍. required-args(:174)·ChangeSetValidation.cs:480은 무변경(optional 필드) |
| 수정 | `docs/operation-contract.md:82-84, :152` | audit kind 목록 + updateRhinoLayerProperties 행 |
| 수정 | `tests/GPTino.BridgeContract.Tests/RhinoSceneBridgeOperationHandlerTests.cs:340-404` | full-interface fake 갱신(컴파일 강제): AuditAsync를 recording fake로 교체해 layerSemantics 라우팅+truncation diagnostic assert(:22 패턴 복제), UpdateLayerAsync(:390) 스텁에 UserText 통과 확인 |
| 수정 | `tests/GPTino.AgentHost.Tests/DynamicToolDispatcherTests.cs:448-449` | layerSemantics canned 결과 fake |

### 의존 순서
계약 record(IRhinoSceneAdapter) + fake 갱신 → 어댑터 구현 → 툴 스펙/instruction 쌍 → 검증 텍스트 → 문서. **주의**: bridge record는 `BridgeProtocol.JsonOptions`가 `UnmappedMemberHandling.Disallow`(BridgeProtocol.cs:42-48)라 구/신 혼합 실행(플러그인 재로드 없이 AgentHost만 재시작)에서 프로토콜 예외 — dev 중 양쪽 동시 재빌드·재로드.

### DoD
- `/dev/audit?kind=layerSemantics`가 실Rhino에서 LayerFacts 포함 finding을 반환(dev 엔드포인트 코드 변경 **0** — Program.cs:815-831은 kind pass-through).
- userText-only 쓰기가 Changed:true + read-back 대조 통과, fingerprint는 **불변**(LayerFingerprint :3838-3840이 UserText를 안 보므로 CAS 핀 유지 — 이게 설계의 요점).
- InstructionAssetParityTests·DynamicToolSchemaCoverageTests 포함 전체 그린.

### 검증
```powershell
dotnet build GPTino.sln -c Release
dotnet test GPTino.sln -c Release --no-build   # Windows 전용 (GPTino.Rhino가 net8.0-windows)
dotnet test tests/GPTino.BridgeContract.Tests -c Release
dotnet test tests/GPTino.AgentHost.Tests -c Release --filter FullyQualifiedName~LiveDocumentBackend
```
단, **audit 분석기 자체는 어떤 단위 테스트도 실행하지 않는다**(fake는 라우팅만) — 실행 커버리지는 라이브 게이트가 유일. W2의 라이브 확인은 게이트 스크립트 이전이라도 `scripts/dev-loop.ps1` 부팅 후 `/dev/audit` 수동 호출로 선행.

---

## W3. 제안 카드 + 적용 파이프라인

### 서버 결정론 경계 (anti-spoof 핵심 결정)

현재 approval_request의 item은 **100% 모델 저작**(DynamicToolDispatcher.cs:854-874) — confidence/색을 모델이 써넣으면 스펙의 "모델 자기신고 금지" 위반. **결정: dispatcher가 layerSemantics audit 실행 시(=rhino_audit 결과가 dispatcher를 통과하는 지점) LayerAliasMatcher+MaterialPalette로 제안 테이블을 서버 합성해 세션에 캐시하고, `kind:"layerSemantics"` approval_request는 target layerId로 캐시 행을 조회해 confidence/currentArgb/proposedArgb/canonical/evidence/preChecked/focusObjectIds를 서버가 채운다.** 모델이 같은 이름의 필드를 보내면 무시(모델 몫은 미매칭 레이어의 family triage 선택지 = 기존 choices 채널). 캐시 미존재 layerId 행은 dispatcher가 drop하고 diagnostic으로 알림.

### 카드 payload 확장 필드

TS(`ui/panel/src/types.ts:138-153`)와 C#(`ApiModels.cs:90-119`, trailing optional record param으로 back-compat) lockstep:
- 카드 레벨: `kind?: "layerSemantics"`, `preset?: { selected: string; options: { id: string; label: string }[] }`
- 항목 레벨: `layerFullPath`, `canonicalLabel`, `material`, `confidence: "high"|"medium"|"low"`, `evidence`, `currentArgbColor: number`, `proposedArgbColor: number`, `preChecked?: boolean`, `focusObjectIds?: string[]`
- `targets` = **(layerId GUID, layer fingerprint)** — grant 핀용(dispatcher Guid guard :861 + MintApprovalGrant 비어있는-fingerprint 거부 LiveDocumentBackend.cs:456-459 충족). **`focusObjectIds`는 별도** — ◎는 POST /focus로 객체를 선택하므로(Program.cs:344-359) 레이어 GUID를 넘기면 전 행이 "0 selected"로 무음 실패(seam 1·3 공통 경고). 서버가 LayerFacts.SampleOccupantIds로 채움.
- `AnswerApprovalRequest`(ApiModels.cs:114-117)에 `Preset?: string` 추가, PUT /approval(:424-477)의 카드 rewrite와 `ComposeApprovalBlock`(SessionOrchestrator.cs:601-642)이 승인 행의 canonical+선택 프리셋을 다음 턴 `<gptino_approval>`에 포함.

### 변경 파일

| 구분 | 파일 | 내용 |
|---|---|---|
| 수정 | `src/GPTino.AgentHost/Codex/DynamicToolSpecs.cs:472-529` | approval_request item 스키마에 신규 필드 선언 — **additionalProperties:false(:523)라 미선언 필드는 provider 레벨 opaque 거부**로 나타남 |
| 수정 | `src/GPTino.AgentHost/Codex/DynamicToolDispatcher.cs:846-898` | kind 분기 + 서버 합성 채움(위 결정) |
| 수정 | `src/GPTino.AgentHost/Api/ApiModels.cs:90-119` | 카드/항목/answer trailing optional 필드 |
| 수정 | `src/GPTino.AgentHost/Program.cs:424-477` | preset roundtrip |
| 수정 | `src/GPTino.AgentHost/Runtime/SessionOrchestrator.cs:601-642` | granted 블록에 canonical/preset |
| 수정 | `ui/panel/src/types.ts` | 위 shape |
| 수정 | `ui/panel/src/components/ApprovalCard.tsx` | :25 `useState({})` → **`preChecked` lazy initializer**(현재 전 행 unchecked 기본 — 스펙은 기본 체크+커스텀색 의심만 해제, 그대로 재사용하면 정반대 UX); `card.kind` 분기로 행 레이아웃([현재 스와치→제안 스와치], layerFullPath+"이름은 바뀌지 않음", confidence 뱃지, evidence, 카드 레벨 preset 라디오 — choices 라디오 관용구 :75-89 재사용); ◎는 `focusObjectIds`를 `useFocusTarget` 경유로(직접 onFocus 호출 금지) |
| 수정 | `ui/panel/src/styles.css:1616-1669` 인접 | net-new: `.approval-swatch`(스와치 관용구 0건 — 신규), `.approval-confidence.{high,medium,low}`, `.approval-evidence` — 토큰만 사용(--fs-micro/var(--mono)/var(--warn), 10px 타입스케일 규범); **`.audit-card-meta`(:2285)는 DataView가 살아있는 소비자 — prune 금지** |
| 수정 | `ui/panel/src/api/mock.ts:55-77, 845-861` | `demoLayerApprovalCard` 픽스처(high=정확일치 1행, medium="SC5 (Bracing)" prefix 1행, low=triage 1행, 커스텀색 preChecked:false 1행, ARGB int 스와치 쌍, evidence 문자열, 프리셋 2종) + **answerApprovalCard가 answer.choices/preset을 drop하는 lossy 버그 수정**(안 고치면 ?demo=1 검증이 거짓 실패) |
| 수정 | `ui/panel/src/api/client.ts:49-53, 205-213` / `hooks/useRuntime.ts:269-278` / `App.tsx:547` | answer payload에 preset 추가 시에만; optimistic patch 금지 원칙 유지(useRuntime.ts:267-268) |
| 수정 | `tests/GPTino.AgentHost.Tests/SessionOrchestratorTests.cs:48-71` | 레이어 카드 granted 블록 형제 테스트 |
| 무변경 | `ui/panel/src/messageMarkers.ts` | 카드는 세션 필드, 마커 아님 |

### 적용 배치 시퀀스 (스냅샷→CAS 일괄→재관측)

1. `rhino_layers` 1회 읽기 — 이 스냅샷의 fingerprint만 사용(대량 recolor는 레이어별+테이블 fingerprint 전부 재작성하므로 배치 중 재읽기 혼용 금지).
2. `saveRhinoLayerState` op `"GPTino: before-layer-curation"` — rhinoLayerTable write 선언 필수(OperationValidation.cs:1031-1040). 유일한 원클릭 복구선(W0-3 결정).
3. 승인 행마다 `updateRhinoLayerProperties` 1 op = **argbColor + userText 동시**(레이어당 1 op이면 자기-무효화 없음; userText는 fingerprint 밖, 색은 안이지만 각 레이어는 자기 op 1회만 받음). ChangeSet당 ≤20 op(curator-plan 관례 — 코드 강제는 없음, ChangeSetValidation은 ≥1만 요구). **visible/locked 토글 금지**(캐스케이드가 자손 fingerprint를 연쇄 오염 — DescribeCascadedLayerChanges :3377-3410).
4. 실패 모드: preflight는 ChangeSet 단위 all-or-nothing이므로 stale fingerprint 1건이 청크 전체를 Block → **skip-and-report는 청크 재구성으로 구현**: Block된 청크에서 stale 행 제거 후 나머지 재제출, 제거 행은 최종 리포트에 "사용자 수정으로 건너뜀"으로 명시(TOCTOU). 쓰기 후 mismatch throw(:3347-3352)는 롤백하지 않음 — 혼합 상태 발생 시 layerState 복원 카드 제안.
5. 재관측: `rhino_layers` 재읽기로 승인 행별 argbColor+userText 대조(관측점 2) — 서버 predicate, 모델 프로즈 신뢰 금지.

### 의존 순서
W1(팔레트·매처) + W2(audit facts) 완료 후. 서버 합성(dispatcher) → 계약 필드(C#+TS 같은 커밋 — raw-JSON 채널에 스키마 체크 없음, 한쪽만 고치면 무음 미렌더) → 패널 → mock → 게이트.

### DoD
- `?demo=1`에서 4행 카드(스와치/뱃지/evidence/preset 라디오/기본 체크 상태)가 렌더되고 answer roundtrip에 preset이 보존됨.
- 실Rhino에서: 승인 2행+거부 1행 → 승인 행만 색+라벨 반영, 거부 행 무변경, 재관측 predicate 통과, 스냅샷 존재.

### 검증
```powershell
cd ui/panel; npm run test; npm run build      # vitest(순수 로직) + 이중 tsc --noEmit 게이트
# ?demo=1 렌더 확인: npm run dev 후 claude-in-chrome javascript_tool 측정 (패널 UI 검증법 메모리)
dotnet test tests/GPTino.AgentHost.Tests -c Release --filter SessionOrchestratorTests
```
스와치 ARGB→#hex 변환은 export된 순수 헬퍼로 작성(ChatPane.tsx:337-361 패턴)해 vitest로 커버.

---

## W4. 마감

**W4-1. 프리셋 선택 UX 영속화.** 선택 프리셋을 프로젝트 `layer-standard.json`의 `"preset"` 필드에 저장(GET/POST /api/v1/language의 tiny-file 영속 관용구 — Program.cs:409-412, ProjectContextStore.WriteLanguage :86-91). 카드의 preset 라디오 초기값 = 저장값, 미저장 시 material-realistic. 주의: 컨텍스트 루트는 Rhino 경로 해시 기반(AgentHostOptions.cs:65-76) — Save As 재앵커 시나리오가 있으므로 **세션 간 경로 캐시 금지**.

**W4-2. RenderMaterial Plaster opt-in (fill-empty-only).** 진짜 신규 mutation 표면(Layer.RenderMaterial 쓰기 코드는 현재 0곳) — **신규 typed op `ensureLayerRenderMaterial`**, 7-레이어 전체 관통 비용 예산: Changes.cs:13-52 OperationKind + DynamicToolSchemaCoverageTests(:17-35, 레거시 "updateRhinoLayer" 항목과 혼동 주의) + IRhinoSceneAdapter record + DocumentBound 쌍(:95-175) + handler 라우트(:204-230) + 어댑터 구현(Plaster 템플릿 + 레이어 표시색 동일 색, `RenderMaterialIndex >= 0`이면 skip) + OperationValidation 4곳(:64-97/:119-179/:300-401/:1016-1046) + payload-guide 쌍 + operation-contract.md 표. **RenderMaterialIndex는 LayerFingerprint(:3840)에 포함** — 색 op와 같은 ChangeSet 혼합 금지(도메인 중복 규칙 DynamicToolSpecs.cs:43 + 자기-무효화), 별도 카드/별도 배치 + fingerprint 재취득 사이클. 색·라벨 파이프라인과 독립이므로 W1-3 릴리즈를 블록하지 않는 후순위 항목으로 배치.

**W4-3. Data 탭 라벨 현황 뷰 (선택).** DataView.tsx 관용구 그대로: openGroups Set(:43-49), "as of r{N}" 스탬프(:111-113), Rescan reloadKey(:114-121), 요약 칩 + honest-zero(:151-156). 렌더 슬롯 App.tsx:509-522. 읽기 전용, GET /layers의 UserText 필드 소비 — 신규 서버 표면 없음.

**W4-4. 개인 테이블 승격 — 후속 phase 마커만.** v1 범위 밖(사용자 확정: 프로젝트→개인 승격은 채택하되 반복 데이터가 쌓인 뒤). `layer-standard.json` 스키마가 shipped seed와 동일 `entries` 구조이므로 승격 = 행 복사 — 스키마 변경 없이 후속 가능. 회사 계층은 로드맵 밖. IFC 컬럼은 스키마에 자리만 유지(`gptino.ifcClass` 키 예약, 쓰지 않음).

### DoD / 검증
- W4-1: 프리셋 저장→AgentHost 재시작→카드 초기값 유지. `dotnet test tests/GPTino.AgentHost.Tests`.
- W4-2: fill-empty-only 판정 라우팅 단위 테스트 + 라이브 게이트에 "이미 머티리얼 있는 레이어 skip" 항목 추가. 전체: `dotnet build GPTino.sln -c Release; dotnet test GPTino.sln -c Release --no-build`.

---

## 라이브 게이트 계획

> CI에는 Rhino가 없다(.github/workflows/ci.yml) — 게이트는 dev 머신 수동 절차(Rhino 완전 종료 + 설치 패키지 전제, dev-loop.ps1:10-11,40-42). **랜딩 순서 강제**: (1) W2 audit kind → (2) 픽스처 kind → (3) 게이트 스크립트. 역순이면 stage 0이 "Unknown audit kind"(:252-255)로 hard-fail.

### 변경 파일
| 구분 | 파일 | 내용 |
|---|---|---|
| 수정 | `scripts/dev-loop.ps1:23` | ValidateSet에 `'layer-curation'` 추가; 부팅은 `-NoGrasshopper`(:27) — .gh 없는 Rhino-only 경로가 스펙의 해소 조건이므로 게이트 자체가 이를 증명 |
| 수정 | `scripts/dev-scene.py:224-228` | `build_layer_curation()` 추가(:58 build_hygiene 옆). **픽스처는 artifacts/에 두지 않는다**(KeepRuns=10 prune의 empty-definition.gh 교훈, dev-loop.ps1:69-75) |
| 신규 | `scripts/gate-layer-curation.ps1` | gate-approval.ps1 모델; **UTF-8 with BOM 필수**(PS 5.1 한국어 프롬프트 mojibake — gate-structural.ps1:15-16); `gate-layer-curation.json` PASS/FAIL 아티팩트 + exit 1 |
| 신규(선택) | `scripts/verify-layer-palette.ps1` | verify-structural-data.ps1 선례: shipped 팔레트 전 항목 gamut-after-clamp + hue 간격 ≥25° 자가검증 |

### 픽스처 씬 구성 (전 finding 경로를 실제로 밟도록 의도 배치 — gate-approval.ps1:9-12 원칙)
1. `콘크리트 벽` — 한글 정확 일치(high 경로)
2. `SC5 (Bracing)` — prefix/변형 마크(medium 경로, 실모델 회귀 재현)
3. `wall` + `Wall` — 케이스 쌍둥이(layerNameHazard 선행 배출 교차)
4. ` 마감 ` — 앞뒤 공백(정규화 경로)
5. `misc-stuff-01` — 미매칭(모델 triage/low 경로)
6. **블록 전용 레이어** — 객체가 블록 정의 멤버로만 존재(EnumerateLayerOccupants 경유 없이는 0개로 보이는 스코프 갭 검증)
7. **커스텀 색 레이어** — 검정/흰색/기본색이 아닌 색(preChecked:false 경로)
8. RenderMaterial이 이미 할당된 레이어 1개 — 신호 2 + W4-2 fill-empty skip 검증

### 게이트 스테이지
- **Stage 0**: `/dev/audit?kind=layerSemantics`로 픽스처가 기대 finding을 실제 배출하는지 사전 검증, 불일치 시 throw(gate-structural.ps1:73-77).
- **Stage 1**: POST /sessions + /messages 한국어 발화("레이어 정리해줘") → 카드 생성 대기 → **부분 승인**(승인 N-1행, 거부 1행 — 거부 의미론 증명, gate-approval.ps1:13-17) + preset 선택 전달.
- **Stage 2**: 모델-프리 이중 관측 교차 검증(아래 표) → JSON 아티팩트.

### 관측점 2개 교차 검증 항목표

| # | 검증 항목 | 관측점 1 | 관측점 2 | PASS 조건 |
|---|---|---|---|---|
| 1 | 픽스처가 경로를 밟음 | `/dev/audit?kind=layerSemantics` findings | 픽스처 기대 목록(스크립트 상수) | 8개 레이어 유형별 기대 finding 전부 존재, scanned=전체 레이어 수 |
| 2 | 승인 행 색 반영 | `GET /layers` argbColor | 카드의 proposedArgbColor(서버 합성값) | 정확 일치 + alpha=0xFF |
| 3 | 승인 행 라벨 영속 | `GET /layers` userText(gptino.*) | audit 재실행 — 라벨된 레이어가 finding에서 사라짐 | 두 관측이 정합(어긋나면 그 불일치가 버그) |
| 4 | 거부 행 불가침 | `GET /layers` 해당 행 argbColor+userText | 배치 전 스냅샷 값 | 무변경 |
| 5 | CAS 핀 비무효화 | 라벨-only 행의 fingerprint(전/후 `GET /layers`) | LayerFingerprint 설계(UserText 해시 밖) | 동일 |
| 6 | 블록 전용 레이어 스코프 | finding의 BlockMemberCount > 0 | `/dev/rhino-objects` 블록 정의 멤버 | 정합 |
| 7 | 스냅샷 존재 | rhino.layerState 목록 | 게이트가 아는 스냅샷 이름 | "GPTino: before-layer-curation" 존재 |
| 8 | per-row choice 도달 | 다음 턴 `<gptino_approval>` 블록(세션 로그) | 게이트가 보낸 answer payload | 선택 프리셋·choices 일치 |

**최종 스모크**: 사용자 실모델(33MB, 70레이어, InstanceReference 181)에서 전 파이프라인 1회 — 게이트 아티팩트는 prune 전에 run 디렉터리 밖으로 복사 보존.