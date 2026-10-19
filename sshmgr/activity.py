"""Append-only activity log (commands seen in SSH sessions, file operations), kept as JSON lines."""
import json
import threading
from collections import deque
from datetime import datetime, timedelta, timezone
from pathlib import Path

from . import agents


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class ActivityLog:
    def __init__(self, path: Path, keep: int = 5000):
        self.path = path
        self.lock = threading.Lock()
        self.items: deque[dict] = deque(maxlen=keep)
        self.next_id = 1
        if path.exists():
            with path.open(encoding="utf-8") as f:
                for line in f:
                    try:
                        item = json.loads(line)
                    except ValueError:
                        continue
                    self.items.append(item)
                    self.next_id = max(self.next_id, item.get("id", 0) + 1)

    def add(self, kind: str, ts: str | None = None, **fields) -> dict:
        if "agent" not in fields and (agent := agents.current.get()):
            fields["agent"] = agent   # done by an AI agent (see agents.py)
        with self.lock:
            item = {"id": self.next_id, "ts": ts or now_iso(), "kind": kind, **fields}
            self.next_id += 1
            self.items.append(item)
            self.path.parent.mkdir(parents=True, exist_ok=True)
            with self.path.open("a", encoding="utf-8") as f:
                f.write(json.dumps(item, ensure_ascii=False) + "\n")
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
            except (KeyError, ValueError):
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
                c["bytes"] += int(i.get("size") or 0)
        return c

    def list(self, after: int = 0, limit: int = 500, kind: str | None = None) -> list[dict]:
        with self.lock:
            items = [i for i in self.items if i["id"] > after and (not kind or i["kind"] == kind)]
        return items[-limit:]
