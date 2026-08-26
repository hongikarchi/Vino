"""Stage 0 extractor: `<project>/host.log` -> `.log-mine/hostlog-events.jsonl`.

host.log lines look like

    2026-08-26 07:56:15.317 +09:00 [Information] Vino.AgentHost: message text

Lines that do not open with a timestamp are continuations of the previous line (exception text
and stack frames); they are attached to the preceding record as `continuation` (<=3000 chars).

Kept:
  * every Warning / Error / Critical / Fatal line, whatever the category
    (this is what preserves `Microsoft.Extensions.Hosting.Internal.Host` "Hosting failed to
    start" errors together with their stack)
  * Information lines whose category starts with `Vino.` or `GPTino.`

Dropped: Information noise from `Microsoft.AspNetCore.*`, `Microsoft.Hosting.*`,
`Microsoft.Extensions.*` (request/endpoint/static-file chatter) and their continuations.

Also emits `.log-mine/hostlog-summary.json`.

Run: PYTHONIOENCODING=utf-8 C:/Python314/python scripts/log-mine/extract_hostlog.py
"""

import collections
import json
import os
import re
import sys
import time
from datetime import timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from common import (  # noqa: E402
    OUT_ROOT,
    JsonlWriter,
    capture_watermark,
    common_fields,
    ensure_out,
    iter_projects,
    normalize_signature,
    parse_iso,
)

# `2026-08-26 07:56:15.317 +09:00 [Information] Category: message`
HEADER_RE = re.compile(
    r"^(?P<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)? "
    r"(?:[+-]\d{2}:\d{2}|Z))\s+\[(?P<level>\w+)\]\s+(?P<category>[^\s:]+):\s?(?P<message>.*)$"
)
LOUD_LEVELS = ("Warning", "Error", "Critical", "Fatal")
KEPT_INFO_PREFIXES = ("Vino.", "GPTino.")
NOISE_PREFIXES = ("Microsoft.AspNetCore.", "Microsoft.Hosting.", "Microsoft.Extensions.")
CONTINUATION_CAP = 3000
TOP_SIGNATURES = 80
HOSTING_FAILED = "Hosting failed to start"


def to_iso_utc(ts):
    """'2026-08-26 07:56:15.317 +09:00' -> (local ISO, UTC ISO). UTC is None when unparsable."""
    iso_local = ts.replace(" ", "T", 1).replace(" ", "", 1)
    parsed = parse_iso(iso_local)
    if parsed is None:
        return iso_local, None
    return parsed.isoformat(), parsed.astimezone(timezone.utc).isoformat()


def keep(level, category):
    if level in LOUD_LEVELS:
        return True
    if level == "Information" and category.startswith(KEPT_INFO_PREFIXES):
        return True
    return False


class Pending:
    """A header line plus the continuation lines that follow it, flushed on the next header."""

    __slots__ = ("record", "parts", "length", "kept")

    def __init__(self, record, kept):
        self.record = record
        self.parts = []
        self.length = 0
        self.kept = kept

    def add(self, line):
        if not self.kept or self.length >= CONTINUATION_CAP:
            return
        piece = line[: CONTINUATION_CAP - self.length]
        self.parts.append(piece)
        self.length += len(piece) + 1

    def finish(self):
        if self.parts:
            self.record["continuation"] = "\n".join(self.parts)[:CONTINUATION_CAP]
        return self.record


def main():
    watermark = capture_watermark()   # before the first source file is opened
    started = time.time()
    writer = JsonlWriter("hostlog-events.jsonl")

    by_lvl_cat_sig = collections.Counter()
    errors_by_project = collections.Counter()
    warnings_by_project = collections.Counter()
    by_level = collections.Counter()
    by_level_category = collections.Counter()
    hosting_failed_first_lines = collections.Counter()
    hosting_failed_projects = collections.Counter()
    stats = collections.Counter()
    unparsed_headers = []

    def flush(pending):
        if pending is None or not pending.kept:
            return
        record = pending.finish()
        writer.write(record)
        level = record["level"]
        category = record["category"]
        by_lvl_cat_sig["{}|{}|{}".format(level, category, record["message_signature"])] += 1
        by_level[level] += 1
        by_level_category["{}|{}".format(level, category)] += 1
        who = "{}/{}/{}".format(record["brand"], record["project_dir"], record["project_name"])
        if level in ("Error", "Critical", "Fatal"):
            errors_by_project[who] += 1
        elif level == "Warning":
            warnings_by_project[who] += 1
        if HOSTING_FAILED in (record["message"] or ""):
            lines = (record.get("continuation") or "").splitlines()
            hosting_failed_first_lines[lines[0].strip() if lines else "(no continuation)"] += 1
            hosting_failed_projects["{}/{}".format(record["brand"], record["project_dir"])] += 1

    for project in iter_projects():
        path = os.path.join(project["path"], "host.log")
        if not os.path.exists(path):
            continue
        stats["files"] += 1
        base = common_fields(project)
        pending = None

        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                line = line.rstrip("\r\n")
                stats["source_lines"] += 1
                match = HEADER_RE.match(line)
                if match is None:
                    if not line.strip():
                        continue
                    if line[:4].isdigit() and len(unparsed_headers) < 10:
                        unparsed_headers.append(line[:200])
                    if pending is None:
                        stats["orphan_continuations"] += 1
                        continue
                    stats["continuation_lines"] += 1
                    if pending.kept:
                        stats["continuation_lines_attached"] += 1
                    pending.add(line)
                    continue

                flush(pending)
                level = match.group("level")
                category = match.group("category")
                message = match.group("message")
                if not keep(level, category):
                    stats["dropped_information"] += 1
                    if category.startswith(NOISE_PREFIXES):
                        stats["dropped_information_microsoft"] += 1
                    pending = Pending(None, False)
                    continue
                at_local, at_utc = to_iso_utc(match.group("ts"))
                record = dict(base)
                record.update(
                    {
                        "source_file": path,
                        "at": at_local,
                        "at_utc": at_utc,
                        "at_day": (at_utc or at_local or "")[:10] or None,
                        "level": level,
                        "category": category,
                        "message": message,
                        "message_signature": normalize_signature(message),
                        "continuation": None,
                    }
                )
                pending = Pending(record, True)
            flush(pending)

    total = writer.close()

    summary = {
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "capture_watermark_utc": watermark,
        "source_files": stats["files"],
        "source_lines": stats["source_lines"],
        "records": total,
        "dropped_information_lines": stats["dropped_information"],
        "dropped_information_microsoft_noise": stats["dropped_information_microsoft"],
        "continuation_lines_seen": stats["continuation_lines"],
        "continuation_lines_attached": stats["continuation_lines_attached"],
        "orphan_continuation_lines": stats["orphan_continuations"],
        "unparsed_timestamped_lines": unparsed_headers,
        "by_level": dict(by_level.most_common()),
        "by_level_category": dict(by_level_category.most_common()),
        "top_level_category_signature": [
            {"key": k, "count": c} for k, c in by_lvl_cat_sig.most_common(TOP_SIGNATURES)
        ],
        "errors_by_project": dict(errors_by_project.most_common()),
        "warnings_by_project": dict(warnings_by_project.most_common()),
        "hosting_failed_first_lines": [
            {"first_line": k, "count": c} for k, c in hosting_failed_first_lines.most_common()
        ],
        "hosting_failed_by_project": dict(hosting_failed_projects.most_common()),
    }
    ensure_out()
    out = os.path.join(OUT_ROOT, "hostlog-summary.json")
    with open(out, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(summary, fh, ensure_ascii=False, indent=2)

    print(
        json.dumps(
            {
                "script": "extract_hostlog.py",
                "files": stats["files"],
                "records": total,
                "seconds": round(time.time() - started, 2),
            },
            ensure_ascii=False,
        )
    )


if __name__ == "__main__":
    main()
