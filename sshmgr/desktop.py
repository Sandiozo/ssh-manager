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


class ScreenUnavailable(RuntimeError):
    """Windows won't hand out the screen right now (locked, sign-in screen, an administrator prompt)."""


SCREEN_UNAVAILABLE = ("The screen can't be captured right now: the PC may be locked or at the sign-in screen, "
                      "or showing an administrator (UAC) prompt.")


def _grab(fn):
    try:
        return fn()
    except (NotInDesktopSession, WindowGone):
        raise
    except Exception as e:   # mss.ScreenShotError (BitBlt failed), OSError from GDI
        raise ScreenUnavailable(SCREEN_UNAVAILABLE) from e


# ---- screen ----

def monitors() -> list[dict]:
    _require_desktop()
    import mss

    def get():
        with mss.mss() as sct:
            return [{"index": i, "width": m["width"], "height": m["height"]} for i, m in enumerate(sct.monitors) if i > 0]
    return _grab(get)


def capture(monitor: int = 1, width: int = 1280, quality: int = 70, fmt: str = "jpeg") -> bytes:
    _require_desktop()
    import mss
    from PIL import Image

    def grab():
        with mss.mss() as sct:
            mons = sct.monitors
            mon = mons[monitor] if 0 <= monitor < len(mons) else mons[1 if len(mons) > 1 else 0]
            return sct.grab(mon)
    shot = _grab(grab)
    return _encode(Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX"), width, quality, fmt)


def _encode(img, width: int, quality: int, fmt: str = "jpeg") -> bytes:
    """Scaled down to `width` (0 = full size), as JPEG or lossless PNG."""
    from PIL import Image
    if width and img.width > width:
        img = img.resize((width, round(img.height * width / img.width)), Image.LANCZOS)
    buf = io.BytesIO()
    if fmt == "png":
        img.save(buf, "PNG", compress_level=3)
    else:   # at high quality keep full color resolution too (no chroma subsampling): sharper edges
        img.save(buf, "JPEG", quality=quality, optimize=True, subsampling=0 if quality >= 90 else 2)
    return buf.getvalue()


# ---- single app windows ----

class WindowGone(RuntimeError):
    pass


GW_OWNER = 4
GWL_EXSTYLE = -20
WS_EX_TOOLWINDOW = 0x80
DWMWA_EXTENDED_FRAME_BOUNDS = 9
DWMWA_CLOAKED = 14
PW_RENDERFULLCONTENT = 2
# own library handles with exact argument types (64-bit handles; mss sets its own on ctypes.windll)
_u32 = ctypes.WinDLL("user32")
_gdi = ctypes.WinDLL("gdi32")
_dwm = ctypes.WinDLL("dwmapi")
for f, args, res in (
    (_u32.IsWindowVisible, [wt.HWND], wt.BOOL), (_u32.GetWindow, [wt.HWND, wt.UINT], wt.HWND),
    (_u32.GetWindowLongPtrW, [wt.HWND, ctypes.c_int], ctypes.c_ssize_t),
    (_u32.GetWindowTextLengthW, [wt.HWND], ctypes.c_int), (_u32.GetWindowTextW, [wt.HWND, wt.LPWSTR, ctypes.c_int], ctypes.c_int),
    (_u32.GetWindowThreadProcessId, [wt.HWND, ctypes.POINTER(wt.DWORD)], wt.DWORD),
    (_u32.IsIconic, [wt.HWND], wt.BOOL), (_u32.IsWindow, [wt.HWND], wt.BOOL),
    (_u32.GetWindowRect, [wt.HWND, ctypes.POINTER(wt.RECT)], wt.BOOL),
    (_u32.GetWindowDC, [wt.HWND], wt.HDC), (_u32.ReleaseDC, [wt.HWND, wt.HDC], ctypes.c_int),
    (_u32.PrintWindow, [wt.HWND, wt.HDC, wt.UINT], wt.BOOL),
    (_gdi.CreateCompatibleDC, [wt.HDC], wt.HDC), (_gdi.CreateCompatibleBitmap, [wt.HDC, ctypes.c_int, ctypes.c_int], wt.HBITMAP),
    (_gdi.SelectObject, [wt.HDC, wt.HGDIOBJ], wt.HGDIOBJ), (_gdi.DeleteObject, [wt.HGDIOBJ], wt.BOOL),
    (_gdi.DeleteDC, [wt.HDC], wt.BOOL),
    (_gdi.GetDIBits, [wt.HDC, wt.HBITMAP, wt.UINT, wt.UINT, ctypes.c_void_p, ctypes.c_void_p, wt.UINT], ctypes.c_int),
    (_dwm.DwmGetWindowAttribute, [wt.HWND, wt.DWORD, ctypes.c_void_p, wt.DWORD], ctypes.c_long),
):
    f.argtypes, f.restype = args, res
try:   # real pixel sizes for windows on scaled monitors
    _u32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    pass


def _frame(hwnd) -> tuple[int, int, int, int]:
    """Window rectangle without the invisible resize border."""
    r = wt.RECT()
    if _dwm.DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, ctypes.byref(r), ctypes.sizeof(r)) != 0:
        _u32.GetWindowRect(hwnd, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def windows() -> list[dict]:
    """The app windows on the taskbar: visible, titled, not owned by another window."""
    _require_desktop()
    import psutil
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def each(hwnd, _):
        if not _u32.IsWindowVisible(hwnd) or _u32.GetWindow(hwnd, GW_OWNER):
            return True
        if _u32.GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW:
            return True
        cloaked = wt.DWORD()
        _dwm.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, ctypes.byref(cloaked), ctypes.sizeof(cloaked))
        if cloaked.value:
            return True
        n = _u32.GetWindowTextLengthW(hwnd)
        if n == 0:
            return True
        title = ctypes.create_unicode_buffer(n + 1)
        _u32.GetWindowTextW(hwnd, title, n + 1)
        pid = wt.DWORD()
        _u32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        try:
            app = psutil.Process(pid.value).name().removesuffix(".exe")
        except Exception:
            app = ""
        if app.lower() == "sshmanagerserver":
            return True
        l, t, r, b = _frame(hwnd)
        found.append({"id": int(hwnd), "title": title.value, "app": app, "minimized": bool(_u32.IsIconic(hwnd)),
                      "width": r - l, "height": b - t})
        return True

    _u32.EnumWindows(each, 0)
    return found


def window_app(hwnd: int) -> str:
    """Name of the program that owns a window, without .exe ("" if unknown)."""
    import psutil
    pid = wt.DWORD()
    _u32.GetWindowThreadProcessId(wt.HWND(hwnd), ctypes.byref(pid))
    try:
        return psutil.Process(pid.value).name().removesuffix(".exe")
    except Exception:
        return ""


def capture_window(hwnd: int, width: int = 1280, quality: int = 70, fmt: str = "jpeg") -> bytes:
    """One app window, also when other windows cover it (not when it's minimized)."""
    _require_desktop()
    return _grab(lambda: _capture_window(hwnd, width, quality, fmt))


def _capture_window(hwnd: int, width: int, quality: int, fmt: str) -> bytes:
    from PIL import Image
    hwnd = wt.HWND(hwnd)
    if not _u32.IsWindow(hwnd):
        raise WindowGone("That window was closed.")
    if _u32.IsIconic(hwnd):
        raise WindowGone("That window is minimized. Restore it on the PC to see it.")
    win = wt.RECT()
    _u32.GetWindowRect(hwnd, ctypes.byref(win))
    w, h = win.right - win.left, win.bottom - win.top
    if w <= 0 or h <= 0:
        raise WindowGone("That window has no size.")
    wdc = _u32.GetWindowDC(hwnd)
    mdc = _gdi.CreateCompatibleDC(wdc)
    bmp = _gdi.CreateCompatibleBitmap(wdc, w, h)
    old = _gdi.SelectObject(mdc, bmp)
    try:
        ok = _u32.PrintWindow(hwnd, mdc, PW_RENDERFULLCONTENT)
        header = (ctypes.c_uint32 * 10)(40, w, ctypes.c_uint32(-h).value, 1 | (32 << 16), 0, 0, 0, 0, 0, 0)
        pixels = ctypes.create_string_buffer(w * h * 4)
        _gdi.GetDIBits(mdc, bmp, 0, h, pixels, header, 0)
    finally:
        _gdi.SelectObject(mdc, old)
        _gdi.DeleteObject(bmp)
        _gdi.DeleteDC(mdc)
        _u32.ReleaseDC(hwnd, wdc)
    img = Image.frombuffer("RGB", (w, h), pixels.raw, "raw", "BGRX", 0, 1)
    l, t, r, b = _frame(hwnd)
    img = img.crop((l - win.left, t - win.top, r - win.left, b - win.top))
    if not ok or not img.getbbox():
        # some apps (games, video) draw nothing into PrintWindow: copy the window's area of the screen instead
        import mss
        with mss.mss() as sct:
            shot = sct.grab({"left": l, "top": t, "width": r - l, "height": b - t})
        img = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
    return _encode(img, width, quality, fmt)


# ---- clipboard (text) ----

def _open_clipboard():
    for _ in range(20):
        if user32.OpenClipboard(None):
            return
        time.sleep(0.05)
    raise RuntimeError("The clipboard is busy.")


_u32.RegisterClipboardFormatW.argtypes, _u32.RegisterClipboardFormatW.restype = [wt.LPCWSTR], wt.UINT
_u32.IsClipboardFormatAvailable.argtypes, _u32.IsClipboardFormatAvailable.restype = [wt.UINT], wt.BOOL
_u32.GetClipboardData.argtypes, _u32.GetClipboardData.restype = [wt.UINT], wt.HANDLE
# formats password managers (and Windows' own clipboard history rules) use to say "don't copy this elsewhere"
CF_EXCLUDE = _u32.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing")
CF_HISTORY = _u32.RegisterClipboardFormatW("CanIncludeInClipboardHistory")
CF_VIEWER_IGNORE = _u32.RegisterClipboardFormatW("Clipboard Viewer Ignore")


def _private() -> bool:
    """The clipboard (already open) holds something its owner asked not to be shared or recorded."""
    if _u32.IsClipboardFormatAvailable(CF_EXCLUDE) or _u32.IsClipboardFormatAvailable(CF_VIEWER_IGNORE):
        return True
    if _u32.IsClipboardFormatAvailable(CF_HISTORY):
        handle = _u32.GetClipboardData(CF_HISTORY)
        if handle:
            ptr = kernel32.GlobalLock(handle)
            if ptr:
                try:
                    return ctypes.c_uint32.from_address(ptr).value == 0
                finally:
                    kernel32.GlobalUnlock(handle)
    return False


def get_text(skip_private: bool = True) -> str | None:
    """The clipboard's text; None when there is none, or (skip_private) when it is marked as not to be
    shared (passwords from a password manager)."""
    _require_desktop()
    _open_clipboard()
    try:
        if skip_private and _private():
            return None
        handle = user32.GetClipboardData(CF_UNICODETEXT)
        if not handle:
            return None
        ptr = kernel32.GlobalLock(handle)
        if not ptr:
            return None
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
        self.changed_at: float | None = None   # when (monotonic) the clipboard last changed; None: not since start
        threading.Thread(target=self._loop, daemon=True).start()

    def _loop(self):
        while True:
            time.sleep(0.7)
            if not in_desktop_session():
                continue
            win_seq = user32.GetClipboardSequenceNumber()
            if win_seq == self.last_win_seq:
                continue
            if self.last_win_seq:   # the first look at startup is not a change
                self.changed_at = time.monotonic()
            self.last_win_seq = win_seq
            try:
                text = get_text()
            except Exception:
                continue
            with self.lock:
                if text is not None and text != self.text:
                    self.text, self.origin, self.seq = text, "", self.seq + 1

    def put(self, text: str, origin: str) -> bool:
        """Puts text on this PC's clipboard. Does nothing (returns False) when it already holds exactly that
        text: two PCs that share clipboards both ways would otherwise keep sending it back and forth, and
        replace a richer copy (formatting, an image) with plain text."""
        try:
            if get_text(skip_private=False) == text:
                return False
        except NotInDesktopSession:
            raise
        except Exception:
            pass
        set_text(text)
        with self.lock:
            self.text, self.origin, self.seq = text, origin, self.seq + 1
            self.last_win_seq = user32.GetClipboardSequenceNumber()
            self.changed_at = time.monotonic()
        return True

    def since(self, seq: int) -> dict:
        """The text when it changed after seq. age_s: seconds since the clipboard last changed on this PC (any
        content, also private or non-text); None when it hasn't changed since the server started."""
        with self.lock:
            age = None if self.changed_at is None else round(time.monotonic() - self.changed_at, 1)
            if self.seq <= seq or self.text is None:
                return {"seq": self.seq, "changed": False, "age_s": age}
            return {"seq": self.seq, "changed": True, "text": self.text, "origin": self.origin, "age_s": age}
