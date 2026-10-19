"""File browser and transfer API for this PC: list, download (folders as a streamed zip), upload
(raw PUT per file, written to <name>.part and renamed), mkdir, rename and delete (to the Recycle Bin)."""
import os
import stat
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import quote

import psutil
from fastapi import APIRouter, HTTPException, Query, Request
from fastapi.responses import FileResponse, StreamingResponse
from pydantic import BaseModel
from send2trash import send2trash

from .activity import ActivityLog

CHUNK = 1 << 20


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


def _walk(paths: list[Path]):
    """Yield (archive name, full path) for every file and directory under the given paths."""
    for root in paths:
        base = root.parent
        if root.is_file():
            yield root.name, root
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            d = Path(dirpath)
            yield d.relative_to(base).as_posix() + "/", d
            for f in filenames:
                yield (d / f).relative_to(base).as_posix(), d / f


def zip_stream(paths: list[Path]):
    sink = _Sink()
    with zipfile.ZipFile(sink, "w", zipfile.ZIP_STORED, allowZip64=True) as zf:
        for arcname, full in _walk(paths):
            try:
                zi = zipfile.ZipInfo.from_file(full, arcname)
                if full.is_dir():
                    zf.writestr(zi, b"")
                else:
                    with open(full, "rb") as src, zf.open(zi, "w", force_zip64=True) as dst:
                        while chunk := src.read(CHUNK):
                            dst.write(chunk)
                            yield sink.take()
            except OSError:
                continue  # locked/unreadable file: skip it rather than abort the whole archive
            yield sink.take()
    yield sink.take()


def tree_size(paths: list[Path]) -> int:
    total = 0
    for _, full in _walk(paths):
        try:
            if full.is_file():
                total += full.stat().st_size
        except OSError:
            pass
    return total


def client_label(request: Request, port: int, monitor) -> str:
    """Who is calling: a browser on this PC, or another computer through an SSH tunnel (the client
    socket to this server is then owned by that computer's sshd session process)."""
    cport = request.client.port if request.client else None
    for c in psutil.net_connections("tcp"):
        if c.laddr and c.laddr.port == cport and c.raddr and c.raddr.port == port:
            try:
                name = psutil.Process(c.pid).name().lower() if c.pid else ""
            except psutil.Error:
                name = ""
            if name.startswith("sshd"):
                return monitor.label_for_pid(c.pid) or "SSH tunnel"
            return monitor.host_name
    return "unknown"


class PathBody(BaseModel):
    path: str


class RenameBody(BaseModel):
    path: str
    new_name: str


class DeleteBody(BaseModel):
    paths: list[str]


def make_router(config: dict, activity: ActivityLog, monitor) -> APIRouter:
    r = APIRouter(prefix="/api/fs")
    port = config.get("port", 8765)

    def record(request: Request, action: str, **fields):
        activity.add("file", action=action, client=client_label(request, port, monitor), **fields)

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
    def list_dir(path: str):
        p = resolve(path)
        if not p.is_dir():
            raise HTTPException(404, f"Not a folder: {p}")
        try:
            items = [e for c in p.iterdir() if (e := entry(c))]
        except PermissionError:
            raise HTTPException(403, f"Access denied: {p}")
        items.sort(key=lambda e: (not e["dir"], e["name"].lower()))
        parent = str(p.parent) if p.parent != p else None
        return {"path": str(p), "parent": parent, "items": items}

    @r.get("/download")
    def download(request: Request, path: list[str] = Query(...)):
        paths = [resolve(x) for x in path]
        for p in paths:
            if not p.exists():
                raise HTTPException(404, f"Not found: {p}")
        if len(paths) == 1 and paths[0].is_file():
            p = paths[0]
            record(request, "download", path=str(p), size=p.stat().st_size)
            return FileResponse(p, filename=p.name)
        name = (paths[0].name or "drive") if len(paths) == 1 else f"{paths[0].parent.name or 'files'}-{len(paths)}-items"
        record(request, "download-zip", path=str(paths[0]) if len(paths) == 1 else str(paths[0].parent),
               items=[p.name for p in paths], size=tree_size(paths))
        headers = {"Content-Disposition": f"attachment; filename*=UTF-8''{quote(name)}.zip"}
        return StreamingResponse(zip_stream(paths), media_type="application/zip", headers=headers)

    @r.put("/upload")
    async def upload(request: Request, path: str, overwrite: bool = False, mtime: float | None = None):
        dest = resolve(path)
        if dest.exists() and not overwrite:
            raise HTTPException(409, f"Already exists: {dest}")
        if dest.is_dir():
            raise HTTPException(409, f"A folder with that name exists: {dest}")
        dest.parent.mkdir(parents=True, exist_ok=True)
        part = dest.with_name(dest.name + ".part")
        size = 0
        try:
            with open(part, "wb") as f:
                async for chunk in request.stream():
                    f.write(chunk)
                    size += len(chunk)
            os.replace(part, dest)
        except BaseException:
            part.unlink(missing_ok=True)
            raise
        if mtime:
            os.utime(dest, (mtime, mtime))
        record(request, "upload", path=str(dest), size=size, overwrote=overwrite)
        return entry(dest)

    @r.post("/mkdir")
    def mkdir(request: Request, body: PathBody):
        p = resolve(body.path)
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
        if target.exists():
            raise HTTPException(409, f"Already exists: {target}")
        p.rename(target)
        record(request, "rename", path=str(p), to=str(target))
        return entry(target)

    @r.post("/delete")
    def delete(request: Request, body: DeleteBody):
        done = []
        for x in body.paths:
            p = resolve(x)
            if p.parent == p:
                raise HTTPException(400, "Refusing to delete a drive root")
            send2trash(str(p))
            done.append(str(p))
        record(request, "delete", paths=done, note="moved to Recycle Bin")
        return {"deleted": done}

    return r
