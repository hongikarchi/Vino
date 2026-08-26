"""Stage 0 extractor: `<project>/problem-log.jsonl` -> `.log-mine/problem-events.jsonl`.

Every source record is written verbatim (all original fields survive) plus the common project
stamp and a few derived keys:

    at_day             `at[:10]` — the ISO date, for day buckets
    message_signature  normalize_signature(message or summary or detail or reason)
    summary_signature  normalize_signature(summary) when the record carries a summary
    detail_first_line  first line of `detail` (job-exception) and its signature
    state              normalize_state(state) when present — the ORIGINAL casing is preserved
                       as `state_raw` so nothing from the source is lost

Also emits `.log-mine/problem-summary.json` (counts by kind / kind x version / kind x day,
predicate pass-fail by predicateKind, job-state by state, top signatures).

Run: PYTHONIOENCODING=utf-8 C:/Python314/python scripts/log-mine/extract_problem_log.py
"""

import collections
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from common import (  # noqa: E402
    OUT_ROOT,
    JsonlWriter,
    capture_watermark,
    common_fields,
    ensure_out,
    iter_projects,
    normalize_signature,
    normalize_state,
    read_jsonl,
)

TOP_SIGNATURES = 40
# `validating`/`executing`/`verifying` are in-flight breadcrumbs, not outcomes; these three are
# the states a job actually ends on without committing.
TERMINAL_NON_COMMITTED = ("failed", "blocked", "recoveryrequired")


def _first_line(text, limit=300):
    if not text:
        return None
    return str(text).splitlines()[0].strip()[:limit]


def derive(record):
    """Derived keys for one problem-log record (never drops an original field)."""
    out = {}
    at = record.get("at")
    out["at_day"] = str(at)[:10] if at else None

    message = record.get("message")
    summary = record.get("summary")
    detail = record.get("detail")
    reason = record.get("reason")
    basis = message or summary or detail or reason
    out["message_signature"] = normalize_signature(basis) if basis else None
    if summary:
        out["summary_signature"] = normalize_signature(summary)
    if detail:
        head = _first_line(detail)
        out["detail_first_line"] = head
        out["detail_signature"] = normalize_signature(head)
    if "state" in record:
        out["state_raw"] = record.get("state")
        out["state"] = normalize_state(record.get("state"))
    return out


def main():
    watermark = capture_watermark()   # before the first source file is opened
    started = time.time()
    writer = JsonlWriter("problem-events.jsonl")

    by_kind = collections.Counter()
    by_kind_version = collections.Counter()
    by_kind_day = collections.Counter()
    by_kind_brand = collections.Counter()
    predicate = collections.defaultdict(lambda: collections.Counter())
    job_state = collections.Counter()
    job_state_conflicts = 0
    non_committed_sig = collections.Counter()
    terminal_sig = collections.Counter()
    non_committed_summary_sig = collections.Counter()
    exception_sig = collections.Counter()
    exception_type = collections.Counter()
    per_project = collections.Counter()
    by_kind_record_version = collections.Counter()
    files = 0
    bad_lines = 0
    day_min = None
    day_max = None

    for project in iter_projects():
        path = os.path.join(project["path"], "problem-log.jsonl")
        if not os.path.exists(path):
            continue
        files += 1
        base = common_fields(project)
        raw_lines = 0
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if line.strip():
                    raw_lines += 1
        parsed = 0
        for record in read_jsonl(path):
            parsed += 1
            out = dict(record)
            out.update(base)
            out.update(derive(record))
            writer.write(out)

            kind = record.get("kind") or "?"
            version = base["version"]
            day = out["at_day"] or "?"
            by_kind[kind] += 1
            by_kind_version["{}|{}".format(kind, version)] += 1
            by_kind_day["{}|{}".format(kind, day)] += 1
            by_kind_brand["{}|{}".format(kind, base["brand"])] += 1
            by_kind_record_version["{}|{}".format(kind, record.get("v") or "unstamped")] += 1
            per_project[base["project_dir"]] += 1
            if day != "?":
                day_min = day if day_min is None or day < day_min else day_min
                day_max = day if day_max is None or day > day_max else day_max

            if kind == "predicate-outcome":
                pk = record.get("predicateKind") or "?"
                predicate[pk]["pass" if record.get("passed") else "fail"] += 1
            elif kind == "job-state":
                state = out.get("state") or "?"
                job_state[state] += 1
                if record.get("conflicts"):
                    job_state_conflicts += 1
                if state in TERMINAL_NON_COMMITTED:
                    terminal_sig[
                        "{}|{}".format(state, out.get("message_signature") or "")
                    ] += 1
                if state != "committed":
                    non_committed_sig["{}|{}".format(state, out.get("message_signature") or "")] += 1
                    if out.get("summary_signature"):
                        non_committed_summary_sig[out["summary_signature"]] += 1
            elif kind == "job-exception":
                exception_type[record.get("exceptionType") or "?"] += 1
                exception_sig[
                    "{}|{}".format(record.get("exceptionType") or "?", out.get("detail_signature") or "")
                ] += 1
        bad_lines += max(0, raw_lines - parsed)

    total = writer.close()

    def top(counter, n=TOP_SIGNATURES):
        return [{"key": k, "count": c} for k, c in counter.most_common(n)]

    summary = {
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "capture_watermark_utc": watermark,
        "source_files": files,
        "records": total,
        "unparsable_lines": bad_lines,
        "day_range": [day_min, day_max],
        "by_kind": dict(by_kind.most_common()),
        "by_kind_version": dict(by_kind_version.most_common()),
        "by_kind_brand": dict(by_kind_brand.most_common()),
        "by_kind_record_version": dict(by_kind_record_version.most_common()),
        "by_kind_day": dict(sorted(by_kind_day.items())),
        "by_project": dict(per_project.most_common()),
        "predicate_outcome_by_kind": {
            k: {
                "pass": v.get("pass", 0),
                "fail": v.get("fail", 0),
                "total": v.get("pass", 0) + v.get("fail", 0),
                "fail_rate": round(v.get("fail", 0) / max(1, v.get("pass", 0) + v.get("fail", 0)), 4),
            }
            for k, v in sorted(predicate.items(), key=lambda kv: -sum(kv[1].values()))
        },
        "job_state_by_state": dict(job_state.most_common()),
        "job_state_with_conflicts": job_state_conflicts,
        "top_signatures_job_state_non_committed": top(non_committed_sig),
        "top_signatures_job_state_terminal_non_committed": top(terminal_sig),
        "top_summaries_job_state_non_committed": top(non_committed_summary_sig),
        "job_exception_by_type": dict(exception_type.most_common()),
        "top_signatures_job_exception": top(exception_sig),
    }
    ensure_out()
    with open(os.path.join(OUT_ROOT, "problem-summary.json"), "w", encoding="utf-8", newline="\n") as fh:
        json.dump(summary, fh, ensure_ascii=False, indent=2)

    print(
        json.dumps(
            {
                "script": "extract_problem_log.py",
                "files": files,
                "records": total,
                "seconds": round(time.time() - started, 2),
            },
            ensure_ascii=False,
        )
    )


if __name__ == "__main__":
    main()
