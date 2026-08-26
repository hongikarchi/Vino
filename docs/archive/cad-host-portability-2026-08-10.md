# CAD 호스트 이식성 분석 — 2026-08-10

**작성일**: 2026-08-10 · **상태**: 조사 완료 · 구현 전 · **근거**: 1차 출처(Autodesk ObjectARX/DXF 레퍼런스, 벤더 SDK 바이너리 메타데이터 직접 파싱, GitHub 소스 직독) 기반 웹 조사 4갈래 + 코드베이스 전수 탐색 1갈래 + 핵심 주장 8건 반박검증. 라이브 실행 검증은 **아직 0건**

**판정 표기** — **확정**: 1차 출처를 직접 읽고 교차 확인 / **유력**: 근거는 있으나 마지막 고리 미증명 / **가설**: 논리상 성립하나 실측 필요

---

## 0. 세 줄 요약

1. **핸들은 GUID가 아니다.** 도면 하나 안에서만 유일한 64비트 정수이고, 객체를 복제하는 모든 연산이 새 값을 발급한다. Rhino의 영속 GUID 모델을 그대로 옮길 수 없다.
2. **패널형 플러그인은 4개 호스트 전부 가능하다.** AutoCAD는 Autodesk 공식 샘플이 팔레트 안 WebView2를 이미 시연했다. ZWCAD·GstarCAD·BricsCAD도 동일 시그니처의 PaletteSet을 갖는다 — 다만 그 세 곳에서 WebView2를 실제로 띄운 사례는 **한 건도 없다**.
3. **Vino의 신뢰성 계층은 거의 그대로 살아남는다.** src의 55~60%, 패널의 85%가 무수정 이식. 대신 Grasshopper 자산 4,293 LOC와 브리지 연산 38개 중 19개가 **삭제**된다. 이건 엔지니어링이 아니라 제품 정의 문제다.

---

## 1. DWG 객체 신원 — 핸들 = GUID? **아니다**

### 1-1. 세 가지 식별자 — **확정**

| | AutoCAD | Rhino | 성격 |
|---|---|---|---|
| 영속 신원 | `AcDbHandle` / `Handle` | `RhinoObject.Id` (진짜 GUID) | 파일에 저장됨 |
| 세션 신원 | `AcDbObjectId` / `ObjectId` | `RuntimeSerialNumber` | 재열기 시 달라짐 |
| 파일 신원 | `$FINGERPRINTGUID` / `$VERSIONGUID` | (없음) | 둘 다 read/**write** |

ObjectARX 레퍼런스 원문: *"A handle uniquely identifies an AcDbObject within a **single database**"*, 그리고 결정적으로 *"Handles are not unique across databases... duplication across databases is almost a certainty, since all databases start with the same handseed value of 1 and go up from there."*

DXF 그룹코드 5 = *"Entity handle; text string of up to 16 hexadecimal digits (fixed)"*. 예외 하나: DIMSTYLE 테이블 레코드는 5가 DIMBLK에 이미 쓰여 **105**를 쓴다. 객체 간 참조는 별도 대역(320-329 arbitrary, 330-369 soft/hard pointer·owner, 1005 xdata handle).

`ObjectId`는 *"a container for the address of a database-resident object's stub"* — 말 그대로 포인터다. Rhino의 `RuntimeSerialNumber`가 크래시 방지용으로 포인터를 감싼 것보다 **더** 위험하다.

### 1-2. 핸들 안정성 매트릭스 — **확정 (반박검증에서 정정됨)**

최초 조사는 "DB 경계를 넘을 때 재부여"라고 했으나 검증 결과 **트리거는 DB 경계가 아니라 객체 재생성(clone)** 이다.

| 연산 | 핸들 | 비고 |
|---|---|---|
| SAVE + 재열기 | **보존** | |
| SAVEAS / 하위 DWG 버전 저장 | **보존** | 메모리 DB가 그대로이므로 |
| DXFOUT → DXFIN 왕복 | **보존** | |
| XREF ATTACH | **보존** | 참조 파일은 자기 DB에 남음 |
| **COPY / ARRAY / MIRROR** | **재부여** | 같은 도면 안에서도 |
| **EXPLODE** | **재부여** | 완전히 새 엔티티 |
| **WBLOCK / INSERT / 도면 간 붙여넣기 / XREF BIND** | **재부여** | wblockClone 경유 |
| AUDIT / RECOVER | **미확인** | 중복 핸들 복구 시 변동 가능 — 실측 필요 (**가설**) |

`$HANDSEED`는 *"Next available handle"* 단조 카운터라 한 파일 수명 안에서 재사용은 없다. 단 read/**write**이므로 정리 유틸리티가 되감을 수 있다.

### 1-3. 결론 — 우리에게 무슨 뜻인가

- 외부 인덱스 키는 **반드시 (도면 신원, 핸들) 쌍**. 핸들 단독은 안 된다.
- `$FINGERPRINTGUID`는 "생성 시 설정, 도면을 고유 식별"이라고 문서화되어 있지만 **SAVEAS로 복제**되고 쓰기 가능하다 → 파일 신원으로 단독 신뢰 불가.
- Vino가 Rhino에서 쓰는 방식 — **호출자가 GUID를 정해서 넣고**(`CreateRhinoPrimitiveRequest.ObjectId`, `UpsertRhinoObjectRequest.ObjectId`가 필수 입력) 교체 시에도 ID를 보존(`RhinoSceneFoundationAdapter.cs:20`) — 은 AutoCAD에서 **불가능**하다. 핸들을 미리 정할 수 없다.
- 이미 있는 2차 신원층 `GPTino.LogicalEntityId`(`RhinoSceneFoundationAdapter.cs:24`, user string에 스탬프)가 **주 키로 승격**되어야 한다. 이게 이번 조사에서 나온 가장 실용적인 발견이다.

---

## 2. 객체가 실제로 담는 것 — **확정**

**소유권**: 단일 부모 트리. *"every object in the database must have an owner, and a given object can have only one owner"*, *"Only block table records can own entities."* 심볼 테이블은 **9개 고정**이고 추가 불가 — APPID, BLOCK_RECORD, DIMSTYLE, LAYER, LTYPE, STYLE, UCS, VIEW, VPORT.

**Entity 공통 속성** (managed 레퍼런스 실측): BlockId, Color(AcCmColor — RGB/ACI/컬러북 중 하나), ColorIndex, Ecs(OCS↔WCS 행렬), Layer/LayerId, Linetype/LinetypeScale, LineWeight, Material, PlotStyleName, Transparency, Visible, CastShadows, GeometricExtents, Hyperlinks 등. `Normal`은 베이스에 없고 구상 타입(Circle.Normal 등)에 있다.

**부착 데이터 3계층** — 이게 신원 토큰을 심을 자리다:

| 방식 | 용량 | 알림 | 복제 거동 |
|---|---|---|---|
| XData (RegApp 스코프, 1000-1071) | 객체당 16K | `ModifiedXData` 있음 | COPY/OFFSET이 **그대로 복제**, ATTSYNC가 **삭제** |
| ExtensionDictionary + Xrecord (1-369) | 무제한 | **없음** — *"No notification is sent when an xrecord is modified"* | ATTSYNC에 **생존** |
| 커스텀 ARX 클래스 | — | — | ARX 부재 시 **프록시**로 강등 |

**참조 강도**가 purge 생존과 clone 전파를 결정한다: hard(340 pointer/360 owner)는 purge를 막고, soft(330/350)는 못 막는다. Deep clone은 소유권만, wblock clone은 hard owner+hard pointer만 따라간다. 명명객체사전(NOD)은 **soft 소유**라 `wblockClone()`이 우리 커스텀 항목을 **안 가져간다** — `beginDeepCloneXlation()`에서 직접 개입해야 한다.

**파일 포맷**: DWG는 공개 명세가 없다. AC1032가 AutoCAD 2018부터 2027까지 **8개 릴리스 연속 동일** — 즉 포맷 버전은 변경 신호로 쓸 수 없다. DXF가 문서화된 교환 포맷이고, 바이너리 DXF는 *"preserve all of the accuracy in the drawing database, unlike ASCII DXF files"*.

---

## 3. "무엇이 어떻게 업데이트되었는가" — 핵심 질문

### 3-1. 이벤트 표면 — **확정**

- **Database 6종**: ObjectAppended / ObjectErased / ObjectModified / ObjectOpenedForModify / ObjectReappended / ObjectUnappended
- **Database 확장군**: BeginSave·SaveComplete, DatabaseConstructed·ToBeDestroyed, BeginDeepClone·BeginDeepCloneTranslation·DeepCloneEnded, Begin/End Insert(+**InsertMappingAvailable**), Begin/End Wblock(+**WblockMappingAvailable**), SystemVariableChanged/WillChange, Xref 13종, Dxf I/O 6종 (Autodesk 자체 도구 [ADN-DevTech/MgdDbg](https://github.com/ADN-DevTech/MgdDbg)의 `Reactors/Events/DatabaseEvents.cs`가 전부 후킹함 — 읽어볼 것)
- **DBObject 12종**: Cancelled, Copied, Erased, Goodbye, Modified, **ModifiedXData**, ModifyUndone, ObjectClosed, OpenedForModify, Reappended, **SubObjectModified**, Unappended
- **Document**: CommandWillStart/Ended/Cancelled/Failed, ImpliedSelectionChanged, LayoutSwitched 등
- **Overrule 계열** (2010+): ObjectOverrule, **TransformOverrule**, GeometryOverrule, GripOverrule, PropertiesOverrule, DrawableOverrule…

### 3-2. 이벤트가 주는 것 / 안 주는 것 — **확정, 최초 조사에서 정정됨**

최초 조사는 "모든 이벤트가 ObjectId만 준다"고 단정했는데 **틀렸다**. 반박검증 결과:

- `ObjectEventArgs`의 유일 속성은 `DBObject` — *"Accesses the object that is changed."* **열린 객체 자체**를 준다.
- `ObjectErasedEventArgs.Erased` — erase인지 unerase인지 **방향**을 준다.
- `SystemVariableChangedEventArgs.Name` — 바뀐 **변수 이름**을 준다.
- `TransformOverrule.TransformBy`는 변환 **행렬 자체**를 적용 전에 받는다.
- **`ObjectOpenedForModify`는 변경 *전* 훅이다** — *"The event is invoked before the object has been modified."* 즉 도면 전체를 상시 지문화할 필요 없이 **지연 스냅샷**이 가능하다.

여전히 참인 것: **어떤 이벤트도 이전 값이나 바뀐 속성 이름을 주지 않는다**(시스템 변수 제외). 속성 단위 diff는 우리가 계산해야 한다.

### 3-3. 권장 아키텍처 — **유력**

```
ObjectOpenedForModify  →  before 스냅샷 지연 캡처 (핸들 키)
Object{Modified,Erased,Appended}  →  dirty set에 핸들만 적재 (작업 금지)
*MappingAvailable / beginDeepCloneXlation  →  IdMapping 캡처 = 유일한 old→new 핸들 매핑 기회
Document.CommandEnded  →  dirty set 재개봉 + 지문 diff + 저널 커밋
Document.Command{Cancelled,Failed}  →  dirty set 폐기
```

Autodesk 자체 지침이 인라인 작업을 금지한다: *"do not rely on the sequence of events"*, *"modifying the object that issued the event should be avoided"*, *"Do not perform any action from an event handler that might trigger the same event."*

### 3-4. 이 구조로도 못 잡는 것 — **확정**

1. **모달 대화상자가 떠 있는 동안 이벤트가 아예 안 난다** — *"No events are fired while AutoCAD is displaying a modal dialog box."* 속성 대화상자로 한 편집은 통째로 안 보인다.
2. **Xrecord 수정은 무알림.** 폴링 지문화 외에 방법 없음.
3. `SubObjectModified`는 **어느** 서브엔티티인지 말하지 않는다.
4. Grip 편집 중 `MoveGripPointsAt()`에 오는 엔티티는 **ObjectId 없는 임시 clone**이다 (Kean Walmsley 확인). 핸들로 키를 못 잡는다.
5. 정의 ARX가 없는 프록시 객체는 참여 자체가 불투명하다.
6. `ObjectModified`는 grip 하나·정점 하나마다 발화한다. `DocumentActivated` 안에서 핸들러를 등록하면 문서 전환마다 **중복 등록**되어 배수로 발화한다.
7. 의도는 절대 안 나온다. 속성이 달라졌다는 사실뿐.

### 3-5. 뜻밖의 자산 — **확정**

Vino는 지금도 객체 변경 이벤트를 **안 쓴다**. `VinoPlugIn.cs:90-107`이 구독하는 건 `CloseDocument`, `EndSaveDocument`, `EndOpenDocument`, `SelectObjects`, `DeselectObjects`, `DeselectAllObjects` 여섯 개뿐이고, 상태는 매번 다시 읽어 지문을 다시 뜬다. 이 선택이 AutoCAD로 **거의 그대로 넘어간다** — `DocumentToBeDestroyed`, `CommandEnded`, `Editor.SelectionAdded`에 매핑되고, 위에 나열한 함정 7개 중 6개를 애초에 밟지 않는다.

---

## 4. 패널형 플러그인 — 가능한가

### 4-1. AutoCAD — **확정, 벤더 시연 있음**

- `Autodesk.AutoCAD.Windows.PaletteSet` (AcMgd.dll). `Add(string, Control)` = WinForms, `AddVisual(string, Visual)` = WPF. `DockEnabled`, `KeepFocus`, Load/Save 이벤트로 상태 영속화, `[PerDocumentClass]`로 문서별 상태.
- **Autodesk 공식 샘플이 실재한다**: [ADN-DevTech/AcadWebView](https://github.com/ADN-DevTech/AcadWebView) (2025-02-28 생성, MIT, APS Developer Advocate 작성). `net8.0-windows`, `UseWPF`, `Microsoft.Web.WebView2 1.0.3065.39`, `ps.AddVisual("Web View", webControl)`. 도킹 팔레트 안에서 HTML/JS가 돈다.
- **반박검증 정정**: 그 샘플은 **단방향**이다. `PostWebMessageAsString`으로 C#→JS만 하고, dashboard.html에 `postMessage`가 없으며 C#에 `WebMessageReceived`/`AddHostObjectToScript`/`ExecuteScript`가 **하나도 없다**. 채팅이 필요로 하는 JS→C# 복귀 경로, 문서 락, WebView2 비동기 콜백의 UI 스레드 마샬링은 **미증명**.
- AutoCAD는 **2023 릴리스부터 WebView2 런타임에 의존**한다(없으면 실행 자체가 안 됨) → 런타임 전제조건은 사실상 이미 충족.

### 4-2. ZWCAD / GstarCAD / BricsCAD — **확정 (바이너리 메타데이터 직접 파싱)**

세 벤더의 NuGet 패키지를 받아 CLI 메타데이터를 직접 디코드한 결과:

| 호스트 | 클래스 | 어셈블리 | 런타임 | 특이사항 |
|---|---|---|---|---|
| GstarCAD 2026 | `Gssoft.Gscad.Windows.PaletteSet` | GcMgd.dll | **net8.0** | `Add(string,Uri)`, `AddVisualBrowser(...)` 추가 보유 |
| ZWCAD 2026 | `ZwSoft.ZwCAD.Windows.PaletteSet` | ZwManaged.dll | **net47** | `[Wrapper("AdUiPaletteSet")]`. **Overrule API 전무** |
| BricsCAD V26/V27 | `Bricscad.Windows.PaletteSet` | BrxMgd.dll | **net8.0** | WPF는 `ElementHost` 경유 (내부에 `paletteSetElementHostExceptionFix` 존재) |

셋 다 `Add(string, System.Windows.Forms.Control)`과 `AddVisual(string, System.Windows.Media.Visual)`을 공개한다. `Microsoft.Web.WebView2.WinForms.WebView2`는 `Control` 파생, `.Wpf.WebView2`는 `Visual` 파생이므로 **타입 수용은 컴파일 타임 보장**이지 유추가 아니다.

**그러나 결정적 공백**: ZWCAD·GstarCAD·BricsCAD 팔레트 안에 WebView2/CEF를 띄운 샘플·포럼글·저장소가 **단 하나도 없다**. 미검증은 런타임 쪽이다 — CoreWebView2 환경 초기화, CAD 모달 명령 루프와의 메시지 펌프 상호작용, 키보드 포커스, 고DPI. AutoCAD 쪽에서 이미 알려진 지뢰가 있다: `AcWebBrowser`→`AcCef` 전환이 CefSharp 애드인을 깼고, WebView2가 CefSharp와 동거 시 크래시한다.

**PyRx가 강력한 방증**이다 — ObjectARX 2022-2027 / ZRX 2024-2027 / GRX 2024-2027 / BRX v24-v27을 **한 C++ 코드베이스**로 타깃하는데, `PyUiPalette.cpp`의 호스트 분기가 GstarCAD 한 군데(`#ifdef _GRXTARGET`, 미니프레임 클래스명 차이)뿐이고 **ZRX 분기는 아예 없다**. AdUi 팔레트 계층이 네 호스트에서 사실상 동일하다는 뜻이다.

### 4-3. 아닌 것

- **AutoCAD LT**: LT 2024부터 AutoLISP만. ObjectARX·.NET **불가**. 대상 아님.
- **AutoCAD Web/Mobile**: 플러그인 API 없음. 2027에서는 저장 옵션 자체가 제거됨.
- 버티컬(Civil 3D, Plant 3D, Architecture/MEP)은 API를 **추가**할 뿐 제한하지 않는다. 팔레트 플러그인은 그대로 돈다.

---

## 5. 스레딩·문서 락 — 진짜 비용

**Rhino에는 락이 없다. AutoCAD에는 있다. 그리고 우리 코드베이스에는 락 개념이 어디에도 없다.**

- AutoCAD API는 스레드 안전하지 않다. 백그라운드 스레드에서 DB를 건드리면 `eLockViolation` 또는 하드 크래시.
- 팔레트 콜백 / 이벤트 핸들러 / HTTP continuation은 **application context**다. 여기서는 `Editor` 프롬프트와 명령 호출이 무효(`eInvalidInput`).
- 다리는 둘: `Application.DocumentManager.ExecuteInCommandContextAsync(Func<object,Task>, object)` (2016+, 2015에는 `BeginExecuteInCommandContext`), 또는 `Application.Idle` + **`Application.IsQuiescent` 가드**.
- **IsQuiescent 가드를 빼면 `LockDocument()`가 조용히 실패한다.** 예외도 없고 결과도 없다. AutoCAD가 idle이어도 PLINE 프롬프트 안에서 락을 쥐고 있을 수 있다.
- 락이 **필요한** 경우 4가지(문서화됨): 모달리스 대화상자(= 우리 팔레트)에서 상호작용, 현재 문서가 아닌 로드된 문서 접근, COM 서버, `CommandFlags.Session` 명령. 반대로 **일반(비-Session) 명령 안에서는 이미 쓰기 락을 쥐고 있어 불필요** — 그래서 에이전트 변이를 진짜 명령으로 라우팅하는 게 가장 깔끔하다(덤으로 undo 마커가 자연히 생긴다).
- **LLM HTTP await를 `[CommandMethod]` 안에서 하면 그 시간 내내 명령이 활성 상태로 남는다.** CMDACTIVE가 서고 명령줄이 점유된다. 네트워크 I/O는 팔레트 컨텍스트에서, 변이만 명령 컨텍스트로.
- 알려진 함정: async 명령 안에서 모달 폼을 띄우면 SynchronizationContext가 복원되지 않아 이후 `Editor.WriteMessage`가 **조용히 사라진다**(2015/.NET 4.5 기준 — .NET 8/10에서 재검증 필요, **가설**).

권장 파이프라인:

```
WebView2(UI 스레드) → JS 메시지 → 팔레트 핸들러에서 async LLM I/O (명령 밖)
    → 직렬 작업 큐
    → ExecuteInCommandContextAsync (또는 Idle + IsQuiescent)
    → using(doc.LockDocument()) { using(var tr = db.TransactionManager.StartTransaction()) { … tr.Commit(); } }
    → UI 스레드로 복귀해 PostWebMessageAsJson
DocumentActivated 변경 시 큐 취소 · CMDACTIVE면 ^C^C 후 주입
```

**Vino 현황과의 대조**: `RhinoUiThreadDispatcher.cs:12,22`가 `RhinoApp.InvokeOnUiThread`로 마샬링하고, 파이프 수신 루프는 용량 256 바운디드 채널에 넣어 **워커 1개**가 순서대로 뺀다(`VinoRuntimeHost.cs:803-812`). 이 골격은 그대로 쓸 수 있다. 하지만 **문서 락은 이름 바꾸기가 아니라 신규 요구사항**이고, `RhinoSceneFoundationAdapter.cs`의 `BeginUndoRecord` 11군데(2137, 2226, 2358, 2478, 2602, 2680, 3450, 3698, 3745, 3889, 4029) 전부와 배치 경로에 락+트랜잭션을 새로 넣어야 한다.

---

## 6. 배포·보안·런타임 매트릭스 — **확정**

**런타임** (Autodesk 공식 "About Managed .NET Compatibility" 표):

| 릴리스 | 시리즈 | 런타임 | 바이너리 호환 |
|---|---|---|---|
| 2021–2024 | R24.0–R24.3 | .NET Framework 4.8 | 상호 호환 |
| 2025 | R25.0 | .NET 8.0 | |
| 2026 | R25.1 | .NET 8.0 → **Update 1.2부터 .NET 10** | 2025 SDK 빌드 수용 |
| 2027 | R26.0 | .NET 10.0 | **비호환** (전면 재컴파일) |

2026-07-24 Autodesk 공지: .NET 8/9가 2026-11-10 지원 종료라 2025/2026 라인을 .NET 10으로 인플레이스 업데이트 중(2026.1.2가 2026-08 첫 주). API 변경은 없다지만 **BinaryFormatter가 .NET 10에서 제거**됐다.

⇒ 2024~2027을 덮으려면 **바이너리 3종**(net48 / net8.0-windows / net10.0-windows).

**배포**:
- `.bundle` + `PackageContents.xml`. **2025부터 `SeriesMax` 사실상 필수** — 빠뜨리면 Autoloader가 4.8 DLL을 .NET 8 호스트에 로드해서 **크래시**한다(Autodesk 원문 경고).
- **AutoCAD 2026부터 `C:\ProgramData\Autodesk\ApplicationPlugins` 자동로드가 제거**됐다(권한 상승 위험 대응). 2025에서 되던 번들이 2026에서 조용히 안 뜬다. `%APPDATA%` 또는 `%PROGRAMFILES%`로. (**유력** — 포럼 리포트 + blog.autodesk.io의 "partial modifications" 언급, help.autodesk.com 공식 KB는 미확인)
- `SECURELOAD` 기본값 **1**: TRUSTEDPATHS 밖의 모듈은 경고. 서명 자체는 필수가 아니지만, 서명하면 "Always trust applications from …"이 뜬다. Authenticode 연 $200~500.
- **AutoCAD 2025+는 AssemblyLoadContext 격리가 없다.** 플러그인이 호스트보다 새 버전의 프레임워크 패키지를 끌고 오면 `FileLoadException`으로 로드 실패한다(System.Text.Json 9.0.0 사례). — **Vino가 AgentHost를 별도 프로세스로 분리한 구조가 여기서 큰 이점이다.** 무거운 의존성 그래프가 acad.exe 안에 안 들어간다.

---

## 7. 경쟁 지형 — **확정**

- **AutoCAD 2027** (2026-03-25 출시)은 **Autodesk Assistant**와 **제품 내장 로컬 MCP 서버**를 탑재했다. 그러나 Tech Preview이고 **읽기 전용**이다: 표준 파일 대조, 자연어 기반 선택, 도면 질의(레이어 목록·블록 사용·개수). *"drawing creation, editing, and PDF output are not yet provided."* Autodesk 자체 도움말이 *"its results may not be accurate"*라고 적어놨다. Windows 전용.
- **서드파티 MCP 서버를 Assistant에 등록할 수 없다.** 마켓플레이스 인증 경로는 발표됐을 뿐 미출시.
- BricsCAD **Assist** (V26.2): 문서 Q&A 전용, 도면 수정 불가. AI Predict는 명령 예측.
- ZWCAD 2026 AI: Smart Match / Similar Search / Smart Dimension — 챗 없음.
- **Gstarsoft가 2026-06-17에 자연어 AI Assistant를 발표**하며 "design generation"을 명시했다. 실체 미확인이지만 **유일한 실질 위협**.
- App Store 서드파티: CADGPT(BackToCAD)가 주요 등재작인데 ObjectARX/LISP **코드 생성** + Q&A이지, 트랜잭션 하에서 살아있는 도면을 자율 변이시키는 물건이 아니다.

⇒ **Rhino에서 잡은 차별점(변이의 신뢰성 계층)이 그대로 유효하다.** 오히려 AutoCAD가 Grasshopper보다 나은 원시 도구를 준다: 명령 단위 undo 마커, `CommandFlags.NoUndoMarker`, `DocReadLock`/`DocExclusiveLock`, `[PerDocumentClass]`.

---

## 8. Vino 이식성 실측

src ~45,300 LOC + 패널 ~11,700 라인 기준.

### 8-1. 그대로 이식 — **src 55~60%, 패널 85%**

`Vino.Core` (887, 헤더가 *"deliberately contains no Rhino integration"*), `Vino.History` (418), `Vino.Terminal` (844), `BridgeContract` 전송 절반(~1,000: NamedPipeTransport, BridgeAuthentication, FrameCodec, ProcessHub), AgentHost의 `Codex/`(~3,900) · `Data/`(~2,900) · `Security/` · `Hosting/` 대부분 · `Runtime/` 세션 기구(SessionOrchestrator 1,723, AsyncDocumentGate, EventHub), HTTP/SSE 표면 전체와 패널 인증 핸드셰이크, 패널의 세션 리스트·드래그 순서·ChatPane·draftStore·SSE 클라이언트·승인 카드 *기구*·아카이브·halt 배너, `StructuralAxisMath.cs`(명시적으로 RhinoCommon-free), `AgentHostBootstrapper.cs`(592).

**제품의 차별점이 정확히 이 층에 있다** — 단일 작성자 브로커, 지문 기반 낙관적 동시성, 검증 술어, 잡 상태 기계, 복구 정지 래치, 승인 그랜트 서버측 발급, git 히스토리, 다중 세션 순서. 이 중 어느 것도 지오메트리를 언급하지 않는다.

`InheritedHandleGuard.CloseInheritedDiskHandles()` (`Program.cs:17`)는 그대로 필요하다 — AutoCAD도 `.dwg` + `.dwl` 락으로 같은 문제를 낸다.

### 8-2. 어댑터 필요 — **10~12%**

`CanvasSceneBridgeOperationHandlers.cs`(528)·`ScriptBridgeOperationHandler.cs`(235)의 스위치 재작성, `IRhinoSceneAdapter`(607) → AutoCAD형 계약(19개 멤버 중 12개는 정직한 대응물 존재), `DocumentRuntime`/`DocumentRuntimeTargeting`의 필드명(`RhinoProcessId`, `RhinoDocumentSerial`, `GrasshopperDocumentId`…)이 와이어 포맷·SQLite 스키마·HTTP 투영·패널 TS 타입까지 새어 있어 **넓지만 얕은** 리팩터, `VinoPlugIn`/`VinoOpenPanelCommand`(~200) → `IExtensionApplication` + `[CommandMethod]` + `PaletteSet`, `VinoPanel.cs`(515) → WebView2 (vino:// 스킴 가로채기 `:370-396`는 `NavigationStarting` + `SendStringToExecute`로), `RhinoUiThreadDispatcher.cs`(60) → 컨텍스트 전환 + **락(신규)**, `VinoRuntimeHost.cs`(1,939) 중 ~1,400은 그대로 / ~500은 신원 배관 재작업.

### 8-3. 재작성 — **28~32%**

`RhinoSceneFoundationAdapter.cs` **4,122 LOC 전량**(ObjectTable, LayerTable, geometryJson, BeginUndoRecord, user string, 감사 분석기 8종, 구조 추출, purge, 명명 레이어 상태) + 락·트랜잭션 규율 신설. AgentHost 내 캔버스 도메인(`CanvasAutoPlacement` 461, `CanvasLayout` 588, `CanvasLayoutAudit` 167, `arrange_layout`, `LiveDocumentBackend.OperationValidation`의 ~1,300). 도메인 어휘 자체(`OperationKind` 32값, `ResourceKind` 19값, `PredicateKind`) — 지속 ChangeSet 계약·SQLite 잡 행·멱등 해시·모델 툴 스키마에 박혀 있어 별칭으로 못 넘긴다 → 새 열거형 = 프로토콜 버전 + 마이그레이션. `DynamicToolSpecs.cs`(791) 산문 전량, `house-rules.md`(350행), `assets/skills/` 7개. 레이어 큐레이션(material→OKLCH 아이디어는 생존, `FullPath` 기구는 사망). `tests/Vino.AgentHost.Tests`(20,036) 상당 부분.

### 8-4. 그냥 사라짐

**`src/Vino.Grasshopper/` 4,293 LOC 전체.** AutoCAD에 노드 그래프 호스트가 없다(Dynamo는 Revit/Civil 3D). 함께 사라지는 것: `canvas.*` 13개 op, `python.*` 6개 op → **브리지 연산 38개 중 19개**, solution pumping, `GrasshopperDocumentLiveness`, `GrasshopperSelectionWatcher`, `VinoDocumentBackup`, `arrange_layout`/`component_catalog`/`inspect_outputs`.

⇒ AutoCAD판 제품은 *"AI 파라메트릭 저작"*이 아니라 *"검증되고 승인 게이트가 걸린 도면 위생 + 타입드 씬 편집"*이다. **코드를 옮기기 전에 정해야 할 제품 결정이다.**

### 8-5. 이미 있는 이음매 5개 / 없는 추상화 3개

**있는 것**:
1. `IBridgeOperationHandler` (`BridgeMessages.cs:174-182`) — **이게 그 이음매다**. 위쪽은 RhinoCommon을 모르고, **RhinoCommon 타입이 이 선을 넘지 않는다**(지오메트리는 불투명 JSON 문자열, `IRhinoSceneAdapter.cs:378-390`).
2. `DocumentBoundRhinoSceneAdapter<TRhinoDocument>` / `DocumentBoundCanvasAdapter<TDocument>` — **이미 문서 타입에 제네릭**이다. `AcadSceneAdapter : DocumentBoundRhinoSceneAdapter<Document>`가 베이스 수정 없이 꽂힌다.
3. `ILiveDocumentBackend` — 널 구현 `DisconnectedDocumentBackend`가 이미 출하 중.
4. `IJobExecutor` / `ISingleWriterBroker`.
5. `BridgeAdapterOwner` 3값 + 오너별 핸들러 레지스트리.

**없는 것 — 도입해야 함**:
- (a) 호스트 중립 `DocumentIdentity` (현 `DocumentRuntime`이 Rhino 필드를 하드코딩)
- (b) `IUiDispatcher` + **`IDocumentLockScope`** (Rhino가 락을 요구하지 않아 모델링된 것이 전무)
- (c) 호스트 중립 지오메트리 페이로드 (현재는 RhinoCommon `GeometryBase` JSON + `IsValidWithLog` 사전검증 — ObjectARX에 직접 대응물 **없음**. 이게 프로토콜 수준 최대 재작성)

### 8-6. 값싼 사전 검증

`?demo=1` 목 런타임(`ui/panel/src/api/mock.ts`, 1,106 LOC)과 `DisconnectedDocumentBackend`가 이미 있으므로, **ObjectARX 호출을 한 줄도 쓰기 전에** 에이전트+프로토콜+UI 스택 전체를 AutoCAD 호스트 안에서 돌려볼 수 있다.

---

## 9. CAD → Rhino 연결

### 9-1. 네 가지 경로

**(a) `Rhino3dm` NuGet — MIT, Rhino 설치·라이선스 불필요** — **확정 (nupkg 8.32.0 직접 해체 검증)**
- TFM: net48 / netstandard2.0 / net7.0, 의존성 0개. net48 = AutoCAD 2024, net7.0 자산이 net8/net10 호스트를 커버.
- 쓸 수 있는 것: `File3dm.Write`, `File3dmLayerTable.AddLayer`, `File3dmInstanceDefinitionTable.Add`, `File3dmObjectTable.AddInstanceObject`, `ObjectAttributes.SetUserString`, 단위계·톨러런스·머티리얼·주석·해치.
- **반박검증 정정**: "계산 기하 전무"는 **과장**이다. 실제로 들어있다 — `Intersection.LineLine/PlaneCircle/ArcArc/CircleCircle/LineSphere/LineCylinder/SphereSphere/LineBox` 등 해석적 교차 15종, `Brep.CreateFromMesh`, `CreateTrimmedPlane`, `Extrusion.Create`(+`AddInnerProfile`, 캡), `NurbsSurface.CreateRuledSurface`, `SubD.Subdivide`(Catmull-Clark), `Transform.PlanarProjection`. **진짜 없는 것**: 불리언, 톨러런스 기반 curve-curve/curve-surface 교차, 모든 오프셋, 메싱, 최근접점, 커브 피팅, 질량속성.
- **배포 함정**: `Rhino3dm.dll`(1,082,368 B) ≠ `librhino3dm_native.dll`(4,262,912 B) — 문서의 "임베드된다"는 서술은 **낡았다**. 게다가 패키지의 props가 네이티브를 출력 폴더의 `Win64\` **하위 디렉터리**로 복사하는데 기본 P/Invoke 탐색 경로가 아니고, NETLOAD된 플러그인은 acad.exe 프로세스 디렉터리를 우선 탐색한다 → **명시적 resolver 필요**(`NativeLibrary.SetDllImportResolver` 또는 `AddDllDirectory`).

**(b) Rhino.Inside.AutoCAD — 실재, 유지보수 중, 그러나 알파/베타** — **확정**
- 현행 저장소는 `mcneel/rhino.inside-autocad` (Bimorph 공동). Rhino 8 + AutoCAD/Civil3D **2024~2027**, Rhino 9 + **2025~2027**.
- **그 저장소의 `Directory.Build.props` / `Directory.Packages.props`가 우리 빌드 매트릭스의 최고 템플릿이다**: net48/net8.0-windows/net10.0-windows ↔ AutoCAD.NET 24.3.0 / 25.0.1 / 26.0.0.
- README 원문: *"in early Alpha testing, and it is not recommended for use in a production environment."* 좌석마다 Rhino 라이선스 필요. 실제 사고 기록: 4.8→8.0 이동으로 라이선스 서버 접속 파손(v1.2.28, 2026-07-10에 수정), 쓰기금지 파일 하드 크래시(2026-07-30), AutoCAD 2026.0.1에서 `eInvalidInput`, AutoCAD 안에서 headless Rhino 문서 생성 시 치명적 크래시(issue #291, 수정 기록 없이 닫힘).

**(c) 살아있는 Rhino에 로컬 IPC 스트리밍** — Vino가 **이미** named pipe + loopback HTTP를 갖고 있다. 인프로세스 런타임 충돌(§6의 ALC 격리 부재)을 통째로 우회한다. 문서화된 사고가 가장 적은 경로.

**(d) 파일 교환** — Rhino는 ODA 라이브러리를 쓰고(McNeel은 ODA 창립 멤버), 2018 포맷(AC1032)까지 읽는다 = 2024~2027 전 범위 커버, 포맷 갭 없음. 손실 목록은 확정적: 다이내믹 블록 → 변형마다 별개 정적 블록 + 정의 자체는 미사용 정적 정의로, 해치 원점 불일치, 주석 텍스트 높이 미보존(RH-70555, McNeel 확인), 필드→평문, SubD→폴리서피스, 폴리페이스 메시/3DFACE→메시.

### 9-2. 신원 보존 — 핵심 설계

Rhino 도움말이 **양방향 다리**를 문서화한다: *"AutoCAD xData is imported as Attribute User Text"* / *"Attribute User Text is exported as AutoCAD xData"*. 이론상 커스텀 코드 0으로 왕복 가능.

**그러나 비대칭이 보고돼 있다** — RH-69312(Dale Fugier, 2022-07-06): AutoCAD에서 네이티브로 쓴 XData는 임포트 실패, Rhino가 쓴 XData는 왕복 성공. 수정 여부 미확인(**가설**). 그리고 Rhino `UserData`(user string이 아닌)는 DWG로 **안 나간다**.

**권고 설계** — 어느 쪽의 네이티브 id도 크로스 키로 쓰지 않는다:

```
자체 GUID (RVID) 발급
  AutoCAD측:  정본 = 엔티티 확장사전의 Xrecord   (ATTSYNC 생존, 무제한)
              미러 = 등록 앱명 XData             (ssget 필터용, 빠름)
              동반 = 기록 시점의 handle           (clone 탐지기)
  Rhino측:    ObjectAttributes.SetUserString("RVID", …)
  사이드카:   (도면신원, RVID) → 마지막 동기 지오메트리 해시 + 변환
```

`handle` 동반 저장이 핵심 트릭이다(Kean Walmsley 패턴): COPY/OFFSET은 XData를 **그대로 복제**하므로, 저장된 handle ≠ 실제 handle이면 그 객체는 사본임을 스스로 신고한다.

**이 링크를 깨는 것들**: WBLOCK/INSERT(핸들 재부여 → 재시딩 필요), COPY/OFFSET/ARRAY/MIRROR(토큰 중복 → handle 대조로 dedupe), ATTSYNC(XData 삭제, 확장사전은 생존), EXPLODE(부모 토큰 소실), 블록 정의 vs 참조(1:N), 다이내믹 블록(Rhino로 1:N), Rhino측 불리언/조인/explode(새 GUID).

### 9-3. AutoCAD 없이 DWG 읽기 — **확정**

| 라이브러리 | 라이선스 | 바이너리 DWG |
|---|---|---|
| ODA Drawings SDK | 유료 멤버십(Commercial $3,000/1년차·$2,250 갱신, Sustaining $7,500/$4,500 — **수치 상충 있음**, ODA 직접 확인 필요) | 읽기·쓰기. "Drawings.NET Classic"은 AutoCAD .NET API와 **동일한 API**라고 ODA가 명시 |
| ACadSharp | **MIT** | 읽기 AC1014~AC1032, 쓰기 AC1014~AC1032(AC1021 제외) — 유일한 신뢰할 만한 오픈소스 |
| netDxf / IxMilia.Dxf | MIT | DXF 전용 |
| LibreDWG | **GPLv3+** | 읽기 우수하나 상용 플러그인에 **사용 불가** |

---

## 10. 권고 순서

사용자가 그린 순서(로컬 CAD 플러그인 → 그 다음 CAD-Rhino 연결)는 맞다. 앞에 스파이크 두 개만 끼운다.

**S0 — 팔레트 + 양방향 WebView2 (1일).** 선택한 호스트에서 도킹 팔레트에 WebView2를 띄우고, **JS→C# 복귀 경로**(`WebMessageReceived`)를 붙이고, `LockDocument` 안에서 선 하나를 긋고 결과를 JS로 되돌린다. 텍스트 입력 포커스가 명령줄에 뺏기지 않는지 확인(`KeepFocus`, 그리고 필요하면 `CoreWebView2ControllerOptions.AllowHostInputProcessing`, WebView2 SDK 1.0.3351+). **이게 안 되면 나머지는 전부 무의미하다.** GstarCAD는 SDK가 익명 즉시 다운로드라 가장 빠르게 답을 준다.

**S1 — 변경 저널 실측 (1~2일).** `ObjectOpenedForModify`에서 before, `CommandEnded`에서 diff, `*MappingAvailable`에서 IdMapping. 실제 도면으로 event storm·undo/redo·모달 대화상자 구멍·grip 편집을 실측해서 §3-4 목록이 이론이 아니라 수치가 되게 한다. Autodesk의 MgdDbg를 먼저 로드해 어떤 이벤트가 실제로 나는지 눈으로 본다.

**W1 — 호스트 중립 추상화 3종을 *Rhino 쪽에서 먼저* 도입.** `DocumentIdentity`, `IUiDispatcher`+`IDocumentLockScope`(Rhino 구현은 no-op 락), 지오메트리 페이로드. 이렇게 하면 현행 Rhino 제품이 회귀 없이 게이트를 통과하며 검증되고, AutoCAD는 첫 번째가 아니라 **두 번째 인스턴스**가 된다. `DocumentBound*Adapter<TDocument>` 제네릭이 이미 있으니 패턴은 증명돼 있다.

**W2 — AcadSceneAdapter 최소 세트.** list / inspect / listLayers / updateLayer / moveObjectsToLayer / upsert / delete / transform / audit. 사용자가 말한 세 가지 시나리오가 정확히 여기서 나온다 — 도면 검사(audit), 레이어 껐다 켜기(layerState), 자연어 편집(upsert/transform).

**W3 — Rhino 내보내기.** 먼저 `Rhino3dm`로 `.3dm` 직접 쓰기(라이선스 0, 프로세스 격리 위험 0). 커널 연산이 필요해지는 순간에만 (c) 라이브 IPC로 살아있는 Rhino에 붙인다. Rhino.Inside는 알파 딱지가 떨어지기 전까지 보류.

---

## 11. 사용자 결정 대기 (3건)

**D1. 첫 번째 비-Rhino 호스트.**
- **AutoCAD** — 시장·문서·Autodesk 공식 WebView2 샘플·MgdDbg 레퍼런스가 전부 있다. 대가: 코드 서명, 번들 규약, 연간 ABI 파손(2024/2025-2026/2027 = 바이너리 3종), 2027 Assistant가 읽기 전용이지만 존재.
- **GstarCAD** — SDK가 유일하게 **익명 즉시 다운로드**, 한국 사용자 ~20만(국내 대체 CAD 1위), .NET이 AutoCAD와 가장 가깝다. 대가: Gstarsoft가 2026-06 자연어 AI Assistant 발표(경쟁 위험), 네임스페이스 매핑이 단순 치환이 **아님**(UI/리본은 `Gssoft.Windows`, COM은 `GcadVbaLib`, 타입명 `Acad*`→`Gcad*`).
- **BricsCAD** — 문서 품질 최고, **유일하게 AutoCAD와 원소스 듀얼타깃을 공식 문서화**(`#if BRX_APP` + `_AcDb` 별칭). 대가: 한국 존재감 미미, V26에서 .NET 8 강제 전환(4.8과 혼용 불가), BRX는 등록+Pro 이상 라이선스.

**D2. AutoCAD판 제품 정의.** Grasshopper 층(브리지 op의 절반, 스킬 7종, house-rules 350행)이 통째로 사라진다. "검증된 도면 위생 + 타입드 씬 편집"으로 좁히는 것을 받아들일지, 아니면 CAD 쪽 파라메트릭 대체물(블록/다이내믹 블록/제약조건)을 새 도메인으로 세울지.

**D3. CAD↔Rhino 연결 방식.** 파일 기반(`Rhino3dm`로 `.3dm` 직접 작성) / 라이브 IPC(기존 파이프 재사용) / Rhino.Inside. 권고는 파일 기반으로 시작해 필요시 IPC.

---

## 12. 미검증 · 후속 확인 목록

- **[결정적]** ZWCAD/GstarCAD/BricsCAD 팔레트에서 WebView2 런타임 동작 — 사례 0건. S0에서 해소.
- AutoCAD 2026의 ProgramData 자동로드 제거가 공식 KB에 있는지, 레지스트리 우회가 무엇인지.
- `PaletteSet.AddVisual`에 키보드 interop을 켜는 3인자 오버로드가 실재하는지(2009년 블로그 댓글이 유일 출처). ILSpy로 30분.
- `PaletteSet.AddVisualBrowser` / `switchVisualBrowser`의 정체 — 2026 레퍼런스 목록에는 있으나 상세 페이지 404. AutoCAD 자체 브라우저 호스트를 노출한다면 WebView2 의존을 없앨 수도 있다.
- AUDIT/RECOVER가 핸들을 보존하는지 — 벤더 진술 없음. 손상 파일 복구가 외부 인덱스를 통째로 고아로 만들 수 있다.
- RH-69312(AutoCAD 네이티브 XData 임포트 실패)와 RH-70555(주석 텍스트 높이) 수정 여부. YouTrack 접근 실패.
- SAVEAS가 `FingerprintGuid`/`VersionGuid`에 정확히 무엇을 하는지 — 포럼 1건뿐, 라이브 테스트 필요.
- 다분 단위 LLM HTTP await가 .NET 8/10 AutoCAD 메시지 펌프와 어떻게 상호작용하는지(SynchronizationContext 오염 증거는 2015/.NET 4.5 기준).
- ODA 멤버십 실가격 — 페이지마다 상충($3,000/$2,250 vs $6,000/$3,600). 직접 문의 필요.
- Gstarsoft AI Assistant의 실제 출하 여부와 지오메트리 편집 가능 여부.

---

## 13. 기록 갱신 필요

- `vino-competitive-landscape.md` — AutoCAD 2027 Assistant(읽기 전용 + 로컬 MCP, 서드파티 등록 불가) 항목 추가
- 신규: CAD 호스트 이식성 요약 메모 (본 문서 포인터 + 결정 3건)
