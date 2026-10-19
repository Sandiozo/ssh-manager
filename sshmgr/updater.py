"""Checks GitHub for a newer release and, on an installed copy, installs it.

The server runs with admin rights, so it can run the installer silently (no UAC prompt). The
installer updates everything installed on this PC (server and app), restarts the server and lets
Windows restart the app. Copies running from source are only told about updates (use git pull).
"""
import hashlib
import json
import logging
import subprocess
import threading
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

from . import __version__
from .home import FROZEN

log = logging.getLogger("sshmgr.update")
ASSET = "SSHManager-Setup.exe"
CHECK_EVERY = 6 * 3600
DETACHED = 0x00000008 | 0x00000200 | 0x01000000   # DETACHED_PROCESS | NEW_PROCESS_GROUP | BREAKAWAY_FROM_JOB


def parse_version(v: str) -> tuple:
    parts = []
    for p in v.strip().lstrip("vV").split("."):
        digits = "".join(ch for ch in p if ch.isdigit())
        parts.append(int(digits or 0))
    return tuple(parts + [0] * (3 - len(parts)))


def _get(url: str, timeout: float = 20):
    req = urllib.request.Request(url, headers={"User-Agent": f"ssh-manager/{__version__}",
                                               "Accept": "application/vnd.github+json"})
    return urllib.request.urlopen(req, timeout=timeout)


class Updater:
    def __init__(self, config: dict, data: Path, activity):
        self.config, self.data, self.activity = config, data, activity
        self.lock = threading.Lock()
        self.state = {"current": __version__, "latest": None, "available": False, "checked": None,
                      "error": None, "status": "idle", "installable": FROZEN, "notes": "", "url": None}
        self.release = None

    @property
    def repo(self) -> str:
        return self.config.get("update_repo") or "Sandiozo/ssh-manager"

    @property
    def auto(self) -> bool:
        return self.config.get("auto_update", True)

    def start(self):
        threading.Thread(target=self._loop, daemon=True).start()

    def _loop(self):
        time.sleep(30)
        while True:
            try:
                self.check()
                if self.state["available"] and self.auto and FROZEN:
                    self.install(reason="automatic update")
            except Exception:
                log.exception("update check failed")
            time.sleep(CHECK_EVERY)

    def check(self) -> dict:
        try:
            with _get(f"https://api.github.com/repos/{self.repo}/releases/latest") as r:
                rel = json.load(r)
            latest = rel.get("tag_name", "").lstrip("vV")
            asset = next((a for a in rel.get("assets", []) if a["name"] == ASSET), None)
            sha = next((a for a in rel.get("assets", []) if a["name"] == ASSET + ".sha256"), None)
            self.release = {"version": latest, "asset": asset, "sha": sha}
            with self.lock:
                self.state.update(latest=latest, error=None, notes=(rel.get("body") or "")[:4000], url=rel.get("html_url"),
                                  available=bool(asset) and parse_version(latest) > parse_version(__version__),
                                  checked=datetime.now(timezone.utc).isoformat(timespec="seconds"))
        except Exception as e:
            with self.lock:
                self.state.update(error=f"Couldn't check for updates: {e}",
                                  checked=datetime.now(timezone.utc).isoformat(timespec="seconds"))
        return self.snapshot()

    def snapshot(self) -> dict:
        with self.lock:
            return {**self.state, "auto": self.auto}

    def install(self, reason: str = "update") -> dict:
        if not FROZEN:
            raise RuntimeError("This copy runs from source; update it with git pull.")
        if not (self.release and self.state["available"]):
            raise RuntimeError("No update available.")
        with self.lock:
            if self.state["status"] != "idle":
                return self.snapshot()
            self.state["status"] = "downloading"
        threading.Thread(target=self._download_and_run, args=(reason,), daemon=True).start()
        return self.snapshot()

    def _download_and_run(self, reason: str):
        try:
            folder = self.data / "updates"
            folder.mkdir(exist_ok=True)
            version = self.release["version"]
            target = folder / f"SSHManager-Setup-{version}.exe"
            digest = hashlib.sha256()
            with _get(self.release["asset"]["browser_download_url"], timeout=60) as r, open(target, "wb") as f:
                while chunk := r.read(1 << 20):
                    f.write(chunk)
                    digest.update(chunk)
            if self.release["sha"]:
                with _get(self.release["sha"]["browser_download_url"]) as r:
                    expected = r.read().decode().split()[0].lower()
                if digest.hexdigest() != expected:
                    target.unlink(missing_ok=True)
                    raise RuntimeError("The downloaded update didn't match its checksum; not installing it.")
            with self.lock:
                self.state["status"] = "installing"
            self.activity.add("setup", action="update", client="SSH Manager", version=version, note=reason)
            # The installer stops this server, replaces the files and starts the new one.
            args = [str(target), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", f"/LOG={folder / 'install.log'}"]
            try:
                subprocess.Popen(args, creationflags=DETACHED, close_fds=True)
            except OSError:   # inside a job that doesn't allow breaking away
                subprocess.Popen(args, creationflags=DETACHED & ~0x01000000, close_fds=True)
        except Exception as e:
            log.exception("update failed")
            with self.lock:
                self.state.update(status="idle", error=str(e))
