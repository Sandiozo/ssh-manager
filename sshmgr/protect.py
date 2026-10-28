"""Paths AI agents may not use (files, logs and wait endpoints; see agents.py).

- network and device paths: \\\\server\\share, \\\\?\\..., \\\\.\\... (an admin server reaching out to other
  hosts with its credentials), and alternate data streams ("file:stream")
- SSH Manager's own files: the data folder (access token, app secret, activity, logs), config.json and its
  copies, the plugins folder, and the sshmgr-home.txt pointer in the program folder
- SSH keys: %ProgramData%\\ssh\\*key* (host keys, administrators_authorized_keys), every .ssh folder; nothing
  in %ProgramData%\\ssh may be changed

Paths are compared after resolving short (8.3) names, junctions and symbolic links, without case."""
import os
from pathlib import Path

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
        p = _real(os.path.abspath(os.path.expandvars(raw)))
        if p.startswith("\\\\"):   # a junction or link that leads to the network
            return NETWORK
        if reason := self._hidden(p):
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

    def hidden_in_walk(self, path: str) -> bool:
        """For folder walks (zip downloads, trees): leave this entry out for agents. Cheap: walks give full
        long names and don't follow junctions."""
        return self._hidden(_norm(path)) is not None
