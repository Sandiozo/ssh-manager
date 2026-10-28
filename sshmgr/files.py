"""File browser and transfer API for this PC: list, download (folders as a streamed zip), upload
(raw PUT per file, written to <name>.sshmgr-upload and renamed), mkdir, rename and delete (to the
Recycle Bin when the drive has one).

AI agents can't use network/device paths or SSH Manager's own files and SSH keys (protect.py); folder
walks leave those out for them."""
import asyncio
import ctypes
import ctypes.wintypes as wt
import os
import stat
import threading
import time
import zipfile
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import quote

import psutil
from fastapi import APIRouter, HTTPException, Query, Request
from fastapi.responses import FileResponse, StreamingResponse
from pydantic import BaseModel
from send2trash import send2trash

from . import agents as agents_mod
from .activity import ActivityLog

CHUNK = 1 << 20
UPLOAD_SUFFIX = ".sshmgr-upload"     # an upload in progress; never listed, never synced
SKIPPED_NAME = "_files-not-included.txt"
FILE_ATTRIBUTE_REPARSE_POINT = 0x400

# ---- transfers in progress (the updater waits for them) ----
_transfers = 0
_transfers_lock = threading.Lock()


def transfers_active() -> int:
    """Uploads and downloads running right now."""
    return _transfers


@contextmanager
def _transfer():
    global _transfers
    with _transfers_lock:
        _transfers += 1
    try:
        yield
    finally:
        with _transfers_lock:
            _transfers -= 1


class _CountedFile(FileResponse):
    async def __call__(self, scope, receive, send):
        with _transfer():
            await super().__call__(scope, receive, send)


class _CountedStream(StreamingResponse):
    async def __call__(self, scope, receive, send):
        with _transfer():
            await super().__call__(scope, receive, send)


# ---- paths ----

def resolve(path: str) -> Path:
    if not path:
        raise HTTPException(400, "path is required")
    p = Path(os.path.expandvars(path))
    if not p.is_absolute():
        raise HTTPException(400, "path must be absolute")
    return Path(os.path.abspath(p))


def entry(p: Path) -> dict | None:
    try:
        st = p.stat()
    except OSError:
        return None
    is_dir = stat.S_ISDIR(st.st_mode)
    hidden = bool(getattr(st, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_HIDDEN)
    return {"name": p.name, "path": str(p), "dir": is_dir, "size": None if is_dir else st.st_size,
            "mtime": datetime.fromtimestamp(st.st_mtime, timezone.utc).isoformat(timespec="seconds"),
            "hidden": hidden}


def walk(root: str, skip=None, errors: list | None = None):
    """Like os.walk (top-down), but never enters junctions, symbolic links or other reparse-point folders,
    and never visits the same folder twice, so link loops can't make it run forever. `skip(path)` leaves
    entries out; unreadable folders are added to `errors` as (path, reason)."""
    seen: set[tuple[int, int]] = set()
    stack = [root]
    while stack:
        top = stack.pop()
        try:
            st = os.stat(top)
            key = (st.st_dev, st.st_ino)
            if st.st_ino and key in seen:
                continue
            seen.add(key)
            with os.scandir(top) as it:
                entries = list(it)
        except OSError as e:
            if errors is not None:
                errors.append((top, e.strerror or str(e)))
            continue
        dirs, files = [], []
        for e in entries:
            if skip and skip(e.path):
                continue
            try:
                is_dir = e.is_dir(follow_symlinks=False)
                reparse = bool(e.stat(follow_symlinks=False).st_file_attributes & FILE_ATTRIBUTE_REPARSE_POINT)
            except OSError:
                continue
            if is_dir:
                if not reparse and not e.is_symlink():
                    dirs.append(e.name)
            else:
                files.append(e.name)
        yield top, dirs, files
        stack.extend(os.path.join(top, d) for d in reversed(dirs))


class _Sink:
    """Write-only stream for ZipFile; the generator drains it after every chunk."""

    def __init__(self):
        self.buf = bytearray()
        self.pos = 0

    def write(self, b) -> int:
        self.buf += b
        self.pos += len(b)
        return len(b)

    def tell(self) -> int:
        return self.pos

    def flush(self):
        pass

    def take(self) -> bytes:
        out = bytes(self.buf)
        self.buf.clear()
        return out


def _items(paths: list[Path], skip=None, errors: list | None = None):
    """(archive name, full path, is folder) for every file and folder under the given paths, found while
    streaming (nothing is walked in advance)."""
    for root in paths:
        base = root.parent
        if not root.is_dir():
            yield root.name, root, False
            continue
        for dirpath, _, filenames in walk(str(root), skip, errors):
            d = Path(dirpath)
            yield d.relative_to(base).as_posix() + "/", d, True
            for f in filenames:
                yield (d / f).relative_to(base).as_posix(), d / f, False


def zip_stream(paths: list[Path], skip=None, done=None):
    """Streams a zip of `paths`. Files that can't be read (locked, no access) are left out and listed in
    _files-not-included.txt inside the zip. `done(stats)` is called at the end (or when the download stops)."""
    sink = _Sink()
    stats = {"files": 0, "bytes": 0, "skipped": [], "complete": False}
    errors: list = []
    try:
        with zipfile.ZipFile(sink, "w", zipfile.ZIP_STORED, allowZip64=True) as zf:
            for arcname, full, is_dir in _items(paths, skip, errors):
                try:
                    zi = zipfile.ZipInfo.from_file(full, arcname)
                    if is_dir:
                        zf.writestr(zi, b"")
                    else:
                        with open(full, "rb") as src:
                            with zf.open(zi, "w", force_zip64=True) as dst:
                                while chunk := src.read(CHUNK):
                                    dst.write(chunk)
                                    stats["bytes"] += len(chunk)
                                    yield sink.take()
                        stats["files"] += 1
                except OSError as e:
                    stats["skipped"].append(f"{full}: {e.strerror or e}")
                yield sink.take()
            stats["skipped"] += [f"{p}: {why}" for p, why in errors]
            if stats["skipped"]:
                note = ("These files and folders could not be read (in use or no access) and are not in this zip:\r\n\r\n"
                        + "\r\n".join(stats["skipped"]) + "\r\n")
                zf.writestr(SKIPPED_NAME, note.encode("utf-8"))
        yield sink.take()
        stats["complete"] = True
    finally:
        if done:
            try:
                done(stats)
            except Exception:
                pass


# ---- who is calling ----
_label_cache: dict[int, tuple[float, str, int | None]] = {}   # client port -> (time, label, sshd pid)
_cache_lock = threading.Lock()
LABEL_TTL = 60


def _peer_process(request: Request, port: int) -> tuple[bool, int | None, str]:
    """(found, pid, process name) at the other end of this request's TCP connection to the server."""
    cport = request.client.port if request.client else None
    for c in psutil.net_connections("tcp"):
        if c.laddr and c.laddr.port == cport and c.raddr and c.raddr.port == port:
            try:
                name = psutil.Process(c.pid).name().lower() if c.pid else ""
            except psutil.Error:
                name = ""
            return True, c.pid, name
    return False, None, ""


def _lookup(request: Request, port: int, monitor) -> tuple[str, int | None]:
    cport = request.client.port if request.client else 0
    now = time.monotonic()
    with _cache_lock:
        hit = _label_cache.get(cport)
        if hit and now - hit[0] < LABEL_TTL:
            return hit[1], hit[2]
    try:
        found, pid, name = _peer_process(request, port)
    except Exception:
        return "unknown", None
    if not found:
        return "unknown", None
    if name.startswith("sshd"):
        label = monitor.label_for_pid(pid)
        if not label:
            return "SSH tunnel", pid   # the session's login isn't read from the event log yet: don't cache
    else:
        label, pid = monitor.host_name, None
    with _cache_lock:
        if len(_label_cache) > 2000:
            _label_cache.clear()
        _label_cache[cport] = (now, label, pid)
    return label, pid


def tunnel_pid(request: Request, port: int, monitor=None) -> int | None:
    """The sshd process at the other end of this request's connection, when it came through an SSH tunnel."""
    if monitor is not None:
        return _lookup(request, port, monitor)[1]
    _, pid, name = _peer_process(request, port)
    return pid if name.startswith("sshd") else None


def client_label(request: Request, port: int, monitor) -> str:
    """Who is calling: a browser on this PC, or another computer through an SSH tunnel (the client
    socket to this server is then owned by that computer's sshd session process). Cached per client
    port for a minute (keep-alive connections make many requests)."""
    return _lookup(request, port, monitor)[0]


# ---- Recycle Bin ----

class _RBInfo(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("i64Size", ctypes.c_longlong), ("i64NumItems", ctypes.c_longlong)]


_shell32 = ctypes.WinDLL("shell32")
_shell32.SHQueryRecycleBinW.argtypes = [wt.LPCWSTR, ctypes.POINTER(_RBInfo)]
_shell32.SHQueryRecycleBinW.restype = ctypes.c_long
_kernel32 = ctypes.WinDLL("kernel32")
_kernel32.GetDriveTypeW.argtypes = [wt.LPCWSTR]
_kernel32.GetDriveTypeW.restype = wt.UINT
DRIVE_FIXED = 3
_delete_lock = threading.Lock()


def _recycle_bin(path: Path) -> tuple[int, int] | None:
    """(size, items) of the Recycle Bin of path's drive, or None when the drive has none (network,
    removable)."""
    root = path.anchor
    if not root or _kernel32.GetDriveTypeW(root) != DRIVE_FIXED:
        return None
    info = _RBInfo(ctypes.sizeof(_RBInfo), 0, 0)
    if _shell32.SHQueryRecycleBinW(root, ctypes.byref(info)) != 0:
        return None
    return info.i64Size, info.i64NumItems


def delete(p: Path) -> bool:
    """Moves p to the Recycle Bin. Returns False when Windows deleted it permanently instead (no Recycle
    Bin on that drive, or too big for it): send2trash can't tell, so the Recycle Bin is checked."""
    with _delete_lock:
        before = _recycle_bin(p)
        send2trash(str(p))
        after = _recycle_bin(p) if before else None
    return bool(before and after and (after[1] > before[1] or after[0] > before[0]))


# ---- API ----

class PathBody(BaseModel):
    path: str


class RenameBody(BaseModel):
    path: str
    new_name: str


class DeleteBody(BaseModel):
    paths: list[str]


def make_router(config: dict, activity: ActivityLog, who, protect) -> APIRouter:
    r = APIRouter(prefix="/api/fs")

    def record(request: Request, action: str, **fields):
        activity.add("file", action=action, client=who(request), **fields)

    def guard(request: Request, p: Path | str, write: bool = False, whole_folder: bool = False):
        """AI agents: no network/device paths, nor SSH Manager's own files or SSH keys."""
        if agents_mod.is_agent(request) and (why := protect.why(p, write, whole_folder)):
            raise HTTPException(403, why)

    def walk_skip(request: Request):
        """What folder walks leave out: in-progress uploads, and for agents the protected paths."""
        agent = agents_mod.is_agent(request)
        return lambda path: path.endswith(UPLOAD_SUFFIX) or (agent and protect.hidden_in_walk(path))

    @r.get("/roots")
    def roots():
        drives = []
        for part in psutil.disk_partitions():
            try:
                u = psutil.disk_usage(part.mountpoint)
                drives.append({"name": part.mountpoint, "path": part.mountpoint, "free": u.free, "total": u.total})
            except OSError:
                pass
        bookmarks = []
        for b in config.get("bookmarks", []):
            p = os.path.expandvars(b["path"])
            bookmarks.append({"name": b["name"], "path": p, "exists": os.path.isdir(p)})
        return {"drives": drives, "bookmarks": bookmarks}

    @r.get("/list")
    def list_dir(request: Request, path: str):
        p = resolve(path)
        guard(request, p)
        if not p.is_dir():
            raise HTTPException(404, f"Not a folder: {p}")
        try:
            items = [e for c in p.iterdir() if not c.name.endswith(UPLOAD_SUFFIX) and (e := entry(c))]
        except PermissionError:
            raise HTTPException(403, f"Access denied: {p}")
        except OSError as e:
            raise HTTPException(409, f"Can't list {p}: {e.strerror or e}")
        items.sort(key=lambda e: (not e["dir"], e["name"].lower()))
        parent = str(p.parent) if p.parent != p else None
        return {"path": str(p), "parent": parent, "items": items}

    @r.get("/tree")
    def tree(request: Request, path: str, max_files: int = 100_000):
        """Every file under a folder with size and modification time (for folder sync). Junctions and links
        are not followed; uploads in progress are left out."""
        root = resolve(path)
        guard(request, root)
        if not root.is_dir():
            raise HTTPException(404, f"Not a folder: {root}")
        files = []
        for dirpath, _, filenames in walk(str(root), walk_skip(request)):
            for name in filenames:
                full = os.path.join(dirpath, name)
                try:
                    st = os.stat(full)
                except OSError:
                    continue
                files.append({"rel": Path(full).relative_to(root).as_posix(), "size": st.st_size, "mtime": st.st_mtime})
                if len(files) > max_files:
                    raise HTTPException(413, f"More than {max_files} files under {root}.")
        return {"root": str(root), "files": files}

    @r.get("/download")
    def download(request: Request, path: list[str] = Query(...)):
        paths = [resolve(x) for x in path]
        for p in paths:
            guard(request, p)
        for p in paths:
            if not p.exists():
                raise HTTPException(404, f"Not found: {p}")
        if len(paths) == 1 and not paths[0].is_dir():
            p = paths[0]
            record(request, "download", path=str(p), size=p.stat().st_size)
            return _CountedFile(p, filename=p.name)
        name = (paths[0].name or "drive") if len(paths) == 1 else f"{paths[0].parent.name or 'files'}-{len(paths)}-items"
        client = who(request)
        agent = agents_mod.agent_of(request)
        where = str(paths[0]) if len(paths) == 1 else str(paths[0].parent)

        def done(stats: dict):
            activity.add("file", action="download-zip", client=client, path=where, items=[p.name for p in paths],
                         size=stats["bytes"], files=stats["files"], skipped=len(stats["skipped"]),
                         **({} if stats["complete"] else {"note": "stopped before the end"}),
                         **({"agent": agent} if agent else {}))

        headers = {"Content-Disposition": f"attachment; filename*=UTF-8''{quote(name)}.zip"}
        return _CountedStream(zip_stream(paths, walk_skip(request), done), media_type="application/zip", headers=headers)

    @r.put("/upload")
    async def upload(request: Request, path: str, overwrite: bool = False, mtime: float | None = None):
        dest = resolve(path)
        guard(request, dest, write=True, whole_folder=True)
        if dest.name.endswith(UPLOAD_SUFFIX):
            raise HTTPException(400, f"File names ending in {UPLOAD_SUFFIX} are reserved for uploads in progress.")

        def check():
            if dest.is_dir():
                raise HTTPException(409, f"{dest} is a folder. Give the full path of the file to create, including "
                                         "its name (e.g. folder\\file.txt).")
            if dest.exists() and not overwrite:
                raise HTTPException(409, f"Already exists: {dest}")
            dest.parent.mkdir(parents=True, exist_ok=True)

        await asyncio.to_thread(check)
        part = dest.with_name(dest.name + UPLOAD_SUFFIX)
        size = 0
        with _transfer():
            f = await asyncio.to_thread(open, part, "wb")
            try:
                buf = bytearray()
                async for chunk in request.stream():
                    buf += chunk
                    size += len(chunk)
                    if len(buf) >= CHUNK:
                        data, buf = bytes(buf), bytearray()
                        await asyncio.to_thread(f.write, data)
                if buf:
                    await asyncio.to_thread(f.write, bytes(buf))
                await asyncio.to_thread(f.close)
                await asyncio.to_thread(os.replace, part, dest)
            except BaseException:
                try:
                    f.close()
                    part.unlink(missing_ok=True)
                except OSError:
                    pass
                raise
        if mtime:
            try:
                os.utime(dest, (mtime, mtime))
            except (OSError, ValueError, OverflowError):
                pass
        client = await asyncio.to_thread(who, request)
        activity.add("file", action="upload", client=client, path=str(dest), size=size, overwrote=overwrite)
        return entry(dest)

    @r.post("/mkdir")
    def mkdir(request: Request, body: PathBody):
        p = resolve(body.path)
        guard(request, p, write=True)
        if p.exists():
            raise HTTPException(409, f"Already exists: {p}")
        p.mkdir(parents=True)
        record(request, "mkdir", path=str(p))
        return entry(p)

    @r.post("/rename")
    def rename(request: Request, body: RenameBody):
        p = resolve(body.path)
        if not body.new_name or any(ch in body.new_name for ch in '\\/:*?"<>|'):
            raise HTTPException(400, "Invalid name")
        target = p.with_name(body.new_name)
        guard(request, p, write=True, whole_folder=True)
        guard(request, target, write=True, whole_folder=True)
        if not p.exists():
            raise HTTPException(404, f"Not found: {p}")
        if target.exists():
            raise HTTPException(409, f"Already exists: {target}")
        p.rename(target)
        record(request, "rename", path=str(p), to=str(target))
        return entry(target)

    @r.post("/delete")
    def delete_items(request: Request, body: DeleteBody):
        paths = [resolve(x) for x in body.paths]
        for p in paths:
            guard(request, p, write=True, whole_folder=True)
            if p.parent == p:
                raise HTTPException(400, "Refusing to delete a drive root")
        done, permanent, failed = [], [], []
        for p in paths:
            if not p.exists():
                failed.append(f"{p}: not found")
                continue
            try:
                recycled = delete(p)
            except OSError as e:
                failed.append(f"{p}: {e.strerror or e}")
                continue
            done.append(str(p))
            if not recycled:
                permanent.append(str(p))
            record(request, "delete", path=str(p), permanent=not recycled,
                   note="moved to Recycle Bin" if recycled else "deleted permanently (no Recycle Bin for it)")
        if failed:
            ok = f"Deleted {len(done)} of {len(paths)}. " if done else ""
            missing = all(f.endswith(": not found") for f in failed)
            raise HTTPException(409 if done else 404 if missing else 400, f"{ok}Could not delete: " + "; ".join(failed))
        return {"deleted": done, "permanent": permanent}

    return r
