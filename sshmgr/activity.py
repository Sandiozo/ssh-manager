"""Append-only activity log (commands seen in SSH sessions, file operations), kept as JSON lines.

The file is compacted at startup to the newest COMPACT_TO entries once it holds more than COMPACT_AT.
A corrupt line (a crash mid-write, an editor) is skipped instead of stopping the server. Writing the log
never makes a request fail: a full disk or a locked file is logged and the entry stays in memory.

`log_id` changes whenever entry ids start over (the file was deleted or emptied), so a page that
remembers "entries after id N" can tell that it must start from the beginning again."""
import json
import logging
import os
import secrets
import threading
from collections import deque
from datetime import datetime, timedelta, timezone
from pathlib import Path

from . import agents

log = logging.getLogger("sshmgr")
COMPACT_AT = 50_000
COMPACT_TO = 20_000


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class ActivityLog:
    def __init__(self, path: Path, keep: int = 5000):
        self.path = path
        self.lock = threading.Lock()
        self.items: deque[dict] = deque(maxlen=keep)
        self.next_id = 1
        self._write_failed = False
        lines = 0
        if path.exists():
            try:
                with path.open(encoding="utf-8", errors="replace") as f:
                    for line in f:
                        lines += 1
                        try:
                            item = json.loads(line)
                        except ValueError:
                            continue
                        if not isinstance(item, dict) or not isinstance(item.get("id"), int) or "kind" not in item:
                            continue
                        self.items.append(item)
                        self.next_id = max(self.next_id, item["id"] + 1)
            except OSError as e:
                log.warning("couldn't read %s: %s", path, e)
        if lines > COMPACT_AT:
            self._compact()
        self.log_id = self._log_id()

    def _log_id(self) -> str:
        """Stays the same while ids keep counting up; new when they start over from 1."""
        id_file = self.path.with_name(self.path.name + ".id")
        try:
            if self.items and (value := id_file.read_text(encoding="utf-8").strip()):
                return value
        except OSError:
            pass
        value = secrets.token_hex(8)
        try:
            id_file.write_text(value, encoding="utf-8")
        except OSError as e:
            log.warning("couldn't write %s: %s", id_file, e)
        return value

    def _compact(self):
        """Keeps the newest COMPACT_TO entries in the file (ids unchanged)."""
        keep: deque[str] = deque(maxlen=COMPACT_TO)
        tmp = self.path.with_name(f"{self.path.name}.{secrets.token_hex(4)}.tmp")
        try:
            with self.path.open(encoding="utf-8", errors="replace") as f:
                for line in f:
                    if line.strip():
                        keep.append(line if line.endswith("\n") else line + "\n")
            with tmp.open("w", encoding="utf-8") as f:
                f.writelines(keep)
            os.replace(tmp, self.path)
            log.info("activity log compacted to the newest %d entries", len(keep))
        except OSError as e:
            log.warning("couldn't compact %s: %s", self.path, e)
        finally:
            tmp.unlink(missing_ok=True)

    def add(self, kind: str, ts: str | None = None, **fields) -> dict:
        if "agent" not in fields and (agent := agents.current.get()):
            fields["agent"] = agent   # done by an AI agent (see agents.py)
        with self.lock:
            item = {"id": self.next_id, "ts": ts or now_iso(), "kind": kind, **fields}
            self.next_id += 1
            self.items.append(item)
            try:
                self.path.parent.mkdir(parents=True, exist_ok=True)
                with self.path.open("a", encoding="utf-8") as f:
                    f.write(json.dumps(item, ensure_ascii=False, default=str) + "\n")
                self._write_failed = False
            except (OSError, ValueError, TypeError) as e:
                if not self._write_failed:   # once per failure streak, not for every entry
                    log.warning("couldn't write the activity log %s: %s", self.path, e)
                self._write_failed = True
            return item

    def counts_24h(self) -> dict:
        """How much happened here in the last 24 hours (for the app's All PCs totals)."""
        cutoff = datetime.now(timezone.utc) - timedelta(hours=24)
        c = {"commands": 0, "actions": 0, "files": 0, "bytes": 0}
        with self.lock:
            items = list(self.items)
        for i in reversed(items):
            try:
                ts = datetime.fromisoformat(i["ts"])
            except (KeyError, ValueError, TypeError):
                continue
            if ts.tzinfo is None:
                ts = ts.replace(tzinfo=timezone.utc)
            if ts < cutoff:
                break
            kind = i.get("kind")
            if kind == "command" and i.get("type") != "dashboard":
                c["commands"] += 1
            elif kind == "action":
                c["actions"] += 1
            elif kind == "file" and i.get("action") in ("upload", "download", "download-zip"):
                c["files"] += 1
                try:
                    c["bytes"] += int(i.get("size") or 0)
                except (TypeError, ValueError):
                    pass
        return c

    def last_id(self) -> int:
        with self.lock:
            return self.next_id - 1

    def list(self, after: int = 0, limit: int = 500, kind: str | None = None) -> list[dict]:
        with self.lock:
            items = [i for i in self.items if i["id"] > after and (not kind or i["kind"] == kind)]
        return items[-limit:]
