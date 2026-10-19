"""Log viewer: follows the end of a text file. A configured log can name a file or a pattern (e.g.
logs\\app-*.log); a pattern follows the newest matching file, so daily rotation is handled."""
import glob
import os
from pathlib import Path

MAX_CHUNK = 256 * 1024


def resolve(pattern: str) -> Path | None:
    path = os.path.expandvars(pattern)
    if any(ch in path for ch in "*?["):
        matches = [Path(p) for p in glob.glob(path) if os.path.isfile(p)]
        return max(matches, key=lambda p: p.stat().st_mtime) if matches else None
    p = Path(path)
    if p.is_dir():   # a folder: its newest file
        files = [f for f in p.iterdir() if f.is_file()]
        return max(files, key=lambda f: f.stat().st_mtime) if files else None
    return p if p.is_file() else None


def _decode(data: bytes, utf16: bool) -> str:
    if utf16:
        return data.decode("utf-16-le", errors="replace").lstrip("﻿")
    return data.decode("utf-8", errors="replace").lstrip("﻿")


def read(pattern: str, offset: int = -1, file: str = "") -> dict:
    """offset -1: the last part of the file. Otherwise the text after offset. If the file changed (rotated
    to a new file, or truncated), starts over from the end of the new one and says so."""
    path = resolve(pattern)
    if path is None:
        return {"file": None, "text": "", "offset": 0, "reset": True, "error": "No file matches this log yet."}
    size = path.stat().st_size
    reset = offset < 0 or str(path) != file or offset > size
    with open(path, "rb") as f:
        head = f.read(2)
        utf16 = head in (b"\xff\xfe",)
        start = max(0, size - MAX_CHUNK) if reset else offset
        if utf16 and start % 2:
            start += 1
        f.seek(start)
        data = f.read(min(MAX_CHUNK, size - start))
    text = _decode(data, utf16)
    if reset and start > 0:
        text = text.split("\n", 1)[-1]   # drop the partial first line
    return {"file": str(path), "text": text, "offset": start + len(data), "reset": reset, "size": size,
            "mtime": path.stat().st_mtime, "error": None}
