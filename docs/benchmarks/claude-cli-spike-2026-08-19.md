# Claude CLI 스파이크 (Phase 0) — 2026-08-19

**결론: 게이트 PASS.** 계획서(claude-backend-plan.md)의 미확인 7항목 전부 실측으로 확정, 폴백 불필요.
측정 대상: 구독 OAuth `claude` CLI **v2.1.235** (native), Windows 11. 원시 로그: scratchpad `spike/{streamjson,mcp,perms,hostside}/`.

## 7문항 답

### 1. 플래그 표면 — 전부 존재 + 계획을 뒤집는 신규 플래그
`-p` / `--output-format stream-json` / `--verbose` / `--resume <sid>` / `--mcp-config` / `--strict-mcp-config` /
`--allowedTools`·`--disallowedTools` / `--permission-mode (acceptEdits|auto|bypassPermissions|manual|dontAsk|plan)` /
`--append-system-prompt` / `--model` 모두 확인. **신규(계획 수정 필요)**:
- **`--effort <low|medium|high|xhigh|max>`** — 계획서 "effort는 Claude 대응 없음"은 **구식**. Vino effort 어휘와 1:1 매핑.
- **`--session-id <uuid>`** — 사전 발급 UUID 수락·에코·`--resume` 가능 (실측). StartThread가 id를 먼저 만들고 스폰하면 init-파싱 경쟁 제거.
- **`--tools ""`** — 내장 툴 **전부** 제거(Task/Bash/Read/Edit/Write 0개), mcp__* 만 남음. `--disallowedTools` 나열보다 우월.
- `--input-format stream-json` — 실시간 스트리밍 입력 = **세션당 지속 프로세스 옵션 존재** (Phase 5 재스폰-vs-지속 결정에 직결, 미측정).
- 기타: `--json-schema`(구조화 출력), `--no-session-persistence`, `--fallback-model`, `--setting-sources`, `--fork-session`, `--system-prompt`.
- **함정: `--bare`는 OAuth를 안 읽음**(API key 전용) → 구독 경로에서 사용 금지.

### 2. stream-json 이벤트 스키마 (실측 전수)
- 이벤트 타입: `system`(subtype: `hook_started`/`hook_response`/`init`/`thinking_tokens`), `assistant`, `rate_limit_event`, `result`.
- **init**: `session_id`, `tools[]`, `mcp_servers[{name,status}]`, `model`, `permissionMode`, `apiKeySource`("none"=구독), `claude_code_version`, `uuid` 등.
- **assistant**: 전체 API message(`content[]`, `usage{...cache_creation{ephemeral_5m,1h}}`), `session_id`, `request_id`. **content 블록당 1이벤트**(thinking→text)가 같은 `message.id` 공유 — usage 순진 합산 시 중복 계상.
- **result**: `is_error`, `terminal_reason`, `api_error_status`, `total_cost_usd`, `usage`, `modelUsage{...}`, `permission_denials[]`, `result`(텍스트), `duration_ms`, `num_turns`.
- **오류 판정: `subtype`은 404에서도 "success"** — 절대 쓰지 말 것. exit code(1) + `is_error`/`terminal_reason`/`api_error_status`로 판정.
- 파서는 미지 이벤트 관용 필수(사용자 훅이 -p에서도 발화, init 앞에 옴).
- **stdin 리다이렉트 필수**: 없으면 3초 대기 + stderr 경고.
- 비용 실측: 콜드 $0.0207(시스템 컨텍스트 캐시 생성) vs 재개 $0.0037 — **resume이 5배 저렴**.
- `rate_limit_event`(5h 윈도·resetsAt·overageStatus)가 매 콜 옴 — Vino 스케줄링 공짜 텔레메트리.

### 3. 자격증명 = 파일 (키체인 아님)
`~/.claude/.credentials.json` → `claudeAiOauth{accessToken,refreshToken,expiresAt,refreshTokenExpiresAt,scopes,subscriptionType,rateLimitTier}`.
→ `ClaudeAuthProbe` = 파일 존재+`expiresAt` 읽기(값 로깅 금지). `subscriptionType`/`rateLimitTier`로 칩 표기 가능.

### 4. Windows 실행 해석 — 네이티브 exe, 심 없음
- `%USERPROFILE%\.local\bin\claude.exe` = **진짜 PE32+ (~311MB, Bun 번들)**. `Process.Start` 직접 가능, cmd /c 불필요.
- 버전 저장소 `%USERPROFILE%\.local\share\claude\versions\<v>` (풀 exe, 현재 3버전). 레지스트리 발자국 **0**.
- 탐색 순서: PATH → `.local\bin\claude.exe` → versions 저장소. 핀: 자식 프로세스 env `DISABLE_AUTOUPDATER=1` + `claude install <v>`(또는 versions 파일 직접 실행) — 백그라운드 자동업데이트가 세션 도중 exe를 갈아치우는 것 방지.

### 5. 헤드리스 신뢰/권한
- **-p는 신뢰 다이얼로그 생략**(도움말 명시 + 처음 보는 빈 디렉터리에서 실측 OK).
- 헤드리스 기본 모드는 안전 명령(echo)을 자동 허용 — `--setting-sources ""`(사용자 설정 격리)로도 동일 → auto 분류기 내장 동작.
- **제품 경로는 이와 무관**: `--tools ""`로 내장 툴 자체가 없음. MCP 툴은 `--allowedTools "mcp__vino__…"` 화이트리스트로 무프롬프트 호출 확인(6번 실측에 포함). bypassPermissions 불필요.
- **`--strict-mcp-config` 필수**: 없으면 사용자 전역 커넥터 MCP(Gmail/Calendar/vercel + pending "rhino")가 세션에 유입.

### 6. MCP 전송 — 루프백 Streamable HTTP 완전 동작 (crux 해결)
stdlib Python HTTP 서버(순수 요청/응답 JSON, SSE 없음)로 end-to-end 성공:
- init 이벤트 `mcp_servers:[{name:"vino",status:"connected"}]`, `tools/call` → 결과 텍스트 왕복 확인.
- **`--mcp-config`의 `headers:{"X-Vino-Secret":…}`가 6/6 요청 전부에 실림**(pre-init 포함, 소문자화되어 도착 → 대소문자 무시 조회 필수). per-session 시크릿→sessionId 매핑 성립.
- 프로토콜: initialize 요청 `protocolVersion:"2025-11-25"`; POST Accept `application/json, text/event-stream` — **평문 JSON 응답 수락**(SSE 불필요); `notifications/initialized`→202; GET /mcp(SSE 구독 시도)→405 응답해도 무해.
- **비표준 사전 프로브**: initialize 전에 `server/discover`(id "server-discover-probe-1") POST가 옴 — **JSON-RPC -32601로 응답해야** 정상 폴백. HTTP 에러/무응답이면 연결 실패 위험.
- `Mcp-Session-Id` 미사용, 종료 신호 없음(정리는 프로세스 수명으로). `tools/call`의 `_meta.claudecode/toolUseId` = stream-json tool_use와의 공짜 상관관계 ID.

### 7. 패키지 핀
- AgentHost TFM net8.0. `ModelContextProtocol.Core` 최신 안정 **2.2.0**(net8.0 타깃, 계획의 2.0.0-preview.1은 구식) / `ModelContextProtocol.AspNetCore` 2.2.0(전체 `ModelContextProtocol` 2.2.0 동반).
- **대안(권장 검토)**: 6번 실측상 필요한 메서드가 5개뿐(server/discover→-32601, initialize, initialized→202, tools/list, tools/call)이라 **SDK 없이 Kestrel MapPost("/mcp") 수제 JSON-RPC 핸들러**로 충분. 의존성 0 + 헤더 제어 완전 — Phase 3b에서 SDK 채택 여부를 코드 크기로 저울질.

## 카탈로그·저장소 사실
- **정적 모델 카탈로그**: `claude-fable-5`(기본, `[1m]` 롱컨텍스트 변형), `claude-opus-5`, `claude-sonnet-5`, `claude-haiku-4-5`. effort: 5세대는 low..max 전부, haiku는 xhigh/max 불가(바이너리 게이트 실측).
- **대화 저장**: `~/.claude/projects/<slug>/<sid>.jsonl`, slug = cwd의 비영숫자 전부 '-' 치환(**한글도 '-'**, 충돌 가능) → Vino는 **DataDirectory 아래 ASCII 전용 고정 cwd**로 스폰해 전용 slug 확보.
- `~/.claude/sessions/<pid>.json` = **라이브 프로세스 레지스트리**(pid,sessionId,cwd,status…) — bench-run 무필터 kill 사고(.rhl 포스트모템)의 PID 기준 kill에 그대로 쓸 수 있는 부기.

## 계획 대비 설계 변경점 (Phase 3 반영)
1. §3a "effort 대응 없음" 삭제 → `--effort` 직통.
2. StartThread: AgentHost가 UUID 발급 → `--session-id`로 스폰(1턴), 이후 `--resume`. init 파싱 경쟁 제거.
3. 표준 스폰 인자: `-p --output-format stream-json --verbose --model <m> --effort <e> --session-id/--resume <sid> --mcp-config <파일> --strict-mcp-config --tools "" --allowedTools "mcp__vino__…" --settings/--setting-sources(격리) + stdin=NUL + env DISABLE_AUTOUPDATER=1 + cwd=<DataDirectory>\claude-cwd`.
4. 오류 분류: exit code + `is_error`/`terminal_reason`/`api_error_status` (subtype 금지).
5. R5(재스폰 vs 지속): `--input-format stream-json` 지속 모드가 존재하므로 Phase 5 벤치 대상 2개 확정. resume 5배 저렴은 재스폰 편에 유리한 데이터.
6. 3b 서버: MCP SDK 대신 수제 JSON-RPC 5메서드 옵션 추가(위 7번).

## 미결(비차단)
- `--input-format stream-json` 지속 프로세스 실측(Phase 5). `--allowedTools "mcp__vino"` 프리픽스 매칭 범위. 대화 깊이 성장곡선. `--json-schema` 활용처.

## 추기 (2026-08-24, CLI v2.1.241, Phase 3 step-0 프로브)
- **stream-json 입력의 "단발 메시지 후 stdin close" 재스폰 모드 3/3 PASS**: ① 단일 user 메시지 → 정상 종결(exit 0, result "PROBE1", terminal_reason completed, $0.0149 콜드) ② `--resume` + **base64 image content block** → 모델이 이미지를 봄("red", $0.0013 — resume 저가 재확인) ③ 2차 resume에서 1턴 내용 정확 회상(연속성). → Phase 3a 턴 전달 = stdin stream-json 확정, 이미지 채널 성립, 폴백 불필요.
