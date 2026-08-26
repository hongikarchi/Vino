# Stage 0 extractor: agent rollouts / transcripts -> tool-calls, turn-events, threads.
#
#   (A) ~/.codex/sessions/**/*.jsonl   — 939 files, keep the ~237 whose session_meta cwd is under
#       \{Vino,GPTino}\projects\<dir>\ plus the ~24 \Temp\vino-bench\ runs (bench=true).
#   (B) ~/.claude/projects/*{Vino,GPTino}-projects-*/*.jsonl — Claude Code transcripts.
#
# Both are mapped onto ONE tool-call schema and ONE turn-event schema so stats.py can compare
# backends. Outputs: .log-mine/{tool-calls,turn-events,threads}.jsonl + rollouts-summary.json.
#
# Streaming rules: every file is read line by line. Lines > 2 MB (779 of them, 79% of the 2.5 GB)
# are NOT json.loads()'d unless their payload type is one we actually need (a tool call or its
# output); the rest (base64 image messages, `compacted` history dumps) are classified from a 4 KB
# prefix and summarised.
#
# Local to this script (not in common.py): version_from_date(), the exec-JS tool-call scanner,
# and the error heuristics.
import collections
import glob
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common  # noqa: E402

LARGE_LINE = 2 * 1024 * 1024
HEAD = 4096
ARGS_MAX = 2000
CODE_MAX = 300
RESULT_MAX = 500
ERROR_MAX = 300
DETAIL_MAX = 500

# Vino/GPTino tool surfaces, in every encoding seen across the corpus.
NS_PREFIXES = [
    ("gptino_v1__", "gptino_v1"),
    ("vino_v1__", "vino_v1"),
    ("mcp__cordyceps__", "mcp_cordyceps"),
    ("mcp__vino__", "mcp_vino"),
    ("mcp__gptino__", "mcp_gptino"),
    ("mcp__wireify__", "mcp_wireify"),
]
VINO_NS = {ns for _, ns in NS_PREFIXES}
# function_call carries the bare name plus a `namespace` field instead of a prefix.
VINO_NAMESPACES = {"gptino_v1", "vino_v1", "cordyceps", "vino", "gptino", "wireify"}

_JS_CALL_RE = re.compile(r"\btools\.([A-Za-z0-9_]+)\s*\(")
_TYPE_RE = re.compile(r'"type"\s*:\s*"([A-Za-z0-9_]+)"')
_PAYLOAD_TYPE_RE = re.compile(r'"payload"\s*:\s*\{\s*"type"\s*:\s*"([A-Za-z0-9_]+)"')
_TS_RE = re.compile(r'"timestamp"\s*:\s*"([^"]+)"')
# exec outputs open with one of three preambles; 'running' means the cell was still executing and
# the real result never made it into the transcript.
_EXEC_PREAMBLE_RE = re.compile(
    r"^Script (?:completed|failed|running[^\n]*)\n(?:Wall time [^\n]*\n)?Output:\n?", re.S)
_EXEC_KIND_RE = re.compile(r"^Script (completed|failed|running)")
_IDENT_RE = re.compile(r"^[A-Za-z_$][A-Za-z0-9_$]*$")
_UUID_RE = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")


def strip_ns(raw_name):
    """('gptino_v1__snapshot_read') -> ('snapshot_read', 'gptino_v1'); builtins -> (name, None)."""
    if not raw_name:
        return None, None
    for prefix, ns in NS_PREFIXES:
        if raw_name.startswith(prefix):
            return raw_name[len(prefix):], ns
    return raw_name, None


def version_from_date(dt):
    """Version stamp for records with no project folder to read it from (bench, dead projects)."""
    if dt is None:
        return None
    return common.ALPHA7 if dt >= common.ALPHA7_DATE else "pre-alpha7"


def trunc(text, limit):
    if text is None:
        return None
    text = text if isinstance(text, str) else str(text)
    return text[:limit]


def js_arg_text(source, open_paren_index):
    """Return the raw JS argument text between the balanced parens starting at open_paren_index."""
    depth = 0
    i = open_paren_index
    n = len(source)
    quote = None
    while i < n:
        ch = source[i]
        if quote:
            if ch == "\\":
                i += 2
                continue
            if ch == quote:
                quote = None
        elif ch in "\"'`":
            quote = ch
        elif ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
            if depth == 0:
                return source[open_paren_index + 1:i]
        i += 1
    return source[open_paren_index + 1:open_paren_index + 1 + ARGS_MAX]


def resolve_js_var(source, ident):
    """Code-mode usually builds the payload first (`const req = {...}; tools.x(req)`), so a bare
    identifier as the argument is useless on its own — follow it to its declaration."""
    m = re.search(r"(?:const|let|var)\s+%s\s*=\s*" % re.escape(ident), source)
    if not m:
        return None
    tail = source[m.end():]
    closers = {"(": ")", "[": "]", "{": "}"}
    for i, ch in enumerate(tail[:4]):
        if ch in closers:
            return ch + js_arg_text(tail, i) + closers[ch]
    return tail[:ARGS_MAX].split("\n")[0]


def scan_exec(source):
    """Scan an exec() JS body: -> (all tool names, vino tool names, first vino (name, ns, args))."""
    all_tools, vino_tools = [], []
    first = None
    for m in _JS_CALL_RE.finditer(source or ""):
        name, ns = strip_ns(m.group(1))
        all_tools.append(m.group(1))
        if ns in VINO_NS:
            vino_tools.append(name)
            if first is None:
                args = js_arg_text(source, m.end() - 1)
                if _IDENT_RE.match(args.strip()):
                    resolved = resolve_js_var(source, args.strip())
                    if resolved:
                        args = resolved
                first = (name, ns, args)
    return all_tools, vino_tools, first


def output_text(output):
    """codex outputs are either a str or a list of {type,text} blocks; claude tool_result likewise."""
    if output is None:
        return ""
    if isinstance(output, str):
        return output
    if isinstance(output, list):
        parts = []
        for block in output:
            if isinstance(block, dict):
                value = block.get("text")
                if value is None:
                    value = block.get("content")
                if isinstance(value, (dict, list)):
                    value = json.dumps(value, ensure_ascii=False, default=str)
                parts.append(value or "")
            elif isinstance(block, str):
                parts.append(block)
        return "\n".join(p for p in parts if isinstance(p, str))
    if isinstance(output, dict):
        return output.get("text") or json.dumps(output, ensure_ascii=False, default=str)
    return str(output)


def classify_error(text, call_status=None, is_exec=False, flag_is_error=None):
    """-> (is_error, error_kind, error_text). error_kind records WHICH heuristic fired, so a
    downstream analysis can drop the weak ones ('error_in_head') without re-mining."""
    text = text or ""
    body = text
    if is_exec:
        body = _EXEC_PREAMBLE_RE.sub("", text, count=1)
    head = body[:200]
    kind = None
    if flag_is_error is True:
        kind = "tool_result_is_error"
    elif "exceeds maximum allowed tokens" in text[:8000]:
        kind = "spill"
    elif is_exec and text.startswith("Script failed"):
        kind = "script_failed"
    elif '"isError":true' in text or '"isError": true' in text:
        kind = "isError"
    elif str(call_status or "").lower() == "failed":
        kind = "call_status_failed"
    elif re.search(r'"status"\s*:\s*"(failed|error)"', text[:4000], re.I):
        kind = "status_failed"
    elif body.lstrip().startswith("Error") or body.lstrip().startswith("<tool_use_error>"):
        kind = "starts_error"
    elif "Script error:" in text[:400]:
        kind = "script_error"
    elif "error" in head.lower():
        kind = "error_in_head"
    if kind is None:
        return False, None, None
    snippet = None
    if kind == "spill":
        idx = text.find("exceeds maximum allowed tokens")
        snippet = text[max(0, idx - 120):idx + 180]
    else:
        offset = 0
        for line in body.splitlines(keepends=True):
            low = line.lower()
            if "error" in low or "fail" in low:
                # take the rest of the body from that line — a bare "Script error:" header alone
                # says nothing; the exception is on the following lines.
                snippet = body[offset:offset + ERROR_MAX].strip()
                break
            offset += len(line)
    return True, kind, trunc(snippet or body[:ERROR_MAX], ERROR_MAX)


# --------------------------------------------------------------------------- project attribution
def build_project_index():
    index = {}
    for project in common.iter_projects():
        index[(project["brand"], project["project_dir"].upper())] = project
    return index


def attribute(index, cwd, fallback_dt):
    """-> (common fields dict, bench flag). Bench + unmapped cwds get project_dir/brand = null."""
    brand, pdir = common.project_dir_from_cwd(cwd)
    if brand:
        project = index.get((brand, pdir))
        if project:
            return dict(common.common_fields(project)), False
        return {
            "project_dir": pdir,
            "brand": brand,
            "project_name": None,
            "version": version_from_date(fallback_dt),
        }, False
    bench = bool(cwd) and "vino-bench" in cwd.lower()
    return {
        "project_dir": None,
        "brand": None,
        "project_name": None,
        "version": version_from_date(fallback_dt),
    }, bench


# --------------------------------------------------------------------------- accumulators
class Summary:
    def __init__(self):
        self.files_scanned = 0
        self.files_selected = 0
        self.bytes_scanned = 0
        self.by_tool = collections.Counter()          # (source, tool) -> calls
        self.errors_by_tool = collections.Counter()   # (source, tool) -> errors
        self.error_kinds = collections.Counter()
        self.calls_per_turn = collections.Counter()   # calls-in-turn -> turns
        self.encodings = collections.Counter()
        self.turn_types = collections.Counter()
        self.compactions = 0
        self.interrupted = 0
        self.aborted = 0
        self.parse_errors = 0
        self.big_lines = 0
        self.unresolved_calls = 0


# --------------------------------------------------------------------------- codex rollouts
def select_codex_files(summary, notes):
    files = sorted(glob.glob(os.path.join(common.CODEX_SESSIONS, "**", "*.jsonl"), recursive=True))
    selected = []
    no_meta = 0
    for path in files:
        summary.files_scanned += 1
        try:
            with open(path, encoding="utf-8", errors="replace") as fh:
                first = fh.readline()
            meta = json.loads(first)
        except (OSError, ValueError):
            no_meta += 1
            continue
        if meta.get("type") != "session_meta":
            no_meta += 1
            continue
        payload = meta.get("payload") or {}
        cwd = payload.get("cwd") or ""
        brand, _ = common.project_dir_from_cwd(cwd)
        if brand or "vino-bench" in cwd.lower():
            selected.append((path, meta))
    if no_meta:
        notes.append("codex: %d file(s) had no parsable session_meta first line (skipped)" % no_meta)
    summary.files_selected += len(selected)
    return selected


def codex_pass(index, tc_writer, te_writer, th_writer, summary, notes, issues):
    selected = select_codex_files(summary, notes)
    print("codex: %d/%d rollouts selected" % (len(selected), summary.files_scanned), flush=True)
    for n, (path, meta) in enumerate(selected, 1):
        if n % 25 == 0 or n == len(selected):
            print("  [%d/%d] %s" % (n, len(selected), os.path.basename(path)), flush=True)
        try:
            codex_file(path, meta, index, tc_writer, te_writer, th_writer, summary)
        except Exception as exc:  # one bad rollout must not kill the run
            issues.append("codex %s: %s: %s" % (os.path.basename(path), type(exc).__name__, exc))


def codex_file(path, meta, index, tc_writer, te_writer, th_writer, summary):
    meta_payload = meta.get("payload") or {}
    cwd = meta_payload.get("cwd") or ""
    started_at = meta.get("timestamp") or meta_payload.get("timestamp")
    fields, bench = attribute(index, cwd, common.parse_iso(started_at))
    rollout_file = os.path.basename(path)
    thread_id = meta_payload.get("id") or meta_payload.get("session_id") or rollout_file
    source_meta = meta_payload.get("source")
    thread_source = meta_payload.get("thread_source")
    is_subagent = bool(thread_source == "subagent" or (
        isinstance(source_meta, dict) and "subagent" in source_meta))
    size = os.path.getsize(path)
    summary.bytes_scanned += size

    base = dict(fields)
    base.update({"source": "codex", "thread_id": thread_id, "rollout_file": rollout_file,
                 "bench": bench})

    # Turn boundaries: codex emits task_started BEFORE the user_message of the same turn
    # (4,462 of 4,521 observed), so a naive "increment on either" doubles the turn count.
    # `just_started` lets the user_message join the turn task_started already opened.
    state = {"turn_index": 0, "turn_open": False, "just_started": False, "tool_errors": 0}
    line_count = 0
    parse_errors = 0
    tool_calls = 0
    compactions = 0
    interrupted = 0
    aborted = 0
    ended_at = started_at
    pending = {}          # call_id -> (record, is_exec, call_status)
    calls_this_turn = {}

    def emit_turn(at, ttype, detail):
        te_writer.write(dict(base, at=at, turn_index=state["turn_index"], type=ttype,
                             detail=trunc(detail, DETAIL_MAX)))
        summary.turn_types[("codex", ttype)] += 1

    def flush_call(record, out, out_at, call_status, is_exec):
        is_error, kind, err_text = classify_error(out, call_status, is_exec)
        if is_exec:
            km = _EXEC_KIND_RE.match(out or "")
            # 'running' = the cell was still executing; the real result never reached the model.
            record["result_kind"] = km.group(1) if km else None
        t0 = common.parse_iso(record.get("at"))
        t1 = common.parse_iso(out_at)
        record["result_len"] = len(out) if out is not None else None
        record["result_preview"] = trunc(out, RESULT_MAX)
        record["is_error"] = is_error
        record["error_kind"] = kind
        record["error_text"] = err_text
        record["duration_ms"] = int((t1 - t0).total_seconds() * 1000) if (t0 and t1) else None
        tc_writer.write(record)
        summary.by_tool[("codex", record["tool"])] += 1
        if is_error:
            state["tool_errors"] += 1
            summary.errors_by_tool[("codex", record["tool"])] += 1
            summary.error_kinds[kind] += 1

    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line_count += 1
            if not line.strip():
                continue
            record = None
            if len(line) > LARGE_LINE:
                summary.big_lines += 1
                head = line[:HEAD]
                tm = _TYPE_RE.search(head)
                top_type = tm.group(1) if tm else None
                pm = _PAYLOAD_TYPE_RE.search(head)
                p_type = pm.group(1) if pm else None
                tsm = _TS_RE.search(head)
                at = tsm.group(1) if tsm else ended_at
                # Only tool traffic justifies parsing a multi-MB line.
                if p_type in ("custom_tool_call", "custom_tool_call_output",
                              "function_call", "function_call_output"):
                    try:
                        record = json.loads(line)
                    except ValueError:
                        parse_errors += 1
                        continue
                else:
                    ended_at = at or ended_at
                    if top_type == "compacted":
                        compactions += 1
                        emit_turn(at, "compacted",
                                  "large compacted line (%d bytes, not parsed)" % len(line))
                    else:
                        emit_turn(at, "other:%s" % (p_type or top_type or "?"),
                                  "large line skipped (%d bytes)" % len(line))
                    continue
            else:
                try:
                    record = json.loads(line)
                except ValueError:
                    parse_errors += 1
                    continue

            at = record.get("timestamp") or ended_at
            ended_at = at or ended_at
            rtype = record.get("type")
            payload = record.get("payload")
            if not isinstance(payload, dict):
                payload = {}
            ptype = payload.get("type")

            if rtype == "event_msg":
                if ptype == "user_message":
                    if not (state["turn_open"] and state["just_started"]):
                        state["turn_index"] += 1
                        state["turn_open"] = True
                        calls_this_turn.setdefault(state["turn_index"], 0)
                    state["just_started"] = False
                    emit_turn(at, "user_message", payload.get("message"))
                elif ptype == "task_started":
                    if not state["turn_open"]:
                        state["turn_index"] += 1
                        state["turn_open"] = True
                        state["just_started"] = True
                        calls_this_turn.setdefault(state["turn_index"], 0)
                    emit_turn(at, "task_started", "turn_id=%s window=%s mode=%s" % (
                        payload.get("turn_id"), payload.get("model_context_window"),
                        payload.get("collaboration_mode_kind")))
                elif ptype == "task_complete":
                    state["turn_open"] = False
                    state["just_started"] = False
                    emit_turn(at, "task_complete", payload.get("last_agent_message"))
                elif ptype == "turn_aborted":
                    state["turn_open"] = False
                    state["just_started"] = False
                    reason = payload.get("reason")
                    if reason == "interrupted":
                        interrupted += 1
                        emit_turn(at, "interrupted", "turn_id=%s duration_ms=%s" % (
                            payload.get("turn_id"), payload.get("duration_ms")))
                    else:
                        aborted += 1
                        emit_turn(at, "turn_aborted", "reason=%s turn_id=%s duration_ms=%s" % (
                            reason, payload.get("turn_id"), payload.get("duration_ms")))
                elif ptype == "context_compacted":
                    # Same event as the top-level `compacted` record (1088 == 1088 corpus-wide);
                    # only the latter is counted so `compactions` is not doubled.
                    emit_turn(at, "context_compacted", None)
                elif ptype == "token_count":
                    info = payload.get("info") or {}
                    total = info.get("total_token_usage") or {}
                    last = info.get("last_token_usage") or {}
                    emit_turn(at, "token_count",
                              "total=%s input=%s cached=%s output=%s reasoning=%s "
                              "last_total=%s window=%s" % (
                                  total.get("total_tokens"), total.get("input_tokens"),
                                  total.get("cached_input_tokens"), total.get("output_tokens"),
                                  total.get("reasoning_output_tokens"), last.get("total_tokens"),
                                  info.get("model_context_window")))
                elif ptype == "thread_settings_applied":
                    st = payload.get("thread_settings") or {}
                    effort = st.get("reasoning_effort") or st.get("effort")
                    emit_turn(at, "thread_settings_applied",
                              "model=%s effort=%s approval=%s cwd=%s" % (
                                  st.get("model"), effort, st.get("approval_policy"),
                                  st.get("cwd")))
                elif ptype == "sub_agent_activity":
                    emit_turn(at, "sub_agent_activity", "%s %s thread=%s" % (
                        payload.get("agent_path"), payload.get("kind"),
                        payload.get("agent_thread_id")))
                elif ptype in ("error", "stream_error"):
                    emit_turn(at, "error", payload.get("message") or json.dumps(
                        payload, ensure_ascii=False, default=str))
                else:
                    emit_turn(at, "other:%s" % ptype,
                              json.dumps(payload, ensure_ascii=False, default=str))
                continue

            if rtype == "turn_context":
                emit_turn(at, "turn_context",
                          "model=%s effort=%s summary=%s cwd=%s approval=%s" % (
                              payload.get("model"),
                              payload.get("effort") or payload.get("reasoning_effort"),
                              payload.get("summary"), payload.get("cwd"),
                              payload.get("approval_policy")))
                continue
            if rtype == "session_meta":
                emit_turn(at, "session_meta", "cli_version=%s originator=%s source=%s cwd=%s" % (
                    payload.get("cli_version"), payload.get("originator"),
                    json.dumps(payload.get("source"), ensure_ascii=False, default=str),
                    payload.get("cwd")))
                continue
            if rtype == "compacted":
                compactions += 1
                hist = payload.get("replacement_history")
                emit_turn(at, "compacted", "replacement_history=%s message=%s" % (
                    len(hist) if isinstance(hist, list) else None, payload.get("message")))
                continue
            if rtype == "inter_agent_communication_metadata":
                emit_turn(at, "other:inter_agent_communication_metadata",
                          json.dumps(payload, ensure_ascii=False, default=str))
                continue
            if rtype == "world_state":
                continue  # bulky environment echo; nothing per-turn to learn
            if rtype != "response_item":
                emit_turn(at, "other:%s" % rtype,
                          json.dumps(payload, ensure_ascii=False, default=str)[:DETAIL_MAX])
                continue

            # ---- response_item: the tool traffic
            if ptype == "custom_tool_call":
                name = payload.get("name")
                call_id = payload.get("call_id") or payload.get("id")
                js = payload.get("input") or ""
                if name == "exec":
                    all_tools, vino_tools, first = scan_exec(js)
                    if first:
                        tool, ns, arg_text = first
                        summary.encodings["exec_js:tools.%s__*" % ns] += 1
                    else:
                        tool, ns, arg_text = "exec(other)", None, None
                        summary.encodings["exec_js:no-vino-call"] += 1
                    rec = dict(base, turn_index=state["turn_index"], call_id=call_id, at=at,
                               tool=tool, tool_ns=ns, tools_in_exec=vino_tools,
                               other_tools_in_exec=all_tools,
                               args_preview=trunc(arg_text, ARGS_MAX),
                               args_len=len(arg_text) if arg_text is not None else None,
                               code_preview=trunc(js, CODE_MAX), code_len=len(js),
                               encoding="exec_js", result_kind=None, sub_agent=is_subagent)
                else:
                    tool, ns = strip_ns(name)
                    summary.encodings["custom_tool_call:%s" % (ns or "builtin")] += 1
                    rec = dict(base, turn_index=state["turn_index"], call_id=call_id, at=at,
                               tool=tool, tool_ns=ns, tools_in_exec=[], other_tools_in_exec=[],
                               args_preview=trunc(js, ARGS_MAX), args_len=len(js),
                               code_preview=trunc(js, CODE_MAX), code_len=len(js),
                               encoding="custom_tool_call", sub_agent=is_subagent)
                pending[call_id] = (rec, name == "exec", payload.get("status"))
                tool_calls += 1
                calls_this_turn[state["turn_index"]] = \
                    calls_this_turn.get(state["turn_index"], 0) + 1
                continue

            if ptype == "function_call":
                name = payload.get("name")
                ns_field = payload.get("namespace")
                tool, ns = strip_ns(name)
                if ns is None and ns_field in VINO_NAMESPACES:
                    ns = ns_field
                args = payload.get("arguments")
                if not isinstance(args, str):
                    args = json.dumps(args, ensure_ascii=False, default=str) if args else ""
                call_id = payload.get("call_id") or payload.get("id")
                summary.encodings["function_call:%s" % (ns_field or ns or "builtin")] += 1
                rec = dict(base, turn_index=state["turn_index"], call_id=call_id, at=at, tool=tool,
                           tool_ns=ns, tools_in_exec=[], other_tools_in_exec=[],
                           args_preview=trunc(args, ARGS_MAX), args_len=len(args),
                           code_preview=None, code_len=None, encoding="function_call",
                           sub_agent=is_subagent)
                pending[call_id] = (rec, False, payload.get("status"))
                tool_calls += 1
                calls_this_turn[state["turn_index"]] = \
                    calls_this_turn.get(state["turn_index"], 0) + 1
                continue

            if ptype in ("custom_tool_call_output", "function_call_output"):
                entry = pending.pop(payload.get("call_id"), None)
                if entry is None:
                    continue  # output whose call was compacted out of the file
                rec, is_exec, call_status = entry
                flush_call(rec, output_text(payload.get("output")), at,
                           payload.get("status") or call_status, is_exec)
                continue

    for rec, is_exec, call_status in pending.values():
        summary.unresolved_calls += 1
        rec.update({"result_len": None, "result_preview": None, "is_error": None,
                    "error_kind": "no_output", "error_text": None, "duration_ms": None})
        tc_writer.write(rec)
        summary.by_tool[("codex", rec["tool"])] += 1

    for count in calls_this_turn.values():
        summary.calls_per_turn[count] += 1
    summary.compactions += compactions
    summary.interrupted += interrupted
    summary.aborted += aborted
    summary.parse_errors += parse_errors

    th_writer.write(dict(
        fields, source="codex", thread_id=thread_id, rollout_file=rollout_file, bench=bench,
        version_by_date=version_from_date(common.parse_iso(started_at)),
        cwd=cwd, cli_version=meta_payload.get("cli_version"),
        originator=meta_payload.get("originator"), session_id=meta_payload.get("session_id"),
        parent_thread_id=meta_payload.get("parent_thread_id"),
        forked_from_id=meta_payload.get("forked_from_id"), thread_source=thread_source,
        sub_agent=is_subagent, started_at=started_at, ended_at=ended_at, line_count=line_count,
        bytes=size, turns=state["turn_index"], tool_calls=tool_calls,
        tool_errors=state["tool_errors"], compactions=compactions, interrupted=interrupted,
        aborted=aborted, parse_errors=parse_errors))


# --------------------------------------------------------------------------- claude transcripts
def claude_files():
    roots = []
    for pattern in ("*Vino-projects-*", "*GPTino-projects-*"):
        roots.extend(glob.glob(os.path.join(common.CLAUDE_PROJECTS, pattern)))
    out = []
    for root in sorted(set(roots)):
        out.extend(sorted(glob.glob(os.path.join(root, "*.jsonl"))))
    return out


def claude_pass(index, tc_writer, te_writer, th_writer, summary, notes, issues):
    files = claude_files()
    print("claude: %d transcript(s)" % len(files), flush=True)
    notes.append("claude: %d transcript(s) matched *{Vino,GPTino}-projects-*" % len(files))
    for path in files:
        summary.files_scanned += 1
        summary.files_selected += 1
        try:
            claude_file(path, index, tc_writer, te_writer, th_writer, summary)
        except Exception as exc:
            issues.append("claude %s: %s: %s" % (os.path.basename(path), type(exc).__name__, exc))


def claude_file(path, index, tc_writer, te_writer, th_writer, summary):
    rollout_file = os.path.basename(path)
    m = _UUID_RE.search(rollout_file)
    thread_id = m.group(0) if m else os.path.splitext(rollout_file)[0]
    size = os.path.getsize(path)
    summary.bytes_scanned += size

    # cwd is repeated on every record; take the first one for attribution.
    cwd = ""
    first_ts = None
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if '"cwd"' not in line:
                continue
            try:
                rec = json.loads(line)
            except ValueError:
                continue
            cwd = rec.get("cwd") or ""
            first_ts = rec.get("timestamp")
            if cwd:
                break
    fields, bench = attribute(index, cwd, common.parse_iso(first_ts))
    base = dict(fields)
    base.update({"source": "claude", "thread_id": thread_id, "rollout_file": rollout_file,
                 "bench": bench})

    state = {"turn_index": 0, "tool_errors": 0}
    line_count = 0
    parse_errors = 0
    tool_calls = 0
    compactions = 0
    started_at = None
    ended_at = None
    cli_version = None
    pending = {}
    calls_this_turn = {}

    def emit_turn(at, ttype, detail):
        te_writer.write(dict(base, at=at, turn_index=state["turn_index"], type=ttype,
                             detail=trunc(detail, DETAIL_MAX)))
        summary.turn_types[("claude", ttype)] += 1

    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line_count += 1
            if not line.strip():
                continue
            if len(line) > LARGE_LINE and '"tool_result"' not in line[:HEAD] \
                    and '"tool_use"' not in line[:HEAD]:
                summary.big_lines += 1
                emit_turn(ended_at, "other:large_line", "skipped (%d bytes)" % len(line))
                continue
            try:
                rec = json.loads(line)
            except ValueError:
                parse_errors += 1
                continue
            at = rec.get("timestamp")
            if at:
                started_at = started_at or at
                ended_at = at
            cli_version = rec.get("version") or cli_version
            rtype = rec.get("type")
            message = rec.get("message")
            message = message if isinstance(message, dict) else {}
            content = message.get("content")
            if isinstance(content, str):
                content = [{"type": "text", "text": content}]
            if not isinstance(content, list):
                content = []

            if rec.get("isApiErrorMessage") or rec.get("error"):
                text = "".join(b.get("text") or "" for b in content
                               if isinstance(b, dict) and b.get("type") == "text")
                emit_turn(at, "error", text or json.dumps(rec.get("error"), ensure_ascii=False,
                                                          default=str))

            if rtype == "user":
                has_tool_result = any(isinstance(b, dict) and b.get("type") == "tool_result"
                                      for b in content)
                if not has_tool_result and not rec.get("isMeta"):
                    text = "".join(b.get("text") or "" for b in content
                                   if isinstance(b, dict) and b.get("type") == "text")
                    if text.strip():
                        state["turn_index"] += 1
                        calls_this_turn.setdefault(state["turn_index"], 0)
                        emit_turn(at, "user_message", text)
            elif rtype == "attachment":
                att = rec.get("attachment") or {}
                akind = att.get("type") if isinstance(att, dict) else None
                if akind == "compact_boundary":
                    compactions += 1
                if akind and akind != "total_tokens_reminder":
                    emit_turn(at, "other:attachment:%s" % akind,
                              json.dumps(att, ensure_ascii=False, default=str))
            elif rtype == "summary":
                compactions += 1
                emit_turn(at, "compacted", json.dumps(rec, ensure_ascii=False, default=str))
            elif rtype != "assistant":
                emit_turn(at, "other:%s" % rtype, json.dumps(
                    {k: v for k, v in rec.items() if k != "message"},
                    ensure_ascii=False, default=str))

            for block in content:
                if not isinstance(block, dict):
                    continue
                btype = block.get("type")
                if btype == "tool_use":
                    tool, ns = strip_ns(block.get("name"))
                    summary.encodings["claude_tool_use:%s" % (ns or "builtin")] += 1
                    args = json.dumps(block.get("input"), ensure_ascii=False, default=str)
                    code = None
                    inp = block.get("input")
                    if isinstance(inp, dict):
                        code = inp.get("command") or inp.get("code") or inp.get("script")
                        if not isinstance(code, str):
                            code = None
                    call_rec = dict(base, turn_index=state["turn_index"], call_id=block.get("id"),
                                    at=at, tool=tool, tool_ns=ns, tools_in_exec=[],
                                    other_tools_in_exec=[], args_preview=trunc(args, ARGS_MAX),
                                    args_len=len(args), code_preview=trunc(code, CODE_MAX),
                                    code_len=len(code) if code else None,
                                    encoding="claude_tool_use",
                                    sub_agent=bool(rec.get("isSidechain")))
                    pending[block.get("id")] = call_rec
                    tool_calls += 1
                    calls_this_turn[state["turn_index"]] = \
                        calls_this_turn.get(state["turn_index"], 0) + 1
                elif btype == "tool_result":
                    call_rec = pending.pop(block.get("tool_use_id"), None)
                    out = output_text(block.get("content"))
                    if call_rec is None:
                        continue
                    is_error, kind, err_text = classify_error(
                        out, flag_is_error=block.get("is_error") is True)
                    t0 = common.parse_iso(call_rec.get("at"))
                    t1 = common.parse_iso(at)
                    call_rec.update({
                        "result_len": len(out), "result_preview": trunc(out, RESULT_MAX),
                        "is_error": is_error, "error_kind": kind, "error_text": err_text,
                        "duration_ms": int((t1 - t0).total_seconds() * 1000) if (t0 and t1)
                        else None})
                    tc_writer.write(call_rec)
                    summary.by_tool[("claude", call_rec["tool"])] += 1
                    if is_error:
                        state["tool_errors"] += 1
                        summary.errors_by_tool[("claude", call_rec["tool"])] += 1
                        summary.error_kinds[kind] += 1

    for call_rec in pending.values():
        summary.unresolved_calls += 1
        call_rec.update({"result_len": None, "result_preview": None, "is_error": None,
                         "error_kind": "no_output", "error_text": None, "duration_ms": None})
        tc_writer.write(call_rec)
        summary.by_tool[("claude", call_rec["tool"])] += 1

    for count in calls_this_turn.values():
        summary.calls_per_turn[count] += 1
    summary.compactions += compactions
    summary.parse_errors += parse_errors
    th_writer.write(dict(
        fields, source="claude", thread_id=thread_id, rollout_file=rollout_file, bench=bench,
        version_by_date=version_from_date(common.parse_iso(started_at)),
        cwd=cwd, cli_version=cli_version, originator="claude-code", session_id=thread_id,
        parent_thread_id=None, forked_from_id=None, thread_source=None, sub_agent=False,
        started_at=started_at, ended_at=ended_at, line_count=line_count, bytes=size,
        turns=state["turn_index"], tool_calls=tool_calls, tool_errors=state["tool_errors"],
        compactions=compactions, interrupted=0, aborted=0, parse_errors=parse_errors))


# --------------------------------------------------------------------------- main
def main():
    watermark = common.capture_watermark()   # before the first source file is opened
    t0 = time.time()
    notes, issues = [], []
    index = build_project_index()
    notes.append("project index: %d project folders" % len(index))
    summary = Summary()

    tc = common.JsonlWriter("tool-calls.jsonl")
    te = common.JsonlWriter("turn-events.jsonl")
    th = common.JsonlWriter("threads.jsonl")
    try:
        codex_pass(index, tc, te, th, summary, notes, issues)
        claude_pass(index, tc, te, th, summary, notes, issues)
    finally:
        counts = (tc.close(), te.close(), th.close())

    by_tool = {}
    for (src, tool), n in summary.by_tool.items():
        errs = summary.errors_by_tool.get((src, tool), 0)
        by_tool.setdefault(src, {})[tool] = {
            "calls": n, "errors": errs, "error_rate": round(errs / n, 4) if n else None}
    out = {
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "capture_watermark_utc": watermark,
        "run_seconds": round(time.time() - t0, 1),
        "files_scanned": summary.files_scanned,
        "files_selected": summary.files_selected,
        "bytes_scanned": summary.bytes_scanned,
        "records": {"tool_calls": counts[0], "turn_events": counts[1], "threads": counts[2]},
        "tool_calls_by_tool": {src: dict(sorted(d.items(), key=lambda kv: -kv[1]["calls"]))
                               for src, d in by_tool.items()},
        "error_kinds": dict(summary.error_kinds.most_common()),
        "calls_per_turn": {str(k): v for k, v in sorted(summary.calls_per_turn.items())},
        "turn_event_types": {"%s/%s" % k: v for k, v in summary.turn_types.most_common()},
        "vino_call_encodings": dict(summary.encodings.most_common()),
        "compactions": summary.compactions,
        "interrupted": summary.interrupted,
        "turn_aborted": summary.aborted,
        "parse_errors": summary.parse_errors,
        "large_lines_skipped": summary.big_lines,
        "calls_without_output": summary.unresolved_calls,
        "notes": notes,
        "issues": issues,
    }
    with open(os.path.join(common.ensure_out(), "rollouts-summary.json"), "w",
              encoding="utf-8", newline="\n") as fh:
        json.dump(out, fh, ensure_ascii=False, indent=2)
    print(json.dumps({k: out[k] for k in ("run_seconds", "files_selected", "records",
                                          "parse_errors", "issues")},
                     ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
