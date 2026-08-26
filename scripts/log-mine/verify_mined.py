# Independent QA of the Stage 0 mined outputs (docs/log-review-2026-08-26/plan.md).
# Reads .log-mine/*.jsonl + stats/* and re-derives every headline from the RAW sources
# (live-jobs.db, problem-log.jsonl, runtime.db, codex rollouts, claude transcripts).
# Writes nothing except .log-mine/verify-report.json; never touches the extractors or sources.
import collections
import glob
import json
import os
import random
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common  # noqa: E402

OUT = common.OUT_ROOT
STATS = os.path.join(OUT, "stats")
CHECKS = []
BLOCKING = []
FIXES = []
T0 = time.time()


def check(cid, status, measured, expected, note=""):
    CHECKS.append({"id": cid, "status": status, "measured": measured,
                   "expected": expected, "note": note})


def pct(n, d):
    return round(100.0 * n / d, 1) if d else 0.0


def count_lines(path):
    n = 0
    with open(path, "rb") as fh:
        for _ in fh:
            n += 1
    return n


# --------------------------------------------------------------------- 1. record counts
FILES = ["jobs.jsonl", "problem-events.jsonl", "messages.jsonl", "sessions.jsonl",
         "threads.jsonl", "tool-calls.jsonl", "turn-events.jsonl", "hostlog-events.jsonl"]
counts = {}
for f in FILES:
    p = os.path.join(OUT, f)
    counts[f] = count_lines(p) if os.path.exists(p) else None

EXPECT = {"jobs.jsonl": 2655, "problem-events.jsonl": 16454, "messages.jsonl": 1362,
          "sessions.jsonl": 81}
for f, exp in EXPECT.items():
    got = counts[f]
    if got is None:
        check("1." + f, "FAIL", None, exp, "file missing")
        BLOCKING.append("%s missing" % f)
        continue
    delta = got - exp
    status = "PASS" if abs(delta) <= max(3, exp * 0.01) else "FAIL"
    check("1." + f, status, got, "~%d" % exp,
          "delta %+d (%.2f%%)" % (delta, 100.0 * delta / exp))


# --------------------------------------------------------------------- raw re-count of jobs
raw_jobs = 0
raw_states = collections.Counter()
raw_job_ids = set()
projects = list(common.iter_projects())
proj_by_dir = {p["project_dir"]: p for p in projects}
proj_with_db = 0
proj_with_rows = 0
raw_jobs_by_project = collections.Counter()
for p in projects:
    db = os.path.join(p["path"], "live-jobs.db")
    if not os.path.exists(db):
        continue
    proj_with_db += 1
    try:
        conn = common.open_sqlite_readonly(db)
        if not common.table_exists(conn, "live_jobs"):
            conn.close()
            continue
        rows = conn.execute("select job_id, state from live_jobs").fetchall()
        conn.close()
    except Exception:
        continue
    if rows:
        proj_with_rows += 1
    for jid, st in rows:
        raw_jobs += 1
        raw_job_ids.add(jid)
        raw_states[common.normalize_state(st)] += 1
        raw_jobs_by_project[p["project_dir"]] += 1

check("1.jobs-vs-raw", "PASS" if counts["jobs.jsonl"] == raw_jobs else "FAIL",
      {"jsonl": counts["jobs.jsonl"], "raw_live_jobs_rows": raw_jobs,
       "raw_states": dict(raw_states.most_common())},
      "equal", "jobs.jsonl must equal the raw row count exactly")

# raw problem-log re-count
raw_problem = 0
raw_kinds = collections.Counter()
raw_problem_files = 0
for p in projects:
    pl = os.path.join(p["path"], "problem-log.jsonl")
    if not os.path.exists(pl):
        continue
    raw_problem_files += 1
    with open(pl, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if not line.strip():
                continue
            raw_problem += 1
            try:
                raw_kinds[json.loads(line).get("kind")] += 1
            except ValueError:
                raw_kinds["<unparsable>"] += 1
check("1.problem-vs-raw",
      "PASS" if counts["problem-events.jsonl"] == raw_problem
      else ("WARN" if raw_problem > counts["problem-events.jsonl"] else "FAIL"),
      {"jsonl": counts["problem-events.jsonl"], "raw_lines": raw_problem,
       "raw_files": raw_problem_files, "raw_kinds": dict(raw_kinds.most_common()),
       "delta": raw_problem - counts["problem-events.jsonl"]}, "equal",
      "raw > mined is adjudicated by check 7 (live append); mined > raw would be fabrication")

# raw runtime.db re-count
raw_sessions = 0
raw_messages = 0
for p in projects:
    db = os.path.join(p["path"], "runtime.db")
    if not os.path.exists(db):
        continue
    try:
        conn = common.open_sqlite_readonly(db)
        if common.table_exists(conn, "sessions"):
            raw_sessions += conn.execute("select count(*) from sessions").fetchone()[0]
        if common.table_exists(conn, "messages"):
            raw_messages += conn.execute("select count(*) from messages").fetchone()[0]
        conn.close()
    except Exception:
        pass
check("1.sessions-vs-raw",
      "PASS" if counts["sessions.jsonl"] == raw_sessions
      else ("WARN" if raw_sessions > counts["sessions.jsonl"] else "FAIL"),
      {"jsonl": counts["sessions.jsonl"], "raw_rows": raw_sessions,
       "delta": raw_sessions - counts["sessions.jsonl"]}, "equal", "see check 7")
check("1.messages-vs-raw",
      "PASS" if counts["messages.jsonl"] == raw_messages
      else ("WARN" if raw_messages > counts["messages.jsonl"] else "FAIL"),
      {"jsonl": counts["messages.jsonl"], "raw_rows": raw_messages,
       "delta": raw_messages - counts["messages.jsonl"]}, "equal", "see check 7")

# threads: codex selected vs bench
th_src = collections.Counter()
th_bench = collections.Counter()
threads = {}
for r in common.read_jsonl(os.path.join(OUT, "threads.jsonl")):
    th_src[r["source"]] += 1
    if r["source"] == "codex":
        th_bench[bool(r.get("bench"))] += 1
    threads[r["thread_id"]] = r
codex_project = th_bench[False]
codex_bench = th_bench[True]
ok = abs(codex_project - 237) <= 5 and abs(codex_bench - 24) <= 5
check("1.threads", "PASS" if ok else "WARN",
      {"codex_project_cwd": codex_project, "codex_bench": codex_bench,
       "claude": th_src.get("claude", 0), "total": sum(th_src.values())},
      {"codex_project": "~237", "codex_bench": "~24", "claude": 2})


# --------------------------------------------------------------------- stream tool-calls once
tc_total = 0
tc_by_source = collections.Counter()
tc_null = collections.Counter()
tc_dur_null = collections.Counter()
tc_reslen_null = collections.Counter()
tc_err = collections.Counter()
tc_errkind = collections.Counter()
tc_change_submit_by_project = collections.Counter()
tc_by_project = collections.Counter()
tc_spill = []
tc_sample_pool = []
tc_bench = 0
tc_claude_by_file = collections.Counter()
rng = random.Random(20260826)
for r in common.read_jsonl(os.path.join(OUT, "tool-calls.jsonl")):
    tc_total += 1
    s = r["source"]
    tc_by_source[s] += 1
    if r.get("bench"):
        tc_bench += 1
    for k in ("project_dir", "brand", "version"):
        if r.get(k) is None:
            tc_null[k] += 1
    if r.get("duration_ms") is None:
        tc_dur_null[s] += 1
    if r.get("result_len") is None:
        tc_reslen_null[s] += 1
    if r.get("is_error"):
        tc_err[s] += 1
        tc_errkind[r.get("error_kind")] += 1
    if r.get("error_kind") == "spill":
        tc_spill.append({k: r.get(k) for k in ("project_dir", "at", "tool", "thread_id",
                                               "source", "result_len")})
    if s == "claude":
        tc_claude_by_file[r.get("rollout_file")] += 1
    if r.get("tool") == "change_submit" and r.get("project_dir"):
        tc_change_submit_by_project[r["project_dir"]] += 1
    if r.get("project_dir"):
        tc_by_project[r["project_dir"]] += 1
    if len(tc_sample_pool) < 40:
        tc_sample_pool.append(r)
    elif rng.random() < 40.0 / tc_total:
        tc_sample_pool[rng.randrange(40)] = r

claude_ok = 60 <= tc_by_source["claude"] <= 110 and len(tc_claude_by_file) == 2
check("1.tool-calls",
      "PASS" if tc_by_source["codex"] > 0 and claude_ok else "WARN",
      {"total": tc_total, "codex": tc_by_source["codex"], "claude": tc_by_source["claude"],
       "claude_files": dict(tc_claude_by_file), "bench_rows": tc_bench},
      {"codex": ">0", "claude": "~70 across 2 files"})


# --------------------------------------------------------------------- 2. field completeness
def completeness(fname, extra=None):
    n = 0
    nn = collections.Counter()
    ex = collections.Counter()
    for r in common.read_jsonl(os.path.join(OUT, fname)):
        n += 1
        for k in ("project_dir", "brand", "version"):
            if r.get(k) is not None:
                nn[k] += 1
        if extra:
            for label, fn in extra.items():
                if fn(r):
                    ex[label] += 1
    return n, nn, ex


jobs_n, jobs_nn, jobs_ex = completeness("jobs.jsonl", {
    "op_kinds_nonempty": lambda r: bool(r.get("op_kinds")),
    "payload_dir_resolved": lambda r: bool(r.get("payload_dir")),
    "payload_ops_nonempty": lambda r: bool(r.get("payload_ops")),
    "summary_present": lambda r: bool(r.get("summary")),
    "state_lower": lambda r: r.get("state") == (r.get("state") or "").lower(),
})
check("2.jobs.common-fields",
      "PASS" if all(jobs_nn[k] == jobs_n for k in ("project_dir", "brand", "version")) else "FAIL",
      {k: "%.1f%%" % pct(jobs_nn[k], jobs_n) for k in ("project_dir", "brand", "version")}, "100%")
check("2.jobs.op_kinds",
      "PASS" if jobs_ex["op_kinds_nonempty"] >= jobs_n * 0.98 else "WARN",
      {"non_empty": jobs_ex["op_kinds_nonempty"], "pct": pct(jobs_ex["op_kinds_nonempty"], jobs_n),
       "empty": jobs_n - jobs_ex["op_kinds_nonempty"]},
      "~100%")
pd_pct = pct(jobs_ex["payload_dir_resolved"], jobs_n)
check("2.jobs.payload_dir", "PASS" if pd_pct >= 50 else "WARN",
      {"resolved": jobs_ex["payload_dir_resolved"], "pct": pd_pct,
       "payload_ops_nonempty_pct": pct(jobs_ex["payload_ops_nonempty"], jobs_n)},
      "majority (>50%)")
check("2.jobs.state-normalized",
      "PASS" if jobs_ex["state_lower"] == jobs_n else "FAIL",
      {"lowercase": jobs_ex["state_lower"], "of": jobs_n}, "100% lowercase")

pe_n, pe_nn, _ = completeness("problem-events.jsonl")
check("2.problem.common-fields",
      "PASS" if all(pe_nn[k] == pe_n for k in ("project_dir", "brand", "version")) else "FAIL",
      {k: "%.1f%%" % pct(pe_nn[k], pe_n) for k in ("project_dir", "brand", "version")}, "100%")

ms_n, ms_nn, ms_ex = completeness("messages.jsonl", {
    "content_present": lambda r: r.get("content") is not None,
    "backend": lambda r: bool(r.get("backend_norm") or r.get("backend")),
})
check("2.messages.common-fields",
      "PASS" if all(ms_nn[k] == ms_n for k in ("project_dir", "brand", "version")) else "FAIL",
      {k: "%.1f%%" % pct(ms_nn[k], ms_n) for k in ("project_dir", "brand", "version")},
      "100%")
check("2.messages.content", "PASS" if ms_ex["content_present"] >= ms_n * 0.99 else "WARN",
      {"content_non_null_pct": pct(ms_ex["content_present"], ms_n),
       "backend_pct": pct(ms_ex["backend"], ms_n)}, ">99%")

se_n, se_nn, _ = completeness("sessions.jsonl")
check("2.sessions.common-fields",
      "PASS" if all(se_nn[k] == se_n for k in ("project_dir", "brand", "version")) else "FAIL",
      {k: "%.1f%%" % pct(se_nn[k], se_n) for k in ("project_dir", "brand", "version")}, "100%")

hl_n, hl_nn, _ = completeness("hostlog-events.jsonl")
check("2.hostlog.common-fields",
      "PASS" if all(hl_nn[k] == hl_n for k in ("project_dir", "brand", "version")) else "FAIL",
      dict({k: "%.1f%%" % pct(hl_nn[k], hl_n) for k in ("project_dir", "brand", "version")},
           records=hl_n), "100%")

tc_pd_pct = pct(tc_total - tc_null["project_dir"], tc_total)
check("2.tool-calls.common-fields", "PASS" if tc_null["version"] == 0 else "FAIL",
      {"project_dir_non_null_pct": tc_pd_pct,
       "brand_non_null_pct": pct(tc_total - tc_null["brand"], tc_total),
       "version_non_null_pct": pct(tc_total - tc_null["version"], tc_total),
       "null_project_dir": tc_null["project_dir"], "bench_rows": tc_bench},
      "version 100%; project_dir null only for bench/unmapped cwd",
      "plan allows project_dir=brand=null when unattributable")

dur_ok = pct(tc_total - sum(tc_dur_null.values()), tc_total)
res_ok = pct(tc_total - sum(tc_reslen_null.values()), tc_total)
check("2.tool-calls.duration_ms", "PASS" if dur_ok >= 95 else "WARN",
      {"non_null_pct": dur_ok, "null_by_source": dict(tc_dur_null)}, ">95%")
check("2.tool-calls.result_len", "PASS" if res_ok >= 99 else "WARN",
      {"non_null_pct": res_ok, "null_by_source": dict(tc_reslen_null)}, ">99%")
check("2.tool-calls.pairing",
      "PASS" if tc_errkind.get("no_output", 0) == 0 else "WARN",
      {"unpaired_no_output": tc_errkind.get("no_output", 0),
       "paired_pct": pct(tc_total - tc_errkind.get("no_output", 0), tc_total),
       "error_kinds": dict(tc_errkind.most_common())},
      "100% of codex calls paired to an output")


# --------------------------------------------------------------------- 3. spot checks
spot = []


def spot_jobs(k=5):
    rows = []
    allr = [json.loads(line) for line in
            open(os.path.join(OUT, "jobs.jsonl"), encoding="utf-8") if line.strip()]
    r2 = random.Random(7)
    for r in r2.sample(allr, k):
        p = proj_by_dir.get(r["project_dir"])
        res = {"file": "jobs.jsonl", "job_id": r["job_id"], "project_dir": r["project_dir"]}
        if not p:
            res["verdict"] = "FAIL"
            res["why"] = "project folder not found"
            rows.append(res)
            continue
        conn = common.open_sqlite_readonly(os.path.join(p["path"], "live-jobs.db"))
        cols = common.table_columns(conn, "live_jobs")
        row = conn.execute("select * from live_jobs where job_id=?", (r["job_id"],)).fetchone()
        conn.close()
        if row is None:
            res["verdict"] = "FAIL"
            res["why"] = "job_id absent from raw db"
            rows.append(res)
            continue
        raw = dict(zip(cols, row))
        mism = []
        if common.normalize_state(raw["state"]) != r["state"]:
            mism.append("state %r vs %r" % (raw["state"], r["state"]))
        for f in ("summary", "message", "phase", "idempotency_key", "created_at",
                  "updated_at", "request_hash", "target_doc", "session_id"):
            if (raw.get(f) or None) != (r.get(f) or None):
                mism.append("%s raw=%r mined=%r" % (f, str(raw.get(f))[:60], str(r.get(f))[:60]))
        try:
            cs = json.loads(raw["change_set_json"] or "{}")
            ops = cs.get("operations") or []
            raw_kinds = [o.get("kind") for o in ops]
        except ValueError:
            raw_kinds = None
        if raw_kinds is not None and sorted(set(raw_kinds)) != sorted(set(r.get("op_kinds") or [])):
            mism.append("op_kinds raw=%s mined=%s" % (sorted(set(raw_kinds)), r.get("op_kinds")))
        res["verdict"] = "PASS" if not mism else "FAIL"
        res["fields_compared"] = 11
        res["state"] = r["state"]
        if mism:
            res["mismatches"] = mism[:4]
        rows.append(res)
    return rows


def spot_problem(k=5):
    rows = []
    mined = []
    with open(os.path.join(OUT, "problem-events.jsonl"), encoding="utf-8") as fh:
        for i, line in enumerate(fh):
            if i % 97 == 0:
                mined.append(json.loads(line))
    r2 = random.Random(11)
    for r in r2.sample(mined, k):
        p = proj_by_dir.get(r["project_dir"])
        res = {"file": "problem-events.jsonl", "kind": r.get("kind"), "at": r.get("at"),
               "project_dir": r["project_dir"], "jobId": r.get("jobId")}
        found = None
        with open(os.path.join(p["path"], "problem-log.jsonl"), encoding="utf-8",
                  errors="replace") as fh:
            for line in fh:
                if r.get("at") and r["at"] not in line:
                    continue
                try:
                    cand = json.loads(line)
                except ValueError:
                    continue
                if cand.get("at") == r["at"] and cand.get("kind") == r.get("kind"):
                    found = cand
                    break
        if found is None:
            res["verdict"] = "FAIL"
            res["why"] = "no raw line with that at+kind"
        else:
            # `state` is lower-cased by contract (plan.md: "상태는 소문자"), compare case-folded
            mism = []
            for k2, v in found.items():
                if k2 in ("project_dir", "brand", "project_name", "version"):
                    continue
                mv = r.get(k2)
                if k2 == "state" and isinstance(v, str) and isinstance(mv, str):
                    if v.lower() != mv:
                        mism.append("state raw=%r mined=%r" % (v, mv))
                elif mv != v:
                    mism.append(k2)
            res["verdict"] = "PASS" if not mism else "FAIL"
            res["raw_fields"] = len(found)
            if mism:
                res["mismatches"] = mism[:5]
        rows.append(res)
    return rows


def spot_messages(k=5):
    rows = []
    mined = [json.loads(line) for line in
             open(os.path.join(OUT, "messages.jsonl"), encoding="utf-8") if line.strip()]
    r2 = random.Random(13)
    for r in r2.sample(mined, k):
        p = proj_by_dir.get(r["project_dir"])
        res = {"file": "messages.jsonl", "session_id": r["session_id"], "msg_id": r["msg_id"]}
        conn = common.open_sqlite_readonly(os.path.join(p["path"], "runtime.db"))
        row = conn.execute(
            "select role, content, phase, created_at, session_id from messages "
            "where id=? and session_id=?", (r["msg_id"], r["session_id"])).fetchone()
        conn.close()
        if row is None:
            res["verdict"] = "FAIL"
            res["why"] = "no raw message row with that id+session"
        else:
            mism = []
            if row[0] != r["role"]:
                mism.append("role")
            if (row[1] or "") != (r.get("content") or ""):
                mism.append("content(len raw=%d mined=%d)" % (len(row[1] or ""),
                                                              len(r.get("content") or "")))
            if (row[2] or None) != (r.get("phase") or None):
                mism.append("phase raw=%r mined=%r" % (row[2], r.get("phase")))
            if row[3] != r["created_at"]:
                mism.append("created_at")
            if len(row[1] or "") != (r.get("content_len") or 0):
                mism.append("content_len")
            res["verdict"] = "PASS" if not mism else "FAIL"
            res["role"] = r["role"]
            res["content_len"] = r.get("content_len")
            if mism:
                res["mismatches"] = mism
        rows.append(res)
    return rows


def spot_sessions(k=5):
    rows = []
    mined = [json.loads(line) for line in
             open(os.path.join(OUT, "sessions.jsonl"), encoding="utf-8") if line.strip()]
    r2 = random.Random(17)
    for r in r2.sample(mined, k):
        p = proj_by_dir.get(r["project_dir"])
        res = {"file": "sessions.jsonl", "session_id": r["session_id"]}
        conn = common.open_sqlite_readonly(os.path.join(p["path"], "runtime.db"))
        cols = common.table_columns(conn, "sessions")
        row = conn.execute("select * from sessions where id=?", (r["session_id"],)).fetchone()
        conn.close()
        if row is None:
            res["verdict"] = "FAIL"
            res["why"] = "session id absent"
        else:
            raw = dict(zip(cols, row))
            mism = [c for c in ("name", "role", "model", "state", "created_at", "updated_at")
                    if c in raw and (raw[c] or None) != (r.get(c) or None)]
            res["verdict"] = "PASS" if not mism else "FAIL"
            res["name"] = r.get("name")
            if mism:
                res["mismatches"] = [[c, str(raw[c])[:60], str(r.get(c))[:60]] for c in mism]
        rows.append(res)
    return rows


def build_rollout_index():
    idx = {}
    for path in glob.glob(os.path.join(common.CODEX_SESSIONS, "**", "*.jsonl"), recursive=True):
        idx[os.path.basename(path)] = path
    for pattern in ("*Vino-projects-*", "*GPTino-projects-*"):
        for root in glob.glob(os.path.join(common.CLAUDE_PROJECTS, pattern)):
            for path in glob.glob(os.path.join(root, "*.jsonl")):
                idx[os.path.basename(path)] = path
    return idx


def spot_toolcalls(rollout_idx, picks):
    rows = []
    for r in picks:
        res = {"file": "tool-calls.jsonl", "thread_id": r["thread_id"], "tool": r["tool"],
               "call_id": r.get("call_id"), "source": r["source"],
               "mined": {"args_len": r.get("args_len"), "result_len": r.get("result_len"),
                         "duration_ms": r.get("duration_ms"), "at": r.get("at")}}
        path = rollout_idx.get(r["rollout_file"])
        if not path:
            res["verdict"] = "FAIL"
            res["why"] = "rollout file not found: %s" % r["rollout_file"]
            rows.append(res)
            continue
        cid = r.get("call_id")
        raw_call = None
        raw_out = None
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if cid and cid not in line:
                    continue
                try:
                    rec = json.loads(line)
                except ValueError:
                    continue
                if r["source"] == "codex":
                    pl = rec.get("payload") or {}
                    t = pl.get("type")
                    same = (pl.get("call_id") or pl.get("id")) == cid
                    if t in ("function_call", "custom_tool_call", "local_shell_call") and same:
                        raw_call = (rec, pl)
                    elif t in ("function_call_output", "custom_tool_call_output") and same:
                        raw_out = (rec, pl)
                else:
                    msg = (rec.get("message") or {})
                    cont = msg.get("content")
                    if not isinstance(cont, list):
                        continue
                    for b in cont:
                        if not isinstance(b, dict):
                            continue
                        if b.get("type") == "tool_use" and b.get("id") == cid:
                            raw_call = (rec, b)
                        if b.get("type") == "tool_result" and b.get("tool_use_id") == cid:
                            raw_out = (rec, b)
        mism = []
        if raw_call is None:
            mism.append("call not found in raw")
        else:
            rec, pl = raw_call
            if r["source"] == "codex":
                res["raw_tool_name"] = pl.get("name") or pl.get("tool_name")
                raw_args = pl.get("arguments") or pl.get("input") or ""
                if not isinstance(raw_args, str):
                    raw_args = json.dumps(raw_args, ensure_ascii=False)
                res["raw_args_len"] = len(raw_args)
                # code-mode: the raw payload is an `exec` whose arguments hold the JS source, and
                # the mined row splits it into code_* (the script) + args_* (the inner tool call).
                if r.get("encoding") == "exec_js":
                    try:
                        js = json.loads(raw_args).get("code") or raw_args
                    except ValueError:
                        js = raw_args
                    res["raw_js_len"] = len(js)
                    cl = r.get("code_len")
                    if cl is not None and min(abs(len(js) - cl), abs(len(raw_args) - cl)) > 4:
                        mism.append("code_len raw_js=%d raw_args=%d mined=%d"
                                    % (len(js), len(raw_args), cl))
                    if r.get("code_preview") and r["code_preview"][:40] not in js:
                        mism.append("code_preview head not found in raw JS")
                elif r.get("args_len") is not None and \
                        abs(len(raw_args) - r["args_len"]) > max(4, 0.05 * len(raw_args)):
                    mism.append("args_len raw=%d mined=%d" % (len(raw_args), r["args_len"]))
            else:
                res["raw_tool_name"] = pl.get("name")
            ts = rec.get("timestamp")
            if ts and ts != r["at"]:
                mism.append("at raw=%s mined=%s" % (ts, r["at"]))
        if raw_out is None:
            if r.get("result_len") is not None:
                mism.append("output not found in raw but result_len=%s" % r["result_len"])
        else:
            rec, pl = raw_out
            if r["source"] == "codex":
                txt = pl.get("output")
                if isinstance(txt, str):
                    try:
                        parsed = json.loads(txt)
                        if isinstance(parsed, dict) and "output" in parsed:
                            txt = parsed.get("output")
                    except ValueError:
                        pass
                if not isinstance(txt, str):
                    txt = json.dumps(txt, ensure_ascii=False, default=str)
            else:
                c = pl.get("content")
                if isinstance(c, list):
                    txt = "\n".join(b.get("text") or "" for b in c if isinstance(b, dict))
                elif isinstance(c, str):
                    txt = c
                else:
                    txt = json.dumps(c, ensure_ascii=False, default=str)
            res["raw_result_len"] = len(txt)
            if r.get("result_len") is not None and \
                    abs(len(txt) - r["result_len"]) > max(200, 0.02 * len(txt)):
                mism.append("result_len raw=%d mined=%d" % (len(txt), r["result_len"]))
            if r.get("result_preview"):
                head = r["result_preview"][:60]
                if head and head not in txt[:4000]:
                    mism.append("result_preview head not found in raw output")
        res["verdict"] = "PASS" if not mism else "FAIL"
        if mism:
            res["mismatches"] = mism[:4]
        rows.append(res)
    return rows


rollout_idx = build_rollout_index()
spot += spot_jobs()
spot += spot_problem()
spot += spot_messages()
spot += spot_sessions()
r3 = random.Random(23)
codex_pool = [x for x in tc_sample_pool if x["source"] == "codex"]
spot += spot_toolcalls(rollout_idx, r3.sample(codex_pool, min(4, len(codex_pool))))
# always include one claude tool call
claude_rows = [r for r in common.read_jsonl(os.path.join(OUT, "tool-calls.jsonl"))
               if r["source"] == "claude"]
if claude_rows:
    spot += spot_toolcalls(rollout_idx, [claude_rows[len(claude_rows) // 2]])

fails = [s for s in spot if s["verdict"] != "PASS"]
check("3.spot-checks", "PASS" if not fails else "FAIL",
      {"checked": len(spot), "pass": len(spot) - len(fails), "fail": len(fails),
       "failures": fails[:8], "examples": spot[:3]},
      "every spot-checked record matches the raw source")


# --------------------------------------------------------------------- 4. cross-consistency
job_ids = set()
jobs_by_project = collections.Counter()
jobs_by_project_a7 = collections.Counter()
jobs_state = collections.Counter()
job_rows = []
for r in common.read_jsonl(os.path.join(OUT, "jobs.jsonl")):
    job_ids.add(r["job_id"])
    jobs_by_project[r["project_dir"]] += 1
    jobs_state[r["state"]] += 1
    if r["version"] == common.ALPHA7:
        jobs_by_project_a7[r["project_dir"]] += 1
    job_rows.append((r["project_dir"], r["session_id"], r["state"],
                     r.get("message_signature"), r.get("request_hash"), r.get("message")))

jobstate_ids = set()
jobstate_events = 0
pe_kind = collections.Counter()
pe_pred = collections.Counter()
for r in common.read_jsonl(os.path.join(OUT, "problem-events.jsonl")):
    k = r.get("kind")
    pe_kind[k] += 1
    if k == "job-state":
        jobstate_events += 1
        jid = r.get("jobId")
        if jid:
            jobstate_ids.add(jid)
    elif k == "predicate-outcome":
        name = r.get("predicateKind") or r.get("predicateName") or "?"
        pe_pred[(name, bool(r.get("passed")))] += 1

inter = job_ids & jobstate_ids
check("4.jobs-vs-jobstate",
      "PASS" if pct(len(inter), len(jobstate_ids)) >= 90 else "WARN",
      {"jobs.jsonl_ids": len(job_ids), "problem_job_state_ids": len(jobstate_ids),
       "overlap": len(inter),
       "pct_of_problem_ids_in_jobs": pct(len(inter), len(jobstate_ids)),
       "pct_of_jobs_seen_in_problem": pct(len(inter), len(job_ids)),
       "job_state_ids_not_in_jobs": len(jobstate_ids - job_ids),
       "jobs_never_in_problem_log": len(job_ids - jobstate_ids)},
      ">=90% of job-state jobIds resolve to a jobs.jsonl row")

a7_projects = {d for d, n in jobs_by_project_a7.items() if n > 0}
rows = []
for d in sorted(a7_projects, key=lambda x: -jobs_by_project[x]):
    cs = tc_change_submit_by_project.get(d, 0)
    jb = jobs_by_project[d]
    rows.append({"project_dir": d, "change_submit_calls": cs, "jobs": jb,
                 "ratio": round(cs / jb, 2) if jb else None})
tot_cs = sum(x["change_submit_calls"] for x in rows)
tot_jb = sum(x["jobs"] for x in rows)
overall = round(tot_cs / tot_jb, 2) if tot_jb else None
bad = [x for x in rows if x["jobs"] >= 20 and (x["ratio"] is None or x["ratio"] < 0.3
                                               or x["ratio"] > 2.0)]
check("4.change_submit-vs-jobs", "PASS" if not bad else "WARN",
      {"overall_ratio": overall, "change_submit_total": tot_cs, "jobs_total": tot_jb,
       "per_project": rows[:12], "divergent_projects": bad},
      "rough agreement, ratio ~0.3-2.0 per project with >=20 jobs")


# --------------------------------------------------------------------- 5. known one-off
ONEOFF = "999ACAEE8D863470"
o_jobs = jobs_by_project.get(ONEOFF, 0)
o_threads = [t for t in threads.values()
             if t.get("project_dir") == ONEOFF and t["source"] == "claude"]
o_spill = [s for s in tc_spill if s["project_dir"] == ONEOFF and s["tool"] == "snapshot_read"
           and str(s["at"]).startswith("2026-08-26T00:03:47")]
any_spill_0347 = [s for s in tc_spill if str(s["at"]).startswith("2026-08-26T00:03:47")]
raw_o = raw_jobs_by_project.get(ONEOFF, 0)
ok5 = o_jobs == raw_o and len(o_threads) == 2 and len(o_spill) >= 1
check("5.one-off-999ACAEE8D863470", "PASS" if ok5 else "FAIL",
      {"jobs": o_jobs, "claude_threads": len(o_threads),
       "claude_thread_ids": [t["thread_id"] for t in o_threads],
       "spill_snapshot_read_at_00:03:47": len(o_spill),
       "all_spill_events": tc_spill, "spill_at_0347_any_project": len(any_spill_0347),
       "raw_live_jobs_rows_for_project": raw_jobs_by_project.get(ONEOFF, 0)},
      {"jobs": "16 (stale brief) / %d (raw live-jobs.db now)" % raw_o,
       "claude_threads": 2, "spill": ">=1"},
      "mined job count is verified against the raw db, not against the brief's 16")


# --------------------------------------------------------------------- 6. stats files
missing = []
empty = []
for name in ("failure-rates", "error-clusters", "retry-chains", "tool-friction",
             "session-lifecycle", "user-signals", "session-timelines", "read-path"):
    for ext in (".md", ".json"):
        p = os.path.join(STATS, name + ext)
        if not os.path.exists(p):
            missing.append(name + ext)
        elif os.path.getsize(p) == 0:
            empty.append(name + ext)
corpus = os.path.join(OUT, "corpus.md")
if not os.path.exists(corpus) or os.path.getsize(corpus) == 0:
    missing.append("corpus.md")
check("6.stats-files", "PASS" if not missing and not empty else "FAIL",
      {"missing": missing, "empty": empty,
       "md_files": len(glob.glob(os.path.join(STATS, "*.md"))),
       "json_files": len(glob.glob(os.path.join(STATS, "*.json"))),
       "smallest_md_bytes": min((os.path.getsize(x)
                                 for x in glob.glob(os.path.join(STATS, "*.md"))), default=0)},
      "all present and non-empty")

headline = json.load(open(os.path.join(STATS, "headline.json"), encoding="utf-8"))["headline"]

non_committed = sum(v for k, v in jobs_state.items() if k != "committed")
hashes = collections.Counter(h for (_, _, _, _, h, _) in job_rows if h)
dupes = sum(c - 1 for c in hashes.values() if c > 1)
HEDGE = re.compile(r"empty|WITH ISSUES|warning", re.I)
hedged = sum(1 for (_, _, st, _, _, msg) in job_rows
             if st == "committed" and msg and HEDGE.search(msg))
err_total = sum(tc_err.values())
err_rate = round(100.0 * err_total / tc_total, 1)
bysess = collections.defaultdict(collections.Counter)
for (pd_, sid, st, sig, _, _) in job_rows:
    if st != "committed" and sig:
        bysess[sid][sig] += 1
same_wall_jobs = sum(c for s in bysess.values() for c in s.values() if c > 1)
pure_repeats = sum(c - 1 for s in bysess.values() for c in s.values() if c > 1)
tev = collections.Counter()
for r in common.read_jsonl(os.path.join(OUT, "turn-events.jsonl")):
    tev[r["type"]] += 1
comp = tev.get("compacted", 0) + tev.get("compaction", 0)
oc = {k: v for k, v in pe_pred.items() if "outputcount" in str(k[0]).lower()}
oc_fail = sum(v for k, v in oc.items() if k[1] is False)
oc_tot = sum(oc.values())
oc_pct = pct(oc_fail, oc_tot)

recomputes = [
    {"metric": "jobs_non_committed", "recomputed": non_committed,
     "stats": headline["jobs_non_committed"],
     "agree": non_committed == headline["jobs_non_committed"],
     "by_state": dict(jobs_state.most_common())},
    {"metric": "identical_request_resubmits", "recomputed": dupes,
     "stats": headline["identical_request_resubmits"],
     "agree": dupes == headline["identical_request_resubmits"]},
    {"metric": "committed_but_hedged", "recomputed": hedged,
     "stats": headline["committed_but_hedged"],
     "agree": abs(hedged - headline["committed_but_hedged"]) <= 5},
    {"metric": "projects_with_jobs", "recomputed": len(jobs_by_project),
     "stats": headline["projects_with_jobs"],
     "agree": len(jobs_by_project) == headline["projects_with_jobs"],
     "raw_projects_with_job_rows": proj_with_rows, "raw_projects_with_db": proj_with_db,
     "project_folders": len(projects)},
    {"metric": "tool_error_rate_pct", "recomputed": err_rate,
     "stats": headline["tool_error_rate_pct"],
     "agree": abs(err_rate - headline["tool_error_rate_pct"]) <= 0.2,
     "errors": err_total, "calls": tc_total},
    {"metric": "same_wall_repeat_jobs", "recomputed": {"jobs_in_groups": same_wall_jobs,
                                                       "pure_repeats": pure_repeats},
     "stats": {"same_wall_repeat_jobs": headline["same_wall_repeat_jobs"],
               "reported_group_total": 266},
     "agree": pure_repeats == headline["same_wall_repeat_jobs"] and same_wall_jobs == 266},
    {"metric": "compactions/interrupted", "recomputed": [comp, tev.get("interrupted", 0)],
     "stats": [headline["compactions"], headline["interrupted_turns"]],
     "agree": comp == headline["compactions"]
     and tev.get("interrupted", 0) == headline["interrupted_turns"]},
    {"metric": "predicate_outputcount_fail_pct", "recomputed": oc_pct,
     "stats": headline["predicate_outputcount_fail_pct"],
     "agree": abs(oc_pct - headline["predicate_outputcount_fail_pct"]) <= 1.5,
     "fail": oc_fail, "total": oc_tot},
]
disagree = [r["metric"] for r in recomputes if not r["agree"]]
check("6.headline-recompute", "PASS" if not disagree else "WARN",
      {"recomputed": recomputes, "disagreements": disagree},
      "8 independently recomputed headline numbers agree")

corpus_txt = open(corpus, encoding="utf-8").read()
corpus_hits = {}
for label, val in (("jobs_total", counts["jobs.jsonl"]),
                   ("problem_events", counts["problem-events.jsonl"]),
                   ("messages", counts["messages.jsonl"]),
                   ("sessions", counts["sessions.jsonl"]),
                   ("tool_calls", tc_total)):
    corpus_hits[label] = {"measured": val,
                          "in_corpus_md": bool(re.search(
                              r"(?<![\d,])(%s|%s)(?![\d,])" % (val, "{:,}".format(val)),
                              corpus_txt))}
check("6.corpus.md", "PASS" if all(v["in_corpus_md"] for v in corpus_hits.values())
      else "WARN", corpus_hits, "measured counts appear verbatim in corpus.md")

plan_kinds = {"job-state": 11521, "predicate-outcome": 4493, "auto-approval": 246,
              "auto-fill": 113, "self-stale-rebase": 47, "job-exception": 16,
              "snapshot-read": 13, "visual-review": 5}
kd = {k: {"measured": pe_kind.get(k, 0), "plan": v, "delta": pe_kind.get(k, 0) - v}
      for k, v in plan_kinds.items()}
extra_kinds = {k: v for k, v in pe_kind.items() if k not in plan_kinds}
bad_kinds = [k for k, v in kd.items() if abs(v["delta"]) > max(3, v["plan"] * 0.01)]
check("1.problem-kind-breakdown", "PASS" if not bad_kinds else "WARN",
      {"by_kind": kd, "kinds_not_in_plan": extra_kinds}, "plan table")

# ------------------------------------------------- 7. reconcile the raw-vs-mined count deltas
# The sources are LIVE. Anything the extractors "missed" should be strictly newer than the
# extraction run; if a delta record predates the run, that IS an extraction bug.
def newest_mtime(names):
    ts = []
    for n in names:
        p = os.path.join(OUT, n)
        if os.path.exists(p):
            ts.append(os.path.getmtime(p))
    return max(ts) if ts else 0


run_mtimes = {n: os.path.getmtime(os.path.join(OUT, n))
              for n in FILES if os.path.exists(os.path.join(OUT, n))}
late = []
early = []
cut_msg = run_mtimes.get("messages.jsonl", 0)
mined_msgs = {(r["project_dir"], r["session_id"], r["msg_id"])
              for r in common.read_jsonl(os.path.join(OUT, "messages.jsonl"))}
mined_sess = {r["session_id"] for r in common.read_jsonl(os.path.join(OUT, "sessions.jsonl"))}
for p in projects:
    db = os.path.join(p["path"], "runtime.db")
    if not os.path.exists(db):
        continue
    try:
        conn = common.open_sqlite_readonly(db)
        if common.table_exists(conn, "sessions"):
            for sid, cat in conn.execute("select id, created_at from sessions"):
                if sid in mined_sess:
                    continue
                dt = common.parse_iso(cat)
                (late if dt and dt.timestamp() > cut_msg else early).append(
                    {"what": "session", "project": p["project_dir"], "id": sid, "created_at": cat})
        if common.table_exists(conn, "messages"):
            for mid, sid, cat in conn.execute("select id, session_id, created_at from messages"):
                if (p["project_dir"], sid, mid) in mined_msgs:
                    continue
                dt = common.parse_iso(cat)
                (late if dt and dt.timestamp() > cut_msg else early).append(
                    {"what": "message", "project": p["project_dir"], "id": mid, "created_at": cat})
        conn.close()
    except Exception:
        pass

cut_pe = run_mtimes.get("problem-events.jsonl", 0)
mined_pe = collections.Counter()
for r in common.read_jsonl(os.path.join(OUT, "problem-events.jsonl")):
    mined_pe[(r["project_dir"], r.get("at"), r.get("kind"))] += 1
for p in projects:
    pl = os.path.join(p["path"], "problem-log.jsonl")
    if not os.path.exists(pl):
        continue
    seen = collections.Counter()
    with open(pl, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if not line.strip():
                continue
            try:
                rec = json.loads(line)
            except ValueError:
                continue
            key = (p["project_dir"], rec.get("at"), rec.get("kind"))
            seen[key] += 1
            if seen[key] > mined_pe.get(key, 0):
                dt = common.parse_iso(rec.get("at"))
                (late if dt and dt.timestamp() > cut_pe else early).append(
                    {"what": "problem-event", "project": p["project_dir"],
                     "kind": rec.get("kind"), "at": rec.get("at")})

check("7.live-corpus-reconciliation", "PASS" if not early else "FAIL",
      {"raw_records_absent_from_mined": len(late) + len(early),
       "written_after_the_extraction_run": len(late),
       "written_before_the_run (real loss)": len(early),
       "extraction_run_mtimes": {k: time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(v))
                                 for k, v in sorted(run_mtimes.items())},
       "late_examples": late[:6], "early_examples": early[:6]},
      "every raw record missing from the mined files postdates the extraction run")

# ------------------------------------------------- 8. analysis coverage caveats
jobs_no_problem = job_ids - jobstate_ids
thread_projects = {t.get("project_dir") for t in threads.values()}
jobs_no_thread = sum(n for d, n in jobs_by_project.items() if d not in thread_projects)
running = 0
tc_exec = 0
for r in common.read_jsonl(os.path.join(OUT, "tool-calls.jsonl")):
    if r.get("encoding") == "exec_js":
        tc_exec += 1
        if r.get("result_kind") == "running":
            running += 1
check("8.coverage-caveats", "WARN" if jobs_no_problem or running else "PASS",
      {"jobs_with_no_problem_log_trail": len(jobs_no_problem),
       "pct_of_jobs": pct(len(jobs_no_problem), len(job_ids)),
       "jobs_in_projects_with_no_rollout": jobs_no_thread,
       "projects_with_jobs_but_no_rollout": sorted(d for d in jobs_by_project
                                                   if d not in thread_projects),
       "exec_js_calls_with_result_kind_running": running,
       "pct_of_exec_js": pct(running, tc_exec)},
      "0 uncovered jobs / 0 truncated exec results",
      "not extraction bugs: these are gaps in the SOURCES that Stage 1 must not read as signal")

report = {"checks": CHECKS, "blocking_issues": BLOCKING, "suggested_fixes": FIXES,
          "run_seconds": round(time.time() - T0, 1)}
with open(os.path.join(OUT, "verify-report.json"), "w", encoding="utf-8") as fh:
    json.dump(report, fh, ensure_ascii=False, indent=1, default=str)
print(json.dumps(report, ensure_ascii=False, indent=1, default=str))
