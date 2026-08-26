# Stage 0 extractor: <project>/runtime.db  ->  .log-mine/sessions.jsonl, .log-mine/messages.jsonl,
# .log-mine/messages-summary.json   (docs/log-review-2026-08-26/plan.md)
#
# The `sessions` table has 8 live column layouts plus one project with no tables at all (9 variants);
# `messages` has a single layout. Columns are discovered with `table_columns` and never assumed:
# every session row is widened to the union of the columns seen across the whole corpus, absent
# columns written as null.
import collections
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import common  # noqa: E402

# Union of every `sessions` column observed; discovery below widens this at runtime if a new one
# shows up, so the schema list here is only the stable ordering.
SESSION_COLUMNS = [
    "id",
    "name",
    "role",
    "model_profile",
    "model",
    "state",
    "sort_order",
    "codex_thread_id",
    "external_conversation_id",
    "backend",
    "current_task",
    "created_at",
    "updated_at",
    "gh_doc",
    "deleted_at",
    "goal_card",
    "approval_card",
    "ask_card",
    "permission_mode",
    "goal_enabled",
    "mode",
]

CONTINUE_EN = "Continue from where you left off"
CONTINUE_KO = "계속"
VINO_CONTEXT = "<vino_context>"
JOB_RESULTS = "<vino_job_results>"
FULLAUTO_PREFIX = "[full-auto"


def backend_norm(row):
    """'claude' | 'codex' | 'unknown' — `backend` when the column exists, else codex_thread_id."""
    backend = (row.get("backend") or "").strip().lower()
    if backend == "claude":
        return "claude"
    if backend == "codex":
        return "codex"
    if row.get("codex_thread_id"):
        return "codex"
    return "unknown"


def thread_id_of(row):
    return row.get("external_conversation_id") or row.get("codex_thread_id")


def created_day(created_at):
    dt = common.parse_iso(created_at)
    if dt is not None:
        return dt.date().isoformat()
    text = (created_at or "")[:10]
    return text or None


def fetch_dicts(conn, table, columns, order_by=None):
    """Stream `table` rows as dicts of the columns that actually exist in this database."""
    quoted = ", ".join('"{}"'.format(c) for c in columns)
    sql = 'select {} from "{}"'.format(quoted, table)
    if order_by:
        sql += " order by " + order_by
    for row in conn.execute(sql):
        yield dict(zip(columns, row))


def main():
    watermark = common.capture_watermark()   # before the first source file is opened
    started = common.datetime.now(common.timezone.utc)
    sessions_out = common.JsonlWriter("sessions.jsonl")
    messages_out = common.JsonlWriter("messages.jsonl")

    role_phase = collections.Counter()
    by_backend = collections.Counter()
    by_day = collections.Counter()
    by_project = collections.Counter()
    sessions_per_project = collections.Counter()
    error_signatures = collections.Counter()
    error_examples = {}
    unknown_columns = collections.Counter()
    issues = []
    notes_state = {
        "projects_scanned": 0,
        "with_runtime_db": 0,
        "without_runtime_db": 0,
        "no_sessions_table": [],
        "no_messages_table": [],
        "orphan_messages": 0,
        "unparsed_created_at": 0,
    }
    samples = []

    for project in common.iter_projects():
        notes_state["projects_scanned"] += 1
        db_path = os.path.join(project["path"], "runtime.db")
        if not os.path.exists(db_path):
            notes_state["without_runtime_db"] += 1
            continue
        notes_state["with_runtime_db"] += 1
        base = common.common_fields(project)
        key = "{}/{}".format(project["brand"], project["project_dir"])

        try:
            conn = common.open_sqlite_readonly(db_path)
        except Exception as exc:  # pragma: no cover - corrupt/locked db
            issues.append("open failed {}: {}".format(db_path, exc))
            continue

        try:
            has_sessions = common.table_exists(conn, "sessions")
            has_messages = common.table_exists(conn, "messages")
            if not has_sessions:
                notes_state["no_sessions_table"].append(key)
            if not has_messages:
                notes_state["no_messages_table"].append(key)

            # ---- messages grouped per session (streamed once, ordered) -------------------
            per_session = collections.defaultdict(
                lambda: {"count": 0, "first": None, "last": None}
            )
            message_rows = []
            if has_messages:
                mcols = common.table_columns(conn, "messages")
                for col in mcols:
                    if col not in (
                        "id",
                        "session_id",
                        "role",
                        "content",
                        "phase",
                        "client_message_id",
                        "created_at",
                    ):
                        unknown_columns["messages." + col] += 1
                order = "session_id, created_at, id" if "created_at" in mcols else "session_id, id"
                for row in fetch_dicts(conn, "messages", mcols, order):
                    message_rows.append(row)
                    agg = per_session[row.get("session_id")]
                    agg["count"] += 1
                    at = row.get("created_at")
                    if at:
                        if agg["first"] is None or str(at) < str(agg["first"]):
                            agg["first"] = at
                        if agg["last"] is None or str(at) > str(agg["last"]):
                            agg["last"] = at

            # ---- sessions ---------------------------------------------------------------
            session_index = {}
            if has_sessions:
                scols = common.table_columns(conn, "sessions")
                for col in scols:
                    if col not in SESSION_COLUMNS:
                        unknown_columns["sessions." + col] += 1
                for row in fetch_dicts(conn, "sessions", scols):
                    sid = row.get("id")
                    bnorm = backend_norm(row)
                    session_index[sid] = {
                        "name": row.get("name"),
                        "model": row.get("model"),
                        "backend_norm": bnorm,
                        "thread_id": thread_id_of(row),
                    }
                    agg = per_session.get(sid, {"count": 0, "first": None, "last": None})
                    record = dict(base)
                    record["session_id"] = sid
                    for col in SESSION_COLUMNS + sorted(set(scols) - set(SESSION_COLUMNS)):
                        record[col] = row.get(col)
                    record["backend_norm"] = bnorm
                    record["thread_id"] = thread_id_of(row)
                    record["message_count"] = agg["count"]
                    record["first_message_at"] = agg["first"]
                    record["last_message_at"] = agg["last"]
                    sessions_out.write(record)
                    sessions_per_project[key] += 1

            # ---- message records --------------------------------------------------------
            prev_by_session = {}
            seq_by_session = collections.Counter()
            for row in message_rows:
                sid = row.get("session_id")
                meta = session_index.get(sid)
                if meta is None:
                    notes_state["orphan_messages"] += 1
                    meta = {
                        "name": None,
                        "model": None,
                        "backend_norm": "unknown",
                        "thread_id": None,
                    }
                content = row.get("content")
                text = content or ""
                created_at = row.get("created_at")
                dt = common.parse_iso(created_at)
                if created_at and dt is None:
                    notes_state["unparsed_created_at"] += 1
                prev = prev_by_session.get(sid)
                gap = None
                if prev is not None and dt is not None and prev["dt"] is not None:
                    gap = round((dt - prev["dt"]).total_seconds(), 3)
                seq_by_session[sid] += 1
                stripped = text.strip()
                record = dict(base)
                record.update(
                    {
                        "session_id": sid,
                        "session_name": meta["name"],
                        "backend_norm": meta["backend_norm"],
                        "model": meta["model"],
                        "thread_id": meta["thread_id"],
                        "msg_id": row.get("id"),
                        "client_message_id": row.get("client_message_id"),
                        "role": row.get("role"),
                        "phase": row.get("phase"),
                        "created_at": created_at,
                        "created_day": created_day(created_at),
                        "content": content,
                        "content_len": len(text),
                        "content_signature": common.normalize_signature(text, 120),
                        "prev_role": prev["role"] if prev else None,
                        "prev_phase": prev["phase"] if prev else None,
                        "prev_created_at": prev["created_at"] if prev else None,
                        "gap_seconds": gap,
                        "seq": seq_by_session[sid],
                        "has_vino_context": VINO_CONTEXT in text,
                        "has_job_results": JOB_RESULTS in text,
                        "is_fullauto_marker": stripped.startswith(FULLAUTO_PREFIX),
                        "is_continue": (CONTINUE_EN in text) or (CONTINUE_KO in text),
                        # stricter variant: a short user turn that only says "keep going".
                        "is_continue_strict": (CONTINUE_EN in text)
                        or (
                            row.get("role") == "user"
                            and CONTINUE_KO in text
                            and len(stripped) <= 40
                        ),
                    }
                )
                messages_out.write(record)

                role_phase[(record["role"], record["phase"])] += 1
                by_backend[record["backend_norm"]] += 1
                by_day[record["created_day"]] += 1
                by_project[key] += 1
                if record["role"] == "system" and record["phase"] == "error":
                    sig = record["content_signature"]
                    error_signatures[sig] += 1
                    error_examples.setdefault(
                        sig,
                        {
                            "project_dir": record["project_dir"],
                            "brand": record["brand"],
                            "session_id": sid,
                            "created_at": created_at,
                            "content": text[:2000],
                        },
                    )
                if len(samples) < 3 and record["role"] in ("user", "system"):
                    trimmed = dict(record)
                    if trimmed["content_len"] > 300:
                        trimmed["content"] = text[:300] + "…[truncated in sample only]"
                    samples.append(trimmed)

                prev_by_session[sid] = {
                    "role": row.get("role"),
                    "phase": row.get("phase"),
                    "created_at": created_at,
                    "dt": dt,
                }
        finally:
            conn.close()

    n_sessions = sessions_out.close()
    n_messages = messages_out.close()

    summary = {
        "generated_at": common.datetime.now(common.timezone.utc).isoformat(),
        "capture_watermark_utc": watermark,
        "totals": {
            "sessions": n_sessions,
            "messages": n_messages,
            "projects_scanned": notes_state["projects_scanned"],
            "projects_with_runtime_db": notes_state["with_runtime_db"],
            "projects_without_runtime_db": notes_state["without_runtime_db"],
            "orphan_messages": notes_state["orphan_messages"],
        },
        "by_role_phase": {
            "{}/{}".format(r, p): c for (r, p), c in sorted(role_phase.items(), key=lambda kv: -kv[1])
        },
        "by_backend_norm": dict(by_backend.most_common()),
        "by_day": dict(sorted(by_day.items(), key=lambda kv: (kv[0] or ""))),
        "by_project": dict(by_project.most_common()),
        "sessions_per_project": dict(sessions_per_project.most_common()),
        "system_error_signatures": [
            {"signature": sig, "count": count, "example": error_examples[sig]}
            for sig, count in error_signatures.most_common()
        ],
        "schema": {
            "no_sessions_table": notes_state["no_sessions_table"],
            "no_messages_table": notes_state["no_messages_table"],
            "unexpected_columns": dict(unknown_columns),
        },
        "issues": issues,
    }
    summary_path = os.path.join(common.ensure_out(), "messages-summary.json")
    with open(summary_path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(summary, fh, ensure_ascii=False, indent=2, default=str)

    elapsed = (common.datetime.now(common.timezone.utc) - started).total_seconds()
    print(
        json.dumps(
            {
                "sessions": n_sessions,
                "messages": n_messages,
                "run_seconds": round(elapsed, 2),
                "state": {k: v for k, v in notes_state.items()},
                "by_role_phase": summary["by_role_phase"],
                "by_backend_norm": summary["by_backend_norm"],
                "system_error_signature_groups": len(error_signatures),
                "issues": issues,
                "samples": samples,
            },
            ensure_ascii=False,
            default=str,
        )
    )


if __name__ == "__main__":
    main()
