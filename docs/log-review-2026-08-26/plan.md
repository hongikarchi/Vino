# 로그 전수 검토 → 문제 분류 → 결함 → 원인 → 수정 — 계획 (2026-08-26)

계기: 08-26 "260825 심의 모델링" 세션에서 50K자 스크립트 소스를 Claude 백엔드가 읽지 못한
사건(`memory: vino-claude-script-read-cap`). 이번 기회에 07-21 이후 누적된 실사용 로그를
전수 검토해 **지속적으로 재발하는 문제**를 분류하고, 결함을 확정하고, 원인을 묶어 고친다.

## 코퍼스 (08-26 실측)

| 소스 | 규모 |
|---|---|
| `%LOCALAPPDATA%\{Vino,GPTino}\projects\*\live-jobs.db` | 172 프로젝트, 2,656잡 (Committed 2,139 / Failed 255 / Blocked 222 / RecoveryRequired 40) |
| `…\artifacts\*\.{vino,gptino}-reserved\jobs\<jobId>\operations\*.json` | 잡별 payload 2,712개 (잡 2,656건 전부 매칭, 56개는 DB 행이 사라진 고아) |
| `…\problem-log.jsonl` (10 파일) | 16,462 레코드 (job-state 11,526 · predicate-outcome 4,494 · auto-approval 247 · auto-fill 113 · self-stale-rebase 47 · job-exception 16 · snapshot-read 14 · visual-review 5) |
| `…\runtime.db` (sessions/messages) | 82 세션, 1,376 메시지, 스키마 9종 |
| `…\host.log` (86 파일, alpha.7 이후) | 8,441줄 |
| `~/.codex/sessions/**/*.jsonl` — cwd가 `\{Vino,GPTino}\projects\`인 것 | 261 롤아웃 (프로젝트 cwd 237 + vino-bench 24), 942개 스캔 중 선택, 2.52 GB. Vino 툴 호출은 code-mode `exec`의 JS `tools.vino_v1__<tool>(…)`로 기록 |
| `~/.claude/projects/C--Users-user-AppData-Local-Vino-projects-*/*.jsonl` | 3 트랜스크립트 |
| 선행 분석 (재발견 방지 필터) | `docs/archive/issue-triage-2026-08-10.md`, `archive/convergence-audit-2026-08-12.md`, `archive/capability-integrity-2026-08-11.md`, `benchmarks/constraint-legitimacy-audit-2026-08-19.md`, 메모리 `vino-crud-efficiency-audit`, `vino-claude-script-read-cap` |

버전 경계: `0.1.0-alpha.7`은 2026-08-14(커밋 33bfa01)부터. 그 이전 데이터는 `pre-alpha7`로 스탬프.

## Stage 0 — 결정론적 추출 (`scripts/log-mine/`, 출력 `.log-mine/` gitignored)

에이전트가 2.5GB 원본을 읽지 않도록, 스크립트로 통합 JSONL을 만든다. 공용 헬퍼는
`scripts/log-mine/common.py`. 모든 레코드는 아래 공통 필드를 가진다:

```
project_dir   프로젝트 폴더 이름 (16자 hex)      brand   Vino | GPTino
project_name  context/project.json 의 이름       version 0.1.0-alpha.7 | pre-alpha7
```

| 파일 | 스크립트 | 레코드 |
|---|---|---|
| `jobs.jsonl` | `extract_jobs.py` | live-jobs 전수. `session_id, job_id, idempotency_key, summary, state(소문자 정규화), phase, message, enqueued_at, created_at, updated_at, target_doc, op_kinds[], ops[{operationId,kind,owner}], payload_dir, payload_ops[{file,bridgeOperation}]` |
| `problem-events.jsonl` | `extract_problem_log.py` | problem-log 원본 필드 + 공통 필드 |
| `messages.jsonl` | `extract_messages.py` | `session_id, session_name, backend(codex\|claude\|legacy), model, msg_id, role, phase, created_at, content(전문), content_len, prev_role, prev_phase` |
| `sessions.jsonl` | `extract_messages.py` | 세션 행 전수(스키마 차이 흡수) |
| `hostlog-events.jsonl` | `extract_hostlog.py` | Warning/Error/Critical 전부 + `Vino.*`/`GPTino.*` 카테고리의 Information (ASP.NET 소음 제외). `at, level, category, message` |
| `tool-calls.jsonl` | `extract_rollouts.py` | Codex 롤아웃 + Claude 트랜스크립트 공통 스키마: `source(codex\|claude), thread_id, rollout_file, turn_index, at, tool, args_preview(≤2000자), args_len, result_len, is_error, error_text(≤300), duration_ms, code_preview(≤300, codex exec만)` |
| `turn-events.jsonl` | `extract_rollouts.py` | `source, thread_id, at, type(user_message\|task_started\|task_complete\|interrupted\|compacted\|token_count\|error\|…), detail(≤500)` |
| `stats/*.md`, `stats/*.json` | `stats.py` | 실패율(kind×version×주차), 정규화 오류 클러스터, 재시도 체인, 툴별 오류율·반복 호출, 사용자 교정 신호, 세션 타임라인 요약 |
| `corpus.md` | `stats.py` | 재고표 (이 문서의 표를 실측으로 갱신) |

정규화 규칙: GUID/숫자 → `#`, 메시지 앞 110자로 시그니처. 상태는 소문자. 날짜는 ISO 그대로.

## Stage 1 — 문제 분류 (Opus, 워크플로우 체인 W1→W2→W3)

- **W1 Sweep**: 렌즈별 finder 8 + 대형 세션 deep-dive 4~6. 출력 스키마
  `{category, signature, symptom, evidence:[{ref,at}], count, versions[], firstSeen, lastSeen, presentInAlpha7, hypothesis}`.
- **W2 Dedup·기지 필터·적대 검증**: (category, signature) 중복 제거 → 선행 문서/커밋 대조(still-open / fixed / regressed) → still-open마다 refuter 3(증거 실재·반복성·코드 귀속) 다수결.
- **W3 Synthesis + completeness critic** → `classification.md`: 분류 체계, 빈도×심각도, 버전 추세, 상위 15, Stage 2 포인터.

분류 체계 초안: 계약·스키마 마찰 / fingerprint·동시성 / 세션 생명주기 / 읽기 경로·용량 /
검증 오경보 / 호스트·브릿지 안정성 / 백엔드 특이(codex vs claude) / UX·의도 불일치.

## Stage 2~4

- **Stage 2 결함 확정**: 상위 발견마다 코드 경로(file:line) + mined payload 리플레이/유닛 재현 → defect / by-design / 프롬프트 갭 → `defects.md`.
- **Stage 3 원인 분석**: 결함별 근본원인 + 적대 검증, 뿌리별 묶기.
- **Stage 4 수정**: 뿌리 단위 worktree 구현 → 유닛 green → 라이브 게이트(Rhino, 사용자 개입) → 배포. 회귀 지표 = `stats.py`를 수정 후 로그에 재실행(비커밋율·잡당 재시도·spill 건수).

## 결정 (08-26 사용자 승인)

- 범위: 전 기간 포함, `presentInAlpha7` 우선 랭킹.
- 모델: Stage 1 Opus. 워크플로우는 체인으로 분할.
- mined 데이터(사용자 프롬프트 포함)는 커밋하지 않는다 (`.log-mine/` gitignored).
