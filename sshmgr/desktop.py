"""Things that need the signed-in user's desktop session: screen capture and the clipboard. The server
normally runs there (logon task); started over SSH it runs in session 0, where neither works."""
import ctypes
import ctypes.wintypes as wt
import io
import threading
import time

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002
MAX_TEXT = 1024 * 1024

kernel32.GlobalAlloc.restype = wt.HGLOBAL
kernel32.GlobalLock.restype = ctypes.c_void_p
kernel32.GlobalLock.argtypes = [wt.HGLOBAL]
kernel32.GlobalUnlock.argtypes = [wt.HGLOBAL]
user32.GetClipboardData.restype = wt.HANDLE
user32.SetClipboardData.argtypes = [wt.UINT, wt.HANDLE]


class NotInDesktopSession(RuntimeError):
    pass


def in_desktop_session() -> bool:
    session = wt.DWORD()
    kernel32.ProcessIdToSessionId(kernel32.GetCurrentProcessId(), ctypes.byref(session))
    return session.value != 0


def _require_desktop():
    if not in_desktop_session():
        raise NotInDesktopSession("SSH Manager is running outside your desktop session (it was started over SSH). "
                                  "It moves there at the next sign-in, or restart the PC.")


# ---- screen ----

def monitors() -> list[dict]:
    _require_desktop()
    import mss
    with mss.mss() as sct:
        return [{"index": i, "width": m["width"], "height": m["height"]} for i, m in enumerate(sct.monitors) if i > 0]


def capture(monitor: int = 1, width: int = 1280, quality: int = 70) -> bytes:
    _require_desktop()
    import mss
    from PIL import Image
    with mss.mss() as sct:
        mons = sct.monitors
        mon = mons[monitor] if 0 <= monitor < len(mons) else mons[1]
        shot = sct.grab(mon)
    img = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
    if width and img.width > width:
        img = img.resize((width, round(img.height * width / img.width)), Image.LANCZOS)
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=quality, optimize=True)
    return buf.getvalue()


# ---- clipboard (text) ----

def _open_clipboard():
    for _ in range(20):
        if user32.OpenClipboard(None):
            return
        time.sleep(0.05)
    raise RuntimeError("The clipboard is busy.")


def get_text() -> str | None:
    _require_desktop()
    _open_clipboard()
    try:
        handle = user32.GetClipboardData(CF_UNICODETEXT)
        if not handle:
            return None
        ptr = kernel32.GlobalLock(handle)
        try:
            return ctypes.wstring_at(ptr)[:MAX_TEXT]
        finally:
            kernel32.GlobalUnlock(handle)
    finally:
        user32.CloseClipboard()


def set_text(text: str):
    _require_desktop()
    data = ctypes.create_unicode_buffer(text[:MAX_TEXT])
    size = ctypes.sizeof(data)
    handle = kernel32.GlobalAlloc(GMEM_MOVEABLE, size)
    ptr = kernel32.GlobalLock(handle)
    ctypes.memmove(ptr, data, size)
    kernel32.GlobalUnlock(handle)
    _open_clipboard()
    try:
        user32.EmptyClipboard()
        user32.SetClipboardData(CF_UNICODETEXT, handle)
    finally:
        user32.CloseClipboard()


class ClipboardWatch:
    """Notices copies on this PC (clipboard sequence number) so apps can fetch the new text."""

    def __init__(self):
        self.seq = 0            # bumps on every change seen here or set by an app
        self.text: str | None = None
        self.origin = ""        # who put the current text there: "" (this PC) or the app's machine name
        self.lock = threading.Lock()
        self.last_win_seq = 0
        threading.Thread(target=self._loop, daemon=True).start()

    def _loop(self):
        while True:
            time.sleep(0.7)
            if not in_desktop_session():
                continue
            win_seq = user32.GetClipboardSequenceNumber()
            if win_seq == self.last_win_seq:
                continue
            self.last_win_seq = win_seq
            try:
                text = get_text()
            except Exception:
                continue
            with self.lock:
                if text is not None and text != self.text:
                    self.text, self.origin, self.seq = text, "", self.seq + 1

    def put(self, text: str, origin: str):
        set_text(text)
        with self.lock:
            self.text, self.origin, self.seq = text, origin, self.seq + 1
            self.last_win_seq = user32.GetClipboardSequenceNumber()

    def since(self, seq: int) -> dict:
        with self.lock:
            if self.seq <= seq or self.text is None:
                return {"seq": self.seq, "changed": False}
            return {"seq": self.seq, "changed": True, "text": self.text, "origin": self.origin}
