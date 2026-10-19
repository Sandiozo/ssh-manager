"""The "home" folder holds everything that belongs to one installation rather than to the code:
config.json, plugins/ and data/ (activity log, server logs, the access token, key-file backups).

It is, in order: the SSHMGR_HOME environment variable, the folder named in sshmgr-home.txt next to
server.py, or <install folder>/home. Keeping it outside the code folder lets you put your own
settings and plugins in a separate (private) repository and still `git pull` the tool itself.
"""
import os
import secrets
import shutil
from pathlib import Path


def resolve(root: Path) -> Path:
    if env := os.environ.get("SSHMGR_HOME"):
        home = Path(os.path.expandvars(env))
    elif (pointer := root / "sshmgr-home.txt").exists():
        home = Path(os.path.expandvars(pointer.read_text(encoding="utf-8-sig").strip()))
    else:
        home = root / "home"
    home.mkdir(parents=True, exist_ok=True)
    (home / "data").mkdir(exist_ok=True)
    config = home / "config.json"
    if not config.exists():
        shutil.copyfile(root / "config.example.json", config)
    return home


def token(home: Path) -> str:
    """Secret that every request must carry (cookie or X-SSHMgr-Token header). Clients read it over
    SSH (`server.py --print-token`), so only someone who can log in over SSH can use the dashboard."""
    path = home / "data" / "token"
    if path.exists() and (value := path.read_text(encoding="utf-8").strip()):
        return value
    value = secrets.token_urlsafe(32)
    path.write_text(value, encoding="utf-8")
    return value
