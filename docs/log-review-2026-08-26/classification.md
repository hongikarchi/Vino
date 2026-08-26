<title>로그 전수 검토 — 문제 분류 (2026-08-26)</title>

# 로그 전수 검토 — 문제 분류 (Stage 1 / W3)

입력: W2 검증 클러스터(계약·fingerprint 22건, refuter 3표), `.log-mine/corpus.md`,
`.log-mine/stats/headline.json`, `docs/log-review-2026-08-26/known-findings.md`(K001~K181).
전체 클러스터 JSON = `.log-mine/stats/clusters.json`.

**증거 참조 규약**: 표에서는 id를 앞 8자로 줄였다(`job:5d88e8b8@999ACAEE…`). 전체 id·전체 판정문은
clusters.json에 있다. 형식: `job:<id>@<project>` / `msg:<project>/<session>#<seq>` /
`call:<thread>#<call>` / `host:<project>@<at>` / `problem:<project>@<at>` / `stats:<file>#<table>`.

**검증 등급**: `V3` = W2 적대검증 3표 통과(계약·fingerprint 22건). `W1` = W1 원본 근거로 W3가
재군집만 한 것(적대검증 미수신 — 나머지 7개 범주). W1 등급은 건수·기전이 아직 1인 관측이므로
Stage 2에서 먼저 재확인할 것.

---

## 1. 요약

- **코퍼스**: 잡 2,656(비커밋 517 = 19.5%) · problem-log 16,462 · 메시지 1,376 / 세션 82 ·
  툴 호출 13,574 · host.log 334 · 롤아웃 264. 6주(07-21~08-26), 잡을 실제로 돌린 프로젝트는 **18개뿐**.
- **한 프로젝트(457FDB8091063B0D)가 잡의 64.4%**, 상위 8세션이 64.2%. 코퍼스 전체 비율 ≈ 그 프로젝트의 비율이다.
  또한 **brand ≡ version**(GPTino=pre-alpha7 2,203 / Vino=alpha.7 453)이라 "버전 효과"는 리네임 경계·작업 내용 변화와 분리 불가.
- **문제 가족 5개가 전체를 지배한다**: ① 검증 오경보(빈 출력 초록 커밋 869 = 커밋의 41%, OutputCountInRange
  172실패 = alpha.7 실패의 89%) ② 계약 사다리(predicate 84 + payload 61 = 제출 전 거부 278건 중 145) ③ 읽기 절단
  (40K 캡 274, claude 25K spill 3) ④ 브릿지 데드라인(30s 고아 160, 45s RR 19) ⑤ 왕복 세금(카드 20%, 폴링 55%, sub-agent 2,828콜).
- **alpha.7에서 실패 구성이 뒤집혔다**: 비커밋률 17.4% → **29.6%**인데, blocked(지문)는 204→18로 급감했고
  failed(수용 predicate)가 72→100으로 급증했다. **fingerprint 계열 수정은 먹혔고, 검증층이 새 병목이 됐다.**
- **alpha.7 잔존 비율(클러스터 기준)**: 55개 중 **41개가 presentInAlpha7=true**. 확정 종결(fixed-noise)은 6개뿐이다.
- **최신일(08-26) 로그에도 살아 있는 것**: 계약 사다리(CT01/CT02), 빈출력 초록커밋(VF01), predicate 오경보(VF02),
  claude 읽기 캡(RD02/RD03), 시각 검수 viewport 하드코딩(VF06), auto-fill 거절(FP01 잔여).
- **이번 리뷰 계기(claude 50K 소스 읽기 실패)는 단발이 아니라 RD01/RD02/RD03의 교집합**이다 — 서버는 256KiB를
  허용하는데 전송층은 40K(codex) / 25K토큰(claude)이고 `script:` 스코프에는 페이징이 없다.
- **W2 판정 반영**: 22개 V3 클러스터 중 반증표가 붙은 5건(CT04·CT10·FP01·FP05·FP07)은 기전·상태 라벨을
  정정해 실었고, 2건(CT12·CT15)은 fixed-noise로 §5에 격리했다.

---

## 2. 분류 체계 (9범주)

| # | 범주 | 정의(1줄) | 클러스터 | 증거 규모 | 등급 |
|---|---|---|---|---|---|
| 1 | 계약·스키마 마찰 | 페이로드/선언/predicate 계약이 검증기에만 있어 제출 왕복으로 발견 | 15 (survivor 13 + fixed 2) | 툴콜 ~1,400 / 잡 ~120 | V3 |
| 2 | fingerprint·동시성 | 지문 CAS·auto-fill·다중 writer가 자기 쓰기를 되받아치는 문제 | 7 (survivor 6 + fixed 1) | 잡 232 / 페어 1,951 | V3 |
| 3 | 세션 생명주기 | 턴·세션이 죽거나 멈추거나 응답을 잃는 문제 | 10 | 잡 40 / 메시지 ~300 | W1 |
| 4 | 읽기 경로·용량 | 읽기 결과가 전송 한계에서 잘리거나 통째로 유실 | 9 | 툴콜 ~1,100 | W1 |
| 5 | 검증 오경보·검증 갭 | 커밋 판정이 거짓 초록이거나 오탐으로 잡을 죽임 | 9 | 잡 1,041 / predicate 5,884 | W1 |
| 6 | 호스트·브릿지 안정성 | 데드라인·예외·핸들·프레임 등 호스트/브릿지 층 사고 | 9 | 잡 62 / host 334 | W1 |
| 7 | 백엔드 특이 (codex/claude) | 한 백엔드에서만 나는 문제 | 6 | 툴콜 2,900 | W1 |
| 8 | UX·의도 불일치 | 기계 검증은 통과했는데 사용자 의도와 어긋남 | 10 | 사용자 메시지 659 중 240 | W1 |
| 9 | 기타(비용·관측·하네스) | 위 어디에도 안 붙는 총계·계측 항목 | 3 | 잡 200 / 툴콜 1,530 | W1 |
| | **계** | | **78** (survivor 72 / fixed-noise 6) | | |

카테고리 간 중복은 **교차참조만** 하고 건수는 한 범주에서만 센다(예: 8MiB 프레임 = CT04와 HB05가 같은 2잡).

---

## 3. 문제 목록

### 3.1 계약·스키마 마찰 (V3)

| ID | 시그니처 | 기전(1줄) | 건수 | 버전 | a7 | 심각 | 상태 | K행 | 증거 3 | 판정 |
|---|---|---|---|---|---|---|---|---|---|---|
| CT01 | 한 번 제출 = 결함 1개만 알려주는 payload/선언 계약 사다리 | OperationValidation이 첫 위반에서 throw하고, kind별 필수인자 집합이 검증기에만 존재(inputSchema엔 top-level 4개뿐) | 59~61 | 양쪽 | ✔ | P1 | open-known | K022(열림) | call:b1f24f98#toolu_01ERzm…(08-26) · call:019fa17e#call_kN4qa…(07-27) · call:01a03146#call_otv3H…(08-24) | 3/3 confirmed. **정정**: F089의 "codex 0.57%"는 오산 — codex 거부는 `is_error=false`라 툴오류로 안 잡힌다. 실제 codex 267/2,101(엄격)~447(느슨), claude 11/34 |
| CT02 | acceptance predicate 3단 거부 사다리 | 이름공백→미선언 자원→per-kind expectedValue 문법 순으로 fail-fast | 84 (a7 53) | 양쪽 | ✔ | P1 | partially-fixed | K010(부분), K069/K071 | call:019fb04d#call_xFMeQ…(07-30) · call:01a031f6#call_yZFmi…(08-24) · call:b1f24f98#toolu_01TvW…(08-26) | 3/3 confirmed. **정정**: 스키마 일부는 이미 공개됨(name minLength·kind 12종 enum). 진짜 미공개는 ①per-kind expectedValue 문법 ②resource=선언된 write 규칙. 최대 항목(이름공백 30)은 코드모드 수기 JSON 아티팩트 |
| CT03 | 열거되지 않은 owner 어휘(Wireify/Cordyceps) 거부 | kind→owner 표가 페이로드 가이드에 없어 모델이 추측 | 55 | pre only | ✘ | P3 | **종결(역사적)** — 08-07 v13 리네임 | 없음(K행 신설 권고) | call:019f927e#call_FGEY3…(07-24) · call:019fb1ec#call_UAtpQ…(08-06) · call:019fa14c#call_xzFIr…(07-27) | 3/3 confirmed. **정정**: (a) status 'new' → 종결+회귀마커, (b) `Unknown AdapterOwner`는 08-11까지 단일 세션(019fb138)에서 지속, (c) msg#29/#31(거짓 원인)은 projectId 재사용 가족으로 이관 |
| CT04 | 프리플라이트가 못 잡는 위반이 dispatch에서 폭발 → Unknown outcome → RR | `pivot:"gptino:auto"` 분기가 `DeserializeArguments`를 건너뜀(OperationValidation.cs:502) + 8MiB 프레임 캡 미프리플라이트 | 3 (1+2) | a7 only | ✔ | P1 | **new — K002/K003 인접 커버리지 갭** | K002/K003(수정, 범위 밖) | job:5d88e8b8@999ACAEE(08-26) · job:1af1d173@BD95C956(08-21) · job:3d254b81@BD95C956(08-21) | 2/3 confirmed, **1 refute**: 재발성 미달 — 두 개의 독립 단발 사건(각 1세션)을 합쳐 count 3. 'regressed' 라벨 철회, 프레임 캡은 K행 자체가 없음 |
| CT05 | 스크립트 IO 스키마 append-only — 축소 시 폐기·재생성 | setSchema가 append-only를 강제하고, 유일한 축소 경로 replaceComponentIo는 컴포넌트 원자 교체(22회/397) | 17~18 (a7 4) | 양쪽 | ✔ | P1 | open-known | K013(열림), K012(수정) | job:6bfc257a@999ACAEE(08-26) · job:f486a6b2@C5C77493(08-24) · msg:999ACAEE/106ab0e9#3 | 3/3 confirmed. **정정**: ①"축소 경로 없음"→"불변식 미고지 + 유일 경로가 원자 교체" ②count 18(81e0d043 추가) ③58.8% 비커밋률은 n=17 소표본. 신규 증거: 08-26에도 live outputs가 `['out','a']` — K012 수정 후에도 콘솔 소켓이 스키마에 남아 축소를 불법화 |
| CT06 | exec 샌드박스에 crypto/TextEncoder/atob 없는데 계약은 클라이언트 민팅 강제 | changeSetId·idempotencyKey·sha256을 클라가 만들어야 하는데 런타임에 API가 없음 | 40 (a7 10) | 양쪽 | ✔ | P2 | new | 없음 | call:019fb138#call_qjK7o…(08-05) · call:01a0223b#call_haoZ6…(08-21) · call:01a0311c#call_g1b3k…(08-24) | 3/3 confirmed. **정정**: 헤드라인을 "Vino 샌드박스에 crypto 없음"이 아니라 "서버측 민팅 폴백 부재"로. codex 코드모드 전용, claude 경로엔 없음 |
| CT07 | 동적툴 성공 봉투가 툴마다 string/object/{result} 3형태 | ReadBridgeQueryAsync:811-816은 {result,…}, Ok(bare)와 Ok(string)이 공존, 툴별 봉투 미선언 | ≈1,000~1,150 exec(≈12%) + 12 사망 | 양쪽 | ✔ | P2 | new | 없음(K129는 패널측만 수정) | call:01a0223b#call_zqYTU…(08-21) · call:01a0223b#call_tooc3…(08-21) · call:019fb049#call_Ihl70…(07-29) | 3/3 confirmed(신뢰도 medium×2). **정정**: 정확 수치 금지 — 정규식 의존(1,157/1,046/1,037/763). 봉투 생성 위치는 dispatcher가 아니라 ReadBridgeQueryAsync |
| CT08 | 툴 실패가 산문 문장으로 JSON 채널에 들어와 exec 전체 사망 | `Fail(string)`이 그대로 반환(CodexAppServerClient.cs:1635), 코드모드는 JSON.parse | 14~15 (a7 3) | 양쪽 | ✔ | P2 | new | 없음(K060 비해당) | call:019feab6#call_bQEjI…(08-10) · call:01a03159#call_9s6sG…(08-24) · call:01a0311c#call_Jhi9z…(08-24) | 3/3 confirmed. **정정**: ①count 15(엄격 14) ②a7 근거는 1일·2세션으로 얇음 ③`Steer()`는 산문이 의도된 채널이라 Fail만 직렬화하면 full-auto 넛지가 회귀 |
| CT09 | artifact_read가 없는 경로에 리터럴 `undefined` | (정정) 실제로는 **결과가 JSON 문자열로 오는데 `r.content`를 읽어 undefined를 parse** | 19 (a7 2) | 양쪽 | ✔ | P2 | new | 없음 | call:019f927e#call_K7KK9…(07-24) · call:019fa14c#call_PRzqU…(07-27) · call:01a03196#call_G9l1M…(08-24) | 2/3 confirmed, **기전 정정 1**: 없는 경로는 `Draft artifact was not found.`(26건, 이 19건과 교집합 0)로 정상 동작. 수정은 `{exists:false}`가 아니라 **반환 인코딩 명시**. "최다 오류 read 툴"은 절대 건수(32) 기준 |
| CT10 | 소켓 id를 알아내려 일부러 실패하는 쓰기(write-as-probe) | (정정) 읽기 경로는 이미 존재 — snapshot_read `components:<guid>`가 파라미터 id를 준다 | 17 (a7 1) | 양쪽 | ✔ | P2 | partially-fixed | K019/K018(수정) | job:b0571769@457FDB80(08-06) · job:a702eb59@4CEEB9B6(07-23) · job:2d6d2e7f@C5C77493(08-24) | 2/3 confirmed, **1 refute**: "read 경로 부재" 전제 반증(같은 세션이 15분 전 동일 소켓 id를 이미 읽었고, 프로브 구간엔 snapshot_read 0회). 남는 것 = **하우스룰(write-as-probe 금지)** + id 추측 17잡. 프로브 자체는 단일 세션·alpha.7 0 |
| CT11 | 코드모드 ~10s 셀 예산 < GH solve → 매 무거운 작업이 왕복 1회 추가 | 셀은 ~11s에 서스펜드(485/534), SubmitWaitCap 15s·툴 데드라인 30s가 셀 예산보다 큼 | 534 서스펜션 + 215 job_status | 양쪽 | ✔ | P2 | new | 없음(K088은 브릿지측) | call:019feab6#call_Uaa6j…(08-10) · call:01a0223b#call_KTjD2…(08-21) · call:019fa14c#call_Y55Nl…(07-27) | 3/3 confirmed. **정정**: ①"11.0s 고정"은 91%, 꼬리 21/31/61/71s ②`wait:false`+jobId·job_status는 이미 존재 → 진짜 갭은 **long-poll 부재 + 대기 상한 오calibration** ③"모든 느린 작업"은 과장(셀 단위) |
| CT13 | 코드모드 JSON 이중 이스케이프의 리터럴 `\n`이 컴파일러까지 도달 | 소스를 JS 문자열로 만들어 stringify → RhinoCode가 그대로 컴파일, 서버측 pre-compile lint 없음 | 7 (a7 1) | 양쪽 | ✔ | P2 | new | 없음 | job:3042099f@52AFD2C0(07-27) · job:3206ad38@52AFD2C0(07-27, 동일 [140:121]) · job:0e51dae4@BD95C956(08-21) | 3/3 confirmed. **정정**: ①"halt를 유발"은 코퍼스로 확인 불가(halt 이벤트 종류 자체가 없음) — 13분 공백으로만 추정 ②"생성 소스를 볼 수 없다"는 오류(snapshot_read `script:`로 읽힘). 진짜 갭 = **강제 read-back/lint 부재** |
| CT14 | 제출해야만 알 수 있는 C#/Python 컴파일 오류 | pre-submit 컴파일 게이트 없음; 실패는 잡 왕복 1회 | 71(느슨) / 50(엄격), a7 7 | 양쪽 | ✔ | P2 | **부분(K159 능력만 출하)** | K159(수정, 미사용) | job:8ad9e2d3@457FDB80(07-30) · job:15dc7424@457FDB80(08-06) · job:35635480@C5C77493(08-24) | 3/3 confirmed. **정정**: ①'regressed' 철회 — K159 회귀신호(`os error 2`)는 0건이고 alpha.7 codex의 shell_command 자체가 **0회**(scratch 미사용) ②API-surface 절반은 08-22 쿡북(4bee9c0)으로 커버, alpha.7 잔여는 일반 C# 오류·저자 assert. 프레임을 "pre-submit 컴파일 게이트 부재"(검증 갭)로 |

fixed-noise: **CT12**(C# 예약어 `out`, 9잡, K012 수정 08-20), **CT15**(첫 실행 비용 게이트 단위 오인, 10잡, K073 수정 08-20) → §5.

### 3.2 fingerprint·동시성 (V3)

| ID | 시그니처 | 기전(1줄) | 건수 | 버전 | a7 | 심각 | 상태 | K행 | 증거 3 | 판정 |
|---|---|---|---|---|---|---|---|---|---|---|
| FP01 | `gptino:auto declined … has not written it` | 세션이 안 쓴 자원엔 채울 baseline이 없음 → 첫 접촉 거절, 재제출은 즉시 커밋(92/117) | 117 (a7 3) | 양쪽 | (△) | P2 | **대부분 수정 + 의도적 잔여** | K023/K027(수정 08-20), K028 | job:3a2d5a5f@999ACAEE(08-26) · job:71e77ec7@457FDB80(07-30) · job:3cbfbade@52AFD2C0(07-27) | 2/3 confirmed, **1 refute**: 기전("커밋이 원장을 안 쓴다")은 코드로 반증(UpdateResourceLedgerAsync가 커밋·검증실패 모두에서 기록, self-write fill은 이벤트를 안 남김). alpha.7 3건은 **외래 문서 컴포넌트에 대한 의도적 거부**(코드에 live-gate canary 주석). 5.2%→0.66%. P1→P2, 남는 건 **문구 개선(P3)** |
| FP02 | `it drifted (a manual Grasshopper edit)` — 자기가 만든 컴포넌트에 | ledger 지문이 커밋 순간 값인데 컴포넌트가 이후에도 변함(솔브·소켓 구체화) | 32 (a7 2) | 양쪽 | ✔ | P2(문구는 P1급) | regressed(K024 08-20 이후 재현) | K024(수정) | job:92b0d713→537bc1b5@C5C77493(08-24, 55s) · job:2afebcfa@C5C77493 · job:4d6a61d3@457FDB80(08-11) | 3/3 confirmed. **정정**: ①기전에서 RuntimeMessages는 08-20에 제거됨 → 잔여 원인 **미상**(솔브 후 소켓 구체화 유력) ②C5C77493은 그날 6세션 동시 → K026(설계) 배제 불가 ③산문 인용 `823add33 msg#372`는 미해결 → 삭제 |
| FP03 | `The fingerprint of <resource> changed after the base snapshot` | 자기 실행/쓰기가 옮긴 지문에 엄격 CAS, benign self-writer 리베이스 미적용 | 58 (a7 9) | 양쪽 | ✔ | P2 | **커버리지 갭(신규) + 설계 혼재** | K025(수정), K026/K030(설계) | job:27986682@457FDB80(08-05, 6연속) · job:87d575fe@C5C77493(08-24) · job:82c1a9c0@C5C77493(08-24, RhinoLayer) | 3/3 confirmed. **정정**: ①비율로는 개선 없음(2.2%→2.0%) — 'regressed'는 절대건수 착시 ②a7 9건 구성 = group 3(K030 설계) + **RhinoLayer/LayerTable 2(리베이스 미커버, 진짜 신규 갭)** + component 4(다중세션 K026/K036) ③58건 전체를 결함으로 보고 금지 |
| FP05 | 한 GH 문서 위 두 세션이 서로를 무효화, 상대 에이전트 쓰기를 '사람 편집'으로 보고 | 세션별 ledger vs 프로세스 단위 문서, 크로스세션 예약·프로버넌스 없음 | 1,951 페어 (4 docs) | pre only | ✘(미관측) | P2 | open-known | K036/K026(설계) | job:57b2207c@52AFD2C0(07-27) · job:34c0c637@52AFD2C0 · job:b0437b0f@52AFD2C0 | 3/3 confirmed. **정정**: ①4 docs(5 아님) ②`stats:session-timelines#overlapping-spans` 참조는 실재하지 않음 ③현재 코드엔 foreign-session 분기가 있어 "사람 편집으로 보고" 주장은 alpha.7에 대해 반증 불가 ④K036 회귀 트리거(왕복세금>20%) 미충족(11.9% blocked) ⑤P1→P2 |
| FP06 | predicate 실패가 ledger를 stale로 남겨 다음 잡이 CAS 차단 | 검증실패 분기의 ledger 갱신이 실제로 움직인 지문을 못 따라감 | **5**(3→5) | 양쪽(정정) | ✔ | P2 | new | K020(열림, 인접) | job:552116cb@C5C77493(08-24) · job:87d575fe@C5C77493 · job:736832f0@C5C77493 | 3/3 confirmed. **정정**: ①count 3→5(pre-alpha7 c8420ed5·28ed9787 추가) → "alpha.7 전용" 문장 삭제 ②기전 중 "writeSet 한정" 가설은 반증(layer1이 전체 diff 기록) → 남는 가설은 `after` 캡처 후 지문이 계속 이동 |
| FP07 | 그룹 재편성 배치가 자기 baseline을 무효화 | (정정) CAS는 실행 **전** 평가(16~25ms) → 배치 자기무효화는 반증. group 멤버십 해시 드리프트 = K030 설계 | 3 | a7 only | ✔ | P3 | **K030 빈도 갱신** | K030(부분·의도적 유지) | job:2f20287b@BD95C956(08-21) · job:a762481f@BD95C956(+14s 커밋) · job:52a1a38e@C5C77493(08-23) | 2/3 confirmed, **1 refute**: 기전 반증(단일 setGroup 잡도 포함, 마지막 그룹 쓰기가 17분 전). **정정**: 메시지는 드리프트된 그룹을 전부 나열함("하나만 지목" 아님), op 구성은 move1+setGroup14 |

fixed-noise: **FP04**(stale 메시지가 자원명을 숨겨 동일 해시 재제출 무한, 6잡, K029 수정 08-20) → §5.

### 3.3 세션 생명주기 (W1)

| ID | 시그니처 | 기전(1줄) | 건수 | 버전 | a7 | 심각 | 상태 | K행 | 증거 3 |
|---|---|---|---|---|---|---|---|---|---|
| SL01 | 카드로 끝난 턴이 응답 없이 종료(무음 114 + 오류 19) | 최종 assistant 메시지만 답으로 취함 → 카드 툴이 턴을 끝내면 빈 final_answer | 133 | 양쪽 | ✔ | P1 | open-known(K038b 열림) | K037/K038(수정 08-11), K038b | call:019fee3b#call_PIwKN…(08-11) · msg:457FDB80/823add33#40 · call:01a031f6#call_hsfNp…(08-24) |
| SL02 | recoveryRequired가 세션을 ~12분(중앙값 722s) 정지, 40건 중 8건은 세션 사망 | 결과 불명 상태의 halt 래치를 사람만 풀 수 있음, 자동 재조회·재조정 없음 | 40 | 양쪽 | ✔ | P1 | open-known | K047(열림), K048(부분), K079(부분) | job:5d88e8b8@999ACAEE(08-26) · job:06221588@C5C77493(08-24) · job:3fed720a@457FDB80(07-30, 6.6일 정지) |
| SL03 | full-auto 파킹 → 연속턴 넛지 루프 → 컴팩션과 경합해 턴 실패 | 넛지가 파킹 조건을 스스로 재생성, Compact 턴 중 submit → `ActiveTurnNotSteerable` | 16 + 2 | a7 only | ✔ | P0 | new | K051(수정, 별건) | msg:C5C77493/a641379b#25(08-24) · host:C5C77493@07:05:02 · msg:C5C77493/55362d07#18 |
| SL04 | 인터럽트 턴이 사용자 요청을 삼키고 payload 아티팩트를 고아로 남김 | 인터럽트 시 미충족 요청 기록·예약 디렉터리 정리 없음(고아 56 dir) | 104 | 양쪽 | ✔ | P2 | new | 없음 | msg:52AFD2C0/eb086086#123(07-27) · call:019fa17e#call_fgOkJ… · call:019fa17e#call_lVwA4… |
| SL05 | 컴팩션 재오리엔테이션 세금(2~5턴마다, 직후 오류율 3.3% vs 1.9%) | 턴마다 주입되는 컨텍스트+대형 툴 결과가 창을 채우고, 요약이 에이전트 로컬 상태를 버림 | 131 (코퍼스 1,088) | 양쪽 | ✔ | P2 | partially-fixed | K051(수정), K063(열림) | msg:BD95C956/3c2796cb#6(08-21) · msg:C5C77493/ef588a48#27 · call:019feab6#call_cCRYl…(재탐색) |
| SL06 | AgentHost 재시작이 턴을 죽임(사용자: "꺼졌다/날라감") | 부모 프로세스 사망 → 턴 체크포인트 없이 소실, 재개 경로 없음 | 15 | pre only | ✘ | P1 | open-known(미관측) | K050(열림) | msg:457FDB80/ee055be0#53(07-30) · msg:457FDB80/9d85aea9#2 · msg:7D54F966/a76e4668#6 |
| SL07 | 한 번의 종단 오류가 세션 전체를 `failed`로 고정 | 어떤 terminal 턴 오류든 session.state=failed, resume 경로 없음 → 삭제·재생성뿐 | 6 | 양쪽 | ✔ | P1 | new | 없음 | msg:C5C77493/a641379b#51(08-24) · msg:BD95C956/08ec29b8#2 · msg:457FDB80/0d1d51d4#22 |
| SL08 | 2연속 실패 halt가 "원인·수정 위치를 아는" 상태에서도 사람에게 넘김 | halt 래치가 '문서 불명'과 '내 봉투가 틀림'을 구분 안 함 | 12 + 3 | 양쪽 | ✔ | P2 | open-known | K047(열림) | msg:52AFD2C0/170b2a0b#26(07-27, 23분) · msg:457FDB80/823add33#47 · msg:457FDB80/1267de70#48 |
| SL09 | 신원 재앵커/브랜드 이전이 payload만 복사하고 job 원장은 두고 감 | Save As·데이터 루트 이전이 artifacts만 이동 → self-authored 이력 0에서 시작, projectId 회전 시 `ChangeSet belongs to another project` | 56 + 6 | 양쪽 | (△) | P1 | open-known | K031(열림) | host:5486330A@08-18 · call:019fa14c#call_nIE3t…(07-27) · msg:52AFD2C0/87bc632e#4 |
| SL10 | 검증된 교체 컴포넌트가 LIVE-delete 정리 중 소실, 복구가 깨진 상태로 착지 | 삭제/정리 경로가 비트랜잭션 + 전방 기록 없음, orphan 가드가 swap-then-delete를 다수 잡으로 강제 | 3 | a7 | ✔ | P0 | new | K091(수정, 인접) | job:5bae5c21@457FDB80(08-11) · job:51ab23c5@457FDB80 · job:d3cccea5@457FDB80 |

교차참조: claude 세션 사망(OAuth·문서 바인딩) → BE05 · 다중 세션 문서 바인딩 실패 → FP05 · sub-agent 포크 비용 → BE03.

### 3.4 읽기 경로·용량 (W1)

| ID | 시그니처 | 기전(1줄) | 건수 | 버전 | a7 | 심각 | 상태 | K행 | 증거 3 |
|---|---|---|---|---|---|---|---|---|---|
| RD01 | codex 코드모드가 툴 결과를 ~40,150자에서 무마커 절단 | 서버 캡 256KiB > 전송 캡 40K, 서버는 절단 사실을 모름(telemetry truncated=false) | 274 (a7 83) | 양쪽 | ✔ | P1 | open-known | K056(수정, 서버측만), K061/K062(열림) | call:019fb138#call_DbF9U…(08-06) · call:01a0223b#call_K8G3O…(08-21) · call:01a0311c#call_TD4Fd…(08-24) |
| RD02 | claude MCP 25K 토큰 캡 = 전량 손실, spill 파일은 `--tools ""`로 열 수 없음 | VinoMcpEndpoint에 크기 처리 없음 + CLI가 Read/Bash 제거 → 복구 불가 데드엔드 | 3~5 | a7 | ✔ | P1 | open-known | K107/K108(열림) | call:b1f24f98#toolu_01VDD…(08-26) · call:b1f24f98#toolu_0123N… · call:b1f24f98#toolu_013pS… |
| RD03 | `script:<guid>` 소스 읽기가 전부-아니면-전무(페이징·예산 면제) | inspectionsNode가 예산에 선반영되고 잘리지도 페이징되지도 않으며 응답 맨 끝에 붙음 | 43 (codex 27% 절단) | 양쪽 | ✔ | P1 | open-known | K061/K062(열림) | msg:999ACAEE/106ab0e9#24(08-26) · msg:999ACAEE/106ab0e9#48 · call:019fe92a#call_n3V7m…(08-11) |
| RD04 | function_call 경로는 무캡(250K~337K자/콜, pre 최대 7.15M) | 같은 질의가 호출 방식에 따라 8배 차이, 서버측 응답 예산 없음 | 72 | 양쪽 | ✔ | P1 | partially-fixed | K056(v3 08-21) | call:019fa17e#call_ZFXk4…(07-27) · call:019fa17e#call_6i7uW… · stats:tool-friction.md#Oversize results |
| RD05 | 연속 토큰(`nextOffset`)과 `inspections`가 대용량 배열 **뒤에** 직렬화 | 두 클라 캡 모두 꼬리를 자르므로 재개 토큰과 요청한 소스를 정확히 잃음 | 150 | a7 | ✔ | P2 | new | 없음 | call:01a03146#call_x6T8N…(08-24) · call:01a03146#call_ViKNE… · stats:read-path.md#snapshot_read errors |
| RD06 | delta read 미사용 — 같은 스코프 전량 재수신 | `knownSnapshotId`는 작동하나 602콜 중 51회만 전달(30회는 명시적 null) | 401 + 69 | 양쪽 | ✔ | P2 | new | 없음 | call:019fb138#call_vT40Z…(08-10) · call:019fa14c#call_985sC…(07-27) · stats:read-path.md#All read-side tools |
| RD07 | 파라미터 없는 전체 테이블 읽기(rhino_layers / data_flow_read) | 두 툴 모두 `properties = new {}` — 필터·limit·페이징 전무 | 8 (20% / 12.8% 절단) | 양쪽 | ✔ | P2 | new | 없음 | call:01a0311c#call_BieqF…(08-24) · call:01a03159#call_5ZVmJ… · call:01a0223b#call_4xEDD… |
| RD08 | inspect_outputs가 파라미터당 샘플 5개 하드캡, 확장 손잡이 없음 | `MaximumSampleValuesPerParameter = 5`(GrasshopperCanvasFoundationAdapter.cs:1379) | 3 (관측 파라미터의 34%가 >5) | a7 | ✔ | P2 | new | K059(수정, 별건) | msg:999ACAEE/106ab0e9#21(08-26) · #36 · #31 |
| RD09 | read-path 텔레메트리가 정작 깨지는 캡을 못 봄 | 서버는 자기 256KiB 판정만 기록(14건 전부 truncated=false)인데 실패는 전송층에서 남 | 14 | a7 | ✔ | P2 | new | 없음 | problem:999ACAEE@08-26T00:03:47 · problem:999ACAEE@08-26T02:00:03 · stats:read-path.md#problem-log |

교차참조: 8MiB 프레임 → HB05/CT04 · 10s 셀 분리 → CT11 · artifact 목록 어포던스 부재(413콜, pre only) → §5.

### 3.5 검증 오경보·검증 갭 (W1)

| ID | 시그니처 | 기전(1줄) | 건수 | 버전 | a7 | 심각 | 상태 | K행 | 증거 3 |
|---|---|---|---|---|---|---|---|---|---|
| VF01 | 모든 출력이 비었는데 `Verified and committed` | 커밋 후 빈-출력 점검이 "이 ChangeSet이 채울 수 있었나"를 모른 채 산문으로만 붙음 | 869 (커밋의 40.7%, a7 116) | 양쪽 | ✔ | P1 | **부분(K065 우회당함)** | K065(수정 08-11), K068(열림) | job:8579fff4@999ACAEE(08-26) · job:04c847c8@457FDB80(08-10) · job:bc4af875@457FDB80(07-30) |
| VF02 | agent 작성 OutputCountInRange가 76.8% 실패(alpha.7 82.4%) → alpha.7 실패의 89% | wire/setValue/source 쓰기는 solve를 예약만 하는데 검증기가 pre-solve 캐시를 읽음 + 한 predicate 실패가 잡 전체를 죽임 | 172 (a7 100) | 양쪽 | ✔ | P1 | open-known | K069(부분), K020(열림) | job:4d99d1cd@C5C77493(08-24) · job:5e258624@999ACAEE(08-26) · job:719562a7@999ACAEE |
| VF03 | predicate 실패가 롤백하지 않음 — `failed` 잡의 op는 이미 문서에 있음 | verify가 브릿지 적용 **후**에 돌고 되돌리지 않음 → 다음 ChangeSet의 absent 선언이 거부됨 | 6 | 양쪽 | ✔ | P1 | open-known | K020(열림) | job:ee28a536@BD95C956(08-21) · job:3045504c@BD95C956 · job:17245b51@C5C77493(08-24) |
| VF04 | 선언된 predicate의 95%가 동어반복(존재 확인 5,607건, 실패 0) | 서버 기본 세트가 op 목록에서 기계적으로 파생 — 브릿지 반환값의 재진술 | 2,979 / 5,884 | 양쪽 | ✔ | P2 | open-known | K070(부분), K071(이연) | problem:457FDB80@07-24 · problem:BD95C956@08-21T02:57:54 · problem:999ACAEE@08-26T00:10:23 |
| VF05 | 컴파일된 적 없는 소스에 "Verified and committed"(runtimeErrorAbsent 0/290 발화) | 소스만 쓰면 GH가 재컴파일하지 않아 RuntimeMessages가 비어 진공 통과 | 231 | 양쪽 | ✔ | P1 | new | 없음 | job:20fd4a98@457FDB80(08-06) · job:285cef7d@457FDB80 · job:b13c5042@457FDB80(08-11) |
| VF06 | 의미·시각 수용층이 사실상 안 돎 + viewport 'Top' 하드코딩 100% 실패 | 뷰포트 이름 고정, 캡처 실패를 '이의 없음'으로 처리, visual-review 5/2,656잡 | 5 (캡처 실패 3) | a7 | ✔ | P1 | open-known | K080/K081(열림), K158(수정=캡처툴만) | host:999ACAEE@08-26T09:18:15 · host:C5C77493@08-24T13:33:43 · host:B55C6BD9@08-24T19:39:56 |
| VF07 | goal_score 자기채점이 "6/6 통과", 사용자가 3분 내 뒤집음 | 목표 기준이 전부 셀 수 있는 것(개수·런타임·허용오차) — 형상 비교 항목이 없음 | 3 | 양쪽 | ✔ | P1 | new | K078(열림) | call:019feab6#call_aiP9G…(08-10) · msg:457FDB80/823add33#402 · #403 |
| VF08 | fullAuto 자동승인의 46%가 failed/hedged로 끝나는데 사후 다이제스트 없음 | 승인·질문 카드를 자동 해결하고 problem-log에만 남김 — 사용자에게 결정 요약이 안 감 | 232 | a7 | ✔ | P2 | new | K157(수정=모드만) | problem:999ACAEE@08-26T00:14:38 · problem:B55C6BD9@08-24T10:36:52 · problem:BD95C956@08-21T03:06:11 |
| VF09 | hedge 경보 피로 — 618쌍이 3회 이상 재플래그, 47%는 후속 확인 없음 | VF01이 구조적 소음이라 신호값이 붕괴, 진짜 35%가 구분 불가 | 618 (플래그 5,088) | 양쪽 | ✔ | P2 | new | K068(열림) | job:f8090d0f@999ACAEE(08-26) · job:3ce2750b@999ACAEE · job:15f36d6c@457FDB80 |

### 3.6 호스트·브릿지 안정성 (W1)

| ID | 시그니처 | 기전(1줄) | 건수 | 버전 | a7 | 심각 | 상태 | K행 | 증거 3 |
|---|---|---|---|---|---|---|---|---|---|
| HB01 | 30s 동적툴 데드라인 → `dynamic tool request failed`(27B) + 늦은 브릿지 응답 폐기 | 데드라인(30s) < 브릿지 예산(45s), 만료 시 correlation 삭제 → 응답이 주인을 못 찾음. change_submit 56건 중 42건은 잡 행 자체가 없음(제출 유실) | 160 결과 + 4 host 쌍 | 양쪽 | ✔ | P1 | new | K088(부분, 45s측만) | call:019f927b#call_jM7bv…(07-24) · call:01a0223b#call_q008T…(08-21) · host:BD95C956@08-21T14:44:25 |
| HB02 | 45s 브릿지 예산 초과 → recoveryRequired → 사람이 풀 때까지 정지 | UI 스레드 solve에 고정 예산, 진행 신호 없음 → '느림'과 '행'이 구분 불가 | 19잡(RR의 47.5%) | pre only(래치 행동은 a7 잔존) | (△) | P0 | partially-fixed | K088(부분), K086/K087(반증) | job:d3515bd3@52AFD2C0(07-27) · job:77cbd4ab@457FDB80(08-11) · job:e5cf6e32@29AE16F4(08-12) |
| HB03 | 브로커 쓰기 후 volatile 데이터가 비어 사용자에게 수동 Recompute 요구 | 새 슬라이더/파라미터가 문서 단위 NewSolution 없이는 volatile을 못 채움 | 요청 75 / 사용자 수행 35 | 양쪽 | ✔ | P1 | open-known | K066(부분), K147(열림) | msg:457FDB80/14b181e3#354(08-10) · job:22c24566@457FDB80(08-11) · msg:C5C77493/a641379b#39(08-24) |
| HB04 | 레이어 `visible` write-back 불일치 → BridgeProtocolException → RR halt | 부모 레이어/현재 레이어가 정당하게 덮어쓴 결과를 프로토콜 위반으로 throw | 3 (a7 RR 4건 중 3) | a7 | ✔ | P1 | new | 없음 | job:bb6675a8@BD95C956(08-21) · job:29c472b5@BD95C956 · job:06221588@C5C77493(08-24) |
| HB05 | 8MiB 프레임 캡이 쓰기 경로를 통째로 막음 | `EnrichSnapshotForConflictValidationAsync`가 CAS baseline 계산에 전체 geometry 스코프를 한 프레임에 담음 | 2 | a7 | ✔ | P1 | new | 없음 | job:1af1d173@BD95C956(08-21) · job:3d254b81@BD95C956 · problem:BD95C956@08-21T04:19:16 |
| HB06 | `Hosting failed to start` — ParentProcessMonitor가 기동 중 Kestrel bind 취소 | 부모 PID 조회 실패 시 즉시 lifetime 취소 → 협조적 종료가 기동 크래시로 기록, 그 실행분 잡 0 | 17/32 (핸들 8~9 런치 한정) | a7 | ✔ | P2 | new | 없음 | host:1C5012D7@08-15 · host:D8D11323@08-20 · host:119D96C6@08-24 |
| HB07 | `document bridge connection ended` 경고 55건이 스택과 함께(80%는 정상 종료) | 정상 파이프 종료와 프레임 절단을 구분 안 함 → 진짜 사고 11건이 묻힘 | 55 (실사고 11) | a7 | ✔ | P2 | new | 없음 | host:014FA71C@08-21 · host:E27BC8DB@08-18 · host:7E79ABAC@08-18 |
| HB08 | AgentHost가 Rhino의 열린 `.3dm` 핸들을 매 기동 상속(103/103) | 자식 프로세스 생성 시 핸들 상속 — 현재는 사후 해제(창 존재) | 103 | a7 | ✔ | P2 | new | K097(수정=별 원인) | host:014FA71C@08-21T22:48:01 · host:999ACAEE@08-26T08:29:48 · msg:52AFD2C0/25493177#19 |
| HB09 | 방금 커밋한 객체가 다음 잡의 pre-write 스냅샷에 없음 | 검증기가 쓰는 스냅샷이 직전 create보다 뒤처짐(stale GH_Document 인스턴스 의심) | 3 | 양쪽 | ✔ | P1 | new | 없음 | job:d56ae8d8@457FDB80(07-30) · job:392e7a92@457FDB80 · job:f8b82870@457FDB80 |

### 3.7 백엔드 특이 (W1)

| ID | 시그니처 | 기전(1줄) | 건수 | 버전 | a7 | 심각 | 상태 | K행 | 증거 3 |
|---|---|---|---|---|---|---|---|---|---|
| BE01 | codex sub-agent는 Vino 툴이 0개인데 토큰·시간의 대부분을 씀 | 동적툴 네임스페이스가 최상위 대화에만 등록 → 포크 자식은 exec/shell만; 부모는 wait_agent로 블록 | 2,828콜 / 204스레드 / 4.8h | 양쪽 | ✔ | P1 | new | 없음 | call:019fb130#call_1x3JP…(07-30) · call:01a02299#call_usUyO…(08-21) · call:01a03137#call_pRTIx…(08-24) |
| BE02 | `fork_turns:'all'` — 자식이 컨텍스트 만석으로 시작해 컴팩션만 하다 끝남 | 부모 전사(첨부 포함)를 통째 복제(서브 롤아웃 2.1GB) | 15 spawn / 30 rollout >20MB | 양쪽 | ✔ | P2 | new | 없음 | call:01a0223b#call_sCFLh…(08-21) · call:019fd9a5#session_meta(08-07) · call:019fb1ed#session_meta |
| BE03 | claude: 한 패널 메시지가 3턴으로 분해 제출되어 서로를 중단시킴 | 고정객체 블록/본문/블록을 각각 turn으로 submit → 실행 중 턴이 매번 abort | 3 (11초) | a7 | ✔ | P1 | new | 없음 | msg:999ACAEE/106ab0e9#43(08-26) · #46 · stats:session-lifecycle.md#system_messages |
| BE04 | claude 백엔드가 token_count·compaction 텔레메트리를 전혀 안 냄 | CLI 스트림의 usage를 SessionUsageState에 기록하지 않음 → 비용·컨텍스트 압력 UI가 공백 | 3/3 세션 | a7 | ✔ | P2 | new | K113/K114(설계) | stats:rollouts-summary.json#turn_event_types · call:b1f24f98#toolu_01GrJ… · call:97a19658#toolu_01B9V… |
| BE05 | claude 세션이 작업 중 사망(OAuth 만료 → 25분 공백, 이어서 문서 바인딩 상실) | 턴 디스패치 전 토큰 probe·refresh 없음, 두 번째 .gh가 열리면 '정확히 하나' 해석이 깨짐 | 2 (3시간 사용 중) | a7 | ✔ | P0 | new | K107/K108 인접 | call:b1f24f98#toolu_019eU…(08-26) · msg:999ACAEE/106ab0e9#56 · msg:999ACAEE/106ab0e9#51 |
| BE06 | C# 검증 표면 부재 — scratch가 RhinoCommon을 못 올리고 C# 오류가 `python_error`로 라벨됨 | K159 scratch + K104 하드닝의 합작으로 "저작 중인 그 코드"만 실행 불가 | 8 | 양쪽 | ✔ | P1 | new | K159/K104(수정, 합작 부작용) | job:7cf11a47@BD95C956(08-21) · job:0e51dae4@BD95C956 · call:01a022c6#call_e14uq… |

교차참조: crypto 부재 → CT06 · 10s 셀 → CT11 · artifact_read 봉투 → CT07/CT09 · claude 25K 캡 → RD02.

### 3.8 UX·의도 불일치 (W1)

| ID | 시그니처 | 기전(1줄) | 건수 | 버전 | a7 | 심각 | 상태 | K행 | 증거 3 |
|---|---|---|---|---|---|---|---|---|---|
| UX01 | "검증 통과"와 사용자 수용 기준의 괴리 — 응답 사용자 메시지의 23.9%가 교정 | predicate 어휘가 성공의 유일한 정의라 커밋=과제성공으로 번역되고, 형상 유사도는 루프 밖 | 108 (완료선언 직후 교정 36%) | 양쪽 | ✔ | P1 | open-known | K078(열림), K070(부분) | msg:457FDB80/9d85aea9#19(07-30) · msg:52AFD2C0/eb086086#91(07-27) · msg:C5C77493/726bdfe1#20(08-24) |
| UX02 | 사용자가 타이핑한 메시지의 20%가 카드 확인 문구 | 카드 발행에 비용이 없고 포괄 승인을 세션 정책으로 승격하는 경로가 없음(grant=잡 단위 1회용) | 132/659 | 양쪽 | ✔ | P1 | open-known | K127(부분), K122(열림), K157(수정=모드) | stats:user-signals.md#repeated_user_messages · msg:5486330A/7eac52bf#14 · msg:457FDB80/14b181e3#25 |
| UX03 | canvas 정리가 수렴하지 않고, auto-tidy가 사용자 작업물을 파손(undo 기록 없음) | 정리 대상이 '지목한 것'이 아니라 연결 클러스터 전체이고, 좌표 이동은 비파괴로 분류돼 승인 관문을 그냥 통과 | tidy잡 19(1,128 컴포넌트) / 불만 34 | 양쪽 | ✔ | P0 | open-known | K075/K076/K077, K142(열림) | job:965c8003@BD95C956(08-21) · msg:C5C77493/a641379b#33(08-24) · job:bd32ea3e@457FDB80(08-07, 중복 32개) |
| UX04 | GH 수동 조작 떠넘기기 + "Solver 꺼짐" 오진 | HB03의 빈 volatile을 자기 원인이 아니라 사용자 GH 상태로 귀속 | 78 (12.5%) | 양쪽 | ✔ | P1 | open-known | K147(열림) | msg:457FDB80/14b181e3#31(08-10) · msg:C5C77493/a641379b#39 · #40 |
| UX05 | replace-don't-repair — create 94 : delete 2, 정리는 사용자 몫 | 파괴적 op 승인 + LIVE/orphan 가드로 제자리 수리가 불가능해 병렬 교체가 최저비용 경로 | 94 (코퍼스 748:234) | 양쪽 | ✔ | P2 | open-known | K091(수정), K157 | msg:457FDB80/14b181e3#495(08-11) · job:b882568f@457FDB80 · job:42d54f19@457FDB80 |
| UX06 | 승인된 뒤에도 orphan 가드가 disconnect/delete를 거부 | 채팅/카드 승인이 대상 자원의 authorship·approval grant로 번역되지 않음, 2단계 연결-후-절단 프리미티브 없음 | 15 | pre only | ✘ | P1 | open-known | K035(부분), K091(수정) | job:42d54f19@457FDB80(08-10) · msg:457FDB80/9d85aea9#143 · job:9c453d06@457FDB80 |
| UX07 | 시각 검증 프리미티브 부재 — 캡처 1장에 199-op 레이어 조작, QA decoy bake가 결과물로 오인 | `rhino_view_capture`는 있으나 isolate-for-capture·GH 캔버스 캡처가 없음 | 21/105잡(20%) | a7 | ✔ | P1 | open-known | K158(수정=캡처만), K105 | job:1af1d173@BD95C956(08-21) · msg:BD95C956/3c2796cb#14 · #15 |
| UX08 | `resultOutput:null` 98%(140/143) → 빈출력 가드가 무장되지 않음 | produce-or-scaffold 선택에서 scaffold가 무비용 분기 | 140 | a7 | ✔ | P2 | **K065 우회** | K065(수정) | job:8579fff4@999ACAEE(08-26) · job:3ce2750b@999ACAEE · job:b192eae6@C5C77493 |
| UX09 | 응답 지연 p50 3.7분 / p90 17.9분, alpha.7에서 개선 없음 | 턴당 툴 호출량 + 추론 시간; 카드·recompute 왕복이 한 과제를 여러 턴으로 쪼갬 | 269/627턴(43%>5분) | 양쪽 | ✔ | P2 | 관측(비용 승수) | 없음 | msg:457FDB80/14b181e3#2 · msg:457FDB80/1267de70#67 · stats:headline.json |
| UX10 | full-auto 통지가 오류 채널로 와서 스스로를 부정하는 문구 | FullAutoContinuation이 isError 경로로 반환 → 두 백엔드 모두 실패로 렌더 | 20 | a7 | ✔ | P2 | new | 없음 | call:01a0223b#call_AKWd6…(08-21) · call:01a031f6#call_R0Nxj…(08-24) · call:01a03350#call_1IifW… |

경미(P3, 표 생략): Claude 오류가 'Codex'로 라벨(4/4, K109 열림) · 과잉 서술/포장(4) · 라벨 추측 앵커 이동(8) · 샘플 실행 부재로 사용자 슬라이더 토글(48).

### 3.9 기타 — 비용·관측 (W1)

| ID | 시그니처 | 기전(1줄) | 건수 | 버전 | a7 | 심각 | 상태 | K행 | 증거 3 |
|---|---|---|---|---|---|---|---|---|---|
| MS01 | 같은 벽에 반복 충돌 — 비커밋 517건 중 200건이 이미 본 오류의 순수 반복 | 요청 해시·idempotency가 시도마다 유일해 서버 dedup은 죽은 코드, 오류 문구가 매번 동일해 새 정보가 없음 | 200 (66쌍) | 양쪽 | ✔ | P1 | new | 없음 | stats:retry-chains.md#Relaxed D · job:4d99d1cd@C5C77493(08-24) · job:fba254c3@C5C77493 |
| MS02 | 폴링이 툴 벽시계의 55%(21,830s/39,329s) | 완료 이벤트가 없어 wait_agent/wait/job_status로 busy-poll, 242건은 턴 내 동일 인자 반복 | 1,530콜 | 양쪽 | ✔ | P2 | new | K060(수정=일부) | call:01a022fd#call_za73h…(08-21) · call:019fe92a#call_fCA2Y… · stats:tool-friction.md#Redundant repeats |
| MS03 | 3시간 세션 중 문서 작업은 3.7%(416s/187분), 커밋당 툴콜 9.6 | 위 결함들의 합 — Stage 4 회귀 지표 후보 | 105잡 | 양쪽 | ✔ | P2 | 지표 | 없음 | problem:BD95C956@08-21T02:57:54 · job:562e32ee@BD95C956 · stats:tool-friction.md#Calls per turn |

---

## 4. 랭킹 Top 15 (빈도 × 심각도 × alpha.7 잔존)

| # | ID | 왜 중요한가(1문장) | 건수/a7 | 의심 코드 영역 |
|---|---|---|---|---|
| 1 | VF02 | alpha.7 잡 실패의 89%가 이 하나이고, 서버가 제시하는 해법이 "아무것도 주장하지 말라"라 검증을 0으로 몰고 간다 | 172 / 100 | acceptance-predicate evaluator, verify 단계 solve 대기 |
| 2 | VF01 | 커밋의 41%가 "검증 완료"라면서 산출물이 비어 있다 — 신뢰의 근간이 무너지고 에이전트가 없는 버그를 사냥한다 | 869 / 116 | LiveDocumentBackend verify/commit 메시지 빌더 |
| 3 | RD01 | 4건 중 1건의 snapshot_read가 무마커로 잘려 에이전트가 부분 캔버스로 추론·편집한다 | 274 / 83 | DynamicToolDispatcher 결과 마샬링, snapshot_read 예산/커서 |
| 4 | HB01 | 30s 데드라인이 change_submit 56건을 삼켰고 42건은 잡 행조차 없다 — 중복 제출 위험 구간 | 160 / 3 | CodexAppServerClient 데드라인, LiveDocumentBackend correlation map |
| 5 | SL01 | 카드로 끝난 턴 133건이 사용자에게 아무 답도 주지 않는다(19건은 붉은 오류) | 133 / 다수 | SessionOrchestrator 턴 완료 경로, 카드 툴 스펙 |
| 6 | CT02 | predicate 문법 사다리가 **alpha.7에서 오히려 증가**(미선언자원 9→33) | 84 / 53 | DynamicToolSpecs.PredicateSchema, OperationValidation fail-fast |
| 7 | UX01 | 사용자 응답의 23.9%가 교정 — 기계 검증과 수용 기준이 분리되어 있다는 최종 증거 | 108 / 다수 | predicate 어휘, goal/visual-review 루프 |
| 8 | CT01 | 핫패스 최대 계약 마찰: 한 오퍼레이션에 최대 5왕복, 최신일(08-26) 양 백엔드에서 발화 | 59~61 / 26 | LiveDocumentBackend.OperationValidation.cs:120-200, DynamicToolSpecs.cs:389 |
| 9 | VF05 | 컴파일된 적 없는 소스 231건이 "Verified"로 통과 — runtimeErrorAbsent가 진공 통과 | 231 / 일부 | RuntimeErrorAbsent 평가 시점, setSource 후 expire |
| 10 | BE01 | 서브에이전트 2,828콜이 문서 작업 0인데 4.8시간을 태운다 | 2,828 / 953 | spawn_agent 배선, 동적툴 네임스페이스 등록 범위 |
| 11 | UX03 | auto-tidy가 사용자 캔버스를 undo 기록 없이 재배치·파손(코퍼스 최대 격앙 반응) | 19잡/1,128컴포넌트 | 턴 종료 auto-tidy 훅, CanvasLayout/ILayoutTidyService |
| 12 | SL02 | RR 40건이 세션을 중앙값 12분 세우고 8건은 세션을 죽였다(최장 6.6일) | 40 / 4 | halt 래치, RR 후 자동 재조회·재조정 부재 |
| 13 | HB03 | 사용자가 손으로 Recompute를 38회 했고 결국 폭발했다 — 제품이 사람을 실행기로 쓴다 | 75 요청 / a7 5 | python.execute 경로의 문서 단위 expire/solve |
| 14 | RD02+RD03 | 이번 리뷰의 계기: claude에서 50K 소스가 전량 손실되고 spill 파일도 못 연다 | 3~5 + 43 | VinoMcpEndpoint 크기 처리, ReadSnapshotCoreAsync inspections 페이징 |
| 15 | MS01 | 비커밋의 51.5%가 이미 본 벽 — 자기반복 인지 신호가 어디에도 없다 | 200 / 다수 | 잡 결과 렌더러(SessionOrchestrator), 반복 감지 |

차점(16~19): SL03(full-auto 파킹 P0, a7 전용) · CT05(append-only, 08-26 생존) · FP02(자기 컴포넌트 오탐, 문구가 사용자에게 거짓 귀속) · CT06(crypto, 40건).

---

## 5. 기각·보류 (재심 금지)

| ID | 항목 | 사유 |
|---|---|---|
| CT12 | C# 예약어 `out` 거절 (9잡) | K012 수정 c746f8e(08-20, 서버 흡수). 마지막 발화 08-11, 이후 0. 다만 alpha.7 C# 저작량이 적어 부재 근거는 약함 — 회귀 인지법은 "`out` 거절 재등장 + 흡수 진단 미기록" |
| CT15 | 첫 실행 비용 게이트가 mm 슬라이더를 개수로 오인 (10잡) | K073 수정 c746f8e(08-20, advisory 강등). 전부 pre-alpha7·단일 프로젝트. **후속 확인 필요**: alpha.7 대체 게이트(`predicted to take ~Ns`, 3잡)가 같은 unit-blind 입력을 물려받았는지(K074 부분) |
| FP04 | stale 메시지가 자원명을 숨겨 동일 해시 재제출 (6잡) | K029 수정 845c66e(08-20). 정의 신호(같은 세션 2연속 동일 해시)는 3쌍뿐이고 전부 pre-alpha7. F154의 presentInAlpha7=true/lastSeen=08-24는 **정정**(FP03 재료를 오귀속) |
| CT03 | Wireify/Cordyceps owner 어휘 (55잡) | 08-07 v13 어댑터 리네임으로 종결. alpha.7 0건. **회귀 마커**: Vino 브랜드 로그에 `belongs to owner`가 재등장하면 legacy-owner 컨버터에 구멍 |
| — | codex sandbox helper 부재 (18콜) | K102 수정(resolver 정렬). 07-27 이후 0. 기지 필터의 positive control로만 사용 |
| — | pre-alpha7 무캡 canvas 덤프 (42결과, 최대 7.15M자) | K056 수정 7d9252b(v3, 08-21). 마지막 07-30. 결과적으로 제약이 클라 전송층으로 이동한 것이 RD01~RD04 |
| — | artifact 목록 어포던스 부재 → 셸 포렌식 (413콜) | 단일 세션(07-30) 버스트, alpha.7 0. 근본(목록 API 부재)은 남아 있으나 재발 증거 없음 → 보류 |
| — | `stats/tool-friction.md` 지표 인플레(중복콜·오류율) | 채굴 아티팩트(코드모드 args_preview가 JS 소스식). 제품 결함 아님 — Stage 2가 유령을 쫓지 않도록 명시 |
| — | AgentHost 재시작 턴 유실 (SL06, 15건) | pre-alpha7 전용. 후속 가족(`interrupted` 22 · `could not recover` 19 · 컴팩션)이 같은 사용자 경험을 alpha.7에서 재생산하므로 **SL01/SL03/SL07로 이관해 추적** |

**반증되어 되살리면 안 되는 가설**(known-findings 기준): K067(solver 토글) · K087("45s=무거운 solve" 오진, 실체는 K086 GH 모달) · K092(Modified 게이트).

---

## 6. 버전 추세 (pre-alpha7 vs alpha.7)

분모: 잡 2,203 / 453 · 툴콜 9,975 / 3,599 · 세션 66 / 16 · 프로젝트(잡 보유) 14 / 4.
**caveat**: brand ≡ version이라 버전 효과와 리네임·작업변화가 분리되지 않는다. 457FDB8091063B0D 한
프로젝트가 pre-alpha7 잡의 77.7%(전체의 64.4%)라 pre 수치는 사실상 그 프로젝트의 수치다.

| 범주 | 지표 | pre-alpha7 | alpha.7 | 방향 |
|---|---|---|---|---|
| 전체 | 비커밋률 | 17.4% (383/2,203) | **29.6%** (134/453) | ▲ 악화(구성이 바뀜) |
| 검증 오경보 | predicate 실패 잡 | 3.3% (72) | **22.1%** (100) | ▲▲ ×6.8 |
| 검증 오경보 | 빈출력 hedge / 커밋 | 41.4% (753) | 36.4% (116) | ≈ 보합 |
| fingerprint | auto-no-baseline | 5.2% (114) | **0.66%** (3) | ▼▼ ×0.13 (K023 효과) |
| fingerprint | auto-drifted | 1.36% (30) | 0.44% (2) | ▼ (K024 효과, 기전은 잔존) |
| fingerprint | stale CAS | 2.2% (49) | 2.0% (9) | ≈ 개선 없음(구성만 이동: group/layer) |
| 계약 | append-only | 0.64% (14) | 0.88% (4) | ≈/▲ |
| 계약 | python_error 비커밋 | 2.9% (64) | 1.5% (7) | ▼ |
| 계약 | predicate 제출 거부 /1k콜 | 7.6 (76) | **42.8** (154) | ▲▲ ×5.6 |
| 읽기 | 40K 절단 /1k콜 | 17.9 (179) | **26.4** (95) | ▲ ×1.5 |
| 호스트 | `dynamic tool request failed` /1k콜 | 15.7 (157) | **0.83** (3) | ▼▼ ×19 |
| 호스트 | 45s 예산 초과 잡 | 11 | **0** | ▼ 종결(래치 행동은 잔존) |
| 세션 | recoveryRequired | 1.63% (36) | 0.88% (4) | ▼ (단 잔존 4건 중 3건이 HB04 한 기전) |
| 백엔드 | sub-agent 콜 비중 | 18.8% (1,875) | **26.5%** (953) | ▲ |
| 백엔드 | spawn_agent /1k콜 | 17.3 | 7.5 | ▼ |
| 백엔드 | crypto 오류 /1k콜 | 2.6 | 2.8 | ≈ |
| 코드모드 | `Script running` 서스펜션 /1k콜 | 44.8 | 24.2 | ▼ |

**읽는 법**: alpha.7은 *지문 계열을 고치고 검증 계열을 새로 만들었다*. 08-20 전후의 fingerprint 수정
(K023/K024/K025/K029)은 blocked를 204→18로 떨어뜨렸고, 같은 시기에 도입·강화된 predicate 문화
(K065의 outputCountInRange 자동 주입, DynamicToolSpecs의 "직접 붙여라" 지시)가 failed를 72→100으로 밀어 올렸다.
비커밋률 악화(17.4→29.6%)는 품질 저하가 아니라 **실패 정의가 바뀐 결과**로 읽어야 한다.

---

## 7. Stage 2 착수 포인터

| # | ID | 리플레이할 mined 레코드 | 열어볼 코드 | 재현 성립 조건 |
|---|---|---|---|---|
| 1 | VF02 | job:4d99d1cd@C5C77493, job:719562a7@999ACAEE의 payload + problem-events의 predicate-outcome(4,494) | acceptance-predicate evaluator, verify 단계, `DynamicToolSpecs.cs:56` 지시문 | executePython 없는 ChangeSet에 outputCountInRange를 붙이면 solve 전 캐시를 읽어 실패 → 동일 페이로드로 유닛 재현 |
| 2 | VF01 | hedged 869잡 중 executePython 없는 540건(job:8579fff4 등) | LiveDocumentBackend verify/commit 메시지 빌더, `AttachResultOutputPredicates` | source-only 커밋에 "output(s) … empty"가 붙는지 + 그 문구가 `<vino_job_results>`에 "WITH ISSUES"로 가는지 |
| 3 | RD01 | truncated 274콜(call:01a0223b#call_K8G3O 등)과 같은 시각 서버 `snapshot-read` 레코드 | `LiveDocumentBackend.cs:297 SnapshotReadByteCap`, DynamicToolDispatcher 결과 경로 | 100+ 컴포넌트 캔버스에서 exec 경로 결과가 40,150자에서 끊기고 서버 telemetry는 truncated=false |
| 4 | HB01 | 160개 `dynamic tool request failed` 콜 + host.log 데드라인/고아 4쌍 + ±90초 잡 존재여부 | `CodexAppServerClient` 30s 데드라인, LiveDocumentBackend pending correlation map | 30s 넘는 브릿지 응답 후 correlation이 폐기되고 툴은 27바이트 문자열을 받는지; change_submit에서 잡이 실제로 생성됐는지 |
| 5 | SL01 | 카드로 끝난 133턴(call:019fee3b#call_PIwKN 등) + 뒤따르는 시스템 메시지 | SessionOrchestrator 턴 완료/최종답 추출, ask_user·goal_propose·approval_request 스펙 | 마지막 툴콜이 카드일 때 `agent_message.message == ""` → 패널 무음/오류가 재현되는지 |
| 6 | CT02 | 84개 거부 콜(call:019fb04d, call:01a031f6, call:b1f24f98) | `DynamicToolSpecs.PredicateSchema()` (~910-926), OperationValidation predicate 검증 | 이름공백/미선언자원/문법 세 위반을 한 페이로드에 넣었을 때 하나만 보고되는지; inputSchema는 무엇을 이미 공개하는지 대조 |
| 7 | UX01 | user-signals.md의 교정 108건 + 각 직전 어시스턴트 메시지 | predicate 어휘, goal_score, visual-review 파이프 | "완료" 선언 직후 교정률 36%를 세션 단위로 재계산하고, 통과한 predicate 목록이 사용자 불만과 무관함을 보이기 |
| 8 | CT01 | 08-26 claude 사다리(b1f24f98의 5단) + codex 동일 규칙(call_JI70G, call_otv3H) | `LiveDocumentBackend.OperationValidation.cs:120-200`(required foreach), `DynamicToolSpecs.cs:389` | 필수 인자 3개를 빼면 3왕복이 필요한지; validate-all로 바꿨을 때 한 번에 3개가 나오는지 |
| 9 | VF05 | updatePythonSource-only 290잡(job:20fd4a98 등)과 그 predicate-outcome | RuntimeErrorAbsent 평가, setSource 후 expire 정책 | 소스만 쓴 잡에서 runtimeErrorAbsent가 항상 통과하고, 같은 소스가 execute 잡에서는 22회 실패함을 대조 |
| 10 | BE01 | 204 sub-agent 스레드의 2,828콜(Vino 툴 0) + 588 wait_agent | spawn_agent 배선, 동적툴 네임스페이스 등록 지점 | 자식 스레드에서 `vino_v1__*`가 정말 미등록인지, 부모 wait_agent가 완료 푸시 없이 폴링만 하는지 |
| 11 | UX03 | auto-tidy 19잡(job:965c8003, job:bd32ea3e)의 payload와 직전/직후 좌표 | 턴 종료 auto-tidy 훅, `CanvasLayout.cs`/`ILayoutTidyService`, K141 rules.md 재조회 | 요청 범위 밖 컴포넌트가 이동 대상에 포함되는지, 이동 전 좌표가 어디에도 기록되지 않는지 |
| 12 | SL02 | RR 40잡 + 다음 잡까지의 간격(중앙값 722s) + 미ack 29건 | halt 래치, `LiveDocumentBackend.cs:3145` 분류, RR 후 재조회 경로 | RR 진입 사유의 절반이 사전 차단 가능한 계약/예산 실패인지, ack 없이 세션이 영구 정지되는지 |
| 13 | HB03 | 22c24566 계열 8잡(4분) + self-stale-rebase 4건 + 사용자 "recompute했어" 35건 | python.execute 경로의 expire/recompute, 신규 파라미터 volatile 초기화 | 새 슬라이더 생성 직후 DataCount=0이고 문서 단위 NewSolution만이 채우는지 |
| 14 | RD02/RD03 | b1f24f98의 3개 spill 콜(74,280/66,004/64,003자) + 그 시각 problem `snapshot-read` | `VinoMcpEndpoint.cs`, `ClaudeCliSessionClient.cs:350-357`, `ReadSnapshotCoreAsync` inspections 경로 | 50K+ 소스를 `script:` 스코프로 읽으면 claude는 전량 손실·codex는 무마커 절단, 두 경로 모두 페이징 파라미터가 없음 |
| 15 | MS01 | retry-chains.md의 66쌍/200잡 + 각 재시도의 request_hash·idempotency_key | 잡 결과 렌더러, 브로커 dedup 경로 | 동일 오류 문구 재발 시 서버가 "이미 시도함"을 말하지 않고, 해시가 매번 달라 dedup이 절대 발화하지 않음 |

**공통 준비물**: `.log-mine/jobs.jsonl`(payload_dir로 원본 operations/*.json 복원 가능),
`problem-events.jsonl`(job-state 11,526 · predicate-outcome 4,494), `tool-calls.jsonl`.
`turn-events.jsonl`은 73MB이므로 grep 전용.

**Stage 4 회귀 지표 후보**: 비커밋률 · 잡당 재시도 · 커밋당 툴콜(현재 9.6) · 세션시간 대비 문서작업 비중(현재 3.7%) ·
hedge 커밋 비율(41%) · predicate 실패 비율(alpha.7 22%).
