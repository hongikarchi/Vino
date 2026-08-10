# 첫 공개 배포 체크리스트 (RhinoVibe)

작성 2026-08-10. 근거는 리포 실측 감사 2회(리네임 표면 / 배포 준비도) + Yak·F4R·상표 조사.
아이콘 작업은 [docs/brand/icon-brief.md](brand/icon-brief.md)에서 별도로 다룹니다.

---

## 0. 리포 현황 (2026-08-10 기준)

`main` = `origin/main` = `a569f5e`, **미푸시 커밋 없음**, 작업 트리 깨끗함.
Layer curation은 **W1~W4 전부 커밋됨**(`c2dc7e9`)이고 **라이브 게이트 하니스도 랜딩**했습니다(`a569f5e` — `scripts/gate-layer-curation.ps1` 218줄 + 픽스처 `dev-scene.py` + `dev-loop.ps1` ValidateSet).
두 커밋 모두 **CI 초록**입니다.

즉 계획 문서상의 기능 작업은 사실상 끝났고, 남은 것은 **배포 자체를 위한 작업**입니다.

한 가지 확인되지 않은 것: 게이트 하니스는 들어왔지만 **실제로 1회 실행된 증거를 찾지 못했습니다**(`artifacts/`에 `gate-layer-curation*.json` 없음 — 다만 `artifacts/`는 주기적으로 prune되므로 결정적이지는 않습니다). §7 참고.

---

## 1. 지금 결정해야 하는 것 5가지

나머지 작업이 전부 여기에 달려 있고, **전부 "첫 push 전에만 무료"** 입니다.

| # | 결정 | 지금 상태 | 미루면 |
|---|---|---|---|
| 1 | **Yak 패키지 이름을 `RhinoVibe`로 바꾼다** | `manifest.yml:2` = `GPTino` | Yak은 개명을 새 패키지로 취급합니다. 이미 배포한 뒤 바꾸면 기존 사용자에게 **두 패키지가 동시 설치**되고, 두 `.rhp`가 **같은 플러그인 GUID**로 한 Rhino에 로드되며, 두 AgentHost가 같은 데이터 루트를 다툽니다. 아직 아무도 설치하지 않았으므로 지금은 비용 0 |
| 2 | **문서에 새겨지는 키를 바꿀 것인가** | `GPTino.LogicalEntityId`, `GPTino.SourceDocKey`, `gptino_bake_family`, `gptino.` 레이어 네임스페이스, `GPTino::Quarantine` 레이어 | 이 키들은 **사용자의 .3dm 안에 저장**됩니다. 출시 후 변경 = 기존 파일의 객체 출처 표식 상실 → 에이전트가 자기 과거 산출물을 "사용자 소유 기하"로 보고 승인을 요구하기 시작. 지금은 본인 개발용 파일만 영향 |
| 3 | **Rhino 명령 `GPTinoOpenPanel`** | `GptinoOpenPanelCommand.cs:9` | 사용자가 직접 타이핑하고 툴바 버튼·별칭에 넣는 이름. 개명 시 구 이름 명령을 얇게 하나 남겨 전달하는 방식을 권장 |
| 4 | **`0.1.0-alpha.7`을 유지할 것인가** | prerelease | Package Manager가 **기본으로 숨깁니다.** F4R의 Install 버튼이 Package Manager를 여는데, 신규 사용자는 빈 목록을 봅니다("include pre-releases" 체크 필요) |
| 5 | **Rhino 8.21+로 한정할 것인가** | `Directory.Packages.props:6,10` RhinoCommon 8.21 → 사실상 `rh8_21` 태그 | 8.0~8.20 사용자 전원 제외. 넓히려면 패키징 스크립트가 아니라 **이 핀**이 레버. 현재 아무것도 이 태그를 검증하지 않음 |

---

## 2. 리네임 작업 — 범위 325파일 / 1,749줄

### 2-1. 절대 바꾸면 안 되는 것

| 대상 | 위치 |
|---|---|
| 플러그인 GUID `b903e20d-1cb3-4d8e-b37d-9be263a678d4` | `GptinoPlugIn.cs:8` + `Properties/AssemblyInfo.cs:4` (**두 곳 동기 필수**), 검증 `PluginMetadataTests.cs:9` |
| 패널 GUID `91ab786f-…` | `GptinoPanel.cs:9` |
| GH 어셈블리 GUID `d2b0c9b2-…` | `GptinoAssemblyInfo.cs:27` |

**좋은 소식**: 이 플러그인은 `GH_Component`를 **하나도 등록하지 않습니다**. Grasshopper 리본 탭도, 카테고리도, 보존해야 할 컴포넌트 GUID도 없습니다. 리네임 위험이 그만큼 줄어듭니다.

### 2-2. 원자적으로 함께 바꿔야 하는 것 (한쪽만 바꾸면 조용히 깨짐)

- **네임드 파이프**: `NamedPipeTransport.cs:29` 생성 + `:36` `StartsWith("gptino-")` 검증자 + `:41` 오류문구
- **HMAC 도메인 분리 문자열**: `BridgeAuthentication.cs:40,41` — 어긋나면 핸드셰이크가 전부 인증 거부(원인 안 보임)
- **`GPTINO_*` 환경변수** (`BRIDGE_PIPE`, `BRIDGE_SECRET`, `API_TOKEN`, `READY`, `PYTHON`, `DEV_MODE`, `DEV_DATA_DIRECTORY`) — `.rhp` → `AgentHost.exe` → `Terminal.exe` 3단 계약
- ⚠️ **위 변수를 바꾸면 스크러빙 필터도 같이 바꿔야 합니다.** `AgentHostBootstrapper.cs:349,350`, `CodexAppServerClient.cs:1262,1263`, `TerminalLauncher.cs:174,175`가 `StartsWith("GPTINO_")`로 **Codex 자식 프로세스에 넘기기 전 비밀을 제거**합니다. 변수만 바꾸고 필터를 놔두면 **비밀이 모델의 자식 프로세스로 새어 나갑니다.** 로그 마스킹 정규식(`DevLoop/Program.cs:1209`)도 동일
- **HTTP 인증 표면**: 헤더 `X-GPTino-Token`·`X-GPTino-Panel-Parent`, 쿠키 `gptino_runtime`
- **기동 핸드셰이크 토큰** `GPTINO_READY ` — 안 맞으면 플러그인이 준비 신호를 영원히 기다리다 타임아웃
- **패널 URI 스킴 `gptino://`** — C# 인터셉터(`GptinoPanel.cs:15`)와 React CTA(`App.tsx:70`, `NoGrasshopper.tsx`) 두 코드베이스가 동시에 발행

### 2-3. 이름 변경이 아니라 "이관"이 필요한 것

- `%LOCALAPPDATA%\GPTino\projects\<fingerprint>` — **두 곳에 독립 정의**(`AgentHostOptions.cs:75`, `ProjectArchiveReader.cs:42`). 어긋나면 아카이브 브라우저가 빈 목록
- `%LOCALAPPDATA%\GPTino\backups` (`GptinoDocumentBackup.cs:26,29`) — 사고 복구용 백업. 고아가 되면 사용자가 정확히 필요할 때 찾지 못함
- 🔴 **`.gptino-instance.lock`** (`RuntimeInstanceLock.cs:11`) — 구/신 빌드가 서로 다른 락 이름을 쓰면 **둘 다 같은 데이터 루트를 점유**해 SQLite 동시 쓰기 → DB 손상. 이 목록에서 유일하게 데이터를 망가뜨릴 수 있는 항목

**이미 있는 재사용 가능한 장치**:
- `LegacyDataDirectoryAdoption.cs` — 단계적·전부-아니면-전무 데이터 루트 이관기(이미 구현·테스트됨). 후보 부모 디렉터리를 1개→2개로 일반화하면 그대로 씁니다
- `LegacyAdapterOwnerConverter.cs` — Wireify/Cordyceps 개명 때 만든 "옛 이름도 읽되 새 이름만 쓴다" 컨버터. `gptino:auto`/`gptino:absent` 같은 **열거형 sentinel**에 그대로 적용 가능
- `BridgeProtocol.Version` — W4에서 이미 **16**으로 올라갔습니다(`BridgeProtocol.cs:42`). 개명 커밋에서 **17로 한 번 더** 올리면 버전 불일치가 조용한 `JsonException` 대신 명시적 `protocol_version` 오류가 됩니다

### 2-4. 그냥 두기를 권하는 것

- **`gptino_v1` 툴 네임스페이스** (`DynamicToolSpecs.cs:63`) — 모델이 호출하는 이름. 스키마를 어차피 바꾸는 게 아니라면 유지가 안전합니다. 진행 중이던 Codex 스레드가 재개되면 옛 이름으로 계속 호출합니다
- `docs/archive/*` — 과거 기록이므로 옛 이름 유지가 정확합니다

---

## 3. 배포 차단 항목 (이름과 무관)

1. 🔴 **`.yak`을 한 번도 만들어 설치해본 적이 없습니다.** CI는 `-BuildYak` 없이 돌아서 `Yak.exe build` 경로가 재현 가능한 환경에서 검증된 적이 없습니다. 깨끗한 Rhino 8.21에 실제 설치까지 1회 확인 필요
2. 🔴 **배포 태그 확인** — `rh8_21`로 나갈 것으로 보이나 아무것도 검증하지 않음
3. 🔴 **매니페스트 내용이 자리표시자 수준** — `authors: GPTino contributors`(익명), description이 **Codex CLI + OpenAI 계정이 필수라는 사실을 한 마디도 안 함**, 키워드 빈약. `AssemblyInfo.cs:7,9`의 Email/Phone이 **빈 문자열**이라 Rhino 플러그인 상세에 연락처가 공란
4. 🔴 **git 태그가 0개**인데 `SECURITY.md:27`은 "최신 태그 프리릴리스를 지원한다"고 약속
5. 🔴 **막다른 오류 메시지 2개** — `GptinoPlugIn.cs:68`("bounded development diagnostics를 보라")과 `AgentHostBootstrapper.cs:465`("local runtime log를 보라")가 **릴리스 설치본에 존재하지 않는 것**을 가리킵니다. 진단 추적은 `GPTINO_DEV_MODE=1`에서만 동작. 로드 실패한 사용자는 아무것도 없는 곳을 보게 됩니다
6. 🟡 **문서** — README에 **사용자용 설치 섹션이 아예 없고**, 두 문서 모두 Node.js/npm(내장 설치기가 씀)과 PyNiteFEA(구조해석)를 누락. `installation.md`의 **두 번째 문단이 "Wireify·Cordyceps를 설치하지 마세요"** — 처음 온 사용자는 그게 뭔지 모릅니다. prerelease 체크박스 안내도 필요

---

## 4. 상표·브랜드 잔여 (개명해도 남는 것)

- 🔴 **패널 UI가 "GPT"를 브랜드처럼 씁니다** — `App.tsx:290` "Sign in to GPT", `:303` "Log in to GPT", `:293` "GPTino drives GPT through the Codex CLI". 제품명에서 GPT를 빼는 목적이 여기서 무너집니다. "OpenAI Codex"로 교체 권장
- `client.ts:199` 하드코딩 기본 모델 `gpt-5.6-sol`, `mock.ts:188-205` 데모 카탈로그의 `GPT-5.6 Sol/Terra/Luna` 표기
- **GitHub 리포명** `GPTino` → `RhinoVibe` (public, 스타 1·포크 0 — GitHub가 옛 URL 리다이렉트를 유지하므로 비용 낮음). `manifest.yml:9`, `AssemblyInfo.cs:5,10,11`, `GptinoAssemblyInfo.cs:31`, `Directory.Build.props:12`가 전부 이 URL
- **지원 이메일 개설** — F4R 필수 필드이자 `AssemblyInfo.cs:7` 공란 해소
- **도메인** — `rhinovibe.ai` 등록 가능(RDAP 확인), `archivibe.ai`도 가능. `rhinovibe.com`은 2018년 등록된 파킹(GoDaddy), `archivibe.com`은 타사(브라질 마케팅 SaaS) 사용 중
- **McNeel 상표 확인 메일은 선택** — F4R에 RhinoBIM·RhinoVAULT·RhinoPlus·RhinoGrow·RhinoCityJSON·RhinoMembrane 등 `Rhino*` 선례가 다수(상업 제품 포함). 사실상 관용 영역이라 우선순위 낮음
- **프라이버시 고지** — 프롬프트와 문서 컨텍스트가 사용자 Codex 계정을 통해 OpenAI로 전송된다는 문단이 리스팅에 필요

---

## 5. 리포 위생 (차단은 아니지만 공개 전 권장)

- **패키지 78MB / 382파일** — AgentHost를 `--self-contained`로 퍼블리시해 ASP.NET Core 공유 프레임워크 전체(Components·Identity·OAuth 등 안 쓰는 것 포함)를 끌고 옵니다. 매 버전이 78MB 다운로드
- **`.claude/`가 리포 `.gitignore`에 없습니다** — 이 머신의 전역 ignore로만 가려져 있어, 다른 체크아웃에서는 `settings.local.json`이 커밋됩니다
- `ui/panel/src/api/mock.ts:522`에 개발자 절대경로 `C:\Users\user\AppData\Local\GPTino\...`가 번들에 포함
- **코드 서명 없음** — `.rhp`/`.gha`/`.exe` 2개 전부 미서명 → Rhino 미서명 경고 + SmartScreen
- 버전이 `manifest.yml:3`과 `ui/panel/package.json:4` 두 곳에 있고 비교하는 검사가 없음
- `build-package.ps1:298,299`가 libgit2sharp 버전 `0.31.0`/`2.0.323`을 하드코딩 — 의존성 올리면 "license not found"로 실패
- 매니페스트를 파싱·검증하는 테스트가 0개
- `CHANGELOG.md` 없음, 태그 트리거 릴리스 워크플로 없음
- `docs/`에 한국어 기획문서 대량 공개(`layer-curation-plan.md` 56KB, `curator-plan.md` 37KB 등) — 첫 방문자가 보게 될 내용
- 문서 낡음: `development.md:130-135`의 legal 목록 5개(실제 9개), `packaging/yak/README.md:9-24`에 `legal/`·`icon.png` 누락

**깨끗하다고 확인된 것**: 시크릿·API 키 없음, `.references/`(21MB)와 33MB `.3dm`은 ignore + 패키지 차단 이중 방어, dev 엔드포인트는 설치본에서 도달 불가, 텔레메트리 없음, 서드파티 라이선스 9종 정확히 스테이징(GPL-with-linking-exception인 libgit2 포함).

---

## 6. 리포 밖 작업 (코드에 없음)

- **Food4Rhino 리스팅**: 스크린샷, 긴 설명, 카테고리, 라이선스 폼(Apache-2.0), 지원 링크, 데모 영상/GIF
- 사람 검수 1~2일 소요 (Rhino Account → "Create App from Yak")
- 지원 이메일 주소 개설
- 첫 사용자 유입 후 F4R 댓글 신속 대응 (경쟁 제품 대비 차별점)

---

## 7. 진행 중인 기능 작업과의 관계

Layer curation은 **W1~W4가 모두 커밋**되었고(`c2dc7e9`) **라이브 게이트 하니스도 랜딩**했습니다(`a569f5e`). 기능 측면에서는 배포를 막는 것이 없습니다.

남은 것은 **게이트를 실제로 1회 돌리는 일**입니다. CI에는 Rhino가 없어 dev 머신 수동 절차입니다(`scripts/dev-loop.ps1`의 `layer-curation` 스테이지).
문서 임베드 키(§1-②)를 바꾸기로 했다면 **게이트를 개명 이후에 돌리는 편**이 낫습니다 — 안 그러면 두 번 돌게 됩니다.

---

## 8. 권장 순서

1. **결정 5건** (§1) — 나머지가 전부 여기 달려 있습니다
2. **리네임을 한 커밋으로** — 원자 그룹(§2-2) 동시 변경, 데이터 이관(§2-3) 포함, `BridgeProtocol.Version` 17로 bump, `gptino_v1`은 유지
3. **`.yak` 빌드 → 깨끗한 Rhino 8.21에 설치 검증 → 배포 태그 확인** (§3-1,2)
4. **메타데이터·오류 메시지·문서 수정** (§3-3~6), UI의 "GPT" 문구 교체 (§4)
5. **레이어 큐레이션 라이브 게이트 1회** (§7)
6. **git 태그 + 리포 개명 + 도메인/이메일** (§3-4, §4)
7. **Yak push** — ⚠️ 영구입니다. 삭제 불가, yank만 가능. 첫 push의 대소문자 표기가 영구 고정되므로 반드시 `RhinoVibe`
8. **F4R 앱 생성 + 리스팅 자산** (§6)
