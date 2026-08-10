# 이슈 트리아지 — 2026-08-10

사용자가 한 번에 제기한 9개 이슈에 대한 근본원인 분석. 읽기 전용 조사 10건 + 반증 검증 6건,
그리고 라이브 런타임 데이터(`%LOCALAPPDATA%\GPTino`)와 RhinoCommon/Grasshopper SDK 문서 대조.

**판정 표기** — 확정: 코드를 직접 읽고 검증까지 통과 / 유력: 코드 근거는 있으나 마지막 고리 미증명 /
가설: 코드상 가능하나 라이브 확인 필요.

---

## 0. 이번 라운드에서 새로 드러난 것 (사용자가 묻지 않았지만 더 급한 것)

### 0-1. 사전 백업이 사용자의 라이브 Rhino 문서 정체성을 오염시킨다 — **확정, 데이터 안전 등급**

체인 전체가 코드와 디스크 양쪽으로 확인됐다.

1. `src/GPTino.Grasshopper/GptinoDocumentBackup.cs:78,87` — 백업을
   `rhinoDocument.WriteFile("…\backups\<ghGuid>\.model.3dm.tmp", options)` 로 쓴다.
2. **[라이브 검증 완료 — 2026-08-10, `artifacts/dev-loop/probe-writefile/result.json`]**
   `RhinoDoc.WriteFile`은 `EndSaveDocument` 이벤트를
   **`FileName = "…\.model.3dm.tmp"`** 로 발화한다. 이것이 실제 기전이다.
   *(SDK 문서의 "the active document's name will be changed"는 오해를 부른다 —
   실측상 `FileWriteOptions.UpdateDocumentPath` 기본값은 **false**이고, `doc.Path`·`doc.Modified`는
   7단계 내내 바뀌지 않았다. 그래서 백업 매니페스트가 원본 경로를 기록한 것과도 정합한다.
   §0-1-보정 참조.)*
3. `src/GPTino.Rhino/GptinoPlugIn.cs:257-266` 의 `OnEndSaveDocument` 가드는 `ExportSelected` 와
   **Rhino 자체 autosave 경로만** 거른다(`RhinoAutoSavePaths.cs`). GPTino 자기 백업 폴더는 통과한다.
4. `GptinoRuntimeHost.ObserveRhinoDocument`(`src/GPTino.Rhino/GptinoRuntimeHost.cs:206-265`)가
   그 `.tmp` 경로를 문서 신원으로 채택한다.
5. `src/GPTino.AgentHost/Runtime/RuntimeStateProjector.cs:139-141` 의
   `Path.GetFileNameWithoutExtension(".model.3dm.tmp")` → **`".model.3dm"`**. 사용자가 헤더에서 본 그 문자열.
6. `GptinoDocumentBackup.cs:96` 의 `File.Move(temporary, final)` 가 그 파일을 즉시 다른 이름으로
   옮기므로, 등록된 경로는 **존재하지도 않는 파일**을 가리킨다.

**디스크 물증** — `%LOCALAPPDATA%\GPTino\projects\79C3FE0C3FB9262E\context\project.json`:

```json
"projectName": ".model.3dm",
"rhinoFile": "C:\\Users\\user\\AppData\\Local\\GPTino\\backups\\3f42551106cd4eb7b23f05a7790f1b05\\.model.3dm.tmp"
```

`AgentHostOptions.ResolveDataDirectory()`(`:65-82`)가 RhinoPath의 SHA256으로 프로젝트 루트를
정하므로, 오염된 경로는 **별도의 프로젝트 폴더 하나를 통째로 만들어 냈다.** 그 안에는 세션 1건
(이름 끝 `(imported)`)과 메시지 62건이 있다 — 사용자가 그 유령 프로젝트 안에서 실제로 작업했다는 뜻이다.

**검증자 정정(중요)** — 이 오염이 *그 즉시* 리바인드나 AgentHost Kill을 일으키지는 않는다.
`CreateProjectId`(`GptinoRuntimeHost.cs:1894-1906`)와 `StableTargetKey`
(`DocumentRuntimeTargeting.cs:55-73`)는 둘 다 **path-free**다 — Save As가 세션을 안 깨게 만든
정체성 재설계의 결과다. 갈라짐은 "다음번 AgentHost bootstrap이 오염된 경로를 들고 뜰 때" 발생한다.
따라서 **E1(recoveryRequired 잔존)과 E2(문서명)는 같은 뿌리가 아니라 별개 결함 두 개다.**

### §0-1-보정 — 라이브 프로브 결과 (2026-08-10, `artifacts/dev-loop/probe-writefile/`)

GPTino 플러그인 없이 순수 Rhino 8만 띄워 `WriteFile`/`Write3dmFile`/`SaveQuiet`를 직접 호출한 결과.

| 검증 항목 | 결과 |
|---|---|
| `FileWriteOptions.UpdateDocumentPath` 기본값 | **`false`** |
| `WriteFile` 후 `doc.Path` | **변하지 않음** (7단계 내내 `null` 유지) |
| `WriteFile` 후 `doc.Modified` | **`true` 유지** (not-modified 마킹 없음) |
| `WriteFile`이 `EndSaveDocument` 발화? | **예 — `FileName = "…\.model.3dm.tmp"`** |
| `UpdateDocumentPath=false`로도 발화? | **예** (동일하게 `.model2.3dm.tmp` 전달) |
| `Write3dmFile`이 발화하는 `FileName` | **빈 문자열 `""`** |
| `GH_DocumentIO.SaveQuiet` — 동일 문서(객체 1개) | `.tmp` = **`false`** / `.gh` = `true` / `.ghx` = `true` |

**반증된 것** — "문서가 개명되고 not-modified로 마킹돼 Ctrl+S가 백업 폴더를 향한다"는 **사실이 아니다.**
`doc.Path`는 그대로이고 dirty 플래그도 유지된다. **사용자 파일 손실 위험은 없다.**
따라서 E2의 등급은 "데이터 안전"이 아니라 **"프로젝트 신원 오염"** 이다 — 여전히 심각하지만 급이 다르다.

**확정된 진짜 기전** — `WriteFile`이 `EndSaveDocument`를 백업 임시 경로로 발화하고,
`GptinoPlugIn.OnEndSaveDocument`가 그것을 걸러내지 못해 `ObserveRhinoDocument(serial, args.FileName)`이
그 경로를 문서 신원으로 채택한다. SDK 경로 갱신이 아니라 **플러그인 자신의 이벤트 핸들러가 원인이다.**

**수정(둘 다 하는 것을 권장)**
1. `GptinoDocumentBackup.BackupRhino`의 `WriteFile` → **`Write3dmFile`**. 실측상 후자는 `FileName=""`으로
   발화하고, 빈 문자열은 `ObserveRhinoDocument`의 `Path.IsPathFullyQualified` 가드
   (`GptinoRuntimeHost.cs:208-211`)에 걸려 채택되지 않는다. 한 단어 교체.
2. `OnEndSaveDocument`에 `GptinoDocumentBackup.BackupRoot` 하위 경로 거부를 추가.
   `RhinoAutoSavePaths`와 같은 성격의 가드이며, 1번이 SDK 내부 동작에 의존하므로 두 겹으로 둔다.

**GH 백업 실패 원인 확정** — 동일 문서에 대해 확장자만 바꿨을 때 `.tmp`만 실패했다.
`SaveQuiet`이 **확장자로 디스패치**한다. `.definition.gh.tmp` → `.definition.tmp.gh` 같은 형태로
바꾸면 된다(임시 이름을 유지하되 확장자를 `.gh`로).

**새로 발견 — 백업 실패가 모달 다이얼로그를 띄운다.**
프로브 첫 write가 실패하면서 Rhino가 *"Failed to save … The temporary file could not be renamed."*
모달을 띄웠다. **`SuppressAllInput = True`로도 억제되지 않는다.** 제품에서 `BackupRhino`는
매 execute 직전 Rhino UI 스레드에서 도는데, 사용자의 실제 모델은 450MB이고 백업이 20초마다 걸린다 —
일시적 파일 잠금(Defender/OneDrive/다른 Rhino) 한 번이면 **작업 중 모달이 뜨고 UI 스레드가 멈춘다.**
`GptinoDocumentBackup`은 예외를 삼키지만 **다이얼로그는 예외가 아니다.** D의 "정리 중 멈춤"에
기여했을 수 있는 미조사 경로다.

### 0-2. 크래시 보험이 한 번도 작동한 적 없다 — **확정**

`%LOCALAPPDATA%\GPTino\backups\*\manifest.json` 3건 전부 `"grasshopperBackup": null`.
`BackupGrasshopper`는 throttle 없이 매 execute마다 도는데도 3/3 실패다 —
`GH_DocumentIO.SaveQuiet(".definition.gh.tmp")` 가 항상 false를 반환한다(확장자 디스패치가 원인으로
추정되나 SDK 내부라 미확정). 코드 주석이 "the small GH definition is backed up every execute
(it is the primary IP)"라고 적은 그 보호가 **실재하지 않는다.**

---

## 1. 사용자 요구별 분석

### A. Rhino / Grasshopper 고정을 각각 독립으로 + 폭 흔들림 + 압정 아이콘

**서버는 이미 준비돼 있다.** 와이어 계약(`types.ts:429`, `ApiModels.cs:201`)과 프롬프트 주입
(`SessionOrchestrator.cs:774-819`)이 두 도메인을 이미 각각 문장으로 쓴다. 막힌 곳은 UI 한 곳이다 —
`ChatPane.tsx:439`의 `pinned: PinnedSelection | null` 슬롯 하나에, `pinSelection()`이
`/selection/current` 한 번으로 Rhino+GH를 스냅샷해 **통째로 덮어쓴다**(`:448-464`).
해제도 `setPinned(null)` 전부-아니면-전무(`:1162`).

**폭 흔들림의 확정 원인** — `.selection-strip` 블록 **전체가 조건부 마운트/언마운트**된다
(`ChatPane.tsx:1148 / 1169 / 1187`). 선택이 0↔n으로 바뀔 때마다 컴포저 높이가 약 28px 튄다.
가로 폭 자체는 모든 컨테이너가 `minmax(0,1fr)`/`min-width:0`로 클램프돼 있어 변할 수 없으므로,
"넓이가 들쑥날쑥"의 실체는 (a) 높이 변화의 체감, 또는 (b) `.chat-stream`에 `scrollbar-gutter`가
없어(`styles.css:929-935`) 스크롤바가 생겼다 사라지며 `.message { width: min(92%,680px) }`가
재계산되는 것 — (b)가 유력하다.

**압정** — 아이콘 자산이 아니라 하드코딩 이모지 `📌`(`ChatPane.tsx:1156, 1184`). `Icons.tsx`에 pin이 없다.
hover 강조색 규칙은 `.pin-button:hover`에 이미 뼈대가 있다(`styles.css:1370-1395`).

**요구한 형태(0 placeholder → 카운트 → 클릭 고정)는 서버·계약 변경 없이 구현 가능하다.**
다만 두 가지 함정이 있다:
- GH 문서가 2개 이상이면 카운트는 "버스트 승자" 문서 하나만 반영한다
  (`LiveDocumentBackend.cs:2823-2849`). 서버는 `docId`를 내려주는데 컴포저가 무시한다 → **H와 같은 결손.**
- 칩 카운트(SSE, 상한 GH 64 / Rhino 512, 250~500ms 지연)와 실제 고정되는 집합(REST 재조회)이
  다른 시점·다른 상한이라, "숫자 보고 클릭"이 다른 집합을 고정할 수 있다.

### B. 승인을 버튼 클릭으로 + "승인이 자꾸 안 됨"

**버튼 UI는 이미 있고 응답도 자연어가 아니다.** `ApprovalCard.tsx`는 체크박스 + "선택한 N개 승인"을
렌더하고 구조화 JSON(status/approvedItemIds/choices)을 보낸다. 그런데 사용자가 겪는 두 불만이
모두 실재하며 원인이 다르다.

1. **승인 버튼을 눌러도 서버가 턴을 재개하지 않는다 — 설계다(확정).**
   `PUT /sessions/{id}/approval` 핸들러(`Program.cs:424-431, 543-554`)에는 `SessionOrchestrator`가
   **주입조차 되지 않아** 턴 시작이 물리적으로 불가능하다. 승인 내용은 `ComposeApprovalBlock`을 통해
   **다음 사용자 메시지의 턴 입력에 얹혀야만** 모델에게 도달한다(`SessionOrchestrator.cs:601-653`).
   라이브 게이트 스크립트조차 PUT 직후 `"승인했어. 진행해줘."`를 보내도록 짜여 있다.
   → **"말로 아득바득"은 버그가 아니라 현재 계약이다.**

2. **거절("하지 마세요")은 모델에게 영원히 도달하지 않는다(확정).**
   `ComposeApprovalBlock`은 `status == "granted"`가 아니면 null을 반환한다
   (`SessionOrchestrator.cs:613-618`). 거절하면 카드만 바뀌고 모델은 그 사실을 어떤 턴에서도 모른 채
   다음 턴에 같은 걸 또 시도한다. Claude permission과의 진짜 격차는 "재개 없음"보다 이쪽이다.

3. **카드는 서버가 못 만든다 — 모델이 `approval_request`를 자발적으로 불러야만 뜬다(확정).**
   `SetApprovalCardAsync` 호출부 3곳 전수 확인(`DynamicToolDispatcher.cs:1086`, `Program.cs:445`, `:552`).
   브로커 가드는 예외 메시지로 "툴을 부르라"고 가르칠 뿐이다. 실사용 로그에서는 삭제 거부 후 모델이
   툴 대신 산문으로 승인을 요구했다. 표본 정정: 로컬 40개 runtime.db 중 `approval_card` 컬럼을 가진
   것은 2개(나머지는 구스키마)이고, 컬럼이 있는 활성 프로젝트(세션 7·메시지 303)에서 **저장 사례 0건.**

4. **grant 수명과 카드 수명이 어긋난다(확정).** grant는 인메모리·15분 만료·1회 소비
   (`LiveDocumentBackend.cs:449, 478-481`)인데 카드(grantId를 담은 SQLite 행)는 **절대 지워지지 않는다.**
   만료·소비·재시작 후에도 죽은 grantId가 매 턴 주입된다. 게다가 `MintApprovalGrant`가 돌려주는
   `expiresAt`을 `Program.cs:546`이 버려서 `ApprovalCard` 레코드에 만료 필드 자체가 없다 —
   패널도 서버도 카드가 죽었는지 표시할 수 없다.

5. **실패가 두 겹으로 침묵한다(확정).** `InjectApprovalFlags`는 지문 불일치 시 아무 진단 없이 원본 op를
   통과시키고(`LiveDocumentBackend.cs:604-652`), 어댑터는 "패널이 발급하는 승인을 받아 재제출하라"고만
   말한다(`RhinoSceneFoundationAdapter.cs:2326-2334`). 이미 grant를 실은 모델은 "승인이 없다"고
   결론 내리고 사용자에게 **또** 승인을 요구한다. 로그의 "브로커가 승인을 인식하지 않는다"가 정확히 이 모양이다.

6. **GH 컴포넌트 승인은 구조상 "한 방에 성공해야" 한다(확정).** 승인이 "현재 구조 지문"에 핀되는데
   그 지문이 incoming wire(`CurrentSources`)를 해시하므로
   (`GrasshopperCanvasFoundationAdapter.cs:1094-1121`), 승인과 소비 사이의 어떤 wire 편집도 승인을
   무효화한다. 15분 TTL·1회 소비와 겹친다.

7. 승인 대기는 **세션당 1개**만 존재 가능하고(단일 컬럼), 새 요청이 이전 카드를 조용히 덮어쓰며,
   세션 목록에 대기 배지가 없어 다른 세션에 카드가 떠 있는 줄도 모른다.

*반증된 가설 1건*: "resume에 dynamicTools를 안 실어서 모델이 approval_request를 못 봤다" —
툴은 단일 배열이고 같은 턴에 `change_submit`이 동작했으므로 모델의 사후 변명(환각)으로 보는 게 타당하다.
(다만 `ResumeThreadAsync`에 dynamicTools가 없는 것 자체는 별개 정리 항목으로 유효.)

### C. 세션 전환 시 드래프트/첨부/고정 소실 — **확정, 12/12 검증 통과**

세 가지는 원인이 조금씩 다르다.

- **텍스트·첨부**: `App.tsx:534`의 `key={selected?.id ?? "none"}` 때문에 세션 전환 시 ChatPane이
  통째로 언마운트/리마운트되어 로컬 `useState`가 파괴된다. 유일한 원인.
- **고정**: 그 위에 `ChatPane.tsx:612-617`의 "세션 바뀌면 pinned를 null로" 라는 **의도된 두 번째
  안전장치**(다른 세션으로 오전송 방지)가 겹쳐 있다. key를 없애도 그대로 사라진다.
- **첨부는 더 나쁘다**: 서버 id가 아니라 브라우저 메모리의 `File` 객체이고(`ChatPane.tsx:102-109`)
  base64 인코딩·서버 저장은 전송 시점에만 일어난다. 붙여넣은 이미지는 디스크 원본조차 없다.

팀은 이미 이 사실을 알고 **탭 전환 축만** `hidden`으로 막아뒀다(`App.tsx:527-532`). 세션 축은 안 막았다.

**중요한 정정** — `key`는 "안 막은 축"이 아니라 **의도적 장치**다: `ChatPane.tsx:422-433`이 focus
격리 복구를 언마운트 클린업으로 구현하고, `:526-528`은 openLogs가 "keyed by session via App's key"
라고 적는다. **key를 지우면 세션을 떠날 때 Rhino 문서의 격리/잠금이 복구되지 않는다.** 올바른 수정은
key 제거가 아니라 draft/pending만 App 레벨로 리프팅(`Map<sessionId, …>`)하거나 영속화하는 것.

**캐시로 처리 가능한가 — 부분적으로 가능하다.** 패널 호스트는 WebView2이고 localStorage는 실제로
디스크에 영속한다(라이브 확인). File/Blob도 구조화 복제 대상이라 IndexedDB에 원본째 저장 가능하다
(붙여넣은 스크린샷 포함). **진짜 장벽은 오리진 파편화다** — AgentHost가 `127.0.0.1:0`(임의 포트)로
뜨므로(`Program.cs:25`) 기동마다 오리진이 갈리고, 실제 leveldb 안에 서로 다른 `127.0.0.1:<port>`
오리진이 **59개** 쌓여 있다.

웹 스토리지 해법의 커버리지: 세션 전환 ✅ / Codex 게이트 언마운트 ✅ / 패널 리페인트 ✅
/ **포트 변경 재내비게이션 ❌ / Rhino 재시작 ❌ / Save As 리바인드 ❌**

부수 확인: 같은 오리진 파편화 때문에 **테마·탭 같은 기존 취향값도 Rhino 재시작마다 초기화되고 있다.**

드래프트가 날아가는 확정 경로는 세션 전환 외에 두 개 더 있다 — Codex 로그아웃 게이트 화면 진입
(`App.tsx:284-286`, 트리 전체 언마운트), 그리고 baseUri 변경 시 `_webView.Url = uri` 전체 리로드
(`GptinoPanel.cs:80-84, 343-350`). SSE 끊김은 **아니다**(반증됨 — 최초 연결 후 runtime을 null로
되돌리는 코드가 없다).

### D. 정리 중 Rhino 종료 — 계획 대비 현황

**기록만 낡았다.** 포스트모템 P0 4건과 P1 3파(halt 래치 + 삭제 위상정렬 / 원장 영속화 / 정리 등급제)는
**전부 구현·머지·푸시됐다**(637187f · e7aa9ca · 76ac6d2 · 8c82709 · e8a2c1e). GH-open 크래시 수정
537aaed의 2대 방어도 현재 코드에 살아 있다. 메모 `gptino-p1-reliability-plan`의 "미착수 / W1 진행 중"만
현실과 어긋난다 → **메모 갱신 필요.**

**그러나 08-10 실사용 로그가 새 결함 3건을 실증했다** — 그리고 이 셋은 독립이 아니라 한 사가의 연쇄다:

```
00:19:36  서버 auto-tidy가 프로젝트 rules.md의 "Do not use auto-tidy layout"을 무시하고 109개 재배치 커밋
00:39     모델이 20분 들여 수동 재배치
00:49~56  절단 가드 4연속 거부 → 재구축 교착(임시 와이어를 못 걷어내 중복 배선 잔존)
01:16:56  그 상태에서 python.setSchema가 45s 브리지 예산 초과 → Rhino UI 스레드 잠김 → recoveryRequired
01:46     "Crash 이후 …" 잡
```

- **⓵ 자기가 방금 붙인 와이어조차 되돌릴 수 없다.** 절단 가드가 "소비자 컴포넌트의 저작권"만 본다
  (`LiveDocumentBackend.cs:4338-4354`). 검증자가 우회로(writeSet 위조)를 시도했으나 하드 블록이라
  **비대칭은 우회 불가** = 이 결함이 더 강해졌다.
- **⓶ 서버 소유 auto-tidy가 사용자 프로젝트 규범을 알 수 없다.** `rules.md`는 모델만 구속하고
  오케스트레이터 후크는 구속하지 못한다(`LiveDocumentBackend.cs:1287`).
- **⓷ 45s 프리즈의 진짜 갭은 타임아웃이 아니라 `setSchema`에 비용 게이트가 없다는 점.**
  `PreflightExecuteCost`(`:4096`)는 실행에만 있고 setSchema preflight(`:4085-4093`)에는 없는데,
  어댑터는 무조건 `document.NewSolution`을 돌린다(`GrasshopperPythonFoundationAdapter.cs:279-280`).
  "실행은 비싸니 막고, 스키마 변경은 공짜"라는 잘못된 전제. **소켓 하나 추가가 전체 다운스트림 solve를
  무제한 유발한다.**

**남은 크래시 커버리지 구멍** — `ThrowIfDetached`는 정확히 5곳, 전부 `document.NewSolution` 직후에만
있다. 같은 어댑터의 `document.RemoveObject(…, true)`(`:719`) — **바로 "정리 작업"의 삭제 경로** — 와
`AddObject(…, update:true)`(`:244`)는 무방비다. `GrasshopperDocumentLiveness.cs:19` 주석이 적용 범위를
스스로 "(NewSolution)"으로 좁게 코드화한 것이 근본 원인.

**halt 복귀가 실제로는 안 된다(실증)** — job `2c03d252`의 phase가 `recoveryrequired`
(≠`recoveryrequired-acknowledged`)인데, 같은 세션이 재기동 후 정상 제출을 재개하고 auto-tidy까지
커밋했다. halt가 복귀했다면 `ThrowIfSessionHalted`(`:5806`)에 걸렸어야 한다. 복귀 조건이 "이번 기동이
실제 전환한 잡"뿐이라, 크래시 직전 이미 terminal이 된 인시던트는 이력으로 취급된다 —
**2026-08-07 사용자 결정 ①("재시작 후 halted 복귀")과 어긋난다.**

**미해결 항목**: W4(canvas.deleteMany·지문델타), P2 3종, 크래시 메모의 R3(동기 teardown)·R5(ghfocus lease).

*불일치 하나* — 사용자가 말한 "정리작업하다 꺼짐"과 08-07 정리 사가는 다르다. 08-07은 크래시가 아니라
부분적용 recoveryRequired였고, 실제 프로세스 사망 흔적은 **08-10 01:16의 45s solve 프리즈** 직후다.

### E. recoveryRequired 잔존 / `.model.3dm` / 상단 Rhino 표시

세 개 다 별개다.

- **E1 recoveryRequired가 재시작해도 남는다** — 진짜로 영원히 남는 건 halt가 아니라 **문제 배너**다.
  `ReadRecentProblems`(`LiveDocumentBackend.cs:1870-1902`)가 `entry.State`만 보고
  `recoveryrequired-acknowledged` phase를 보지 않는다. 게다가 재시작 시 durable job 테이블 **전 행이
  무필터로 복원**된다(`DurableJobStore.cs:371-383`). 그 세션이 새 잡을 제출해야만 사라진다.
  실측: 6개 프로젝트에서 최신 잡이 RecoveryRequired로 남아 있고, 전 프로젝트 통틀어
  `recoveryrequired-acknowledged` 행 **0건**. 범위는 더 넓다 — 같은 규칙이 Blocked/Failed도 올리는데
  그쪽엔 해제 수단조차 없고, 최신 잡이 Blocked/Failed로 굳은 프로젝트가 4개 더 있다.
  ("재시작 후 halted 복귀" 자체는 버그가 아니라 08-07 승인된 설계다.)
- **E2 `.model.3dm`** — §0-1 참조. **확정.**
- **E3 상단 Rhino 칩** — 사용자 직관이 맞다. Codex·Grasshopper는 이름 붙은 `StatusChip`을 갖는데
  Rhino만 브랜드마크 "G"의 배경색으로만 표현된다(`App.tsx:333-340`). **서버는 `health`/`healthDetail`을
  이미 투영하고 있다** — 데이터는 있고 표면이 없다. `StatusChip` 재사용으로 끝나는 S 작업.
  덧붙여 **Grasshopper 칩이 파란색인 것은 브리지 연결을 전혀 보증하지 않는다** — "정의 파일 경로를 안다"는
  뜻일 뿐이다(`App.tsx:318-322`). 사용자가 이를 정상 연결로 읽은 것은 UI 의미 오도다.
- 사용자가 본 "연결 확인해보라"는 halt 배너의 재개 실패 메시지(`ChatPane.tsx:342-343`)일 **가능성이
  높다**(유력, 확정 아님). acknowledged 0건과 합치면 "재개를 눌렀는데 POST가 실패했다"가 자연스럽다 —
  그렇다면 미조사 결함이 하나 더 있다(패널이 죽은 포트/구 AgentHost에 붙어 resume이 네트워크 단계에서 실패).

### F. 캔버스 정리 품질 / 스크린샷 검증 / 작성 단계 규칙

**"모델이 추정치를 쓴다"는 반은 맞고 반은 틀리다.** 생성 시점 `CanvasAutoPlacement`는 **160×80 고정
추정값**만 알지만(`:21` 주석이 Phase-1 한계라고 자인), 턴 종료 후 `CanvasLayout`은 **실제 bounds**로
재배치한다. 결과가 이상한 진짜 원인은 크기가 아니라 배치 규칙이다:

- **소스 노드를 무조건 0열에 몰아넣는 longest-path 레이어링**(`CanvasLayout.cs:151,181`) — 슬라이더가
  소비처에서 수 열 떨어진 맨 왼쪽에 쌓인다. 라이브 아티팩트: 109개 중 **46개가 0열**. GH 관행(그리고
  사용자 규칙)인 "소비처 바로 왼쪽"과 정반대.
- **`GH_Group`이 일반 노드로 취급된다**(`GrasshopperCanvasFoundationAdapter.cs:41,56`) — 그룹 사각형
  폭(≈1900px)이 그대로 컬럼 폭이 되어 열 간격이 폭발하고 그룹 자체가 이동 대상이 된다. 라이브 arrange
  7개 중 2개가 실제 GrasshopperGroup id였다. 레이아웃 단위 테스트가 그룹을 메타데이터로만 모델링해
  이 결함이 계속 테스트를 통과해 왔다.
- **컬럼 내 X 정렬이 bounds "중심" 기준**(`CanvasLayout.cs:369`) — 소켓이 붙는 좌·우 엣지가 어긋난다.
  사용자 규칙은 "우측 엣지 공통선 정렬"을 요구한다.
- **`canvas.move`가 전 배치 all-or-nothing CAS** — 컴포넌트 하나의 layout fingerprint만 흔들려도 정리
  전체가 `precondition_refused`. 소켓 추가/솔브 직후가 정확히 그 시점이다.
- **tidy가 실패해도 아무에게도 안 보인다** — `wait:false`로 던지고 예외는 삼키며, arrange 잡의 종료
  상태는 last-terminal 추적에서 의도적으로 제외된다(`LiveDocumentBackend.cs:1319,1324,5757`).
  이것이 "canvas 재배치 미반영"의 정체다.
- **tidy 스킵 경로가 넓다** — 세션 halt, 마지막 잡이 Failed/Blocked/RecoveryRequired/Cancelled, 턴이
  completed 아님, paused. 이 경우 사용자는 160×80 추정 배치 그대로를 본다.
- (가설) 소켓 변경 경로가 `ExpireLayout`만 하고 `PerformLayout`을 안 불러 스냅샷이 읽는 `Attributes.Bounds`가
  아직 옛 사각형일 수 있다 — 이것이 "서버가 아는 크기 vs 화면에 그려진 크기"의 진짜 기전일 가능성.

**"규칙이 없나"는 사실과 다르다.** `house-rules.md:183-189, 224, 281`에 좌→우 흐름·그룹·배선 규칙이
이미 명문화돼 있다. 진짜 결손은 두 개다: ⓐ 모델이 **좌표를 볼 수 없어** 스스로 검증 불가능하다(저작
체인 중 재조회 금지 + 잡 결과에 pivot/bounds 없음), ⓑ 규칙 준수를 재는 **결정론적 서버 술어가 없다**.
덤으로 `house-rules`("자동이라 부를 필요 없다")와 `arrange_layout` 툴 설명("체인 끝에 1회 호출하라")이
서로 다른 말을 한다(`DynamicToolSpecs.cs:348` vs `house-rules.md:280`).

**스크린샷은 지금 구조로 불가능하다.** GH 캔버스를 캡처하는 코드가 저장소 전체에 **0건**이고,
동적 툴 결과는 텍스트 전용이라 이미지가 **턴 도중에** 들어갈 경로가 없다 — 이미지는 `turn/start`의
`localImage` 아이템으로만 들어가며 `turn/steer` 자동 주입은 롤백됐다. 신규 브리지 op + 아티팩트 +
localImage 경로가 전부 필요한 L 작업이다.

**"작업 끝나면 반드시 정리"는 지침으로 못 한다** — 서버 후크가 이미 있고(그래서 사용자 rules.md를
무시하기까지 한다), 지침을 더 써도 모델은 좌표를 못 본다. 올바른 방향은 **arrange 후 서버가 위반을
세어 보고**하는 것 — 역방향 와이어 수, 그룹 미소속 생성 컴포넌트 수, 열 정렬 편차.
`curator-plan.md`의 "탐지는 서버 결정론, 모델은 triage만" 원칙이 그대로 적용되는 자리다.

### G. goal 진행 상태 + 하단 토글

- **진행 상태 필드가 계약에 아예 없다.** `status`는 proposing→confirmed→scored/rejected라는 **승인
  라이프사이클**일 뿐 실행 상태가 아니다. UI의 "진행 중" 배지는 `status === "confirmed"` 하나만 보고
  무조건 찍는다(`GoalCard.tsx:32,39`) → 세션이 idle이든 halt든 **영구히 "진행 중"인 거짓 신호.**
- **목표를 확정해도 턴이 시작되지 않는다.** `PUT /sessions/{id}/goal`은 카드 저장 + SSE 알림만 한다
  (`Program.cs:668-699`). B와 정확히 같은 결함이다 — 승인 직후 "진행 중" 배지가 떠 있는데 실제로는
  아무것도 안 돈다. **사용자가 "끝남? 진행 중?"이라고 묻게 되는 가장 직접적 원인.**
- **닫는 경로가 모델의 자발적 `goal_score` 하나뿐**이고 사용자 측 종료 버튼도 삭제 경로도 없다.
  라이브 46개 프로젝트에서 **scored 카드 0건**, 오늘 확정된 카드 1건이 confirmed로 남아 있다.
- **접힘 기능이 전혀 없고 높이 제한도 없다.**
- **위치에 대해서는 사용자 표현과 코드가 어긋난다** — 카드는 이미 트랜스크립트 **맨 뒤**(라이브 로그 바로
  위)에 렌더된다(`ChatPane.tsx:976-985`). 스크롤이 항상 바닥에 붙는 구조라 긴 카드가 보이는 영역의
  위쪽을 통째로 채워 최근 답변을 밀어내는 것이 "위에 있다"는 체감의 원인으로 보인다(가설).
- 부수 결함 2건: 카드가 새로 생겨도 자동 스크롤이 안 걸려 화면 밖에 렌더될 수 있다
  (`ChatPane.tsx:602-608`). 거절한 카드가 제안 중 카드와 **똑같은 accent 강조**로 남는다
  (`goal-rejected` CSS 규칙 부재).

### H. "02A Frame topology" zoom-selected 실패 — **확정, 결정론적 오작동**

사용자 의심이 정확히 맞다. **여러 GH 파일을 열면 100% 재현된다.**

`02A Frame Topology`는 Rhino가 아니라 **GH 캔버스** 대상이다(라이브 데이터에서
`[[ghfocus:582ebf44-…|02A Frame Topology]]` 마커 확인, C# 스크립트 컴포넌트).
경로는 `POST /api/v1/canvas/focus` → `canvas.focusObjects`.

**요청에 문서 식별자가 전혀 실리지 않는다** — 본문은 `{objectIds, zoom}`뿐(`client.ts:233`).
서버는 세션 바인딩을 무시하고 `RequireDefaultTargetState()` = **"가장 먼저 등록된 GH 문서"** 로 보낸다
(`LiveDocumentBackend.cs:5584`).

live-jobs.db가 어느 쪽이 default인지까지 확정해 준다 — `2d40d0126097dd4f` 잡이 01:15:38에 먼저,
구조 분석 세션 `af19c6c6d729efdd`의 첫 잡은 01:42:56. 02A 컴포넌트는 `af19…`에서 생성됐다.
→ **칩 클릭 → 항상 `2d40…`으로 전달 → FindObject 전부 null → 선택 0개 → 줌 스킵.**
덤으로 **그 무고한 문서의 선택이 전부 해제된다**(오배송의 결정적 지문).

부가 실패 경로 3개:
- 문서가 맞아도 GH 캔버스가 다른 정의를 표시 중이면 프레이밍을 **의도적으로 건너뛴다**(빈 catch 포함).
- **응답 shape 버그** — 서버는 `{result:{selectedCount,…}}`인데 패널은 최상위에서 읽는다
  → 모든 focus 칩 문구가 항상 **`undefined 선택`**. 실패 원인을 구분할 유일한 단서가 소실된다.
  (같은 버그로 Rhino 칩의 `n hidden`/`n locked` 줄도 영구히 사라진다.)
- canvas focus에는 writer 선점 fast-fail이 없어, 잡 실행 중이면 칩이 disabled로 굳고 아무 문구도 안 뜬다.

**두 번째 독립 원인 — 환각 instanceId.** runtime.db의 다른 ghfocus 마커에
`a10c0001-3d5d-4aa0-8a01-000000000001` 같은 **명백히 조작된 패턴 GUID**가 다수다. `house-rules.md:124-129`가
"canvas 툴이 실제로 반환한 id만 쓰라"고 규정하는데 지켜지지 않고, `messageMarkers.ts:79-93`의 검증은
GUID **형식**만 본다. **이 경로는 GH 문서가 하나만 열려 있어도 발동한다.**

**Rhino 쪽이 왜 멀쩡한가** — `/focus`도 똑같이 default target을 쓰지만,
`GptinoRuntimeHost.cs:1825-1828`이 관측된 Rhino 문서가 정확히 1개가 아니면 등록 자체를 포기해서
한 프로젝트에 Rhino 문서가 항상 하나다. 즉 이 결함은 **GH 문서 다중성에만 노출된 구조적 갭**이다.

**재현 절차**: A.gh를 먼저 열고 → B.gh를 나중에 → 세션을 B에 바인딩 → B 안에 컴포넌트 생성 →
칩 클릭. 기대: 줌 안 됨 + 문구 `undefined 선택` + **A.gh 캔버스의 기존 선택이 전부 해제됨**.

### I. "패널 세션이 만료되었다"

**출처는 단 한 곳**: `ui/panel/src/api/client.ts:111-116`. **조건도 단 하나 — `/api/v1/*` 응답이 HTTP 401.**
서버에서 401을 내는 곳도 `Program.cs:195-203` 하나이고, 판정은 쿠키 `gptino_runtime` 값이 그 AgentHost
프로세스의 `ApiToken`과 같은지뿐(`:994-999`). 패널은 헤더를 절대 안 보내므로 쿠키가 유일 자격증명이다.

**시간 기반 만료는 이 경로에 존재하지 않는다.** 2분 만료·1회용인 것은 부트스트랩 nonce뿐이고, 그건
실패하면 대기 페이지로 나타난다. **"만료"라는 단어 자체가 오진을 유도하는 오칭이다** —
괄호 안의 "이 런타임의 토큰이 아닙니다"가 실제 사실.

**구조적 결함(확정)**: 토큰은 프로세스마다 새 난수인데, 담는 쿠키는 이름이 항상 `gptino_runtime` 하나,
Domain 없이 `127.0.0.1` + `Path=/`, Expires 없음(세션 쿠키)이고 모든 AgentHost가 임의 포트에 붙는다.
**쿠키는 포트로 격리되지 않으므로**, 서로 다른 포트의 두 AgentHost가 같은 쿠키 한 칸을 놓고 다툰다.

**유력한 시나리오(확정 아님)**: 다른 Rhino 문서의 패널이 부트스트랩하며 쿠키를 덮어써 먼저 열려 있던
패널이 401을 맞는다. 오늘 동시 가동한 런타임이 **셋**이라는 디스크 증거는 있다(`:49167` 10:33 /
`:60858` 11:50:50 / `:51575` 11:59:59, 첫 번째는 12:10까지 DB를 만짐). 다만 검증자가 "계정당 하나인
Cookies 파일을 공유한다"는 물증을 **반증했다** — 그 파일에 `gptino_runtime`이 0건이다(세션 쿠키라
디스크에 안 쓰인다). 두 Rhino가 같은 **인메모리** 쿠키 항아리를 공유하는지는 플랫폼 동작이라
이번 조사로 실증되지 않았다. *(참고: 이 확인 과정에서 하위 에이전트가 쿠키 DB를 덤프하는 보안 경고가
발생했다. 자격증명 내용은 이 문서에 일절 반영하지 않았고, 앞으로도 이 검증은 `msedgewebview2.exe`의
`--user-data-dir` 인자 개수를 세는 방식으로 해야 한다.)*

**401 자동 회복 경로가 아예 없다.** 재내비게이션 조건은 "baseUri가 바뀌었을 때" 하나뿐이므로
(`GptinoPanel.cs:75-85`), 포트가 그대로인 채 토큰만 어긋난 상태는 코드상 **영원히 자가 복구되지 않는다.**

**안내 문구대로 해도 안 나을 수 있다(가설, 확인 필요)** — 패널은 `PanelType.PerDoc`으로 등록되고
OpenPanel만 호출된다. Rhino가 문서별 패널 인스턴스를 캐시해 재사용하면 `_navigated`가 true인 채라
재내비게이션이 안 일어난다. `App.tsx:269`의 Retry는 `window.location.reload()`라 **확실히 무효**다
(쿠키를 다시 심을 수 없는 URL로 재로드). 실질 회복은 Rhino 재시작뿐일 수 있다.
그리고 덮어쓰기 가설이 맞다면 회복은 **핑퐁**이 된다 — A를 재부트스트랩하면 이번엔 B가 401을 맞는다.

---

## 2. 뿌리 3개로 수렴한다

| 뿌리 | 해당 이슈 |
|---|---|
| **ⓐ 클라이언트 상태가 어디에도 영속되지 않음** | C (직접), A·G (재배치의 선행 조건) |
| **ⓑ 실패가 `useRuntime`의 단일 `error` 문자열로 수렴해 컴포저 하단 10px "✕1" 칩으로만 보임** | B, I, H, E |
| **ⓒ 대상 식별자에 문서 스코프가 없음** | H (직접), A의 칩 분리, F의 tidy 타깃, 그리고 `.model.3dm`을 만든 백업 경로 |

**A와 G는 서로 충돌한다.** 고정 크롬이 이미 4겹(header·tab-bar·session-toolbar·chat-header ≈170px)
+ 컴포저 ~140px이고 스크롤 영역은 `.chat-stream` 하나뿐이다. A(항상 보이는 칩 줄)와 G(하단 goal 토글)를
**각각 별도 줄로 추가하면 좁은 도킹 패널에서 대화가 사라진다.** 하나의 컨텍스트 레일로 합쳐야 한다.

---

## 3. 권고 순서

| # | 작업 | 크기 | 근거 |
|---|---|---|---|
| **W0** | 백업이 문서 정체성을 오염시키는 것 차단 (`Write3dmFile` / `UpdateDocumentPath=false` + BackupRoot 거부 가드) | S | §0-1. 데이터 안전, 유일한 명확한 릴리스 블로커 |
| **W0b** | GH 정의 백업이 항상 실패하는 것 수정 | S | §0-2. 크래시 보험 부재 |
| **W0c** | `setSchema`에 비용 게이트 + 자기 저작 와이어 되돌리기 허용 | M | D-⓵⓷. 08-10 프리즈의 직접 원인 |
| **W1** | 실패 표면 통일 — 액션별 인라인 오류 | M | ⓑ. B·I·H·E의 공통 뿌리이자 W2~W5의 **검증 수단** |
| **W2** | 대상 식별자에 문서 스코프 싣기 (`/canvas/focus`에 docId, 응답 언랩, `framed`/`skipReason` 추가) | M | ⓒ. H 수정 + A의 데이터 전제. R5(ghfocus lease)도 같이 |
| **W3** | 드래프트/첨부/고정 영속화 | M | C. key는 유지하고 draft만 App 레벨로 리프팅 |
| **W4** | 승인 흐름 — deny 채널 개통 + 만료 표시 + 승인 후 진행 | M | B. deny 미도달이 최우선 |
| **W5** | 컴포저 컨텍스트 레일 1행 (A + G 통합) | L | A·G 충돌 해소. W2·W3에 의존 |
| **W6** | 헤더에 Rhino 상태 칩 | S | E3. 순수 추가, 언제든 병렬 |
| **W7** | 문제 배너 sticky 수정 (`ReadRecentProblems`가 phase를 보게) + halt 복귀 조건 교정 | S~M | E1, D |
| **W8** | 정리 품질을 서버가 재게 (위반 카운트 보고). 스크린샷은 별도 라운드로 이연 권장 | L | F |

---

## 4. 사용자 결정 대기 (8건)

1. **`.model.3dm` 백업 오염이 Ctrl+S 대상까지 바꾸는지** 라이브 1회 확인을 지금 할 것인가?
   (결과와 무관하게 W0 수정 방향은 같지만, 릴리스 블로커 등급이 달라진다)
2. **릴리스 블로커 판정 동의?** — E2만 명확한 블로커, B는 준-블로커(제품의 유일한 차별점인 신뢰성
   계층이 첫 사용자에게 깨져 보임), 나머지 7건은 비블로커.
3. **승인 계약** — "승인 직후 자동 턴 재개"가 이 프로젝트의 거짓성공 금지 원칙과 충돌하지 않는가?
   (카드가 "무엇이 실행될지"를 이미 정확히 말했다면 자동 재개, 아니면 "보내기를 눌러 진행" 안내)
4. **드래프트 영속의 깊이** — 텍스트만? 첨부(IndexedDB)까지? 고정까지?
   고정은 오전송 방지 장치와 정합을 맞춰야 한다.
5. **오리진 파편화를 고칠 것인가** — AgentHost를 고정 포트/고정 오리진으로 바꾸면 C뿐 아니라
   I(쿠키 충돌)와 테마·탭 초기화까지 한 번에 덮인다. 다만 다중 문서 동시 사용 설계에 영향.
6. **패널 401 회복** — 자동 재부트스트랩을 시도할 것인가, 현행 안내를 크게 띄우는 데 그칠 것인가?
   (현행 안내는 `PanelType.PerDoc` 캐시 때문에 실효가 없을 수 있다)
7. **테스트 하네스** — jsdom + @testing-library 도입? 현재 8개 테스트 파일 전부 순수 로직 +
   `renderToStaticMarkup`이라 클릭·입력·리마운트를 테스트할 수단이 **0**이다.
8. **스크린샷 기반 검증(F)** — 이번 라운드에 넣을 것인가, 별도 라운드로 이연할 것인가?

---

## 4-B. 라이브 검증 결과 (2026-08-10)

증거물: `artifacts/dev-loop/probe-writefile/` (프로브 스크립트 + 원시 결과 JSON).
Rhino 프로브는 GPTino 플러그인 없이 순수 Rhino 8만 띄웠고, HTTP·스냅샷은 dev-loop 하네스,
패널 실측은 헤드리스 Chrome + CDP(420px 폭 = 도킹 패널 실사용 폭)로 했다. 사용자 파일 무접촉.

| # | 검증 항목 | 결과 |
|---|---|---|
| L1 | `FileWriteOptions.UpdateDocumentPath` 기본값 | **`false`** — 문서 개명·not-modified 마킹 **없음** (초기 가설 반증) |
| L2 | `WriteFile`이 `EndSaveDocument` 발화하는가 | **예 — `FileName = "…\.model.3dm.tmp"`.** E2의 진짜 기전 |
| L3 | `Write3dmFile`이 발화하는 `FileName` | **빈 문자열 `""`** → `IsPathFullyQualified` 가드에 걸림 = 수정 확정 |
| L4 | `GH_DocumentIO.SaveQuiet` 확장자 디스패치 | 동일 문서에 확장자만 변경: `.tmp` **`false`** / `.gh` `true` / `.ghx` `true` |
| L5 | 백업 실패 시 모달 다이얼로그 | **`SuppressAllInput=True`로도 뜬다** ("The temporary file could not be renamed") |
| L6 | `POST /canvas/focus` 응답 shape | `{"result":{"selectedCount":0,"missingCount":1,…},"fingerprint":…,"diagnostics":[]}` |
| L7 | `POST /focus` (Rhino) 응답 shape | 동일 — `hiddenCount`/`lockedCount`도 `result` 안에 갇혀 있음 |
| L8 | 없는 GUID로 focus 호출 | **HTTP 200 + `missingCount:1`.** 실패가 에러로 표면화되지 않음 |
| L9 | 스냅샷의 `GH_Group` 표현 | **`canvas.objects`에 일반 객체로 포함.** `inputs:[]`·`outputs:[]`(→ in-degree 0 → 0열 배정), `bounds 262×100`(그룹 사각형 크기), `pivot {0,0}`(그룹엔 무의미) |
| L10 | 그룹 멤버십 데이터 존재 여부 | **`canvas.groups`에 이미 있다** (`groupId` + `objectIds`). 데이터는 있고 레이아웃이 안 쓸 뿐 |
| L11 | 컴포저 높이 점프 (이슈 A) | **정확히 +30.0px** (채팅 스트림 −30.0px) |
| L12 | 폭 흔들림 (이슈 A) | 스크롤 임계점에서 스크롤바 **0→17px 뒤집힘**, 버블 폭 **372.23 → 356.25 = −15.98px**, 스트립 제거 시 원복 |
| L13 | `.chat-stream`의 `scrollbar-gutter` | **`auto`** (= 미설정). `stable`로 두면 L12가 사라진다 |
| L14 | goal/approval PUT이 턴을 시작하는가 | 핸들러 본문 전체 판독 — 둘 다 저장 후 `NoContent`, 오케스트레이터 주입 없음. **라이브 불필요** |

**L9 + L10이 F의 핵심을 바꾼다.** 그룹은 와이어가 없으므로 그래프 레이어링에서 in-degree 0 =
**소스 노드**로 분류되어 0열에 배정되고, 그 `bounds.width`가 0열의 컬럼 폭이 된다
(테스트 픽스처에서 262px, 사용자의 실제 정의에서는 ≈1900px). 그리고 `pivot`이 `{0,0}`이라
그룹을 pivot으로 이동시키는 것 자체가 무의미한 연산이다. **고칠 재료는 이미 다 있다** —
`canvas.groups`의 멤버십을 쓰면 그룹을 컨테이너로 다룰 수 있다.

**라이브로 확인하지 못한 것 (정직한 한계)**
- H의 "두 번째 GH 문서로 오배송"은 문서 2개 + 각 문서에 컴포넌트가 필요해 재현하지 않았다.
  다만 `Program.cs:364-378` 핸들러가 `{objectIds, zoom}`만 만들어 `RequireDefaultTargetState()`에
  넘기는 것은 코드로 확정이므로, 결론 자체는 흔들리지 않는다.
- I의 "두 Rhino가 인메모리 쿠키 항아리를 공유하는가"는 Rhino 2개 동시 기동이 필요해 보류했다.
- E1의 "문제 배너가 재시작을 넘어 남는가"는 recoveryRequired 잡을 인위로 만들어야 해서 보류했다.

## 4-C. 구현 내역 (2026-08-10)

전 스위트 통과: .NET 714건, 패널 58건. 신규 회귀 테스트 8건.

| 이슈 | 구현 | 파일 |
|---|---|---|
| **E2** 문서 신원 오염 | `WriteFile` → `Write3dmFile`(라이브 검증: 빈 FileName으로 발화) + `SuppressDialogBoxes` + 저장 이벤트/문서 관측 양쪽에 `BackupRoot` 거부 가드 | `GptinoDocumentBackup.cs`, `GptinoPlugIn.cs`, `GptinoRuntimeHost.cs`, **신규** `GptinoBackupPaths.cs` |
| **백업 부재** | GH 임시 파일명을 `.definition.tmp.gh`로 — `SaveQuiet`은 확장자로 디스패치 | `GptinoDocumentBackup.cs` |
| **H** zoom 오배송 | `/canvas/focus`에 `docId` 추가 → `ResolveTargetStateByDocKey`. 미등록 docKey는 400 | `ApiModels.cs`, `Program.cs`, `LiveDocumentBackend.cs`, `client.ts`, `ChatPane.tsx` |
| **H** `undefined 선택` | 클라이언트가 브리지 봉투(`{result,…}`)를 언랩. `framed`/`skipReason` 신설로 "선택은 됐는데 줌만 생략" 구분 | `client.ts`, `ICanvasAdapter.cs`, `GrasshopperCanvasFoundationAdapter.cs`, `GhFocusChip.tsx`, `useFocusTarget.ts` |
| **B** 거절 미도달 | `ComposeApprovalBlock`이 거절도 렌더. 만료된 grant는 "만료됨"으로 전달 | `SessionOrchestrator.cs` |
| **B** 승인 후 정지 | `ResumeAfterApprovalAsync` — 승인/거절이 턴으로 전달됨. 타이핑과 동일 경로 | `SessionOrchestrator.cs`, `Program.cs` |
| **B** 만료 은폐 | `GrantExpiresAt` 저장·표시, 카드 배지가 "승인 만료됨"으로 전환 | `ApiModels.cs`, `ApprovalCard.tsx` |
| **C** 드래프트 소실 | 세션별 드래프트 스토어(모듈 레벨 + localStorage 미러). `key`는 유지 — 격리 복구가 언마운트에 걸려 있음 | **신규** `draftStore.ts`, `ChatPane.tsx` |
| **A** 고정 분리 | Rhino/GH 독립 고정. 항상 렌더되는 1행 레일 → 30px 점프 제거. 압정 이모지 제거, 상태=강조색 | **신규** `SelectionRail.tsx`, `ChatPane.tsx` |
| **A** 폭 흔들림 | `.chat-stream { scrollbar-gutter: stable }` — 실측 15.98px 흔들림 제거 | `styles.css` |
| **G** 거짓 "진행 중" | 배지가 카드 라이프사이클이 아니라 **세션 실행 상태**를 읽음 | `GoalCard.tsx`, `ChatPane.tsx` |
| **G** 위치·길이 | 답변된 goal은 컴포저 위 접이식 선반으로. 한 줄 요약 + 실행 상태 | `ChatPane.tsx`, `styles.css` |
| **E3** Rhino 칩 | 헤더에 이름 붙은 Rhino `StatusChip`. GH 칩 툴팁에 "경로를 안다는 뜻"이라고 명시 | `App.tsx` |
| **E1** 배너 잔존 | `ReadRecentProblems`가 `recoveryrequired-acknowledged` phase 존중. 재개 버튼이 Blocked/Failed도 acknowledge | `LiveDocumentBackend.cs` |
| **W1** 침묵하는 실패 | 액션 키별 오류 맵 → 카드가 자기 실패를 자기 자리에 렌더. 401은 전용 타입 + 상단 배너 | `useRuntime.ts`, `client.ts`, `App.tsx`, 두 카드 |
| **D** 45s 프리즈 | `python.setSchema`에 실행과 동일한 비용 게이트 | `LiveDocumentBackend.cs` |
| **D** 와이어 교착 | 이 세션이 **직접 연결한 와이어**는 승인 없이 절단 가능 (`IsSelfAuthoredWire`) | `LiveDocumentBackend.cs` |
| **D** 크래시 창 | `ThrowIfDetached`를 `AddObject`/`RemoveObject`/슬라이더 `ExpireSolution` 뒤로 확대 | `GrasshopperCanvasFoundationAdapter.cs`, `GrasshopperDocumentLiveness.cs` |
| **F** rules.md 무시 | 프로젝트 rules.md의 auto-tidy 금지를 서버 후크가 존중 (매 턴 재조회) | `ProjectContextStore.cs`, `LiveDocumentBackend.cs`, `Program.cs` |
| **F** 그룹 오분류 | 그룹을 레이아웃 노드·tidy 시드에서 제외 (컨테이너로 취급) | `CanvasLayout.cs`, `LiveDocumentBackend.cs` |
| **F** 0열 쏠림 | 소스 노드 ALAP 당김 — 소비처 직전 컬럼으로 | `CanvasLayout.cs` |
| **F** 정렬 어긋남 | 컬럼 정렬을 중심 → **우측 엣지**(출력 소켓 선) | `CanvasLayout.cs` |
| **F** 간격 | 사용자가 세션에서 합의한 값으로: 행 30px, 열 pitch 400–750px | `CanvasLayout.cs` |
| **F** 합격 기준 부재 | **신규** 결정론적 배치 감사 — 역방향 와이어·컬럼 쏠림·긴 와이어·엣지 산포·미그룹 수 | **신규** `CanvasLayoutAudit.cs` |
| **F** 지침 모순 | "프로젝트 규칙이 house rules를 이긴다" 명시. 소켓 순서·그룹 단위 규칙 추가. `arrange_layout` 설명 정합 | `house-rules.md`, `InstructionAssembler.cs`, `DynamicToolSpecs.cs` |

**구현하지 않은 것 (의도적)**
- **F 스크린샷 검증** — 캡처 op이 저장소에 전무하고, 동적 툴 결과는 텍스트 전용이라 이미지가 턴 도중 들어갈 경로가 없다. 신규 브리지 op + 아티팩트 + `localImage` 경로가 전부 필요한 별도 라운드. 대신 서버 결정론 감사(`CanvasLayoutAudit`)로 같은 목적을 달성했다.
- **I 오리진 파편화 근본 수정** — AgentHost를 고정 포트/오리진으로 바꾸는 건 다중 문서 설계에 영향이 있어 사용자 결정 사항으로 남겼다(§4-5). 지금은 401을 명확히 표면화하는 데까지만.

## 5. 기록 갱신 필요

- `memory/gptino-p1-reliability-plan.md` — "미착수 / W1 진행 중"은 사실과 다름. P1 3파 전부 완료·푸시됨.
- `docs/release-checklist.md` — 이 9개 이슈가 **한 항목도 없다.** 최소 W0는 추가돼야 한다.
