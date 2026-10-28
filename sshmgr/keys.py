"""SSH keys allowed to log in to this PC. For accounts in the Administrators group sshd
only reads C:\\ProgramData\\ssh\\administrators_authorized_keys, which only admins can read or write,
so adding/removing keys needs the server to run elevated."""
import base64
import ctypes
import hashlib
import os
import secrets
import shutil
import threading
from datetime import datetime
from pathlib import Path

from . import cfgfile, winsec

ADMIN_KEYS = Path(os.environ.get("PROGRAMDATA", r"C:\ProgramData")) / "ssh" / "administrators_authorized_keys"
USER_KEYS = Path.home() / ".ssh" / "authorized_keys"
KEY_TYPES = ("ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521",
             "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com")
_lock = threading.Lock()


class KeyError_(ValueError):
    pass


def parse_line(line: str) -> dict | None:
    """Parse one authorized_keys line ([options] type base64 [comment]); None for blanks/comments."""
    s = line.strip()
    if not s or s.startswith("#"):
        return None
    parts = s.split()
    for i, tok in enumerate(parts):
        if tok in KEY_TYPES and i + 1 < len(parts):
            try:
                blob = base64.b64decode(parts[i + 1], validate=True)
            except ValueError:
                return None
            fp = "SHA256:" + base64.b64encode(hashlib.sha256(blob).digest()).decode().rstrip("=")
            return {"type": tok, "fingerprint": fp, "comment": " ".join(parts[i + 2:]),
                    "options": " ".join(parts[:i]), "blob": blob}
    return None


def validate_public_key(text: str) -> dict:
    key = parse_line(" ".join(text.split()))
    if not key or key["options"]:
        raise KeyError_("That doesn't look like an SSH public key. Paste the whole line from the .pub file, "
                        "e.g. \"ssh-ed25519 AAAA… user@laptop\".")
    # the blob starts with the key type as a length-prefixed string
    n = int.from_bytes(key["blob"][:4], "big")
    if key["blob"][4:4 + n].decode(errors="replace") != key["type"]:
        raise KeyError_("The key data doesn't match its type; it may have been cut off while copying.")
    return key


def is_admin() -> bool:
    return bool(ctypes.windll.shell32.IsUserAnAdmin())


def list_keys() -> dict:
    files = []
    keys = []
    for path, scope in ((ADMIN_KEYS, "admin"), (USER_KEYS, "user")):
        info = {"path": str(path), "scope": scope, "exists": path.exists(), "readable": True, "error": None}
        if path.exists():
            try:
                for line in read_text(path).splitlines():
                    if k := parse_line(line):
                        keys.append({"type": k["type"], "fingerprint": k["fingerprint"], "comment": k["comment"],
                                     "file": scope})
            except PermissionError:
                info.update(readable=False, error="Only readable when SSH Manager runs with admin rights.")
        files.append(info)
    return {"files": files, "keys": keys, "can_edit": is_admin()}


def _backup(path: Path, data_dir: Path):
    data_dir.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(path, data_dir / f"{path.name}.{datetime.now():%Y%m%d-%H%M%S}.bak")


def add_key(public_key: str, data_dir: Path) -> dict:
    key = validate_public_key(public_key)
    if not is_admin():
        raise KeyError_("Adding keys needs SSH Manager to run with admin rights (connect with the SSH Manager app, "
                        "or run Setup.cmd on this PC).")
    with _lock:
        text = read_text(ADMIN_KEYS) if ADMIN_KEYS.exists() else ""
        if any((k := parse_line(l)) and k["fingerprint"] == key["fingerprint"] for l in text.splitlines()):
            raise KeyError_("This key is already allowed.")
        if ADMIN_KEYS.exists():
            _backup(ADMIN_KEYS, data_dir)
        line = f"{key['type']} {base64.b64encode(key['blob']).decode()}" + (f" {key['comment']}" if key["comment"] else "")
        if text and not text.endswith("\n"):
            text += "\n"
        write_admin_keys(text + line + "\n")
    return {"fingerprint": key["fingerprint"], "type": key["type"], "comment": key["comment"]}


def remove_key(fingerprint: str, data_dir: Path) -> None:
    if not is_admin():
        raise KeyError_("Removing keys needs SSH Manager to run with admin rights.")
    with _lock:
        lines = read_text(ADMIN_KEYS).splitlines()
        keep = [l for l in lines if not ((k := parse_line(l)) and k["fingerprint"] == fingerprint)]
        if len(keep) == len(lines):
            raise KeyError_("Key not found.")
        _backup(ADMIN_KEYS, data_dir)
        write_admin_keys("\n".join(keep) + ("\n" if keep else ""))


def read_text(path: Path) -> str:
    """A keys file as text, also when an editor saved it with a BOM or as UTF-16 (PowerShell 5's default)."""
    data = path.read_bytes()
    try:
        return cfgfile.decode(data).lstrip("﻿")
    except UnicodeDecodeError:
        return data.decode("latin-1")


ADMIN_ONLY = "D:P(A;;FA;;;SY)(A;;FA;;;BA)"


def write_admin_keys(text: str, target: Path | None = None) -> None:
    """Writes the keys file without ever leaving it half-written: a temp file next to it (with the same
    admin-only permissions sshd insists on) replaces it in one step, so a crash can't lock everyone out.
    Always UTF-8 without BOM, which is what sshd reads."""
    target = target or ADMIN_KEYS
    tmp = target.with_name(f"{target.name}.{secrets.token_hex(4)}.sshmgr-tmp")
    try:
        open(tmp, "x").close()
        if target.exists():
            winsec.copy_dacl(str(target), str(tmp))
        else:
            winsec.set_sddl(str(tmp), ADMIN_ONLY, winsec.DACL_SECURITY_INFORMATION | winsec.PROTECTED_DACL_SECURITY_INFORMATION)
        with open(tmp, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
            f.flush()
            os.fsync(f.fileno())
        if target.exists():
            winsec.replace_file(str(target), str(tmp))   # keeps the file's permissions
        else:
            os.replace(tmp, target)
    finally:
        try:
            tmp.unlink(missing_ok=True)
        except OSError:
            pass
