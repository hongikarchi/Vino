# -*- coding: utf-8 -*-
"""Stage 0 aggregation: read the mined JSONL in .log-mine/ and write stats/*.md + stats/*.json
plus corpus.md.  Pure stdlib, re-runnable (every output is overwritten), streams the big files.

Reads:  jobs.jsonl, problem-events.jsonl, messages.jsonl, sessions.jsonl, hostlog-events.jsonl,
        tool-calls.jsonl, turn-events.jsonl, threads.jsonl, *-summary.json
Writes: .log-mine/stats/{failure-rates,error-clusters,retry-chains,tool-friction,
        session-lifecycle,user-signals,session-timelines,read-path}.{md,json}
        .log-mine/stats/timeline-<sid8>.md, .log-mine/stats/headline.json
        .log-mine/corpus.md
"""
import collections
import io
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import OUT_ROOT, ensure_out, normalize_signature, parse_iso, read_jsonl  # noqa: E402

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

STATS = os.path.join(OUT_ROOT, "stats")
NON_COMMITTED = ("failed", "blocked", "recoveryrequired")
START = time.time()
ISSUES = []
OUTPUTS = []
HEADLINE = {}


# ---------------------------------------------------------------- small helpers
def path(name):
    return os.path.join(OUT_ROOT, name)


def exists(name):
    return os.path.exists(path(name))


def load_jsonl(name):
    if not exists(name):
        ISSUES.append("missing input: {}".format(name))
        return []
    return list(read_jsonl(path(name)))


def load_json(name):
    if not exists(name):
        ISSUES.append("missing input: {}".format(name))
        return {}
    with open(path(name), encoding="utf-8") as fh:
        return json.load(fh)


def iso_week(ts):
    dt = parse_iso(ts)
    if not dt:
        return None
    y, w, _ = dt.isocalendar()
    return "{}-W{:02d}".format(y, w)


def day(ts):
    dt = parse_iso(ts)
    return dt.date().isoformat() if dt else None


def op_kind_set(job):
    """Distinct op kinds in a job.  `op_kinds` is one entry *per operation*, so a job with five
    createComponent ops lists the kind five times; counting jobs needs the set."""
    return sorted(set(job.get("op_kinds") or []))


def pct(num, den):
    return 0.0 if not den else round(100.0 * num / den, 1)


def quantile(values, q):
    if not values:
        return None
    s = sorted(values)
    if len(s) == 1:
        return s[0]
    idx = q * (len(s) - 1)
    lo = int(idx)
    hi = min(lo + 1, len(s) - 1)
    frac = idx - lo
    return round(s[lo] + (s[hi] - s[lo]) * frac, 1)


def dist(values):
    return {
        "n": len(values),
        "min": min(values) if values else None,
        "p50": quantile(values, 0.50),
        "p90": quantile(values, 0.90),
        "p99": quantile(values, 0.99),
        "max": max(values) if values else None,
        "mean": round(sum(values) / len(values), 1) if values else None,
    }


def cell(v):
    if v is None:
        return ""
    if isinstance(v, float):
        v = "{:,.0f}".format(v) if abs(v) >= 10000 else ("%g" % v)
    s = str(v)
    s = s.replace("\\", "\\\\").replace("|", "\\|")
    s = re.sub(r"[\r\n\t]+", " ", s)
    return s


def clip(text, n):
    if text is None:
        return ""
    s = re.sub(r"[\r\n\t]+", " ", str(text)).strip()
    return s if len(s) <= n else s[: n - 1] + "…"


def hms(ms):
    if ms is None:
        return None
    return "{:.1f}s".format(ms / 1000.0)


def is_running_stub(rec):
    """True when the transcript recorded `Script running…` instead of the real result.

    `result_kind='running'` means the exec cell was still executing when the rollout wrote the
    output block, so `result_len` is the length of a ~62-character placeholder. The call is real
    (it happened, it may have errored) but its *size* is the recorder's, not the tool's — so
    every result-size percentile, the ~40 K ceiling table and the read-path size distributions
    exclude these rows.
    """
    return rec.get("result_kind") == "running"


class Doc(object):
    """Collects markdown sections and the parallel JSON payload."""

    def __init__(self, name, title, intro=""):
        self.name = name
        self.md = ["# {}".format(title), ""]
        if intro:
            self.md.append(intro)
            self.md.append("")
        self.json = {"generated_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
                     "tables": {}, "scalars": {}}

    def text(self, *lines):
        self.md.extend(lines)
        self.md.append("")

    def scalar(self, key, value):
        self.json["scalars"][key] = value

    def table(self, key, title, headers, rows, note=None):
        self.md.append("## {}".format(title))
        self.md.append("")
        if note:
            self.md.append(note)
            self.md.append("")
        if not rows:
            self.md.append("_(no rows)_")
            self.md.append("")
        else:
            self.md.append("| " + " | ".join(headers) + " |")
            self.md.append("|" + "|".join(["---"] * len(headers)) + "|")
            for r in rows:
                self.md.append("| " + " | ".join(cell(c) for c in r) + " |")
            self.md.append("")
        self.json["tables"][key] = {"title": title, "columns": headers, "rows": rows}

    def write(self):
        ensure_out("stats")
        md_path = os.path.join(STATS, self.name + ".md")
        js_path = os.path.join(STATS, self.name + ".json")
        with io.open(md_path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("\n".join(self.md).rstrip() + "\n")
        with io.open(js_path, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(self.json, fh, ensure_ascii=False, indent=1, default=str)
        OUTPUTS.append(md_path)
        OUTPUTS.append(js_path)


# ================================================================ load inputs
JOBS = load_jsonl("jobs.jsonl")
MESSAGES = load_jsonl("messages.jsonl")
SESSIONS = load_jsonl("sessions.jsonl")
THREADS = load_jsonl("threads.jsonl")
HOSTLOG = load_jsonl("hostlog-events.jsonl")
JOBS_SUMMARY = load_json("jobs-summary.json")
PROBLEM_SUMMARY = load_json("problem-summary.json")
MESSAGES_SUMMARY = load_json("messages-summary.json")
HOSTLOG_SUMMARY = load_json("hostlog-summary.json")
ROLLOUT_SUMMARY = load_json("rollouts-summary.json")

for _j in JOBS:
    _j["_week"] = iso_week(_j.get("created_at") or _j.get("enqueued_at"))
    _j["_day"] = day(_j.get("created_at") or _j.get("enqueued_at"))
    _j["_state"] = (_j.get("state") or "").lower()


# ---------------------------------------------------------------- problem-log job coverage
# The problem log was not writing for the whole window, so a job can be perfectly real and still
# have no `job-state` trail in it. Any per-job rate computed from problem-log evidence therefore
# has to divide by the jobs that HAVE a trail, not by every row in live-jobs.db.
JOB_IDS = {j.get("job_id") for j in JOBS if j.get("job_id")}
PROBLEM_JOB_KINDS = collections.defaultdict(set)      # job_id -> {kind, ...}
if exists("problem-events.jsonl"):
    for _r in read_jsonl(path("problem-events.jsonl")):
        _jid = _r.get("jobId") or _r.get("job_id")
        if not _jid:
            continue
        _k = _r.get("kind")
        PROBLEM_JOB_KINDS[_jid].add(_k)
        if _k == "predicate-outcome" and _r.get("passed") is False:
            PROBLEM_JOB_KINDS[_jid].add("predicate-outcome:failed")
JOBS_WITH_TRAIL = {jid for jid, ks in PROBLEM_JOB_KINDS.items()
                   if "job-state" in ks and jid in JOB_IDS}
N_JOBS = len(JOBS)
N_TRAIL = len(JOBS_WITH_TRAIL)
_NO_TRAIL = [j for j in JOBS if j.get("job_id") not in JOBS_WITH_TRAIL]
NO_TRAIL_VERSIONS = collections.Counter(j.get("version") for j in _NO_TRAIL)
NO_TRAIL_PROJECTS = sorted({j.get("project_dir") for j in _NO_TRAIL if j.get("project_dir")})
NO_TRAIL_VERSION_DESC = (
    "all {}".format(next(iter(NO_TRAIL_VERSIONS))) if len(NO_TRAIL_VERSIONS) == 1
    else " / ".join("{} {}".format(n, v) for v, n in NO_TRAIL_VERSIONS.most_common()) or "none")
TRAIL_CAVEAT = (
    "**Problem-log coverage caveat.** {} of the {} jobs in `jobs.jsonl` have a `job-state` "
    "trail in `problem-log.jsonl`; {} do not. The uncovered jobs are {}, spread over {} "
    "project(s) ({}) — the problem log was not writing for them, so their absence is a source "
    "gap, not a signal. Every per-job rate derived from problem-log evidence divides by "
    "**{}**, not {}; both denominators are printed next to each such rate.".format(
        N_TRAIL, N_JOBS, N_JOBS - N_TRAIL, NO_TRAIL_VERSION_DESC,
        len(NO_TRAIL_PROJECTS), ", ".join(NO_TRAIL_PROJECTS) or "—", N_TRAIL, N_JOBS))


# ================================================================ 1. failure rates
def build_failure_rates():
    doc = Doc(
        "failure-rates",
        "Job failure rates",
        "Source: `.log-mine/jobs.jsonl` ({} jobs). `non-committed` = failed + blocked + "
        "recoveryrequired — the three terminal states in which the requested write did not "
        "land as asked. Jobs are the server-side unit of work, so this is the closest thing the "
        "corpus has to an end-to-end success metric.".format(len(JOBS)),
    )
    doc.text(TRAIL_CAVEAT,
             "",
             "Every table on *this* page is computed from `jobs.jsonl` alone and so uses the "
             "full {} jobs; the caveat matters for `stats/error-clusters.md`, where the "
             "acceptance-predicate and auto-fill / auto-approval evidence comes from the "
             "problem log.".format(N_JOBS))
    states_order = ["committed", "failed", "blocked", "recoveryrequired"]
    for s in sorted({j["_state"] for j in JOBS}):
        if s not in states_order:
            states_order.append(s)

    def breakdown(keyfn, key_label, sort_by_count=True, limit=None, extra=None,
                  extra_headers=()):
        buckets = collections.defaultdict(collections.Counter)
        for j in JOBS:
            for k in keyfn(j):
                buckets[k][j["_state"]] += 1
        rows = []
        for k, ctr in buckets.items():
            total = sum(ctr.values())
            nc = sum(ctr[s] for s in NON_COMMITTED)
            row = [k, total] + [ctr.get(s, 0) for s in states_order] + [pct(nc, total)]
            if extra:
                row = row + list(extra(k))
            rows.append(row)
        if sort_by_count:
            rows.sort(key=lambda r: -r[1])
        else:
            rows.sort(key=lambda r: (r[0] is None, r[0]))
        if limit:
            rows = rows[:limit]
        headers = [key_label, "jobs"] + states_order + ["non-committed %"] + list(extra_headers)
        return headers, rows

    total = len(JOBS)
    ctr = collections.Counter(j["_state"] for j in JOBS)
    nc_total = sum(ctr[s] for s in NON_COMMITTED)
    doc.scalar("total_jobs", total)
    doc.scalar("by_state", dict(ctr))
    doc.scalar("non_committed", nc_total)
    doc.scalar("non_committed_pct", pct(nc_total, total))
    HEADLINE["jobs_total"] = total
    HEADLINE["jobs_non_committed"] = nc_total
    HEADLINE["jobs_non_committed_pct"] = pct(nc_total, total)

    doc.table(
        "overall", "Overall", ["state", "jobs", "% of all"],
        [[s, ctr.get(s, 0), pct(ctr.get(s, 0), total)] for s in states_order]
        + [["**non-committed**", nc_total, pct(nc_total, total)],
           ["**total**", total, 100.0]],
    )

    h, r = breakdown(lambda j: [j.get("version") or "?"], "version")
    doc.table("by_version", "By version", h, r,
              "Brand maps 1:1 onto version in this corpus (GPTino = pre-alpha7, Vino = "
              "0.1.0-alpha.7), so the next table is the same split under a different name and "
              "no version effect can be separated from the rename boundary.")
    h, r = breakdown(lambda j: [j.get("brand") or "?"], "brand")
    doc.table("by_brand", "By brand", h, r)

    h, r = breakdown(lambda j: [(j.get("op_kinds") or ["(none)"])[0]], "first op kind")
    doc.table("by_first_op_kind", "By first op kind", h, r)

    h, r = breakdown(lambda j: (op_kind_set(j) or ["(none)"]), "op kind (any)")
    doc.table("by_any_op_kind", "By op kind (any op in the job)", h, r,
              "Counted per **job**, deduplicated: a job holding five `createComponent` ops counts "
              "once here. A job with N *distinct* kinds is counted once under each, so the column "
              "still sums above the job total.")
    op_ctr = collections.Counter(k for j in JOBS for k in (j.get("op_kinds") or []))
    op_nc = collections.Counter(k for j in JOBS if j["_state"] in NON_COMMITTED
                                for k in (j.get("op_kinds") or []))
    doc.table("by_operation_count", "By operation (not job): how much work each kind carries",
              ["op kind", "operations", "operations in non-committed jobs", "%"],
              [[k, n, op_nc.get(k, 0), pct(op_nc.get(k, 0), n)] for k, n in op_ctr.most_common()],
              "{} operations across {} jobs. This is the table that matches "
              "`jobs-summary.json:by_op_kind`.".format(sum(op_ctr.values()), len(JOBS)))

    h, r = breakdown(lambda j: [j.get("_week")], "ISO week", sort_by_count=False)
    doc.table("by_week", "By ISO week", h, r)
    HEADLINE["weeks_active"] = len(r)

    top_kinds = [k for k, _ in collections.Counter(
        k for j in JOBS for k in op_kind_set(j)).most_common(14)]
    rows = []
    for kind in top_kinds:
        row = [kind]
        for ver in ("pre-alpha7", "0.1.0-alpha.7"):
            sub = [j for j in JOBS if kind in op_kind_set(j)
                   and j.get("version") == ver]
            nc = sum(1 for j in sub if j["_state"] in NON_COMMITTED)
            row += [len(sub), nc, pct(nc, len(sub))]
        rows.append(row)
    doc.table("op_kind_by_version", "Top op kinds × version (non-committed rate)",
              ["op kind", "pre-alpha7 jobs", "nc", "nc %", "alpha.7 jobs", "nc", "nc %"], rows)

    def proj_extra(k):
        sub = [j for j in JOBS if (j.get("brand"), j.get("project_dir")) == k]
        days = sorted(x for x in {j["_day"] for j in sub} if x)
        sessions = len({j.get("session_id") for j in sub})
        return [clip(sub[0].get("project_name"), 28), sessions,
                days[0] if days else None, days[-1] if days else None]

    h, r = breakdown(lambda j: [(j.get("brand"), j.get("project_dir"))], "project", limit=20,
                     extra=proj_extra,
                     extra_headers=("project_name", "sessions", "first day", "last day"))
    rows = [["{} / {}".format(row[0][0], row[0][1])] + row[1:] for row in r]
    doc.table("by_project_top20", "Top 20 projects by job count", h, rows,
              "{} of the 172 project folders hold a live-jobs.db at all.".format(
                  JOBS_SUMMARY.get("projects_with_live_jobs_db")))

    weeks = sorted({j["_week"] for j in JOBS if j["_week"]})
    rows = []
    for w in weeks:
        row = [w]
        for ver in ("pre-alpha7", "0.1.0-alpha.7"):
            sub = [j for j in JOBS if j["_week"] == w and j.get("version") == ver]
            nc = sum(1 for j in sub if j["_state"] in NON_COMMITTED)
            row += [len(sub), pct(nc, len(sub)) if sub else None]
        sub = [j for j in JOBS if j["_week"] == w]
        nc = sum(1 for j in sub if j["_state"] in NON_COMMITTED)
        row += [len(sub), pct(nc, len(sub))]
        rows.append(row)
    doc.table("week_by_version", "Non-committed rate per week × version (trend)",
              ["ISO week", "pre-alpha7 jobs", "nc %", "alpha.7 jobs", "nc %", "all jobs", "nc %"],
              rows)

    daily = collections.defaultdict(collections.Counter)
    for j in JOBS:
        daily[j["_day"]][j["_state"]] += 1
    rows = []
    for d in sorted(x for x in daily if x):
        c = daily[d]
        t = sum(c.values())
        nc = sum(c[s] for s in NON_COMMITTED)
        rows.append([d, t, c.get("committed", 0), c.get("failed", 0), c.get("blocked", 0),
                     c.get("recoveryrequired", 0), pct(nc, t)])
    doc.table("by_day", "By day", ["day", "jobs", "committed", "failed", "blocked",
                                   "recoveryrequired", "non-committed %"], rows)

    # op count / predicate presence, cheap correlates of failure
    rows = []
    for label, sel in (("1 op", lambda j: (j.get("op_count") or 0) == 1),
                       ("2-3 ops", lambda j: 2 <= (j.get("op_count") or 0) <= 3),
                       ("4-9 ops", lambda j: 4 <= (j.get("op_count") or 0) <= 9),
                       ("10+ ops", lambda j: (j.get("op_count") or 0) >= 10)):
        sub = [j for j in JOBS if sel(j)]
        nc = sum(1 for j in sub if j["_state"] in NON_COMMITTED)
        rows.append([label, len(sub), nc, pct(nc, len(sub))])
    doc.table("by_op_count", "By op count in the job",
              ["bucket", "jobs", "non-committed", "nc %"], rows)

    rows = []
    for label, sel in (("has acceptancePredicates", lambda j: bool(j.get("has_acceptance_predicates"))),
                       ("no acceptancePredicates", lambda j: not j.get("has_acceptance_predicates"))):
        sub = [j for j in JOBS if sel(j)]
        nc = sum(1 for j in sub if j["_state"] in NON_COMMITTED)
        rows.append([label, len(sub), nc, pct(nc, len(sub))])
    doc.table("by_predicates", "By presence of agent-supplied acceptance predicates",
              ["bucket", "jobs", "non-committed", "nc %"], rows)
    doc.write()


# ================================================================ 2. error clusters
def build_error_clusters():
    doc = Doc(
        "error-clusters",
        "Error clusters",
        "`message_signature` = GUIDs / long hex / numbers collapsed to `#`, first 110 chars "
        "(`common.normalize_signature`). Computed over jobs in a terminal non-committed state.",
    )
    nc_jobs = [j for j in JOBS if j["_state"] in NON_COMMITTED]
    groups = collections.defaultdict(list)
    for j in nc_jobs:
        groups[j.get("message_signature") or "(no message)"].append(j)

    rows = []
    for sig, js in groups.items():
        states = collections.Counter(x["_state"] for x in js)
        vers = collections.Counter(x.get("version") for x in js)
        opk = collections.Counter(k for x in js for k in op_kind_set(x))
        days = sorted(x["_day"] for x in js if x["_day"])
        ex = js[0]
        rows.append([
            len(js), clip(sig, 150),
            ",".join("{}:{}".format(s, n) for s, n in states.most_common()),
            ",".join("{}:{}".format(v, n) for v, n in vers.most_common()),
            len({x.get("project_dir") for x in js}),
            days[0] if days else None, days[-1] if days else None,
            ",".join("{}:{}".format(k, n) for k, n in opk.most_common(3)),
            ex.get("job_id"), ex.get("project_dir"),
        ])
    rows.sort(key=lambda r: -r[0])
    top = rows[:80]
    doc.scalar("distinct_non_committed_signatures", len(rows))
    doc.scalar("non_committed_jobs", len(nc_jobs))
    doc.scalar("top80_coverage_jobs", sum(r[0] for r in top))
    doc.scalar("top80_coverage_pct", pct(sum(r[0] for r in top), len(nc_jobs)))
    HEADLINE["distinct_error_signatures"] = len(rows)
    doc.table(
        "top80", "Top 80 non-committed message clusters",
        ["count", "message_signature", "states", "versions", "projects", "first", "last",
         "top op kinds", "example job_id", "example project_dir"], top,
        "{} distinct signatures over {} non-committed jobs; the 80 shown cover {} of them "
        "({}%).".format(len(rows), len(nc_jobs), sum(r[0] for r in top),
                        pct(sum(r[0] for r in top), len(nc_jobs))),
    )

    for state in NON_COMMITTED:
        sub = collections.Counter()
        meta = {}
        for j in nc_jobs:
            if j["_state"] != state:
                continue
            sig = j.get("message_signature") or "(no message)"
            sub[sig] += 1
            m = meta.setdefault(sig, {"days": [], "ex": j})
            if j["_day"]:
                m["days"].append(j["_day"])
        r = []
        for s, n in sub.most_common(25):
            d = sorted(meta[s]["days"])
            ex = meta[s]["ex"]
            r.append([n, clip(s, 150), d[0] if d else None, d[-1] if d else None,
                      ex.get("job_id"), ex.get("project_dir")])
        doc.table("top_" + state, "Top 25 `{}` clusters".format(state),
                  ["count", "message_signature", "first", "last", "example job_id",
                   "example project_dir"], r)

    # verification false-alarm candidates: committed but the message hedges
    hedge_re = re.compile(r"empty|WITH ISSUES|warning", re.IGNORECASE)
    committed = [j for j in JOBS if j["_state"] == "committed"]
    hedged = [j for j in committed if hedge_re.search(j.get("message") or "")]
    counts = collections.Counter(j.get("message_signature") or "(none)" for j in hedged)
    meta = {}
    for j in hedged:
        sig = j.get("message_signature") or "(none)"
        m = meta.setdefault(sig, {"days": [], "ops": collections.Counter(), "ex": j,
                                  "projects": set(), "vers": collections.Counter()})
        if j["_day"]:
            m["days"].append(j["_day"])
        for k in op_kind_set(j):
            m["ops"][k] += 1
        m["projects"].add(j.get("project_dir"))
        m["vers"][j.get("version")] += 1
    rows = []
    for sig, n in counts.most_common(60):
        m = meta[sig]
        d = sorted(m["days"])
        rows.append([n, clip(sig, 150),
                     ",".join("{}:{}".format(v, c) for v, c in m["vers"].most_common()),
                     len(m["projects"]), d[0] if d else None, d[-1] if d else None,
                     ",".join("{}:{}".format(k, c) for k, c in m["ops"].most_common(3)),
                     m["ex"].get("job_id"), m["ex"].get("project_dir")])
    doc.scalar("committed_hedged_jobs", len(hedged))
    doc.scalar("committed_jobs", len(committed))
    doc.scalar("committed_hedged_pct", pct(len(hedged), len(committed)))
    doc.scalar("distinct_hedged_signatures", len(counts))
    HEADLINE["committed_but_hedged"] = len(hedged)
    HEADLINE["committed_but_hedged_pct"] = pct(len(hedged), len(committed))
    doc.table(
        "committed_hedged",
        "Committed jobs whose message says `empty` / `WITH ISSUES` / `warning`",
        ["count", "message_signature", "versions", "projects", "first", "last", "top op kinds",
         "example job_id", "example project_dir"], rows,
        "{} of {} committed jobs ({}%) carry a hedge in the success message — "
        "verification false-alarm candidates: the write was accepted and committed, yet the "
        "server's own message says the result is empty or warned. {} distinct signatures; top 60 "
        "shown.".format(len(hedged), len(committed), pct(len(hedged), len(committed)),
                        len(counts)),
    )

    kw_rows = []
    for label, pat in (("empty", r"empty"), ("WITH ISSUES", r"WITH ISSUES"),
                       ("warning", r"warning")):
        sub = [j for j in hedged if re.search(pat, j.get("message") or "", re.IGNORECASE)]
        vers = collections.Counter(j.get("version") for j in sub)
        opk = collections.Counter(k for j in sub for k in op_kind_set(j))
        kw_rows.append([label, len(sub),
                        ",".join("{}:{}".format(v, n) for v, n in vers.most_common()),
                        ",".join("{}:{}".format(k, n) for k, n in opk.most_common(3)),
                        clip(sub[0].get("message") if sub else "", 160)])
    doc.table("committed_hedged_by_keyword", "Hedge keyword mix",
              ["keyword", "jobs", "versions", "top op kinds", "example message"], kw_rows)

    # acceptance predicate outcomes from the problem log (the other half of verification)
    pred = collections.Counter()
    pred_fail = collections.Counter()
    pred_name_fail = collections.Counter()
    if exists("problem-events.jsonl"):
        for rec in read_jsonl(path("problem-events.jsonl")):
            if rec.get("kind") != "predicate-outcome":
                continue
            k = rec.get("predicateKind") or rec.get("predicateName") or "?"
            pred[k] += 1
            if rec.get("passed") is False:
                pred_fail[k] += 1
                pred_name_fail[clip(rec.get("predicateName"), 70)] += 1
    rows = [[k, n, pred_fail.get(k, 0), pct(pred_fail.get(k, 0), n)]
            for k, n in pred.most_common()]
    doc.table("predicate_outcomes", "Acceptance predicate outcomes (problem-log)",
              ["predicateKind", "evaluations", "failures", "failure %"], rows,
              "Structural predicates (WireExists / ObjectExists / …) never fail; the whole "
              "failure mass sits in count- and shape-predicates the agent predicted itself. "
              "Counted per **evaluation**, not per job — the per-job view, with its own "
              "denominator, is two tables down.")
    doc.table("predicate_failing_names", "Most-failed predicate names (top 25)",
              ["failures", "predicateName"], pred_name_fail.most_common(25))

    # Per-job problem-log evidence. Denominator = jobs that have a job-state trail (not all jobs).
    ev_rows = []
    for label, kind in (("≥1 acceptance predicate evaluated", "predicate-outcome"),
                        ("≥1 acceptance predicate FAILED", "predicate-outcome:failed"),
                        ("≥1 auto-fill", "auto-fill"),
                        ("≥1 auto-approval", "auto-approval"),
                        ("≥1 self-stale-rebase", "self-stale-rebase"),
                        ("≥1 job-exception", "job-exception")):
        n = sum(1 for jid in JOBS_WITH_TRAIL if kind in PROBLEM_JOB_KINDS.get(jid, ()))
        ev_rows.append([label, n, N_TRAIL, pct(n, N_TRAIL), N_JOBS, pct(n, N_JOBS)])
    doc.scalar("jobs_total", N_JOBS)
    doc.scalar("jobs_with_job_state_trail", N_TRAIL)
    doc.scalar("jobs_without_job_state_trail", N_JOBS - N_TRAIL)
    doc.scalar("jobs_without_trail_projects", NO_TRAIL_PROJECTS)
    HEADLINE["jobs_with_job_state_trail"] = N_TRAIL
    HEADLINE["jobs_without_job_state_trail"] = N_JOBS - N_TRAIL
    doc.table("problem_evidence_per_job",
              "Problem-log evidence per job (denominator = jobs with a job-state trail)",
              ["evidence", "jobs", "jobs with a trail", "% of jobs with a trail",
               "all jobs", "% of all jobs"], ev_rows,
              TRAIL_CAVEAT + " The two right-hand columns show what the same numerator would "
              "look like against the full job count, so the difference is visible rather than "
              "assumed.")
    for k, n in pred.items():
        if str(k).lower() == "outputcountinrange":
            HEADLINE["predicate_outputcount_fail_pct"] = pct(pred_fail.get(k, 0), n)
    doc.write()


# ================================================================ 3. retry chains
def idem_prefix(key):
    if not key or "-" not in key:
        return None
    return key.rsplit("-", 1)[0]


def build_retry_chains():
    doc = Doc(
        "retry-chains",
        "Retry chains",
        "A *chain* = consecutive jobs inside one session (ordered by `enqueue_sequence`, then "
        "`created_at`) that keep re-attempting the same thing: the next job's "
        "`idempotency_key` shares the prefix before the last `-`, or its `summary` is "
        "byte-identical to the previous job's. A chain **qualifies** when some non-committed "
        "state occurs ≥2× in it — i.e. the session hit the same wall at least twice.",
    )
    by_session = collections.defaultdict(list)
    for j in JOBS:
        by_session[(j.get("brand"), j.get("project_dir"), j.get("session_id"))].append(j)

    chains = []
    for key, js in by_session.items():
        js.sort(key=lambda x: (x.get("enqueue_sequence") if x.get("enqueue_sequence") is not None
                               else 0, x.get("created_at") or "", x.get("job_id") or ""))
        cur = [js[0]] if js else []
        for prev, nxt in zip(js, js[1:]):
            pk = idem_prefix(prev.get("idempotency_key"))
            nk = idem_prefix(nxt.get("idempotency_key"))
            same_key = bool(pk) and pk == nk
            same_sum = bool(prev.get("summary")) and prev.get("summary") == nxt.get("summary")
            if same_key or same_sum:
                cur.append(nxt)
            else:
                chains.append((key, cur))
                cur = [nxt]
        if cur:
            chains.append((key, cur))

    multi = [c for c in chains if len(c[1]) >= 2]
    qualifying = []
    for key, js in multi:
        states = collections.Counter(x["_state"] for x in js)
        repeated = [s for s in NON_COMMITTED if states.get(s, 0) >= 2]
        if repeated:
            qualifying.append((key, js, repeated, states))

    # ---- relaxed variants, because the strict adjacency rule almost never fires here
    # (idempotency keys carry a per-operation mid-segment: diag-probe-create-01 vs
    #  diag-probe-source-06 share no prefix-before-last-dash, and retries are rarely adjacent).
    def group_key(j):
        p = idem_prefix(j.get("idempotency_key"))
        return ("KEY", p) if p else ("SUM", j.get("summary"))

    relaxed = []
    for key, js in by_session.items():
        buckets = collections.defaultdict(list)
        for j in js:
            buckets[group_key(j)].append(j)
        for gk, group in buckets.items():
            if len(group) < 2:
                continue
            states = collections.Counter(x["_state"] for x in group)
            repeated = [s for s in NON_COMMITTED if states.get(s, 0) >= 2]
            if repeated:
                relaxed.append((key, gk, group, repeated))
    relaxed_wasted = sum(sum(1 for x in g if x["_state"] in NON_COMMITTED)
                         for _, _, g, _ in relaxed)
    relaxed_in = sum(len(g) for _, _, g, _ in relaxed)

    # ---- exact re-submission: the same request_hash enqueued more than once in a session
    hash_groups = collections.defaultdict(list)
    for j in JOBS:
        if j.get("request_hash"):
            hash_groups[(j.get("project_dir"), j.get("session_id"), j["request_hash"])].append(j)
    resubmits = {k: v for k, v in hash_groups.items() if len(v) > 1}
    resubmit_extra = sum(len(v) - 1 for v in resubmits.values())

    # ---- failure streaks: maximal runs of consecutive non-committed jobs, any key
    streaks = []
    for key, js in by_session.items():
        run = []
        for j in js:
            if j["_state"] in NON_COMMITTED:
                run.append(j)
            else:
                if len(run) >= 2:
                    streaks.append((key, run))
                run = []
        if len(run) >= 2:
            streaks.append((key, run))
    streak_jobs = sum(len(r) for _, r in streaks)

    lens = collections.Counter(len(js) for _, js, _, _ in qualifying)
    wasted = sum(sum(1 for x in js if x["_state"] in NON_COMMITTED)
                 for _, js, _, _ in qualifying)
    in_chains = sum(len(js) for _, js, _, _ in qualifying)
    nc_total = sum(1 for j in JOBS if j["_state"] in NON_COMMITTED)
    doc.scalar("chains_len_ge2", len(multi))
    doc.scalar("chains_qualifying", len(qualifying))
    doc.scalar("jobs_in_qualifying_chains", in_chains)
    doc.scalar("non_committed_jobs_in_qualifying_chains", wasted)
    doc.scalar("pct_of_all_jobs_in_qualifying_chains", pct(in_chains, len(JOBS)))
    doc.scalar("pct_of_non_committed_that_are_repeats", pct(wasted, nc_total))
    doc.scalar("sessions_with_jobs", len(by_session))
    doc.scalar("sessions_with_a_qualifying_chain",
               len({k for k, _, _, _ in qualifying}))
    HEADLINE["retry_chains_qualifying"] = len(qualifying)
    HEADLINE["jobs_wasted_in_retry_chains"] = wasted

    doc.text(
        "**Totals (strict rule)** — {} chains of ≥2 consecutive same-target jobs exist; **{}** "
        "of them repeat a non-committed state ≥2×. Those chains hold **{} jobs** ({}% of all {} "
        "jobs), of which **{} are non-committed** — only **{}%** of the non-committed jobs in "
        "the corpus. {} of {} job-bearing sessions contain at least one such chain. That small "
        "number is a property of the *rule*, not of the corpus — see the note and the relaxed "
        "measures below.".format(
            len(multi), len(qualifying), in_chains, pct(in_chains, len(JOBS)), len(JOBS),
            wasted, pct(wasted, nc_total), len({k for k, _, _, _ in qualifying}),
            len(by_session)))

    doc.text(
        "> **The strict rule under-counts here.** Idempotency keys carry a per-operation middle "
        "segment (`diag-probe-**create**-01` vs `diag-probe-**source**-06`), so the prefix before "
        "the last `-` differs between a failed op and its follow-up, and retries are usually not "
        "adjacent — another job runs in between. Only {} of {} consecutive job pairs match the "
        "strict rule at all. Three relaxed measures follow; use them, not the strict number, as "
        "the wasted-work estimate.".format(
            sum(1 for key, js in by_session.items()
                for prev, nxt in zip(js, js[1:])
                if (idem_prefix(prev.get("idempotency_key"))
                    and idem_prefix(prev.get("idempotency_key"))
                    == idem_prefix(nxt.get("idempotency_key")))
                or (prev.get("summary") and prev.get("summary") == nxt.get("summary"))),
            sum(max(0, len(js) - 1) for js in by_session.values())))

    doc.table("length_distribution", "Chain length distribution (strict, qualifying chains)",
              ["chain length (jobs)", "chains", "jobs in them"],
              [[n, c, n * c] for n, c in sorted(lens.items())])

    # ---- relaxed A: session-wide grouping
    doc.scalar("relaxed_groups", len(relaxed))
    doc.scalar("relaxed_jobs_in_groups", relaxed_in)
    doc.scalar("relaxed_non_committed_in_groups", relaxed_wasted)
    doc.scalar("relaxed_pct_of_non_committed", pct(relaxed_wasted, nc_total))
    HEADLINE["retry_groups_relaxed"] = len(relaxed)
    HEADLINE["jobs_wasted_relaxed"] = relaxed_wasted
    rlens = collections.Counter(len(g) for _, _, g, _ in relaxed)
    doc.table("relaxed_length_distribution",
              "Relaxed A — same idempotency-prefix *or* identical summary anywhere in the "
              "session (adjacency dropped)",
              ["group size (jobs)", "groups", "jobs in them"],
              [[n, c, n * c] for n, c in sorted(rlens.items())],
              "{} groups holding {} jobs, of which **{}** are non-committed — **{}%** of all "
              "non-committed jobs are a repeat attempt at something the session already failed "
              "at least once.".format(len(relaxed), relaxed_in, relaxed_wasted,
                                      pct(relaxed_wasted, nc_total)))
    rrows = []
    for (brand, proj, sess), gk, g, repeated in sorted(
            relaxed, key=lambda r: -sum(1 for x in r[2] if x["_state"] in NON_COMMITTED))[:40]:
        seq = " → ".join("{}[{}]".format(x["_state"][:4], clip(x.get("message_signature"), 55))
                         for x in g[:10])
        if len(g) > 10:
            seq += " → …(+{})".format(len(g) - 10)
        rrows.append([len(g), sum(1 for x in g if x["_state"] in NON_COMMITTED),
                      "/".join(repeated), brand, proj, (sess or "")[:8],
                      gk[0], clip(gk[1], 45), g[0]["_day"],
                      clip(",".join(sorted({k for x in g for k in (x.get("op_kinds") or [])})),
                           45), clip(seq, 420)])
    doc.table("relaxed_top40", "Relaxed A — top 40 groups by wasted jobs",
              ["size", "non-committed", "repeated state", "brand", "project_dir", "session",
               "grouped by", "group key", "day", "op kinds", "state[message] sequence"], rrows)

    # ---- relaxed B: byte-identical re-submission
    doc.scalar("identical_request_resubmit_groups", len(resubmits))
    doc.scalar("identical_request_resubmits", resubmit_extra)
    HEADLINE["identical_request_resubmits"] = resubmit_extra
    rows = []
    for k, v in sorted(resubmits.items(), key=lambda kv: -len(kv[1]))[:25]:
        st = collections.Counter(x["_state"] for x in v)
        rows.append([len(v), v[0].get("brand"), k[0], (k[1] or "")[:8],
                     ",".join("{}:{}".format(s, n) for s, n in st.most_common()),
                     clip(v[0].get("summary"), 55), v[0]["_day"],
                     clip(v[0].get("message_signature"), 110)])
    doc.table("identical_requests",
              "Relaxed B — byte-identical requests (same `request_hash`) re-enqueued in one "
              "session",
              ["times", "brand", "project_dir", "session", "states", "summary", "day",
               "message_signature"], rows,
              ("**Never happens: all {} request hashes in the corpus are distinct.** The agents "
               "never resubmit a byte-identical request — every retry changes something (a "
               "renamed operation, a new `-v2` id, a different socket). That is why "
               "hash-based and key-based de-duplication find almost nothing here, and why the "
               "signature-based measure below is the one that fires."
               .format(len({j.get("request_hash") for j in JOBS if j.get("request_hash")}))
               if not resubmits else
               "{} request hashes were submitted more than once inside a single session, {} "
               "redundant submissions in total ({}% of all jobs).".format(
                   len(resubmits), resubmit_extra, pct(resubmit_extra, len(JOBS)))))

    # ---- relaxed D: same wall hit twice — non-committed jobs in one session sharing a
    # message_signature.  This is the measure that actually fires on this corpus, because
    # idempotency keys and request hashes are unique per attempt.
    wall_groups = collections.defaultdict(list)
    for key, js in by_session.items():
        for j in js:
            if j["_state"] in NON_COMMITTED:
                wall_groups[(key, j.get("message_signature") or "(none)")].append(j)
    walls = {k: v for k, v in wall_groups.items() if len(v) > 1}
    wall_jobs = sum(len(v) for v in walls.values())
    wall_repeat = sum(len(v) - 1 for v in walls.values())

    doc.scalar("same_wall_groups", len(walls))
    doc.scalar("same_wall_jobs", wall_jobs)
    doc.scalar("same_wall_repeat_jobs", wall_repeat)
    doc.scalar("same_wall_pct_of_non_committed", pct(wall_jobs, nc_total))
    HEADLINE["same_wall_repeat_jobs"] = wall_repeat
    HEADLINE["same_wall_pct_of_non_committed"] = pct(wall_jobs, nc_total)
    wlens = collections.Counter(len(v) for v in walls.values())
    doc.table("same_wall_distribution",
              "Relaxed D — the same wall hit twice: non-committed jobs in one session sharing a "
              "`message_signature`",
              ["times the wall was hit", "groups", "jobs in them"],
              [[n, c, n * c] for n, c in sorted(wlens.items())],
              "**This is the measure that fires on this corpus.** {} (session, error) pairs "
              "recur; they account for **{} of {} non-committed jobs ({}%)**, of which {} are "
              "pure repeats of an error the session had already seen. Because idempotency keys "
              "and request hashes are unique per attempt, the agent is always *changing* the "
              "request between tries — and still landing on the same "
              "error.".format(len(walls), wall_jobs, nc_total, pct(wall_jobs, nc_total),
                               wall_repeat))
    wrows = []
    for (key, sig), v in sorted(walls.items(), key=lambda kv: -len(kv[1]))[:40]:
        states = collections.Counter(x["_state"] for x in v)
        span = sorted(x.get("created_at") or "" for x in v)
        wrows.append([len(v), v[0].get("brand"), key[1], (key[2] or "")[:8],
                      ",".join("{}:{}".format(a, b) for a, b in states.most_common()),
                      clip(",".join(sorted({k for x in v for k in (x.get("op_kinds") or [])})),
                           45),
                      clip(span[0], 19), clip(span[-1], 19),
                      len({x.get("idempotency_key") for x in v}), clip(sig, 150)])
    doc.table("same_wall_top40", "Relaxed D — top 40 repeated walls",
              ["hits", "brand", "project_dir", "session", "states", "op kinds", "first", "last",
               "distinct idempotency keys", "message_signature"], wrows)

    # ---- relaxed C: consecutive failure streaks
    slens = collections.Counter(len(r) for _, r in streaks)
    doc.scalar("failure_streaks", len(streaks))
    doc.scalar("jobs_in_failure_streaks", streak_jobs)
    doc.scalar("pct_of_non_committed_in_streaks", pct(streak_jobs, nc_total))
    HEADLINE["jobs_in_failure_streaks"] = streak_jobs
    doc.table("streak_distribution",
              "Relaxed C — consecutive non-committed runs of ≥2 (any key)",
              ["streak length", "streaks", "jobs in them"],
              [[n, c, n * c] for n, c in sorted(slens.items())],
              "{} streaks holding {} jobs — {}% of all non-committed jobs arrive back-to-back, "
              "i.e. the session was already stuck when it enqueued them.".format(
                  len(streaks), streak_jobs, pct(streak_jobs, nc_total)))
    rows = []
    for (brand, proj, sess), r in sorted(streaks, key=lambda s: -len(s[1]))[:25]:
        seq = " → ".join("{}[{}]".format(x["_state"][:4], clip(x.get("message_signature"), 50))
                         for x in r[:10])
        if len(r) > 10:
            seq += " → …(+{})".format(len(r) - 10)
        rows.append([len(r), brand, proj, (sess or "")[:8], r[0]["_day"],
                     clip(",".join(sorted({k for x in r for k in (x.get("op_kinds") or [])})),
                          45), clip(seq, 420)])
    doc.table("streak_top25", "Relaxed C — longest failure streaks",
              ["length", "brand", "project_dir", "session", "day", "op kinds",
               "state[message] sequence"], rows)

    rep = collections.Counter()
    for _, _, repeated, _ in qualifying:
        for s in repeated:
            rep[s] += 1
    doc.table("repeated_states", "Which state repeats inside the chain",
              ["state", "chains"], rep.most_common())

    opk = collections.Counter()
    for _, js, _, _ in qualifying:
        for k in {k for x in js for k in (x.get("op_kinds") or [])}:
            opk[k] += 1
    doc.table("chain_op_kinds", "Op kinds present in qualifying chains",
              ["op kind", "chains"], opk.most_common(20))

    wk = collections.Counter()
    for _, js, _, _ in qualifying:
        wk[js[0]["_week"]] += 1
    doc.table("chains_by_week", "Qualifying chains by ISO week",
              ["ISO week", "chains"], [[w, n] for w, n in sorted(wk.items()) if w])

    proj = collections.Counter()
    for key, js, _, _ in qualifying:
        proj["{} / {}".format(key[0], key[1])] += sum(
            1 for x in js if x["_state"] in NON_COMMITTED)
    doc.table("chains_by_project", "Wasted (non-committed, in-chain) jobs by project",
              ["project", "wasted jobs"], proj.most_common(20))

    sigs = collections.Counter()
    for _, js, _, _ in qualifying:
        for x in js:
            if x["_state"] in NON_COMMITTED:
                sigs[x.get("message_signature") or "(none)"] += 1
    doc.table("chain_signatures", "Message signatures inside qualifying chains (top 40)",
              ["count", "message_signature"],
              [[n, clip(s, 160)] for s, n in sigs.most_common(40)])

    qualifying.sort(key=lambda c: (-sum(1 for x in c[1] if x["_state"] in NON_COMMITTED),
                                   -len(c[1])))
    rows = []
    for (brand, proj_dir, sess), js, repeated, states in qualifying[:40]:
        seq = " → ".join(
            "{}[{}]".format(x["_state"][:4], clip(x.get("message_signature"), 55))
            for x in js[:10])
        if len(js) > 10:
            seq += " → …(+{})".format(len(js) - 10)
        rows.append([
            len(js), sum(1 for x in js if x["_state"] in NON_COMMITTED),
            "/".join(repeated), brand, proj_dir, (sess or "")[:8],
            clip(js[0].get("summary"), 55), clip(js[0].get("idempotency_key"), 34),
            js[0]["_day"],
            clip(",".join(sorted({k for x in js for k in (x.get("op_kinds") or [])})), 50),
            clip(seq, 420),
        ])
    doc.table("top40", "Top 40 chains by wasted jobs",
              ["len", "non-committed", "repeated state", "brand", "project_dir", "session",
               "summary", "idempotency prefix", "day", "op kinds",
               "state[message] sequence"], rows)
    doc.write()


# ================================================================ 4. tool friction
def build_tool_friction():
    doc = Doc(
        "tool-friction",
        "Tool friction",
        "Source: `.log-mine/tool-calls.jsonl`. `source=codex` rows come from Codex rollouts "
        "(code-mode `exec` is unwrapped into the Vino/GPTino tool it called; `exec(other)` is an "
        "exec that called no plugin tool), `source=claude` from Claude CLI transcripts.",
    )
    per = collections.defaultdict(lambda: {"calls": 0, "errors": 0, "dur": [], "rlen": [],
                                           "alen": [], "spill": 0,
                                           "err_kinds": collections.Counter()})
    per_turn = collections.Counter()
    turn_args = collections.Counter()
    turn_args_meta = {}
    err_sigs = collections.defaultdict(lambda: {"n": 0, "tools": collections.Counter(),
                                                "ex": None, "days": []})
    big = []
    total = 0
    bench_calls = 0
    running_excluded = 0
    running_by_tool = collections.Counter()
    by_source = collections.Counter()
    for rec in read_jsonl(path("tool-calls.jsonl")):
        total += 1
        by_source[rec.get("source")] += 1
        if rec.get("bench"):
            bench_calls += 1
        key = (rec.get("tool"), rec.get("source"))
        p = per[key]
        p["calls"] += 1
        if rec.get("is_error"):
            p["errors"] += 1
            p["err_kinds"][rec.get("error_kind")] += 1
            sig = normalize_signature(rec.get("error_text") or "(no error text)", 110)
            e = err_sigs[sig]
            e["n"] += 1
            e["tools"][rec.get("tool")] += 1
            if e["ex"] is None:
                e["ex"] = rec
            d = day(rec.get("at"))
            if d:
                e["days"].append(d)
        if rec.get("duration_ms") is not None:
            p["dur"].append(rec["duration_ms"])
        # result-size percentiles exclude `result_kind='running'` placeholder results
        if is_running_stub(rec):
            running_excluded += 1
            running_by_tool[rec.get("tool")] += 1
        elif rec.get("result_len") is not None:
            p["rlen"].append(rec["result_len"])
        if rec.get("args_len") is not None:
            p["alen"].append(rec["args_len"])
        rl = rec.get("result_len") or 0
        if rl > 60000 or rec.get("error_kind") == "spill":
            p["spill"] += 1
            big.append(rec)
        per_turn[(rec.get("thread_id"), rec.get("turn_index"))] += 1
        akey = (rec.get("thread_id"), rec.get("turn_index"), rec.get("tool"),
                rec.get("args_preview"))
        turn_args[akey] += 1
        if akey not in turn_args_meta:
            turn_args_meta[akey] = rec

    total_errors = sum(p["errors"] for p in per.values())
    doc.scalar("tool_calls_total", total)
    doc.scalar("by_source", dict(by_source))
    doc.scalar("bench_tool_calls", bench_calls)
    doc.scalar("tool_errors_total", total_errors)
    doc.scalar("tool_error_rate_pct", pct(total_errors, total))
    doc.scalar("running_stub_calls_excluded_from_result_len", running_excluded)
    doc.scalar("running_stub_calls_by_tool", dict(running_by_tool.most_common()))
    HEADLINE["tool_calls_total"] = total
    HEADLINE["tool_error_rate_pct"] = pct(total_errors, total)
    HEADLINE["running_stub_calls_excluded"] = running_excluded

    rows = []
    for (tool, src), p in sorted(per.items(), key=lambda kv: -kv[1]["calls"]):
        rows.append([
            tool, src, p["calls"], p["errors"], pct(p["errors"], p["calls"]),
            quantile(p["dur"], 0.5), quantile(p["dur"], 0.9),
            quantile(p["rlen"], 0.5), quantile(p["rlen"], 0.9), max(p["rlen"] or [0]),
            quantile(p["alen"], 0.5), p["spill"],
            ",".join("{}:{}".format(k, v) for k, v in p["err_kinds"].most_common(3)),
        ])
    doc.table("per_tool", "Per tool × source",
              ["tool", "source", "calls", "errors", "error %", "dur p50 ms", "dur p90 ms",
               "result_len p50", "result_len p90", "result_len max", "args_len p50",
               "spill/>60k", "error kinds"], rows,
              "{} calls total ({} bench), {} errors ({}%). **The three `result_len` columns "
              "exclude the {} calls whose `result_kind` is `running`** — for those the "
              "transcript recorded a ~62-character `Script running…` placeholder instead of "
              "the real result, so their size measures the recorder, not the tool. They are "
              "still counted in `calls`, `errors` and the duration columns. Top contributors: "
              "{}.".format(
                  total, bench_calls, total_errors, pct(total_errors, total), running_excluded,
                  ", ".join("{} {}".format(v, k) for k, v in running_by_tool.most_common(5))
                  or "none"))
    worst = sorted(per.items(), key=lambda kv: -kv[1]["errors"])[:1]
    if worst:
        HEADLINE["worst_tool_by_errors"] = "{} ({} errors, {}%)".format(
            worst[0][0][0], worst[0][1]["errors"],
            pct(worst[0][1]["errors"], worst[0][1]["calls"]))

    repeats = [(k, n) for k, n in turn_args.items() if n > 1 and k[3] is not None]
    repeats.sort(key=lambda kv: -kv[1])
    dup_calls = sum(n - 1 for _, n in repeats)
    doc.scalar("identical_arg_repeat_groups", len(repeats))
    doc.scalar("redundant_calls_from_repeats", dup_calls)
    doc.scalar("redundant_calls_pct", pct(dup_calls, total))
    HEADLINE["redundant_identical_tool_calls"] = dup_calls
    by_tool = collections.Counter()
    for k, n in repeats:
        by_tool[k[2]] += n - 1
    doc.table("repeat_by_tool",
              "Redundant repeats by tool (same tool + byte-identical args, same turn)",
              ["tool", "redundant calls"], by_tool.most_common(25),
              "{} groups; {} redundant calls ({}% of all tool calls). A repeat inside one turn is "
              "the agent asking the same question twice without new information in between — "
              "except for the polling tools (`wait`, `wait_agent`, `job_status`, `list_agents`), "
              "where repeating the same call *is* the design. Excluding those four leaves "
              "**{}** redundant calls ({}%), and `inspect_outputs` + `artifact_read` + "
              "`snapshot_read` alone account for {} of them.".format(
                  len(repeats), dup_calls, pct(dup_calls, total),
                  dup_calls - sum(by_tool[t] for t in
                                  ("wait", "wait_agent", "job_status", "list_agents")),
                  pct(dup_calls - sum(by_tool[t] for t in
                                      ("wait", "wait_agent", "job_status", "list_agents")),
                      total),
                  sum(by_tool[t] for t in
                      ("inspect_outputs", "artifact_read", "snapshot_read"))))
    rows = []
    for k, n in repeats[:40]:
        rec = turn_args_meta[k]
        rows.append([n, k[2], rec.get("source"), (k[0] or "")[:8], k[1], rec.get("brand"),
                     rec.get("project_dir"), day(rec.get("at")), clip(k[3], 150)])
    doc.table("repeat_top40", "Top 40 identical-args repeat groups",
              ["calls", "tool", "source", "thread", "turn", "brand", "project_dir", "day",
               "args_preview"], rows)

    counts = list(per_turn.values())
    d = dist(counts)
    doc.scalar("calls_per_turn", d)
    hist = collections.Counter()
    for c in counts:
        b = ("1" if c == 1 else "2-3" if c <= 3 else "4-7" if c <= 7 else "8-15" if c <= 15
             else "16-31" if c <= 31 else "32-63" if c <= 63 else "64+")
        hist[b] += 1
    order = ["1", "2-3", "4-7", "8-15", "16-31", "32-63", "64+"]
    doc.table("calls_per_turn", "Calls per turn distribution",
              ["bucket", "turns", "% of turns"],
              [[b, hist.get(b, 0), pct(hist.get(b, 0), len(counts))] for b in order],
              "n={} turns with ≥1 tool call · p50={} · p90={} · p99={} · "
              "max={}".format(d["n"], d["p50"], d["p90"], d["p99"], d["max"]))

    rows = []
    for sig, e in sorted(err_sigs.items(), key=lambda kv: -kv[1]["n"])[:60]:
        days = sorted(e["days"])
        ex = e["ex"] or {}
        rows.append([e["n"], ",".join("{}:{}".format(t, c) for t, c in e["tools"].most_common(3)),
                     clip(sig, 170), days[0] if days else None, days[-1] if days else None,
                     ex.get("brand"), ex.get("project_dir"), (ex.get("thread_id") or "")[:8]])
    doc.scalar("distinct_error_signatures", len(err_sigs))
    doc.table("error_signatures", "Top 60 error_text signatures",
              ["count", "tools", "error signature", "first", "last", "brand", "project_dir",
               "thread"], rows,
              "{} distinct signatures over {} failing calls.".format(len(err_sigs), total_errors))

    per_tool_sig = collections.defaultdict(collections.Counter)
    for sig, e in err_sigs.items():
        for t, c in e["tools"].items():
            per_tool_sig[t][sig] += c
    rows = []
    for tool, ctr in sorted(per_tool_sig.items(), key=lambda kv: -sum(kv[1].values())):
        for sig, n in ctr.most_common(3):
            rows.append([tool, sum(ctr.values()), n, clip(sig, 170)])
    doc.table("error_signatures_per_tool", "Top 3 error signatures per tool",
              ["tool", "tool errors", "count", "error signature"], rows,
              "Per-tool so a loud tool cannot hide a quiet tool's own worst failure.")

    big.sort(key=lambda r: -(r.get("result_len") or 0))
    doc.scalar("oversize_results", len(big))
    HEADLINE["oversize_tool_results"] = len(big)
    by_tool_big = collections.Counter(r.get("tool") for r in big)
    doc.table("spill_by_tool", "Oversize results (>60,000 chars) or `error_kind=spill`, by tool",
              ["tool", "events"], by_tool_big.most_common(),
              "{} events out of {} calls ({}%). `error_kind=spill` specifically: {}.".format(
                  len(big), total, pct(len(big), total),
                  sum(1 for r in big if r.get("error_kind") == "spill")))
    rows = [[r.get("result_len"), r.get("tool"), r.get("source"), r.get("error_kind"),
             r.get("brand"), r.get("project_dir"), day(r.get("at")),
             (r.get("thread_id") or "")[:8], r.get("turn_index"),
             clip(r.get("args_preview"), 110)] for r in big[:40]]
    doc.table("spill_top40", "40 largest results",
              ["result_len", "tool", "source", "error_kind", "brand", "project_dir", "day",
               "thread", "turn", "args_preview"], rows)
    doc.write()


# ================================================================ 5. session lifecycle
def build_session_lifecycle():
    doc = Doc(
        "session-lifecycle",
        "Session lifecycle",
        "Sources: `.log-mine/turn-events.jsonl` (Codex rollouts + Claude transcripts), "
        "`messages.jsonl` (the panel's own runtime.db — what the user actually saw), "
        "`threads.jsonl`, `hostlog-events.jsonl`.",
    )
    type_ctr = collections.Counter()
    week_ctr = collections.defaultdict(collections.Counter)
    ver_ctr = collections.defaultdict(collections.Counter)
    thread_events = collections.defaultdict(list)
    interrupted_dur = []
    error_details = collections.Counter()
    effort_ctr = collections.Counter()
    for rec in read_jsonl(path("turn-events.jsonl")):
        t = rec.get("type")
        type_ctr[t] += 1
        if t in ("interrupted", "compacted", "context_compacted", "error", "task_started",
                 "task_complete", "user_message"):
            week_ctr[t][iso_week(rec.get("at"))] += 1
            ver_ctr[t][rec.get("version")] += 1
        if t in ("task_started", "task_complete"):
            thread_events[rec.get("thread_id")].append(
                (rec.get("at"), t, rec.get("turn_index")))
        if t == "interrupted":
            m = re.search(r"duration_ms=(\d+)", rec.get("detail") or "")
            if m:
                interrupted_dur.append(int(m.group(1)))
        elif t == "error":
            error_details[normalize_signature(rec.get("detail") or "", 110)] += 1
        elif t == "turn_context":
            m = re.search(r"effort=(\S+)", rec.get("detail") or "")
            if m:
                effort_ctr[m.group(1)] += 1

    doc.scalar("turn_event_types", dict(type_ctr))
    doc.table("event_totals", "Lifecycle event totals (rollouts)", ["event", "count"],
              [[k, type_ctr.get(k, 0)] for k in
               ("user_message", "task_started", "task_complete", "compacted",
                "context_compacted", "interrupted", "error", "session_meta")])
    HEADLINE["interrupted_turns"] = type_ctr.get("interrupted", 0)
    HEADLINE["compactions"] = type_ctr.get("compacted", 0)

    weeks = sorted({w for c in week_ctr.values() for w in c if w})
    rows = []
    for w in weeks:
        st = week_ctr["task_started"].get(w, 0)
        rows.append([w, week_ctr["user_message"].get(w, 0), st,
                     week_ctr["task_complete"].get(w, 0), week_ctr["compacted"].get(w, 0),
                     week_ctr["interrupted"].get(w, 0),
                     pct(week_ctr["interrupted"].get(w, 0), st), week_ctr["error"].get(w, 0)])
    doc.table("by_week", "By ISO week",
              ["ISO week", "user messages", "turns started", "turns completed", "compactions",
               "interrupted", "interrupted % of turns", "rollout errors"], rows)

    rows = []
    for ver in sorted({v for c in ver_ctr.values() for v in c}, key=str):
        st = ver_ctr["task_started"].get(ver, 0)
        rows.append([ver, ver_ctr["user_message"].get(ver, 0), st,
                     ver_ctr["task_complete"].get(ver, 0), ver_ctr["compacted"].get(ver, 0),
                     ver_ctr["interrupted"].get(ver, 0),
                     pct(ver_ctr["interrupted"].get(ver, 0), st), ver_ctr["error"].get(ver, 0)])
    doc.table("by_version", "By version",
              ["version", "user messages", "turns started", "turns completed", "compactions",
               "interrupted", "interrupted % of turns", "rollout errors"], rows)

    doc.table("effort", "Turn effort setting seen in rollouts (`turn_context`)",
              ["effort", "turns"], effort_ctr.most_common())
    doc.table("rollout_errors", "Rollout `error` event signatures", ["count", "signature"],
              [[n, clip(s, 170)] for s, n in error_details.most_common(20)])

    # --- Replay filter.  A forked / resumed Codex rollout rewrites the whole prior history into
    # the new file in one burst, so every replayed turn carries the *fork's* wall clock, not the
    # original one.  Those turns look instantaneous.  A "write burst" = consecutive turn events
    # <50 ms apart; a burst containing ≥2 `task_started` is a replay and its turns are dropped.
    sub_agent_thread = {t.get("thread_id"): bool(t.get("sub_agent")) for t in THREADS}
    live_start = {}             # (thread_id, turn_index) -> start time, genuinely timed turns
    durs, replayed_pairs = [], 0
    durs_by_kind = {True: [], False: []}
    for thread, evs in thread_events.items():
        evs.sort(key=lambda e: e[0] or "")
        parsed = [(parse_iso(a), t, ti) for a, t, ti in evs]
        parsed = [p for p in parsed if p[0]]
        burst_id, bursts = 0, []
        for i, (at, t, ti) in enumerate(parsed):
            if i and (at - parsed[i - 1][0]).total_seconds() >= 0.05:
                burst_id += 1
            bursts.append(burst_id)
        starts_in_burst = collections.Counter(
            b for b, (_, t, _) in zip(bursts, parsed) if t == "task_started")
        open_start = None
        for (at, t, ti), b in zip(parsed, bursts):
            replay = starts_in_burst.get(b, 0) >= 2
            if t == "task_started":
                open_start = None if replay else (at, ti, thread)
                if replay:
                    replayed_pairs += 1
                else:
                    live_start[(thread, ti)] = at
            elif t == "task_complete" and open_start:
                if at >= open_start[0]:
                    ms = int((at - open_start[0]).total_seconds() * 1000)
                    durs.append(ms)
                    durs_by_kind[sub_agent_thread.get(thread, False)].append(ms)
                open_start = None
    d = dist(durs)
    doc.scalar("turn_duration_ms", d)
    doc.scalar("replayed_turn_starts_dropped", replayed_pairs)
    doc.table("turn_duration", "Turn duration (task_started → task_complete), replay-filtered",
              ["metric", "ms", "human"],
              [[k, d[k], hms(d[k]) if k != "n" else d[k]] for k in
               ("n", "min", "p50", "p90", "p99", "max", "mean")],
              "{} task_started events were dropped as replayed history: {} of {} rollout threads "
              "are sub-agent threads and forked/resumed rollouts rewrite the whole prior "
              "transcript in one write burst, stamping every replayed turn with the fork's clock. "
              "Without this filter the median turn measures 4 ms, which is an artefact, not a "
              "latency.".format(replayed_pairs,
                                sum(1 for t in THREADS if t.get("sub_agent")), len(THREADS)))
    HEADLINE["turn_duration_p50_s"] = round((d["p50"] or 0) / 1000.0, 1)
    HEADLINE["top_level_turn_duration_p50_s"] = round(
        (dist(durs_by_kind[False])["p50"] or 0) / 1000.0, 1)
    doc.table("turn_duration_by_kind", "Turn duration split by thread kind",
              ["thread kind", "turns", "p50 ms", "p90 ms", "p99 ms", "max ms", "mean ms"],
              [[("sub-agent" if k else "top-level"), dk["n"], dk["p50"], dk["p90"], dk["p99"],
                dk["max"], dk["mean"]] for k, dk in
               ((k, dist(v)) for k, v in sorted(durs_by_kind.items()))],
              "The single distribution above is bimodal because it mixes short sub-agent turns "
              "with long top-level ones; this splits them.")

    di = dist(interrupted_dur)
    doc.scalar("interrupted_duration_ms", di)
    doc.table("interrupted_duration",
              "Interrupted-turn duration (from the `interrupted` event's own duration_ms)",
              ["metric", "ms", "human"],
              [[k, di[k], hms(di[k]) if k != "n" else di[k]] for k in
               ("n", "min", "p50", "p90", "p99", "max", "mean")])

    first_tool = {}
    for rec in read_jsonl(path("tool-calls.jsonl")):
        key = (rec.get("thread_id"), rec.get("turn_index"))
        at = rec.get("at")
        if at and (key not in first_tool or at < first_tool[key]):
            first_tool[key] = at
    ttf, no_tool = [], 0
    for key, a in live_start.items():
        t1 = first_tool.get(key)
        if not t1:
            no_tool += 1
            continue
        b = parse_iso(t1)
        if b and b >= a:
            ttf.append(int((b - a).total_seconds() * 1000))
    dt = dist(ttf)
    doc.scalar("time_to_first_tool_ms", dt)
    doc.scalar("live_turns", len(live_start))
    doc.scalar("turns_with_a_tool_call", len(ttf))
    doc.scalar("turns_with_no_tool_call", no_tool)
    doc.table("time_to_first_tool",
              "Time from turn start to first tool call (replay-filtered turns only)",
              ["metric", "ms", "human"],
              [[k, dt[k], hms(dt[k]) if k != "n" else dt[k]] for k in
               ("n", "min", "p50", "p90", "p99", "max", "mean")],
              "{} genuinely-timed turns; {} reached a tool call, {} never did (pure-answer "
              "turns, or dead turns).".format(len(live_start), len(ttf), no_tool))
    HEADLINE["time_to_first_tool_p50_s"] = round((dt["p50"] or 0) / 1000.0, 1)

    tot = collections.Counter()
    for th in THREADS:
        for k in ("compactions", "interrupted", "aborted", "parse_errors", "tool_errors",
                  "tool_calls", "turns"):
            tot[k] += th.get(k) or 0
    doc.table("thread_totals",
              "Thread totals (`threads.jsonl`, {} threads)".format(len(THREADS)),
              ["metric", "total"],
              [[k, tot[k]] for k in ("turns", "tool_calls", "tool_errors", "compactions",
                                     "interrupted", "aborted", "parse_errors")])
    HEADLINE["threads_aborted"] = tot["aborted"]
    sub = collections.Counter()
    for th in THREADS:
        sub[(th.get("source"), bool(th.get("sub_agent")))] += 1
    doc.table("thread_kinds", "Thread kinds",
              ["source", "sub-agent", "threads", "turns", "tool calls"],
              [[k[0], k[1], n,
                sum(t.get("turns") or 0 for t in THREADS
                    if (t.get("source"), bool(t.get("sub_agent"))) == k),
                sum(t.get("tool_calls") or 0 for t in THREADS
                    if (t.get("source"), bool(t.get("sub_agent"))) == k)]
               for k, n in sub.most_common()],
              "Most rollout files are spawned sub-agents, so any 'per session' reading of the "
              "rollout data is really 'per agent run'.")
    bad = [th for th in THREADS if (th.get("interrupted") or 0) + (th.get("aborted") or 0)
           + (th.get("compactions") or 0) > 0]
    bad.sort(key=lambda t: -((t.get("compactions") or 0) * 10 + (t.get("interrupted") or 0)
                             + (t.get("aborted") or 0)))
    doc.table("worst_threads", "Threads with the most compaction / interruption (top 25)",
              ["thread", "brand", "project_dir", "project_name", "turns", "tool calls",
               "tool errors", "compactions", "interrupted", "aborted", "MB", "started"],
              [[(t.get("thread_id") or "")[:8], t.get("brand"), t.get("project_dir"),
                clip(t.get("project_name"), 26), t.get("turns"), t.get("tool_calls"),
                t.get("tool_errors"), t.get("compactions"), t.get("interrupted"),
                t.get("aborted"), round((t.get("bytes") or 0) / 1048576.0, 1),
                day(t.get("started_at"))] for t in bad[:25]])

    markers = [
        ("could not recover", r"could not recover"),
        ("interrupted", r"interrupted"),
        ("compaction", r"compact"),
        ("AgentHost restart", r"AgentHost restart"),
        ("OAuth", r"OAuth"),
        ("quota / usage limit", r"usage limit|credits|quota|429"),
        ("503 / unavailable", r"503|unavailable"),
        ("JSON-RPC error", r"-326\d\d"),
        ("imported context", r"^Imported|Imported conversation"),
    ]
    sys_msgs = [m for m in MESSAGES if m.get("role") == "system"]
    rows = []
    for label, pat in markers:
        hits = [m for m in sys_msgs if re.search(pat, m.get("content") or "", re.IGNORECASE)]
        vers = collections.Counter(m.get("version") for m in hits)
        wk = collections.Counter(iso_week(m.get("created_at")) for m in hits)
        rows.append([label, len(hits),
                     ",".join("{}:{}".format(v, n) for v, n in vers.most_common()),
                     clip(",".join("{}:{}".format(w, n) for w, n in sorted(wk.items()) if w), 90),
                     clip(hits[0].get("content") if hits else "", 130)])
    doc.table("system_markers",
              "System-message markers (runtime.db, {} system messages)".format(len(sys_msgs)),
              ["marker", "messages", "versions", "weeks", "example"], rows,
              "These are the strings the *user* saw in the panel, not internal telemetry.")
    HEADLINE["system_messages"] = len(sys_msgs)

    sig_ctr = collections.Counter()
    sig_meta = {}
    for m in sys_msgs:
        s = m.get("content_signature") or normalize_signature(m.get("content"), 110)
        sig_ctr[s] += 1
        sig_meta.setdefault(s, {"m": m, "vers": collections.Counter(), "days": []})
        sig_meta[s]["vers"][m.get("version")] += 1
        if m.get("created_day"):
            sig_meta[s]["days"].append(m.get("created_day"))
    rows = []
    for s, n in sig_ctr.most_common(40):
        meta = sig_meta[s]
        days = sorted(meta["days"])
        rows.append([n, meta["m"].get("phase"),
                     ",".join("{}:{}".format(v, c) for v, c in meta["vers"].most_common()),
                     days[0] if days else None, days[-1] if days else None, clip(s, 170)])
    doc.table("system_signatures", "System message signatures",
              ["count", "phase", "versions", "first", "last", "signature"], rows)

    cont_re = re.compile(r"^\s*(계속|continue|go on|이어서|진행)\b",
                         re.IGNORECASE)
    cont = [m for m in MESSAGES if m.get("role") == "user"
            and (m.get("is_continue") or m.get("is_continue_strict")
                 or cont_re.match(m.get("content") or ""))]
    strict = [m for m in cont if m.get("is_continue_strict")]
    by_week = collections.Counter(iso_week(m.get("created_at")) for m in cont)
    by_proj = collections.Counter("{}/{}".format(m.get("brand"), m.get("project_dir"))
                                  for m in cont)
    user_total = sum(1 for m in MESSAGES if m.get("role") == "user")
    doc.scalar("continue_prompts", len(cont))
    doc.scalar("continue_prompts_strict", len(strict))
    doc.scalar("user_messages", user_total)
    doc.scalar("continue_pct_of_user_messages", pct(len(cont), user_total))
    HEADLINE["continue_prompts"] = len(cont)
    doc.table("continue_by_week",
              "`계속` / `Continue` prompts by week (dead-turn proxy)",
              ["ISO week", "continue prompts"],
              [[w, n] for w, n in sorted(by_week.items()) if w],
              "{} of {} user messages ({}%) are a continue nudge ({} of them strict, i.e. the "
              "message is nothing but the nudge).".format(
                  len(cont), user_total, pct(len(cont), user_total), len(strict)))
    doc.table("continue_by_project",
              "`계속` / `Continue` prompts by project", ["project", "prompts"],
              by_proj.most_common(15))
    doc.table("continue_examples", "Continue prompts (all)",
              ["at", "brand", "project_dir", "session", "prev role/phase", "gap s", "text"],
              [[clip(m.get("created_at"), 19), m.get("brand"), m.get("project_dir"),
                (m.get("session_id") or "")[:8],
                "{}/{}".format(m.get("prev_role"), m.get("prev_phase")), m.get("gap_seconds"),
                clip(m.get("content"), 120)] for m in
               sorted(cont, key=lambda m: m.get("created_at") or "")])

    lv = collections.Counter()
    for h in HOSTLOG:
        lv[(h.get("level"), h.get("category"))] += 1
    doc.table("hostlog_levels", "host.log kept records by level × category",
              ["level", "category", "records"], [[k[0], k[1], n] for k, n in lv.most_common()])
    hs = collections.Counter()
    hmeta = {}
    for h in HOSTLOG:
        if h.get("level") in ("Warning", "Error"):
            s = h.get("message_signature") or ""
            hs[s] += 1
            hmeta.setdefault(s, {"h": h, "days": []})
            if h.get("at_day"):
                hmeta[s]["days"].append(h.get("at_day"))
    rows = []
    for s, n in hs.most_common(30):
        days = sorted(hmeta[s]["days"])
        h = hmeta[s]["h"]
        rows.append([n, h.get("level"), h.get("category"), days[0] if days else None,
                     days[-1] if days else None, clip(s, 150)])
    doc.table("hostlog_signatures", "host.log Warning/Error signatures",
              ["count", "level", "category", "first", "last", "signature"], rows,
              "host.log only exists from alpha.7 onward, so this is a 12-day window, not the "
              "whole corpus.")
    HEADLINE["hostlog_warn_error"] = sum(hs.values())
    doc.write()


# ================================================================ 6. user signals
SIGNAL_PATTERNS = [
    ("왜", re.compile(r"왜")),
    ("안됨/안되", re.compile(r"안\s?됨|안\s?되|안돼|안 돼")),
    ("다시", re.compile(r"다시")),
    ("아니", re.compile(r"아니")),
    ("틀렸", re.compile(r"틀렸|틀린|틀림")),
    ("잘못", re.compile(r"잘못")),
    ("뭐야", re.compile(r"뭐야|뭐임|뭐냐|뭐지")),
    ("되돌/롤백/취소", re.compile(r"되돌|롤백|취소")),
    ("하지마", re.compile(r"하지\s?마")),
    ("그게아니", re.compile(r"그게\s?아니|그거\s?아니")),
    ("?? (2+)", re.compile(r"\?[^?]{0,3}\?")),
    ("not", re.compile(r"\bnot\b", re.IGNORECASE)),
    ("wrong", re.compile(r"\bwrong\b", re.IGNORECASE)),
    ("again", re.compile(r"\bagain\b", re.IGNORECASE)),
    ("revert/undo", re.compile(r"\brevert\b|\bundo\b|\brollback\b", re.IGNORECASE)),
]


def build_user_signals():
    doc = Doc(
        "user-signals",
        "User correction / frustration signals",
        "A *signal* = a `role=user` message in runtime.db that directly follows an assistant "
        "message and matches at least one correction/frustration marker. The markers are lexical, "
        "so this over-counts (`아니` inside `아니라`, `not` inside an English "
        "sentence, a `??` inside a code snippet). Every hit is listed in full below so a reviewer "
        "can judge each one instead of trusting the count.",
    )
    by_session = collections.defaultdict(list)
    for m in MESSAGES:
        by_session[(m.get("project_dir"), m.get("session_id"))].append(m)
    for v in by_session.values():
        v.sort(key=lambda m: (m.get("seq") if m.get("seq") is not None else 0,
                              m.get("created_at") or ""))

    hits = []
    marker_ctr = collections.Counter()
    user_after_assistant = 0
    for msgs in by_session.values():
        prev = None
        for m in msgs:
            if m.get("role") == "user" and prev is not None and prev.get("role") == "assistant":
                user_after_assistant += 1
                content = m.get("content") or ""
                found = [lab for lab, rx in SIGNAL_PATTERNS if rx.search(content)]
                if found:
                    for lab in found:
                        marker_ctr[lab] += 1
                    hits.append((m, prev, found))
            prev = m

    doc.scalar("user_messages_after_assistant", user_after_assistant)
    doc.scalar("signal_messages", len(hits))
    doc.scalar("signal_rate_pct", pct(len(hits), user_after_assistant))
    HEADLINE["user_correction_signals"] = len(hits)
    HEADLINE["user_correction_signal_pct"] = pct(len(hits), user_after_assistant)
    doc.text("**{}** of the **{}** user messages that reply to an assistant message match at "
             "least one marker (**{}%**).".format(len(hits), user_after_assistant,
                                                  pct(len(hits), user_after_assistant)))

    doc.table("by_marker", "Hits per marker (one message can match several)",
              ["marker", "messages"], marker_ctr.most_common())

    by_week = collections.Counter(iso_week(m.get("created_at")) for m, _, _ in hits)
    all_week = collections.Counter(iso_week(m.get("created_at")) for m in MESSAGES
                                   if m.get("role") == "user")
    doc.table("by_week", "By ISO week",
              ["ISO week", "signal messages", "all user messages", "signal %"],
              [[w, by_week.get(w, 0), all_week.get(w, 0),
                pct(by_week.get(w, 0), all_week.get(w, 0))] for w in sorted(all_week) if w])

    by_proj = collections.Counter()
    proj_all = collections.Counter()
    proj_name = {}
    for m, _, _ in hits:
        k = "{} / {}".format(m.get("brand"), m.get("project_dir"))
        by_proj[k] += 1
        proj_name[k] = m.get("project_name")
    for m in MESSAGES:
        if m.get("role") == "user":
            k = "{} / {}".format(m.get("brand"), m.get("project_dir"))
            proj_all[k] += 1
            proj_name.setdefault(k, m.get("project_name"))
    doc.table("by_project", "By project",
              ["project", "project_name", "signal messages", "all user messages", "signal %"],
              [[k, clip(proj_name.get(k), 30), n, proj_all.get(k, 0),
                pct(n, proj_all.get(k, 0))] for k, n in by_proj.most_common()])

    rows = []
    for m, prev, found in sorted(hits, key=lambda h: h[0].get("created_at") or ""):
        rows.append([clip(m.get("created_at"), 19), m.get("brand"), m.get("project_dir"),
                     (m.get("session_id") or "")[:8], m.get("model"), "/".join(found),
                     m.get("gap_seconds"), clip(m.get("content"), 300),
                     clip(prev.get("content"), 200)])
    doc.table("all_signals", "Every signal message ({}), oldest first".format(len(hits)),
              ["at", "brand", "project_dir", "session", "model", "markers", "gap s",
               "user message (≤300)", "preceding assistant message (≤200)"], rows)

    rep = {}
    for key, msgs in by_session.items():
        seen = collections.defaultdict(list)
        for m in msgs:
            if m.get("role") != "user":
                continue
            sig = m.get("content_signature") or normalize_signature(m.get("content"), 110)
            if not sig:
                continue
            seen[sig].append(m)
        for sig, ms in seen.items():
            if len(ms) > 1:
                rep[(key, sig)] = ms
    total_rep = sum(len(v) - 1 for v in rep.values())
    doc.scalar("repeated_user_message_groups", len(rep))
    doc.scalar("repeated_user_messages", total_rep)
    HEADLINE["repeated_user_messages"] = total_rep
    rows = []
    for (key, sig), ms in sorted(rep.items(), key=lambda kv: -len(kv[1])):
        rows.append([len(ms), ms[0].get("brand"), key[0], (key[1] or "")[:8],
                     ms[0].get("created_day"), ms[-1].get("created_day"), clip(sig, 200)])
    doc.table("repeated_user_messages",
              "User messages repeated verbatim inside one session",
              ["times", "brand", "project_dir", "session", "first day", "last day", "signature"],
              rows, "{} groups, {} redundant re-asks — the user typing the same thing again "
                    "is the loudest possible 'that did not work' signal.".format(len(rep),
                                                                                 total_rep))
    doc.write()


# ================================================================ 7. session timelines
def build_session_timelines():
    doc = Doc(
        "session-timelines",
        "Session timelines",
        "The 8 sessions with the most jobs. One line per job, capped at 400 lines per file.",
    )
    by_session = collections.defaultdict(list)
    for j in JOBS:
        by_session[(j.get("brand"), j.get("project_dir"), j.get("session_id"))].append(j)
    ranked = sorted(by_session.items(), key=lambda kv: -len(kv[1]))[:8]
    rows = []
    seen_names = collections.Counter()
    for (brand, proj, sess), js in ranked:
        js.sort(key=lambda x: (x.get("created_at") or "",
                               x.get("enqueue_sequence") if x.get("enqueue_sequence")
                               is not None else 0))
        sid8 = (sess or "nosession")[:8]
        seen_names[sid8] += 1
        fname = "timeline-{}.md".format(sid8 if seen_names[sid8] == 1
                                        else "{}-{}".format(sid8, seen_names[sid8]))
        states = collections.Counter(x["_state"] for x in js)
        nc = sum(states[s] for s in NON_COMMITTED)
        opk = collections.Counter(k for x in js for k in op_kind_set(x))
        lines = [
            "# Session {} — {} / {}".format(sid8, brand, proj), "",
            "- project_name: {}".format(js[0].get("project_name")),
            "- session_id: `{}`".format(sess),
            "- version: {}".format(js[0].get("version")),
            "- jobs: {} (committed {} / failed {} / blocked {} / recoveryrequired {}) — "
            "non-committed {}%".format(len(js), states.get("committed", 0),
                                       states.get("failed", 0), states.get("blocked", 0),
                                       states.get("recoveryrequired", 0), pct(nc, len(js))),
            "- operations by kind: {}".format(", ".join("{} {}".format(k, n)
                                              for k, n in opk.most_common(8))),
            "- span: {} … {}".format(js[0].get("created_at"), js[-1].get("created_at")), "",
            "| # | time | state | op kinds | summary | message (≤100) |",
            "|---|---|---|---|---|---|",
        ]
        for i, x in enumerate(js[:400], 1):
            lines.append("| {} | {} | {} | {} | {} | {} |".format(
                i, cell(clip(x.get("created_at"), 19)), cell(x["_state"]),
                cell(clip(",".join(op_kind_set(x)), 40)),
                cell(clip(x.get("summary"), 50)), cell(clip(x.get("message"), 100))))
        if len(js) > 400:
            lines.append("")
            lines.append("_…{} further jobs omitted (400-line cap)._".format(len(js) - 400))
        ensure_out("stats")
        with io.open(os.path.join(STATS, fname), "w", encoding="utf-8", newline="\n") as fh:
            fh.write("\n".join(lines) + "\n")
        OUTPUTS.append(os.path.join(STATS, fname))
        days = sorted(x for x in {x["_day"] for x in js} if x)
        rows.append([sid8, brand, proj, clip(js[0].get("project_name"), 26),
                     js[0].get("version"), len(js), states.get("committed", 0),
                     states.get("failed", 0), states.get("blocked", 0),
                     states.get("recoveryrequired", 0), pct(nc, len(js)),
                     days[0] if days else None, days[-1] if days else None,
                     clip(",".join(k for k, _ in opk.most_common(4)), 55), fname])
    doc.table("index", "Index of the 8 largest sessions",
              ["session", "brand", "project_dir", "project_name", "version", "jobs", "committed",
               "failed", "blocked", "recoveryrequired", "non-committed %", "first", "last",
               "top op kinds", "file"], rows)
    doc.scalar("sessions_with_jobs", len(by_session))
    doc.scalar("jobs_in_top8", sum(len(js) for _, js in ranked))
    doc.scalar("jobs_in_top8_pct", pct(sum(len(js) for _, js in ranked), len(JOBS)))
    HEADLINE["top8_sessions_share_of_jobs_pct"] = pct(
        sum(len(js) for _, js in ranked), len(JOBS))
    doc.write()


# ================================================================ 8. read path
SCOPE_CLASSES = [
    ("script", re.compile(r"^script:")),
    ("canvas", re.compile(r"^canvas$")),
    ("components", re.compile(r"^components:")),
    ("index", re.compile(r"^index$")),
    ("wires", re.compile(r"^wires$")),
    ("groups", re.compile(r"^groups$")),
    ("meta", re.compile(r"^meta$")),
    ("messages", re.compile(r"messages")),
    ("wireify (legacy)", re.compile(r"^wireify")),
    ("inspect", re.compile(r"^inspect")),
]
_QUOTED = re.compile(r"[\"'`]([^\"'`]{1,200})[\"'`]")
_SNAPSHOT_ID = re.compile(r"^s\d+-")


def classify_scope(tok):
    for label, rx in SCOPE_CLASSES:
        if rx.search(tok):
            return label
    return "other"


def build_read_path():
    doc = Doc(
        "read-path",
        "Read path (snapshot_read)",
        "Two observation points on the same path: the server's own `problem-log` "
        "`snapshot-read` records (only emitted since alpha.7) and the client-side "
        "`snapshot_read` tool calls in the rollouts. The 08-26 incident — a 50K-char script "
        "source the Claude backend could not read — lives here.",
    )
    ev = [r for r in read_jsonl(path("problem-events.jsonl")) if r.get("kind") == "snapshot-read"]
    doc.scalar("snapshot_read_problem_events", len(ev))
    doc.table("problem_events",
              "problem-log `snapshot-read` records ({})".format(len(ev)),
              ["at", "brand", "project_dir", "session", "responseBytes", "truncated",
               "inspections", "componentsRequested", "canvas", "index", "wires", "groups",
               "meta", "unchanged", "protocol"],
              [[clip(r.get("at"), 23), r.get("brand"), r.get("project_dir"),
                (r.get("sessionId") or "")[:8], r.get("responseBytes"), r.get("truncated"),
                r.get("inspections"), r.get("componentsRequested"), r.get("canvas"),
                r.get("index"), r.get("wires"), r.get("groups"), r.get("meta"),
                r.get("unchanged"), r.get("protocol")]
               for r in sorted(ev, key=lambda r: r.get("at") or "")],
              "This telemetry landed very late (alpha.7, one project), so it documents a single "
              "session rather than the corpus.")
    rb = [r.get("responseBytes") or 0 for r in ev]
    d = dist(rb)
    trunc = sum(1 for r in ev if r.get("truncated"))
    doc.scalar("problem_event_responseBytes", d)
    doc.scalar("problem_event_truncated", trunc)
    doc.table("problem_event_bytes", "responseBytes distribution (problem-log)",
              ["metric", "bytes"],
              [[k, d[k]] for k in ("n", "min", "p50", "p90", "max", "mean")],
              "`truncated=true` in {} of {} records.".format(trunc, len(ev)))

    calls = [rec for rec in read_jsonl(path("tool-calls.jsonl"))
             if rec.get("tool") == "snapshot_read"]
    # size distributions run over `sized`; counts, scopes and errors keep every call
    sized = [c for c in calls if not is_running_stub(c)]
    snap_running = len(calls) - len(sized)
    doc.scalar("snapshot_read_tool_calls", len(calls))
    doc.scalar("snapshot_read_running_stubs_excluded", snap_running)
    HEADLINE["snapshot_read_calls"] = len(calls)

    scope_ctr = collections.Counter()
    raw_ctr = collections.Counter()
    per_call_scopes = []
    for rec in calls:
        ap = rec.get("args_preview") or ""
        seg = ap
        m = re.search(r"scopes\s*[\"']?\s*:\s*\[(.*)", ap, re.DOTALL)
        if m:
            seg = m.group(1)
        toks = [t.strip() for t in _QUOTED.findall(seg)]
        toks = [t for t in toks if t and not _SNAPSHOT_ID.match(t)]
        if not toks:
            toks = ["(unparsed)" if "scopes" in ap else "(none)"]
        per_call_scopes.append(len(toks))
        seen = set()
        for t in toks:
            raw_ctr[t[:60]] += 1
            c = classify_scope(t)
            if c not in seen:
                scope_ctr[c] += 1
                seen.add(c)
    doc.table("scope_classes", "Scope classes requested (deduped per call)",
              ["scope class", "calls", "% of calls"],
              [[k, n, pct(n, len(calls))] for k, n in scope_ctr.most_common()])
    doc.table("scope_raw", "Most common raw scope tokens (top 30)",
              ["token", "occurrences"], raw_ctr.most_common(30))
    ds = dist(per_call_scopes)
    doc.table("scopes_per_call", "Scopes per call", ["metric", "value"],
              [[k, ds[k]] for k in ("n", "min", "p50", "p90", "max", "mean")])

    rl = [c.get("result_len") or 0 for c in sized]
    d = dist(rl)
    doc.scalar("tool_result_len", d)
    hist = collections.Counter()
    for v in rl:
        b = ("0-1k" if v < 1000 else "1k-10k" if v < 10000 else "10k-30k" if v < 30000
             else "30k-60k" if v < 60000 else "60k-120k" if v < 120000 else "120k+")
        hist[b] += 1
    order = ["0-1k", "1k-10k", "10k-30k", "30k-60k", "60k-120k", "120k+"]
    doc.table("result_len_hist",
              "snapshot_read result_len distribution (excludes `result_kind=running` stubs)",
              ["bucket", "calls", "% of calls"],
              [[b, hist.get(b, 0), pct(hist.get(b, 0), len(sized))] for b in order],
              "n={} · p50={} · p90={} · p99={} · max={}. **{} of the {} "
              "snapshot_read calls are excluded**: their `result_kind` is `running`, i.e. the "
              "transcript holds a ~62-char `Script running…` placeholder where the result "
              "should be, and counting those 62 bytes as a read size would fabricate a peak in "
              "the 0-1k bucket.".format(d["n"], d["p50"], d["p90"], d["p99"], d["max"],
                                        snap_running, len(calls)))
    HEADLINE["snapshot_read_result_p90"] = d["p90"]

    largest = sorted(sized, key=lambda c: -(c.get("result_len") or 0))[:20]
    doc.table("largest", "20 largest snapshot_read results",
              ["result_len", "brand", "project_dir", "day", "thread", "turn", "is_error",
               "args_preview"],
              [[c.get("result_len"), c.get("brand"), c.get("project_dir"), day(c.get("at")),
                (c.get("thread_id") or "")[:8], c.get("turn_index"), c.get("is_error"),
                clip(c.get("args_preview"), 140)] for c in largest])

    err = [c for c in calls if c.get("is_error")]
    doc.table("errors", "snapshot_read errors ({})".format(len(err)),
              ["day", "brand", "project_dir", "error_kind", "error_text", "args_preview"],
              [[day(c.get("at")), c.get("brand"), c.get("project_dir"), c.get("error_kind"),
                clip(c.get("error_text"), 160), clip(c.get("args_preview"), 100)] for c in err])

    script_calls = [c for c in calls if "script:" in (c.get("args_preview") or "")]
    script_sized = [c for c in script_calls if not is_running_stub(c)]
    srl = [c.get("result_len") or 0 for c in script_sized]
    dsr = dist(srl)
    doc.scalar("script_scope_calls", len(script_calls))
    doc.scalar("script_scope_running_stubs_excluded", len(script_calls) - len(script_sized))
    doc.scalar("script_scope_result_len", dsr)
    doc.table("script_scope",
              "Calls asking for a `script:` scope (sizes exclude `result_kind=running` stubs)",
              ["metric", "value"],
              [[k, dsr[k]] for k in ("n", "min", "p50", "p90", "max", "mean")],
              "{} of {} snapshot_read calls ask for script source; {} of those are "
              "`result_kind=running` placeholders and are excluded from the sizes above "
              "(n={}).".format(len(script_calls), len(calls),
                               len(script_calls) - len(script_sized), dsr["n"]))
    doc.table("script_scope_calls", "script: scope calls, largest first",
              ["result_len", "brand", "project_dir", "day", "thread", "is_error",
               "args_preview"],
              [[c.get("result_len"), c.get("brand"), c.get("project_dir"), day(c.get("at")),
                (c.get("thread_id") or "")[:8], c.get("is_error"),
                clip(c.get("args_preview"), 140)]
               for c in sorted(script_calls, key=lambda c: -(c.get("result_len") or 0))[:30]])

    # neighbouring read tools, for context on how much reading the agents do
    read_tools = ("snapshot_read", "inspect_outputs", "artifact_read", "gh_inspect",
                  "data_flow_read", "component_catalog", "rhino_list", "rhino_layers",
                  "job_status", "gh_document", "skill_read")
    agg = collections.defaultdict(lambda: {"calls": 0, "rlen": [], "err": 0})
    ceiling = collections.Counter()
    above = collections.Counter()
    enc_calls = collections.Counter()
    enc_max = collections.defaultdict(int)
    ceiling_running_excluded = 0
    for rec in read_jsonl(path("tool-calls.jsonl")):
        # a `running` row carries a ~62-char placeholder, not a result: it can neither reach the
        # ceiling nor represent a read size, and it would deflate every percentile below.
        if is_running_stub(rec):
            ceiling_running_excluded += 1
            continue
        t = rec.get("tool")
        rl = rec.get("result_len") or 0
        enc = rec.get("encoding")
        enc_calls[enc] += 1
        enc_max[enc] = max(enc_max[enc], rl)
        if 40000 <= rl <= 40300:
            ceiling[(t, enc)] += 1
        elif rl > 40300:
            above[(t, enc)] += 1
        if t in read_tools:
            a = agg[t]
            a["calls"] += 1
            a["rlen"].append(rl)
            if rec.get("is_error"):
                a["err"] += 1
    doc.scalar("results_at_40k_ceiling", sum(ceiling.values()))
    doc.scalar("results_above_40k", sum(above.values()))
    doc.scalar("ceiling_running_stubs_excluded", ceiling_running_excluded)
    HEADLINE["results_at_40k_codemode_ceiling"] = sum(ceiling.values())
    doc.table("encoding_ceiling",
              "Result-size ceiling by call encoding (excludes `result_kind=running` stubs)",
              ["encoding", "calls", "largest result_len", "results landing in 40,000-40,300",
               "results above 40,300"],
              [[e, n, enc_max[e],
                sum(v for (t, ee), v in ceiling.items() if ee == e),
                sum(v for (t, ee), v in above.items() if ee == e)]
               for e, n in enc_calls.most_common()],
              "**Code-mode (`exec_js`) results pile up at ~40,150 characters and essentially "
              "never pass it; `function_call` results are unbounded.** {} exec_js results land "
              "in the 40,000-40,300 band while exactly {} exceeds 40,300 (largest exec_js result "
              "{:,}) — a truncation signature, not a natural size distribution. The same tool on "
              "the `function_call` path returns up to {:,} chars. This is the same failure shape "
              "as the 08-26 script-read incident. Whether the cap belongs to the tool, the Codex "
              "code-mode bridge, or the rollout recorder has to be settled in Stage 2; this "
              "table only establishes that the cliff exists and which encoding it belongs to. "
              "**{} calls are excluded from this table** because their `result_kind` is "
              "`running`: the transcript holds a ~62-char `Script running…` placeholder "
              "rather than a result, so they carry no size evidence either way. The `calls` "
              "column here is therefore the post-exclusion count, not the corpus "
              "total.".format(sum(v for (t, e), v in ceiling.items() if e == "exec_js"),
                              sum(v for (t, e), v in above.items() if e == "exec_js"),
                              enc_max.get("exec_js", 0), enc_max.get("function_call", 0),
                              ceiling_running_excluded))
    doc.table("encoding_ceiling_by_tool", "Which tools hit the ~40 K ceiling",
              ["tool", "encoding", "calls at the ceiling"],
              [[t, e, n] for (t, e), n in ceiling.most_common(20)])

    doc.table("read_tools",
              "All read-side tools compared (excludes `result_kind=running` stubs)",
              ["tool", "calls", "errors", "error %", "result_len p50", "p90", "max",
               "total chars read"],
              [[t, a["calls"], a["err"], pct(a["err"], a["calls"]),
                quantile(a["rlen"], 0.5), quantile(a["rlen"], 0.9), max(a["rlen"] or [0]),
                sum(a["rlen"])] for t, a in
               sorted(agg.items(), key=lambda kv: -sum(kv[1]["rlen"]))],
              "Same exclusion as the table above: the {} `result_kind=running` rows are dropped "
              "before any size is read, so `calls` here counts calls with a real recorded "
              "result.".format(ceiling_running_excluded))
    doc.write()


# ================================================================ 9. corpus.md
def build_corpus():
    lines = ["# Corpus inventory (re-measured from `.log-mine/`)", "",
             "Generated {} by `scripts/log-mine/stats.py`. Supersedes the table in "
             "`docs/log-review-2026-08-26/plan.md`, which was hand-counted before extraction."
             .format(time.strftime("%Y-%m-%d %H:%M")), ""]

    wm_sources = (("jobs", JOBS_SUMMARY), ("problem", PROBLEM_SUMMARY),
                  ("hostlog", HOSTLOG_SUMMARY), ("messages", MESSAGES_SUMMARY),
                  ("rollouts", ROLLOUT_SUMMARY))
    lines.append(
        "**Capture watermarks** — each extractor stamps a UTC instant *before* it opens its "
        "first source file, so anything the live sources gained after that instant is outside "
        "this snapshot by construction rather than lost: " +
        " · ".join("`{}` {}".format(n, (s or {}).get("capture_watermark_utc") or "—")
                        for n, s in wm_sources))
    lines.append("")

    def days_of(records, field):
        ds = sorted({day(r.get(field)) for r in records if r.get(field)} - {None})
        return (ds[0], ds[-1]) if ds else (None, None)

    def vb(records):
        v = collections.Counter(r.get("version") for r in records)
        b = collections.Counter(r.get("brand") for r in records)
        return (", ".join("{}:{}".format(k, n) for k, n in v.most_common()),
                ", ".join("{}:{}".format(k, n) for k, n in b.most_common()))

    rows = []
    jd = days_of(JOBS, "created_at")
    jv, jb = vb(JOBS)
    job_projects = len({(j.get("brand"), j.get("project_dir")) for j in JOBS})
    rows.append(["jobs.jsonl", "live-jobs.db ({} DBs exist, {} hold rows)".format(
        JOBS_SUMMARY.get("projects_with_live_jobs_db"), job_projects), len(JOBS),
        "{} … {}".format(*jd), jv, jb])

    pd_range = PROBLEM_SUMMARY.get("day_range") or [None, None]
    pver, pbrand = collections.Counter(), collections.Counter()
    for k, v in (PROBLEM_SUMMARY.get("by_kind_version") or {}).items():
        pver[k.split("|")[-1]] += v
    for k, v in (PROBLEM_SUMMARY.get("by_kind_brand") or {}).items():
        pbrand[k.split("|")[-1]] += v
    rows.append(["problem-events.jsonl", "problem-log.jsonl ({} files, {} projects)".format(
        PROBLEM_SUMMARY.get("source_files"), len(PROBLEM_SUMMARY.get("by_project") or {})),
        PROBLEM_SUMMARY.get("records"), "{} … {}".format(*pd_range),
        ", ".join("{}:{}".format(k, n) for k, n in pver.most_common()),
        ", ".join("{}:{}".format(k, n) for k, n in pbrand.most_common())])

    md = days_of(MESSAGES, "created_at")
    mv, mb = vb(MESSAGES)
    rows.append(["messages.jsonl", "runtime.db messages ({} projects with a db)".format(
        (MESSAGES_SUMMARY.get("totals") or {}).get("projects_with_runtime_db")), len(MESSAGES),
        "{} … {}".format(*md), mv, mb])
    sd = days_of(SESSIONS, "created_at")
    sv, sb = vb(SESSIONS)
    rows.append(["sessions.jsonl", "runtime.db sessions", len(SESSIONS),
                 "{} … {}".format(*sd), sv, sb])
    hd = days_of(HOSTLOG, "at_utc")
    hv, hb = vb(HOSTLOG)
    rows.append(["hostlog-events.jsonl", "host.log ({} files, {} lines, {} dropped as ASP.NET "
                 "noise)".format(HOSTLOG_SUMMARY.get("source_files"),
                                 HOSTLOG_SUMMARY.get("source_lines"),
                                 HOSTLOG_SUMMARY.get("dropped_information_lines")),
                 len(HOSTLOG), "{} … {}".format(*hd), hv, hb])

    def stream_stats(name):
        v, b, days, n, bench, src = (collections.Counter(), collections.Counter(), set(), 0, 0,
                                     collections.Counter())
        for rec in read_jsonl(path(name)):
            n += 1
            v[rec.get("version")] += 1
            b[rec.get("brand")] += 1
            src[rec.get("source")] += 1
            if rec.get("bench"):
                bench += 1
            d = day(rec.get("at"))
            if d:
                days.add(d)
        ds = sorted(days)
        return n, v, b, ds, bench, src

    tc_n, tc_v, tc_b, tcd, tc_bench, tc_src = stream_stats("tool-calls.jsonl")
    rows.append(["tool-calls.jsonl", "codex rollouts + claude transcripts ({})".format(
        ", ".join("{}:{}".format(k, n) for k, n in tc_src.most_common())), tc_n,
        "{} … {}".format(tcd[0], tcd[-1]) if tcd else "",
        ", ".join("{}:{}".format(k, n) for k, n in tc_v.most_common()),
        ", ".join("{}:{}".format(k, n) for k, n in tc_b.most_common())])
    te_n, te_v, te_b, ted, _, te_src = stream_stats("turn-events.jsonl")
    rows.append(["turn-events.jsonl", "codex rollouts + claude transcripts ({})".format(
        ", ".join("{}:{}".format(k, n) for k, n in te_src.most_common())), te_n,
        "{} … {}".format(ted[0], ted[-1]) if ted else "",
        ", ".join("{}:{}".format(k, n) for k, n in te_v.most_common()),
        ", ".join("{}:{}".format(k, n) for k, n in te_b.most_common())])
    thd = days_of(THREADS, "started_at")
    thv, thb = vb(THREADS)
    rows.append(["threads.jsonl", "one row per rollout / transcript ({} of {} files "
                 "selected, {:.2f} GB scanned)".format(
                     ROLLOUT_SUMMARY.get("files_selected"), ROLLOUT_SUMMARY.get("files_scanned"),
                     (ROLLOUT_SUMMARY.get("bytes_scanned") or 0) / 1e9),
                 len(THREADS), "{} … {}".format(*thd), thv, thb])

    lines.append("| mined file | source | records | day range | versions | brands |")
    lines.append("|---|---|---|---|---|---|")
    for r in rows:
        lines.append("| " + " | ".join(cell(c) for c in r) + " |")
    lines.append("")

    lines.append("## Plan estimate vs. measured")
    lines.append("")
    lines.append("| item | plan.md (hand count) | measured | note |")
    lines.append("|---|---|---|---|")
    plan_vs = [
        ("project folders", 172, 172, "both brands' projects/ roots"),
        ("…with a live-jobs.db file", "—", JOBS_SUMMARY.get("projects_with_live_jobs_db"),
         "42 folders have no DB at all"),
        ("…with at least one job row", "172 (implied)", job_projects,
         "**the real denominator**: 112 of the 130 DBs are empty. Per-project rates must divide "
         "by {}, not 172 and not 130".format(job_projects)),
        ("jobs", 2655, len(JOBS), "+{} — the corpus grew since the plan was written".format(
            len(JOBS) - 2655)),
        ("jobs matched to a payload dir", "2710 dirs",
         JOBS_SUMMARY.get("jobs_with_payload_dir"),
         "every job found its payload; 2,712 dirs exist on disk, so 56 are orphans with no "
         "surviving DB row"),
        ("problem-log records", 16454, PROBLEM_SUMMARY.get("records"),
         "+{}, 0 unparsable lines".format((PROBLEM_SUMMARY.get("records") or 0) - 16454)),
        ("runtime.db sessions", 81, len(SESSIONS), "exact"),
        ("runtime.db messages", 1362, len(MESSAGES), "+{}".format(len(MESSAGES) - 1362)),
        ("host.log lines", 8003, HOSTLOG_SUMMARY.get("source_lines"),
         "+{}; {} kept after dropping ASP.NET Information noise".format(
             (HOSTLOG_SUMMARY.get("source_lines") or 0) - 8003, len(HOSTLOG))),
        ("codex rollouts", 237, sum(1 for t in THREADS if t.get("source") == "codex"),
         "of {} files scanned ({} selected in total, incl. claude); selection is by cwd under "
         "\\{{Vino,GPTino}}\\projects\\".format(ROLLOUT_SUMMARY.get("files_scanned"),
                                                ROLLOUT_SUMMARY.get("files_selected"))),
        ("claude transcripts", 2, sum(1 for t in THREADS if t.get("source") == "claude"),
         "{} claude tool calls in total".format(tc_src.get("claude", 0))),
    ]
    for a, b, c, dd in plan_vs:
        lines.append("| {} | {} | {} | {} |".format(cell(a), cell(b), cell(c), cell(dd)))
    lines.append("")

    lines.append("## Job state totals")
    lines.append("")
    ctr = collections.Counter(j["_state"] for j in JOBS)
    lines.append("| state | jobs | % |")
    lines.append("|---|---|---|")
    for s in ("committed", "failed", "blocked", "recoveryrequired"):
        lines.append("| {} | {} | {} |".format(s, ctr.get(s, 0), pct(ctr.get(s, 0), len(JOBS))))
    nc = sum(ctr[s] for s in NON_COMMITTED)
    lines.append("| **non-committed** | {} | {} |".format(nc, pct(nc, len(JOBS))))
    lines.append("| **total** | {} | 100 |".format(len(JOBS)))
    lines.append("")

    lines.append("## Problem-log record kinds")
    lines.append("")
    lines.append("| kind | records |")
    lines.append("|---|---|")
    for k, n in sorted((PROBLEM_SUMMARY.get("by_kind") or {}).items(), key=lambda kv: -kv[1]):
        lines.append("| {} | {} |".format(cell(k), n))
    lines.append("")

    jobs_projects = {(j.get("brand"), j.get("project_dir")) for j in JOBS}
    msg_projects = {(m.get("brand"), m.get("project_dir")) for m in MESSAGES}
    host_projects = {(h.get("brand"), h.get("project_dir")) for h in HOSTLOG}
    unstamped = ((PROBLEM_SUMMARY.get("by_kind_record_version") or {}).get("job-state|unstamped", 0)
                 + (PROBLEM_SUMMARY.get("by_kind_record_version") or {}).get(
                     "predicate-outcome|unstamped", 0))
    lines.append("## Known gaps")
    lines.append("")
    gaps = [
        "**172 project folders → {} live-jobs.db files → {} projects that ever ran a job.** "
        "The extractor's `projects_with_live_jobs_db={}` counts DB *files*; {} of them are empty. "
        "Every per-project rate in these stats divides by {}. One project "
        "(457FDB8091063B0D, '260729 심의 모델링') alone holds {} of the {} jobs "
        "({}%), so corpus-wide job rates are largely that project's rates.".format(
            JOBS_SUMMARY.get("projects_with_live_jobs_db"), len(jobs_projects),
            JOBS_SUMMARY.get("projects_with_live_jobs_db"),
            (JOBS_SUMMARY.get("projects_with_live_jobs_db") or 0) - len(jobs_projects),
            len(jobs_projects),
            sum(1 for j in JOBS if j.get("project_dir") == "457FDB8091063B0D"), len(JOBS),
            pct(sum(1 for j in JOBS if j.get("project_dir") == "457FDB8091063B0D"), len(JOBS))),
        "**Forked / resumed Codex rollouts replay their whole history in one write burst**, "
        "stamping every replayed turn with the fork's clock. Turn-level timing is therefore only "
        "valid after the replay filter applied in `stats/session-lifecycle.md`; raw "
        "task_started→task_complete deltas show a 4 ms median that is pure artefact.",
        "**{} of {} rollout threads are sub-agent threads.** Tool-call and turn counts are "
        "dominated by spawned sub-agents, not by the top-level conversation.".format(
            sum(1 for t in THREADS if t.get("sub_agent")), len(THREADS)),
        "**`host.log` covers {} projects ({} files), Vino only, and only the alpha.7 era.** It ships with "
        "alpha.7, so every pre-alpha7 project has no host-side log at all: host-level crash / "
        "restart / OAuth evidence exists for roughly the last two weeks of a five-week "
        "corpus.".format(len(host_projects), HOSTLOG_SUMMARY.get("source_files")),
        "**{} of {} jobs have no `job-state` trail in the problem log.** The uncovered jobs "
        "are {}, spread over {} project(s) ({}). Every per-job rate derived from problem-log "
        "evidence (acceptance predicates, auto-fill, auto-approval, job-state trails) therefore "
        "divides by **{}**, not {} — see the `problem_evidence_per_job` table in "
        "`stats/error-clusters.md`, which prints both denominators.".format(
            N_JOBS - N_TRAIL, N_JOBS, NO_TRAIL_VERSION_DESC,
            len(NO_TRAIL_PROJECTS), ", ".join(NO_TRAIL_PROJECTS) or "—",
            N_TRAIL, N_JOBS),
        "**{} tool calls carry `result_kind='running'`** — the rollout recorded a ~62-char "
        "`Script running…` placeholder instead of the real result. They count as calls "
        "everywhere, but every result-size percentile, the ~40 K code-mode ceiling table and the "
        "read-path size distributions exclude them, because 62 bytes measures the recorder, not "
        "the tool.".format(HEADLINE.get("running_stub_calls_excluded", 0)),
        "**`problem-log.jsonl` covers {} projects out of 172**, and its own `v` field is absent "
        "on {} of {} records — those are version-stamped by folder inheritance, not by the "
        "record.".format(len(PROBLEM_SUMMARY.get("by_project") or {}), unstamped,
                         PROBLEM_SUMMARY.get("records")),
        "**{} rollouts selected out of {} scanned.** Selection is by `cwd` under "
        "`\\{{Vino,GPTino}}\\projects\\`; a session driven from any other cwd is invisible "
        "here.".format(ROLLOUT_SUMMARY.get("files_selected"),
                       ROLLOUT_SUMMARY.get("files_scanned")),
        "**brand ≡ version for jobs** (GPTino → pre-alpha7 2203 jobs, Vino → 0.1.0-alpha.7 "
        "453 jobs). The two columns cannot cross-validate each other there, and no 'version "
        "effect' can be separated from the rename boundary or from the change in what the user "
        "was doing on either side of it. The identity does **not** hold everywhere: {} host.log "
        "records live in Vino folders yet carry the pre-alpha7 stamp, because "
        "`common.project_version()` falls back to a date rule when a host.log has no "
        "`packages/8.0/<brand>/<version>/` path in it. Treat `version` as a folder-level "
        "inheritance, not a per-record fact.".format(
            sum(1 for h in HOSTLOG if h.get("version") == "pre-alpha7")),
        "**{} of {} tool calls are marked `bench`** — benchmark runs, not free user work. "
        "They are included in every table unless it says otherwise.".format(tc_bench, tc_n),
        "**Population mismatch across sources**: {} project(s) appear in jobs but not in "
        "messages, {} in messages but not in jobs. Cross-source joins lose rows.".format(
            len(jobs_projects - msg_projects), len(msg_projects - jobs_projects)),
        "**The Claude backend is {} threads / {} tool calls** against {} codex calls, so every "
        "codex-vs-claude comparison here is anecdotal, not statistical.".format(
            sum(1 for t in THREADS if t.get("source") == "claude"), tc_src.get("claude", 0),
            tc_src.get("codex", 0)),
        "**`snapshot-read` telemetry is {} records from one session.** The server-side "
        "read-path ".format((PROBLEM_SUMMARY.get("by_kind") or {}).get("snapshot-read", 0)) +
        "instrumentation landed at the very end of the window, so read-path conclusions lean on "
        "the client-side tool calls instead.",
        "**Live `-wal` sidecars.** The SQLite sources were opened `mode=ro` while the app could "
        "still be writing. A job or message written during extraction may appear in one run and "
        "not the next — re-run the extractors before freezing any number.",
        "**No parse loss.** problem-log: 0 unparsable lines. host.log: 0 orphan continuations, 0 "
        "unparsed timestamped lines. rollouts: {} parse errors, {} large lines skipped, {} calls "
        "without a recorded output. Record loss is not a concern; population coverage "
        "is.".format(sum(t.get("parse_errors") or 0 for t in THREADS),
                     ROLLOUT_SUMMARY.get("large_lines_skipped"),
                     ROLLOUT_SUMMARY.get("calls_without_output")),
        "**Job `message` is a rendered sentence, not a code.** Clustering is lexical "
        "(`normalize_signature`, 110 chars), so a wording change across versions splits one "
        "defect into two clusters and vice versa.",
    ]
    for g in gaps:
        lines.append("- {}".format(g))
    lines.append("")

    lines.append("## Coverage by project (top 20 by jobs)")
    lines.append("")
    cnt = collections.Counter((j.get("brand"), j.get("project_dir")) for j in JOBS)
    names = {(j.get("brand"), j.get("project_dir")): j.get("project_name") for j in JOBS}
    mcnt = collections.Counter((m.get("brand"), m.get("project_dir")) for m in MESSAGES)
    hcnt = collections.Counter((h.get("brand"), h.get("project_dir")) for h in HOSTLOG)
    rcnt = collections.Counter((t.get("brand"), t.get("project_dir")) for t in THREADS)
    pcnt = PROBLEM_SUMMARY.get("by_project") or {}
    lines.append("| brand | project_dir | project_name | jobs | problem-log | messages | "
                 "host.log | rollouts |")
    lines.append("|---|---|---|---|---|---|---|---|")
    for k, n in cnt.most_common(20):
        lines.append("| {} | {} | {} | {} | {} | {} | {} | {} |".format(
            cell(k[0]), cell(k[1]), cell(clip(names.get(k), 30)), n, pcnt.get(k[1], 0),
            mcnt.get(k, 0), hcnt.get(k, 0), rcnt.get(k, 0)))
    lines.append("")

    lines.append("## Generated statistics")
    lines.append("")
    lines.append("| file | what it answers |")
    lines.append("|---|---|")
    for f, what in (
        ("stats/failure-rates.md", "jobs by state × version / brand / op kind / week / "
                                   "project; the non-committed trend"),
        ("stats/error-clusters.md", "top 80 non-committed message clusters + committed-but-hedged "
                                    "messages + acceptance-predicate outcomes"),
        ("stats/retry-chains.md", "consecutive same-target job retries: length distribution, top "
                                  "40 chains, jobs wasted"),
        ("stats/tool-friction.md", "per-tool error rate / latency / result size, identical-args "
                                   "repeats, calls per turn, error signatures, oversize results"),
        ("stats/session-lifecycle.md", "interruption / compaction / recovery / OAuth / quota by "
                                       "version and week, turn durations, continue prompts"),
        ("stats/user-signals.md", "every user message that reads as a correction, with the "
                                  "assistant message it answers"),
        ("stats/session-timelines.md", "index of the 8 largest sessions + one timeline file each"),
        ("stats/read-path.md", "snapshot_read scopes, result sizes, errors, script: reads"),
        ("stats/headline.json", "the dozen numbers this run turned on"),
    ):
        lines.append("| `{}` | {} |".format(f, what))
    lines.append("")

    ensure_out()
    p = os.path.join(OUT_ROOT, "corpus.md")
    with io.open(p, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")
    OUTPUTS.append(p)
    HEADLINE["projects_with_jobs"] = len(jobs_projects)


# ================================================================ main
def main():
    ensure_out("stats")
    for fn in (build_failure_rates, build_error_clusters, build_retry_chains,
               build_tool_friction, build_session_lifecycle, build_user_signals,
               build_session_timelines, build_read_path, build_corpus):
        t0 = time.time()
        fn()
        print("{:<26} {:5.1f}s".format(fn.__name__, time.time() - t0), file=sys.stderr)
    with io.open(os.path.join(STATS, "headline.json"), "w", encoding="utf-8",
                 newline="\n") as fh:
        json.dump({"generated_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
                   "run_seconds": round(time.time() - START, 2),
                   "headline": HEADLINE, "issues": ISSUES}, fh, ensure_ascii=False, indent=1)
    OUTPUTS.append(os.path.join(STATS, "headline.json"))
    print(json.dumps({"outputs": OUTPUTS, "headline": HEADLINE, "issues": ISSUES,
                      "run_seconds": round(time.time() - START, 2)},
                     ensure_ascii=False, indent=1))


if __name__ == "__main__":
    main()
