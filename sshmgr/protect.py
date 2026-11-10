"""Paths AI agents may not use (files, logs and wait endpoints; see agents.py).

- network and device paths: \\\\server\\share, \\\\?\\..., \\\\.\\... (an admin server reaching out to other
  hosts with its credentials), and alternate data streams ("file:stream")
- SSH Manager's own files: the data folder (access token, app secret, activity, logs), config.json and its
  copies, the plugins folder, and the sshmgr-home.txt pointer in the program folder
- SSH keys: %ProgramData%\\ssh\\*key* (host keys, administrators_authorized_keys), every .ssh folder; nothing
  in %ProgramData%\\ssh may be changed

Paths are compared after resolving short (8.3) names, junctions and symbolic links, without case; folder
walks resolve the folder they start from, and file links by their target. A path is also checked as named
(short names expanded, links not followed), so a .ssh folder that is a link to another folder stays
protected when named through .ssh."""
import ctypes
import ctypes.wintypes as wt
import os
from pathlib import Path

_kernel32 = ctypes.WinDLL("kernel32")
_kernel32.GetLongPathNameW.argtypes = [wt.LPCWSTR, wt.LPWSTR, wt.DWORD]
_kernel32.GetLongPathNameW.restype = wt.DWORD

SELF = "AI agents can't read or change SSH Manager's own files (access token, settings, plugins, data folder)."
KEYS = "AI agents can't read or change SSH keys."
SSH_CONFIG = "AI agents can't change SSH's settings or keys on this PC."
NETWORK = "AI agents can't use network paths (\\\\server\\share) or device paths (\\\\?\\, \\\\.\\)."
STREAM = "AI agents can't use alternate data streams (a ':' in a file name)."
CONTAINS = "AI agents can't delete, move or replace a folder that holds SSH Manager's own files or SSH keys."


def _norm(p: str) -> str:
    return os.path.normcase(os.path.normpath(p))


def _real(p: str) -> str:
    try:
        return _norm(os.path.realpath(p))
    except (OSError, ValueError):
        return _norm(os.path.abspath(p))


def _long(p: str) -> str:
    """p with short (8.3) names replaced by long ones, as far as it exists, without following junctions or
    links: the path as named, so a .ssh folder that is a link still shows as .ssh."""
    tail = []
    while True:
        n = _kernel32.GetLongPathNameW(p, None, 0)
        buf = ctypes.create_unicode_buffer(n) if n else None
        if buf is not None and _kernel32.GetLongPathNameW(p, buf, n):
            return os.path.join(buf.value, *reversed(tail))
        head, name = os.path.split(p)
        if not name or head == p:
            return os.path.join(p, *reversed(tail))
        tail.append(name)
        p = head


def _under(path: str, root: str) -> bool:
    return path == root or path.startswith(root.rstrip("\\") + "\\")


def is_network_or_device(raw: str) -> bool:
    s = os.path.expandvars(raw.strip()).replace("/", "\\")
    return s.startswith("\\\\") or s.startswith("\\??\\")


class Protect:
    def __init__(self, home: Path, root: Path):
        self.home = _real(str(home))
        self.data = _real(str(home / "data"))
        self.plugins = _real(str(home / "plugins"))
        self.pointer = _real(str(root / "sshmgr-home.txt"))
        self.ssh = _real(os.path.join(os.environ.get("PROGRAMDATA", r"C:\ProgramData"), "ssh"))
        users = os.path.dirname(os.environ.get("USERPROFILE", r"C:\Users\x"))
        self.profiles = _real(users)

    def _own(self, p: str) -> bool:
        if _under(p, self.data) or _under(p, self.plugins) or p == self.pointer:
            return True
        return os.path.dirname(p) == self.home and os.path.basename(p).startswith("config.json")

    def _hidden(self, p: str) -> str | None:
        """Why p (normalized) can't be read by an agent, or None."""
        if self._own(p):
            return SELF
        if ".ssh" in p.split("\\"):
            return KEYS
        if os.path.dirname(p) == self.ssh and ("key" in os.path.basename(p) or os.path.basename(p) == "administrators_authorized_keys"):
            return KEYS
        if _under(p, self.ssh) and p != self.ssh and "key" in p[len(self.ssh):]:
            return KEYS
        return None

    def why(self, path: str | Path, write: bool = False, whole_folder: bool = False) -> str | None:
        """Why an agent may not use path (None when it may). `write`: it would be created, changed or
        deleted; `whole_folder`: the path and everything in it (delete, rename, replace)."""
        raw = str(path)
        if is_network_or_device(raw):
            return NETWORK
        if ":" in os.path.expandvars(raw)[2:]:
            return STREAM
        full = os.path.abspath(os.path.expandvars(raw))
        p = _real(full)
        if p.startswith("\\\\"):   # a junction or link that leads to the network
            return NETWORK
        # where it leads, and how it is named (a .ssh folder may be a link to a folder named otherwise)
        if reason := self._hidden(p) or self._hidden(_norm(_long(full))):
            return reason
        if write and _under(p, self.ssh):
            return SSH_CONFIG
        if whole_folder:
            for inside in (self.data, self.plugins, self.pointer, self.ssh, os.path.join(self.home, "config.json")):
                if _under(inside, p):
                    return CONTAINS
            if _under(self.profiles, p) or os.path.dirname(p) == self.profiles:   # user profiles hold .ssh folders
                return CONTAINS
        return None

    def walk_filter(self, root: str):
        """For a folder walk from `root` (zip download, tree): a check `hidden(path, link=False)` that says
        which entries to leave out for agents. The root may be named through a junction, an 8.3 short name
        or another alias, so it is resolved once; walks give long names and never enter links, so every
        entry's real path is the root's real path plus the rest of its path. `link`: the entry is a file
        link, so it is checked by where it leads."""
        real_root = _real(root)

        def hidden(path: str, link: bool = False) -> bool:
            if link:
                return self.why(path) is not None
            rest = path[len(root):]
            if path.startswith(root) and (not rest or rest[0] == "\\" or root.endswith("\\")):
                real = os.path.join(real_root, rest.lstrip("\\"))
            else:   # not from this walk: resolve its folder
                real = os.path.join(_real(os.path.dirname(path)), os.path.basename(path))
            return self._hidden(_norm(real)) is not None

        return hidden
