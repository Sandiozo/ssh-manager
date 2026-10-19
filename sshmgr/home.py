"""Where things are.

- install folder: where server.py (or SSHManagerServer.exe when installed) lives
- resources: static/, config.example.json (inside the exe's bundle when installed)
- home: everything that belongs to this installation rather than to the code: config.json, plugins/
  and data/ (activity log, server logs, the access token, key-file backups, downloaded updates).

The home folder is, in order: the SSHMGR_HOME environment variable, the folder named in
sshmgr-home.txt in the install folder, or the default: <install folder>/home when running from
source, %LOCALAPPDATA%/SSHManager/server when installed. Keeping it outside the code lets you put
your own settings and plugins in a separate (private) repository and still update the tool itself.
"""
import os
import secrets
import shutil
import sys
from pathlib import Path

FROZEN = getattr(sys, "frozen", False)


def install_folder() -> Path:
    return Path(sys.executable).resolve().parent if FROZEN else Path(__file__).resolve().parent.parent


def resources() -> Path:
    return Path(getattr(sys, "_MEIPASS", install_folder()))


def resolve(root: Path | None = None) -> Path:
    root = root or install_folder()
    if env := os.environ.get("SSHMGR_HOME"):
        home = Path(os.path.expandvars(env))
    elif (pointer := root / "sshmgr-home.txt").exists():
        home = Path(os.path.expandvars(pointer.read_text(encoding="utf-8-sig").strip()))
    elif FROZEN:
        home = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "SSHManager" / "server"
    else:
        home = root / "home"
    home.mkdir(parents=True, exist_ok=True)
    (home / "data").mkdir(exist_ok=True)
    (home / "plugins").mkdir(exist_ok=True)
    config = home / "config.json"
    if not config.exists():
        shutil.copyfile(resources() / "config.example.json", config)
    return home


def token(home: Path) -> str:
    """Secret that every request must carry (cookie or X-SSHMgr-Token header). Clients read it over
    SSH (`--client-info`), so only someone who can log in over SSH can use the dashboard."""
    path = home / "data" / "token"
    if path.exists() and (value := path.read_text(encoding="utf-8").strip()):
        return value
    value = secrets.token_urlsafe(32)
    path.write_text(value, encoding="utf-8")
    return value
