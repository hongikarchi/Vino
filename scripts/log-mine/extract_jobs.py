# Stage 0 extractor: <project>/live-jobs.db -> .log-mine/jobs.jsonl (+ jobs-summary.json).
# See docs/log-review-2026-08-26/plan.md. Pure stdlib, re-runnable (outputs are overwritten).
#
# One record per row of `live_jobs`, enriched with the parsed change_set_json (operation kinds,
# acceptance predicates, read/write set sizes) and — when the reserved payload folder still exists —
# the per-operation bridge payload *shape* only (bridgeOperation + the top-level argument keys).
# Payload source text (scripts, geometry blobs) is deliberately never copied into the output.
import collections
import json
import os
import sqlite3
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
    open_sqlite_readonly,
    table_columns,
    table_exists,
)

RESERVED_DIRS = (".vino-reserved", ".gptino-reserved")
TOP_CLUSTERS = 60


def _nodash(text):
    return (text or "").replace("-", "").strip().lower()


def index_payload_dirs(project_path):
    """job-id (no dashes, lowercase) -> reserved payload dir, for every session in the project.

    Built once per project by walking `artifacts/*/.{vino,gptino}-reserved/jobs/*`; both brands'
    reserved folder names appear under either brand (legacy GPTino sessions live inside Vino
    project folders), and the session folder is spelled both with and without dashes, so an index
    is cheaper and more reliable than guessing a path per job.
    """
    index = {}
    artifacts = os.path.join(project_path, "artifacts")
    if not os.path.isdir(artifacts):
        return index
    try:
        sessions = os.listdir(artifacts)
    except OSError:
        return index
    for session in sessions:
        for reserved in RESERVED_DIRS:
            jobs_root = os.path.join(artifacts, session, reserved, "jobs")
            if not os.path.isdir(jobs_root):
                continue
            try:
                entries = os.listdir(jobs_root)
            except OSError:
                continue
            for job in entries:
                path = os.path.join(jobs_root, job)
                if os.path.isdir(path):
                    index.setdefault(_nodash(job), path)
    return index


def read_payload_ops(payload_dir):
    """[{file, bridgeOperation, argument_keys}] plus the byte total of the whole payload folder.

    Operation payloads observed top out near 105 KB, so each file is parsed whole; anything larger
    is skipped structurally (recorded with an `error`) rather than pulled into memory.
    """
    ops = []
    total = 0
    for root, _dirs, files in os.walk(payload_dir):
        for name in sorted(files):
            path = os.path.join(root, name)
            try:
                size = os.path.getsize(path)
            except OSError:
                continue
            total += size
            if os.path.basename(root) != "operations" or not name.lower().endswith(".json"):
                continue
            entry = {"file": name, "bridgeOperation": None, "argument_keys": []}
            if size > 8 * 1024 * 1024:
                entry["error"] = "oversized:{}".format(size)
                ops.append(entry)
                continue
            try:
                with open(path, encoding="utf-8") as fh:
                    data = json.load(fh)
            except (OSError, json.JSONDecodeError, UnicodeDecodeError) as exc:
                entry["error"] = type(exc).__name__
                ops.append(entry)
                continue
            if isinstance(data, dict):
                entry["bridgeOperation"] = data.get("bridgeOperation")
                args = data.get("arguments")
                if isinstance(args, dict):
                    entry["argument_keys"] = list(args.keys())
                elif args is not None:
                    entry["argument_keys"] = ["<{}>".format(type(args).__name__)]
            else:
                entry["error"] = "not-an-object"
            ops.append(entry)
    ops.sort(key=lambda e: e["file"])
    return ops, total


def parse_change_set(raw, issues):
    """Pull the structural facts out of change_set_json; tolerate absent/broken payloads."""
    out = {
        "op_kinds": [],
        "ops": [],
        "op_count": 0,
        "has_acceptance_predicates": False,
        "predicate_kinds": [],
        "read_set_count": 0,
        "write_set_count": 0,
        "base_snapshot_revision": None,
    }
    if not raw:
        return out
    try:
        cs = json.loads(raw)
    except (json.JSONDecodeError, TypeError):
        issues["change_set_unparsable"] += 1
        return out
    if not isinstance(cs, dict):
        issues["change_set_not_object"] += 1
        return out
    operations = cs.get("operations") or []
    if isinstance(operations, list):
        for op in operations:
            if not isinstance(op, dict):
                continue
            out["op_kinds"].append(op.get("kind"))
            out["ops"].append(
                {
                    "operationId": op.get("operationId"),
                    "kind": op.get("kind"),
                    "owner": op.get("owner"),
                    "reversible": op.get("reversible"),
                }
            )
        out["op_count"] = len(out["ops"])
    predicates = cs.get("acceptancePredicates") or []
    if isinstance(predicates, list) and predicates:
        out["has_acceptance_predicates"] = True
        out["predicate_kinds"] = [
            p.get("kind") for p in predicates if isinstance(p, dict)
        ]
    read_set = cs.get("readSet")
    write_set = cs.get("writeSet")
    out["read_set_count"] = len(read_set) if isinstance(read_set, list) else 0
    out["write_set_count"] = len(write_set) if isinstance(write_set, list) else 0
    out["base_snapshot_revision"] = cs.get("baseSnapshotRevision")
    return out


def main():
    watermark = capture_watermark()   # before the first source file is opened
    started = time.time()
    issues = collections.Counter()
    by_state = collections.Counter()
    by_brand = collections.Counter()
    by_version = collections.Counter()
    by_op_kind = collections.Counter()
    by_day = collections.Counter()
    by_bridge_op = collections.Counter()
    clusters = {}
    projects_with_db = 0
    payload_hits = 0

    writer = JsonlWriter("jobs.jsonl")
    try:
        for project in iter_projects():
            db_path = os.path.join(project["path"], "live-jobs.db")
            if not os.path.exists(db_path):
                continue
            try:
                conn = open_sqlite_readonly(db_path)
            except sqlite3.Error as exc:
                issues["db_open_failed:{}".format(project["project_dir"])] += 1
                del exc
                continue
            try:
                if not table_exists(conn, "live_jobs"):
                    issues["no_live_jobs_table"] += 1
                    continue
                projects_with_db += 1
                columns = table_columns(conn, "live_jobs")
                payload_index = index_payload_dirs(project["path"])
                base = common_fields(project)
                conn.row_factory = sqlite3.Row
                try:
                    rows = conn.execute("select * from live_jobs order by rowid")
                except sqlite3.Error:
                    issues["db_read_failed"] += 1
                    continue
                for row in rows:
                    get = lambda key: (row[key] if key in columns else None)  # noqa: E731
                    job_id = get("job_id")
                    message = get("message")
                    state = normalize_state(get("state"))
                    enqueued_at = get("enqueued_at")
                    record = dict(base)
                    record.update(
                        {
                            "session_id": get("session_id"),
                            "job_id": job_id,
                            "idempotency_key": get("idempotency_key"),
                            "summary": get("summary"),
                            "state": state,
                            "phase": get("phase"),
                            "message": message,
                            "message_signature": normalize_signature(message),
                            "enqueued_at": enqueued_at,
                            "created_at": get("created_at"),
                            "updated_at": get("updated_at"),
                            "enqueue_sequence": get("enqueue_sequence"),
                            "request_hash": get("request_hash"),
                            "target_doc": get("target_doc"),
                        }
                    )
                    record.update(parse_change_set(get("change_set_json"), issues))

                    payload_dir = payload_index.get(_nodash(job_id))
                    if payload_dir:
                        payload_hits += 1
                        payload_ops, payload_bytes = read_payload_ops(payload_dir)
                    else:
                        payload_ops, payload_bytes = [], 0
                    record["payload_dir"] = payload_dir
                    record["payload_ops"] = payload_ops
                    record["payload_bytes_total"] = payload_bytes
                    writer.write(record)

                    by_state[state] += 1
                    by_brand[project["brand"]] += 1
                    by_version[project["version"]] += 1
                    for kind in record["op_kinds"]:
                        by_op_kind[kind or "<null>"] += 1
                    for op in payload_ops:
                        if op.get("bridgeOperation"):
                            by_bridge_op[op["bridgeOperation"]] += 1
                    day = (enqueued_at or "")[:10] or "<unknown>"
                    by_day[day] += 1
                    if state != "committed":
                        sig = record["message_signature"]
                        entry = clusters.get(sig)
                        if entry is None:
                            entry = clusters[sig] = {
                                "signature": sig,
                                "count": 0,
                                "states": collections.Counter(),
                                "brands": collections.Counter(),
                                "versions": collections.Counter(),
                                "projects": set(),
                                "first_seen": enqueued_at,
                                "last_seen": enqueued_at,
                                "example": {
                                    "project_dir": project["project_dir"],
                                    "job_id": job_id,
                                    "state": state,
                                    "message": (message or "")[:400],
                                },
                            }
                        entry["count"] += 1
                        entry["states"][state] += 1
                        entry["brands"][project["brand"]] += 1
                        entry["versions"][project["version"]] += 1
                        entry["projects"].add(project["project_dir"])
                        if enqueued_at:
                            if not entry["first_seen"] or enqueued_at < entry["first_seen"]:
                                entry["first_seen"] = enqueued_at
                            if not entry["last_seen"] or enqueued_at > entry["last_seen"]:
                                entry["last_seen"] = enqueued_at
            finally:
                conn.close()
    finally:
        total = writer.close()

    top = sorted(clusters.values(), key=lambda e: (-e["count"], e["signature"]))[:TOP_CLUSTERS]
    summary = {
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "capture_watermark_utc": watermark,
        "total_jobs": total,
        "projects_with_live_jobs_db": projects_with_db,
        "jobs_with_payload_dir": payload_hits,
        "by_state": dict(by_state.most_common()),
        "by_brand": dict(by_brand.most_common()),
        "by_version": dict(by_version.most_common()),
        "by_op_kind": dict(by_op_kind.most_common()),
        "by_bridge_operation": dict(by_bridge_op.most_common()),
        "by_day": dict(sorted(by_day.items())),
        "noncommitted_signature_clusters": [
            {
                "signature": e["signature"],
                "count": e["count"],
                "states": dict(e["states"].most_common()),
                "brands": dict(e["brands"].most_common()),
                "versions": dict(e["versions"].most_common()),
                "project_count": len(e["projects"]),
                "first_seen": e["first_seen"],
                "last_seen": e["last_seen"],
                "example": e["example"],
            }
            for e in top
        ],
        "noncommitted_cluster_total": len(clusters),
        "issues": dict(issues),
        "run_seconds": round(time.time() - started, 2),
    }
    summary_path = os.path.join(ensure_out(), "jobs-summary.json")
    with open(summary_path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(summary, fh, ensure_ascii=False, indent=1)

    print(
        json.dumps(
            {
                "jobs": total,
                "projects": projects_with_db,
                "payload_dirs": payload_hits,
                "out": [os.path.join(OUT_ROOT, "jobs.jsonl"), summary_path],
                "run_seconds": summary["run_seconds"],
                "issues": dict(issues),
            },
            ensure_ascii=False,
        )
    )


if __name__ == "__main__":
    main()
