# Shared helpers for the log-mining extractors (docs/log-review-2026-08-26/plan.md, Stage 0).
# Pure stdlib. Every extractor imports this, iterates `iter_projects()`, and writes JSONL via
# `JsonlWriter`. Output root is `.log-mine/` at the repo root (gitignored).
import glob
import json
import os
import re
import sqlite3
from datetime import datetime, timezone

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT_ROOT = os.path.join(REPO_ROOT, ".log-mine")
LOCALAPPDATA = os.environ.get("LOCALAPPDATA", os.path.expanduser("~\\AppData\\Local"))
HOME = os.path.expanduser("~")
CODEX_SESSIONS = os.path.join(HOME, ".codex", "sessions")
CLAUDE_PROJECTS = os.path.join(HOME, ".claude", "projects")

PROJECT_ROOTS = [
    ("Vino", os.path.join(LOCALAPPDATA, "Vino", "projects")),
    ("GPTino", os.path.join(LOCALAPPDATA, "GPTino", "projects")),
]

# 0.1.0-alpha.7 shipped 2026-08-14 (commit 33bfa01). Everything without a stamp before that is
# "pre-alpha7"; a host.log content-root path or a problem-log `v` field wins when present.
ALPHA7 = "0.1.0-alpha.7"
ALPHA7_DATE = datetime(2026, 8, 14, tzinfo=timezone.utc)

_VERSION_RE = re.compile(r"packages[\\/]8\.0[\\/](?:Vino|GPTino)[\\/]([^\\/]+)[\\/]")
_GUID_RE = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
_HEX_RE = re.compile(r"\b[0-9a-f]{16,}\b")
_NUM_RE = re.compile(r"\d+(?:\.\d+)?")


def ensure_out(sub=None):
    path = OUT_ROOT if sub is None else os.path.join(OUT_ROOT, sub)
    os.makedirs(path, exist_ok=True)
    return path


def capture_watermark():
    """UTC ISO timestamp taken *before* an extractor opens its first source file.

    The SQLite/JSONL sources are live: the app can append while we read. The watermark is the
    line that separates 'this record was written after we looked' (expected, harmless) from
    'this record existed and we lost it' (a real extraction bug). Every *-summary.json carries
    it as `capture_watermark_utc`.
    """
    return datetime.now(timezone.utc).isoformat()


class JsonlWriter:
    """Append-only JSONL writer; `write(dict)` one record per line, UTF-8, no ASCII escaping."""

    def __init__(self, name, sub=None):
        self.path = os.path.join(ensure_out(sub), name)
        self._fh = open(self.path, "w", encoding="utf-8", newline="\n")
        self.count = 0

    def write(self, record):
        self._fh.write(json.dumps(record, ensure_ascii=False, default=str))
        self._fh.write("\n")
        self.count += 1

    def close(self):
        self._fh.close()
        return self.count

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


def read_jsonl(path):
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                yield json.loads(line)
            except json.JSONDecodeError:
                continue


def normalize_signature(message, width=110):
    """Collapse GUIDs/long hex/numbers to '#' and cut to `width` chars — the cluster key."""
    text = message or ""
    text = _GUID_RE.sub("#", text)
    text = _HEX_RE.sub("#", text)
    text = _NUM_RE.sub("#", text)
    return text[:width].strip()


def normalize_state(state):
    return (state or "").strip().lower()


def project_version(project_dir):
    """Version stamp for a project folder: host.log content root > problem-log `v` > date."""
    host_log = os.path.join(project_dir, "host.log")
    if os.path.exists(host_log):
        with open(host_log, encoding="utf-8", errors="ignore") as fh:
            for line in fh:
                m = _VERSION_RE.search(line)
                if m:
                    return m.group(1)
    problem_log = os.path.join(project_dir, "problem-log.jsonl")
    if os.path.exists(problem_log):
        for rec in read_jsonl(problem_log):
            v = rec.get("v")
            if v:
                return v
    return "pre-alpha7"


def project_name(project_dir):
    meta = os.path.join(project_dir, "context", "project.json")
    if os.path.exists(meta):
        try:
            with open(meta, encoding="utf-8") as fh:
                data = json.load(fh)
            return data.get("projectName") or "?", data.get("rhinoFile"), data.get("createdAt")
        except (OSError, json.JSONDecodeError):
            pass
    return "?", None, None


def iter_projects():
    """Yield one dict per project folder under both brands, with the common stamp fields."""
    for brand, root in PROJECT_ROOTS:
        if not os.path.isdir(root):
            continue
        for path in sorted(glob.glob(os.path.join(root, "*"))):
            if not os.path.isdir(path):
                continue
            name, rhino_file, created_at = project_name(path)
            yield {
                "path": path,
                "project_dir": os.path.basename(path),
                "brand": brand,
                "project_name": name,
                "rhino_file": rhino_file,
                "project_created_at": created_at,
                "version": project_version(path),
            }


def common_fields(project):
    return {
        "project_dir": project["project_dir"],
        "brand": project["brand"],
        "project_name": project["project_name"],
        "version": project["version"],
    }


def open_sqlite_readonly(path):
    uri = "file:{}?mode=ro".format(path.replace("\\", "/"))
    return sqlite3.connect(uri, uri=True)


def table_columns(conn, table):
    return [row[1] for row in conn.execute('pragma table_info("{}")'.format(table))]


def table_exists(conn, table):
    row = conn.execute(
        "select 1 from sqlite_master where type='table' and name=?", (table,)
    ).fetchone()
    return row is not None


def parse_iso(text):
    """Parse the .NET ISO timestamps found in the logs ('+00:00', 7-digit fractions)."""
    if not text:
        return None
    t = str(text).strip()
    m = re.match(r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:\d{2})?", t)
    if not m:
        return None
    base, frac, tz = m.groups()
    frac = (frac or "0")[:6].ljust(6, "0")
    tz = "+00:00" if tz in (None, "Z") else tz
    try:
        return datetime.fromisoformat("{}.{}{}".format(base, frac, tz))
    except ValueError:
        return None


def project_dir_from_cwd(cwd):
    """Map a codex rollout cwd (…\\{Vino,GPTino}\\projects\\<dir>\\…) to (brand, project_dir)."""
    if not cwd:
        return None, None
    m = re.search(r"[\\/](Vino|GPTino)[\\/]projects[\\/]([0-9A-Fa-f]{16})", cwd)
    if not m:
        return None, None
    return m.group(1), m.group(2).upper()
