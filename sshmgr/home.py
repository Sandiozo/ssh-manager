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


def _secret(path: Path) -> str:
    if path.exists() and (value := path.read_text(encoding="utf-8-sig").strip()):
        return value
    value = secrets.token_urlsafe(32)
    tmp = path.with_name(f"{path.name}.{secrets.token_hex(4)}.tmp")
    tmp.write_text(value, encoding="utf-8")
    os.replace(tmp, path)
    return value


def token(home: Path) -> str:
    """Secret that every request must carry (cookie or X-SSHMgr-Token header). Clients read it over
    SSH (`--client-info`), so only someone who can log in over SSH can use the dashboard."""
    return _secret(home / "data" / "token")


def app_secret(home: Path) -> str:
    """Second secret, only for the SSH Manager app itself (X-SSHMgr-App header): answering approvals and
    pairing requests needs it, so a program that only has the token (an AI agent's tools, a script) can't
    approve its own requests. The app reads it with `--client-info` like the token."""
    return _secret(home / "data" / "app-secret")


# ---- permissions of the home folder ----

security = {"checked": False, "private": None, "warnings": []}   # shown in /api/status


def secure(home: Path, root: Path, log) -> dict:
    """The home folder holds the token (admin access to this PC through the server), config.json and the
    plugins (code the elevated server runs). Running elevated, make it readable and writable only by this
    user, Administrators and SYSTEM (by SID, so it works in every Windows language); the same user keeps
    full access, so the app can still read the token without admin rights. Also warns when the program
    folder can be changed by other accounts."""
    from . import winsec
    warnings = []
    security.update(checked=True, warnings=warnings)
    try:
        user = winsec.current_user_sid()
    except OSError as e:
        log.warning("security: can't tell which account this server runs as: %s", e)
        return security
    trusted = winsec.trusted_sids(user)
    bits = winsec.WRITE_BITS | winsec.READ_BITS
    names = lambda sids: ", ".join(winsec.account_name(s) for s in sids)   # noqa: E731

    def exposed() -> dict[str, list[str]]:
        out = {}
        for p in _sensitive(home):
            if sids := winsec.others_with(str(p), trusted, bits):
                out[str(p)] = sids
        return out

    def summary(problems: dict[str, list[str]], verb: str) -> str:
        first = next(iter(problems))
        more = f" (and {len(problems) - 1} more item(s) in it)" if len(problems) > 1 else ""
        return f"{first}{more} {verb} {names(sorted({s for v in problems.values() for s in v}))}"

    found = exposed()
    if not found:
        security["private"] = True
        log.info("security: home folder %s is private (this user, Administrators, SYSTEM)", home)
    elif not winsec.is_elevated():
        security["private"] = False
        warnings.append(summary(found, "can be read or changed by"))
        log.warning("security: %s; it is made private when the server runs with admin rights", warnings[-1])
    elif user == winsec.SYSTEM:
        security["private"] = False
        warnings.append(f"running as SYSTEM: {home} was left as it is")
        log.warning("security: running as SYSTEM, so the home folder's permissions were not changed")
    else:
        try:
            sd = winsec.parse(winsec.get_sddl(str(home)))
            if sd["owner"] not in trusted:
                winsec.set_owner_admins(str(home))
            winsec.make_private(str(home), user)
            for p in _sensitive(home)[1:]:
                if winsec.others_with(str(p), trusted, bits):
                    if winsec.parse(winsec.get_sddl(str(p)))["owner"] not in trusted:
                        winsec.set_owner_admins(str(p))
                    winsec.inherit_only(str(p))
            left = exposed()
            security["private"] = not left
            log.info("security: made the home folder %s private (was open to: %s)", home,
                     names(sorted({s for v in found.values() for s in v})))
            if left:
                warnings.append(summary(left, "can still be read or changed by"))
                log.warning("security: %s", warnings[-1])
        except OSError as e:
            security["private"] = False
            warnings.append(f"could not make {home} private: {e}")
            log.warning("security: %s", warnings[-1])
    if sids := winsec.others_with(str(root), trusted, winsec.WRITE_BITS):
        warnings.append(f"the program folder {root} can be changed by {names(sids)}")
        log.warning("security: %s; anyone with those rights could change what this server runs", warnings[-1])
    return security


def _sensitive(home: Path) -> list[Path]:
    """The home folder first, then what in it must not be readable or writable by others."""
    out = [home]
    for name in ("data", "plugins", "config.json", "config.json.bak"):
        p = home / name
        if p.exists():
            out.append(p)
        if p.is_dir():
            for dirpath, dirnames, filenames in os.walk(p):
                dirnames[:] = [d for d in dirnames if not os.path.islink(os.path.join(dirpath, d))
                               and not _is_reparse(os.path.join(dirpath, d))]
                out += [Path(dirpath, n) for n in dirnames + filenames]
    return out


def _is_reparse(path: str) -> bool:
    try:
        return bool(os.lstat(path).st_file_attributes & 0x400)
    except OSError:
        return True
