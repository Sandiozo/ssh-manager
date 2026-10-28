"""Checks GitHub for a newer release and, on an installed copy, installs it.

The server runs with admin rights, so it can run the installer silently (no UAC prompt). The
installer updates everything installed on this PC (server and app), restarts the server and reopens
the app. Copies running from source are only told about updates (use git pull).

An update is installed only when the installer matches its published SHA-256 *and* carries a valid
release signature (ed25519, made by the release workflow with the key whose public half is pinned
below; checked with Windows' ssh-keygen). It is downloaded into an admin-only folder and kept open
(no writing, renaming or deleting by others) from the checks until the installer has started.

Automatic installs wait while runs or file transfers are in progress (`busy`), and back off after a
failed attempt instead of retrying it every time. A manual "Install now" goes ahead regardless (the
page asks first when `busy` is set).
"""
import ctypes
import hashlib
import json
import logging
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Optional

from . import __version__
from .home import FROZEN

log = logging.getLogger("sshmgr.update")
ASSET = "SSHManager-Setup.exe"
CHECK_EVERY = 6 * 3600           # ask GitHub
TICK = 10 * 60                   # look again whether a postponed automatic install can go ahead
INSTALL_TIMEOUT = 20 * 60        # "installing" for longer than this (and still running): it failed
BACKOFF = (6 * 3600, 24 * 3600, 3 * 24 * 3600)   # wait after the 1st, 2nd, 3rd+ failure of a version
MAX_INSTALLER = 400 << 20
DETACHED = 0x00000008 | 0x00000200 | 0x01000000   # DETACHED_PROCESS | NEW_PROCESS_GROUP | BREAKAWAY_FROM_JOB
CREATE_NO_WINDOW = 0x08000000

# The release signing keys (public halves). Releases are signed with the first; its private half is the
# release workflow's RELEASE_SIGNING_KEY secret (see .github/workflows/release.yml). The second is a backup
# kept offline: if the first is ever lost or leaked, a release signed with the backup replaces it.
SIGNER = "sshmanager-release"
NAMESPACE = "sshmanager-release"
ALLOWED_SIGNERS = ("sshmanager-release ssh-ed25519 "
                   "AAAAC3NzaC1lZDI1NTE5AAAAINA7i2sOU03vcOMdvoyMlz09XEufb34fqN8PprEfNd8r\n"
                   "sshmanager-release ssh-ed25519 "
                   "AAAAC3NzaC1lZDI1NTE5AAAAICYUvd8e+M5q9ZZMhL76qYzmUk3EN6eWRecEpkikPZIc\n")


class UpdateError(Exception):
    pass


def parse_version(v: str) -> tuple:
    """(major, minor, patch, release): only the leading digits of each part count, and a pre-release
    ("1.7.0-rc.1") comes before the release with the same numbers."""
    core, _, pre = v.strip().lstrip("vV").partition("-")
    nums = []
    for part in core.split(".")[:3]:
        m = re.match(r"\d+", part)
        nums.append(int(m.group()) if m else 0)
    nums += [0] * (3 - len(nums))
    return (*nums, 0 if pre else 1)


def _get(url: str, timeout: float = 20):
    req = urllib.request.Request(url, headers={"User-Agent": f"ssh-manager/{__version__}",
                                               "Accept": "application/vnd.github+json"})
    return urllib.request.urlopen(req, timeout=timeout)


def _read_small(url: str, limit: int = 64 << 10) -> bytes:
    with _get(url) as r:
        data = r.read(limit + 1)
    if len(data) > limit:
        raise UpdateError(f"{url} is unexpectedly large.")
    return data


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


# ---- release signature ----

def _keygen_candidates() -> list:
    """Windows' own ssh-keygen first, then the Win32-OpenSSH MSI's, then whatever is on PATH."""
    windir = os.environ.get("WINDIR", r"C:\Windows")
    found = [Path(windir) / "System32" / "OpenSSH" / "ssh-keygen.exe",
             Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "OpenSSH" / "ssh-keygen.exe"]
    if p := shutil.which("ssh-keygen"):
        found.append(Path(p))
    seen, result = set(), []
    for p in found:
        key = str(p).lower()
        if key not in seen and p.is_file():
            seen.add(key)
            result.append(p)
    return result


def verify_signature(installer: Path, signature: bytes, workdir: Path) -> None:
    """Raises UpdateError unless `signature` is a valid release signature of the installer's bytes."""
    signers = workdir / "allowed_signers"
    sig = workdir / (installer.name + ".sig")
    signers.write_text(ALLOWED_SIGNERS, encoding="ascii")
    sig.write_bytes(signature)
    problem = "ssh-keygen.exe (OpenSSH) wasn't found"
    try:
        for exe in _keygen_candidates():
            try:
                with open(installer, "rb") as f:   # the signed data comes on stdin
                    r = subprocess.run([str(exe), "-Y", "verify", "-f", str(signers), "-I", SIGNER, "-n", NAMESPACE, "-s", str(sig)],
                                       stdin=f, capture_output=True, timeout=120,
                                       creationflags=CREATE_NO_WINDOW if sys.platform == "win32" else 0)
            except (OSError, subprocess.TimeoutExpired) as e:
                problem = f"{exe}: {e}"
                continue
            out = "; ".join(line.strip() for line in (r.stdout + r.stderr).decode("utf-8", "replace").splitlines() if line.strip())
            if r.returncode == 0 and out.startswith("Good"):
                return
            if "unknown option" in out or "usage:" in out.lower():   # OpenSSH older than 8.1: no -Y
                problem = f"{exe} is too old to check signatures (needs OpenSSH 8.1 or newer)"
                continue
            raise UpdateError(f"The update's signature isn't valid ({out or f'exit code {r.returncode}'}); not installing it.")
        raise UpdateError(f"Couldn't check the update's signature: {problem}. Not installing it.")
    finally:
        for p in (signers, sig):
            try:
                p.unlink()
            except OSError:
                pass


# ---- the admin-only download folder ----

def _is_link(p: Path) -> bool:
    try:
        return p.is_symlink() or bool(getattr(os.path, "isjunction", lambda _: False)(p)) or \
            bool(os.lstat(p).st_file_attributes & 0x400)   # FILE_ATTRIBUTE_REPARSE_POINT
    except (OSError, AttributeError):
        return False


def _icacls(*args: str, check: bool = True) -> None:
    windir = os.environ.get("WINDIR", r"C:\Windows")
    r = subprocess.run([str(Path(windir) / "System32" / "icacls.exe"), *args], capture_output=True, timeout=60,
                       creationflags=CREATE_NO_WINDOW)
    if r.returncode and check:
        raise UpdateError("Couldn't set permissions on the update folder: " +
                          (r.stdout + r.stderr).decode("utf-8", "replace").strip())


def updates_folder() -> Path:
    """%ProgramData%\\SSHManager\\updates, emptied and made writable only by Administrators and SYSTEM
    (by SID, so it works on any Windows language)."""
    base = Path(os.environ.get("ProgramData", r"C:\ProgramData")) / "SSHManager"
    folder = base / "updates"
    if _is_link(base):
        raise UpdateError(f"{base} is a link; not downloading updates there. Run the installer again to fix it.")
    base.mkdir(parents=True, exist_ok=True)
    if _is_link(folder):
        os.rmdir(folder)                           # removes the link, not what it points to
    elif folder.exists():
        shutil.rmtree(folder, ignore_errors=True)   # old installers; also anything someone else put there
    folder.mkdir(exist_ok=True)
    if any(folder.iterdir()):
        raise UpdateError(f"Couldn't empty {folder}.")
    # an elevated admin's new folder is owned by Administrators already; this only makes sure
    _icacls(str(folder), "/setowner", "*S-1-5-32-544", "/Q", check=False)
    _icacls(str(folder), "/inheritance:r", "/grant:r", "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-18:(OI)(CI)F", "/Q")
    return folder


class _Locked:
    """The file opened for reading with only read sharing: nobody can change, rename or delete it
    while this is open (starting it as a program still works)."""

    def __init__(self, path: Path):
        self.handle = None
        if sys.platform != "win32":
            return
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.CreateFileW.restype = ctypes.c_void_p
        k32.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
                                    ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
        k32.CloseHandle.argtypes = [ctypes.c_void_p]
        self.k32 = k32
        h = k32.CreateFileW(str(path), 0x80000000, 0x1, None, 3, 0x80, None)   # GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING
        if h in (None, ctypes.c_void_p(-1).value):
            raise UpdateError(f"Couldn't lock {path.name} (Windows error {ctypes.get_last_error()}).")
        self.handle = h

    def close(self):
        if self.handle:
            self.k32.CloseHandle(self.handle)
            self.handle = None

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


class Updater:
    def __init__(self, config: dict, data: Path, activity, busy: Optional[Callable[[], bool]] = None):
        self.config, self.data, self.activity, self.busy_check = config, data, activity, busy
        self.lock = threading.Lock()
        self.state = {"current": __version__, "latest": None, "available": False, "checked": None,
                      "error": None, "status": "idle", "installable": FROZEN, "notes": "", "url": None,
                      "verifiable": False, "postponed": None, "failed": None, "installing_since": None}
        self.release = None
        self.state_file = data / "update-state.json"
        self._busy_error_logged = False

    @property
    def repo(self) -> str:
        return self.config.get("update_repo") or "Sandiozo/ssh-manager"

    @property
    def auto(self) -> bool:
        return self.config.get("auto_update", True)

    # ---- what is remembered across restarts: the install in progress, the last failed version ----

    def _load(self) -> dict:
        try:
            return json.loads(self.state_file.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {}

    def _save(self, saved: dict) -> None:
        try:
            tmp = self.state_file.with_name(f"{self.state_file.name}.{os.getpid()}.tmp")
            tmp.write_text(json.dumps(saved), encoding="utf-8")
            os.replace(tmp, self.state_file)
        except OSError:
            log.exception("couldn't save the update state")

    def _record_failure(self, version: str, error: str) -> None:
        saved = self._load()
        failed = saved.get("failed") or {}
        count = failed.get("count", 0) + 1 if failed.get("version") == version else 1
        saved["failed"] = {"version": version, "error": error, "at": time.time(), "count": count}
        saved.pop("pending", None)
        self._save(saved)
        log.warning("update to %s failed (%d): %s", version, count, error)
        with self.lock:
            self.state.update(status="idle", error=error, installing_since=None, failed=self._failed_view(saved))

    @staticmethod
    def _failed_view(saved: dict):
        f = saved.get("failed")
        if not f:
            return None
        return {"version": f.get("version"), "error": f.get("error"), "count": f.get("count", 1),
                "at": datetime.fromtimestamp(f.get("at", 0), timezone.utc).isoformat(timespec="seconds")}

    def _check_previous_install(self) -> None:
        """After a restart: did the update started before finish? (The installer replaces this
        server, so a version still older than the one being installed means it failed.)"""
        saved = self._load()
        pending = saved.get("pending")
        if pending:
            version = pending.get("version", "")
            if parse_version(__version__) >= parse_version(version):
                saved.pop("pending", None)
                if (saved.get("failed") or {}).get("version") == version:
                    saved.pop("failed", None)
                self._save(saved)
                log.info("update to %s finished", version)
            else:
                self._record_failure(version, f"The update to {version} didn't finish; this PC still has {__version__}. "
                                              f"The installer's log is {self._install_log()}.")
                return
        with self.lock:
            self.state["failed"] = self._failed_view(saved)

    def _install_log(self) -> Path:
        return Path(os.environ.get("ProgramData", r"C:\ProgramData")) / "SSHManager" / "updates" / "install.log"

    def _backoff_left(self, version: str) -> float:
        failed = self._load().get("failed") or {}
        if failed.get("version") != version:
            return 0
        wait = BACKOFF[min(failed.get("count", 1), len(BACKOFF)) - 1]
        return max(0.0, failed.get("at", 0) + wait - time.time())

    def is_busy(self) -> bool:
        if not self.busy_check:
            return False
        try:
            return bool(self.busy_check())
        except Exception:
            if not self._busy_error_logged:
                log.exception("busy check failed")
                self._busy_error_logged = True
            return False

    # ---- checking and installing ----

    def start(self):
        self._check_previous_install()
        threading.Thread(target=self._loop, daemon=True).start()

    def _loop(self):
        time.sleep(30)
        next_check = 0.0
        while True:
            try:
                if time.time() >= next_check:
                    self.check()
                    next_check = time.time() + CHECK_EVERY
                self._auto_install()
                self._watch_installing()
            except Exception:
                log.exception("update check failed")
            time.sleep(TICK)

    def _auto_install(self) -> None:
        with self.lock:
            ready = self.state["available"] and self.state["verifiable"] and self.state["status"] == "idle"
            version = self.state["latest"]
        if not (ready and self.auto and FROZEN):
            return
        postponed = None
        if (left := self._backoff_left(version)) > 0:
            postponed = f"The last try to install {version} failed; trying again in about {max(1, round(left / 3600))} h."
        elif self.is_busy():
            postponed = "Waiting until no commands or file transfers are running."
        with self.lock:
            self.state["postponed"] = postponed
        if not postponed:
            self.install(reason="automatic update")

    def _watch_installing(self) -> None:
        """Still running long after starting the installer: it didn't replace this server."""
        with self.lock:
            since, version = self.state["installing_since"], self.state["latest"]
        if since and time.time() - since > INSTALL_TIMEOUT:
            self._record_failure(version or "?", f"The update to {version} didn't finish within "
                                                 f"{INSTALL_TIMEOUT // 60} minutes. The installer's log is {self._install_log()}.")

    def check(self) -> dict:
        try:
            with _get(f"https://api.github.com/repos/{self.repo}/releases/latest") as r:
                rel = json.load(r)
            latest = str(rel.get("tag_name", "")).lstrip("vV")
            assets = {a.get("name"): a for a in rel.get("assets", []) if isinstance(a, dict)}
            asset, sha, sig = assets.get(ASSET), assets.get(ASSET + ".sha256"), assets.get(ASSET + ".sig")
            self.release = {"version": latest, "asset": asset, "sha": sha, "sig": sig}
            available = bool(asset) and parse_version(latest) > parse_version(__version__)
            verifiable = bool(asset and sha and sig)
            with self.lock:
                self.state.update(latest=latest, notes=(rel.get("body") or "")[:4000], url=rel.get("html_url"),
                                  available=available, verifiable=verifiable, checked=_now(),
                                  error=None if verifiable or not available else
                                  f"SSH Manager {latest} has no signature or checksum, so it isn't installed automatically. "
                                  "Download it from GitHub.")
        except Exception as e:
            with self.lock:
                self.state.update(error=f"Couldn't check for updates: {e}", checked=_now())
        return self.snapshot()

    def snapshot(self) -> dict:
        busy = self.is_busy()
        with self.lock:
            return {**self.state, "auto": self.auto, "busy": busy}

    def install(self, reason: str = "update") -> dict:
        if not FROZEN:
            raise RuntimeError("This copy runs from source; update it with git pull.")
        if not (self.release and self.state["available"]):
            raise RuntimeError("No update available.")
        if not self.state["verifiable"]:
            raise RuntimeError(f"SSH Manager {self.release['version']} has no signature or checksum, so it can't be "
                               "installed from here. Download it from GitHub.")
        with self.lock:
            if self.state["status"] != "idle":
                return {**self.state, "auto": self.auto}
            self.state.update(status="downloading", error=None, postponed=None)
        threading.Thread(target=self._download_and_run, args=(reason,), daemon=True).start()
        return self.snapshot()

    def _download_and_run(self, reason: str):
        version = self.release["version"]
        target = None
        try:
            shutil.rmtree(self.data / "updates", ignore_errors=True)   # where older versions downloaded to
            folder = updates_folder()
            target = folder / f"SSHManager-Setup-{re.sub(r'[^0-9A-Za-z.-]', '', version)[:40] or 'new'}.exe"
            size = 0
            with _get(self.release["asset"]["browser_download_url"], timeout=60) as r, open(target, "wb") as f:
                while chunk := r.read(1 << 20):
                    size += len(chunk)
                    if size > MAX_INSTALLER:
                        raise UpdateError("The download is unexpectedly large; not installing it.")
                    f.write(chunk)
            expected = _read_small(self.release["sha"]["browser_download_url"]).decode("ascii", "replace").split()
            expected = expected[0].lower() if expected else ""
            if not re.fullmatch(r"[0-9a-f]{64}", expected):
                raise UpdateError("The update's checksum file is invalid; not installing it.")
            signature = _read_small(self.release["sig"]["browser_download_url"])
            with self.lock:
                self.state["status"] = "verifying"
            with _Locked(target) as locked:
                # checked on the locked file, which can't change until the installer has started
                if _sha256(target) != expected:
                    raise UpdateError("The downloaded update didn't match its checksum; not installing it.")
                verify_signature(target, signature, folder)
                saved = self._load()
                saved["pending"] = {"version": version, "started": time.time()}
                self._save(saved)
                with self.lock:
                    self.state.update(status="installing", installing_since=time.time())
                self.activity.add("setup", action="update", client="SSH Manager", version=version, note=reason)
                # The installer stops this server, replaces the files and starts the new one.
                args = [str(target), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", f"/LOG={folder / 'install.log'}"]
                try:
                    proc = subprocess.Popen(args, creationflags=DETACHED, close_fds=True)
                except OSError:   # inside a job that doesn't allow breaking away
                    proc = subprocess.Popen(args, creationflags=DETACHED & ~0x01000000, close_fds=True)
                locked.close()
            threading.Thread(target=self._wait_installer, args=(proc, version), daemon=True).start()
        except Exception as e:
            log.exception("update failed")
            try:
                if target:
                    target.unlink(missing_ok=True)
            except OSError:
                pass
            self._record_failure(version, str(e) if isinstance(e, UpdateError) else f"Update failed: {e}")

    def _wait_installer(self, proc, version: str):
        """The installer ends this process when it works; if it exits while we are still here, it failed."""
        try:
            code = proc.wait(timeout=INSTALL_TIMEOUT)
        except subprocess.TimeoutExpired:
            return   # _watch_installing reports it
        time.sleep(30)   # a successful installer may still be stopping this server
        if code != 0:
            self._record_failure(version, f"The installer for {version} stopped with exit code {code}. "
                                          f"Its log is {self._install_log()}.")
        else:
            with self.lock:
                self.state.update(status="idle", installing_since=None)


if __name__ == "__main__":
    # Checks a downloaded installer by hand: python -m sshmgr.updater <installer> <installer.sig>
    if len(sys.argv) != 3:
        sys.exit("usage: python -m sshmgr.updater <SSHManager-Setup.exe> <SSHManager-Setup.exe.sig>")
    exe, sigfile = Path(sys.argv[1]), Path(sys.argv[2])
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        try:
            verify_signature(exe, sigfile.read_bytes(), Path(tmp))
        except UpdateError as e:
            sys.exit(str(e))
    print(f"{exe.name}: good release signature")
