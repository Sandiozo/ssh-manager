"""Is another computer reachable? Language-independent (no parsing of ping.exe's translated output):
the name is resolved with getaddrinfo, then an ICMP echo (IcmpSendEcho, no admin rights needed) is sent;
if that gets no answer (Windows' firewall blocks ping by default), a TCP connection to the SSH port is
tried, where an answer of any kind (accepted or refused) shows the computer is on."""
import ctypes
import ctypes.wintypes as wt
import socket
import struct
import time

_iphlp = ctypes.WinDLL("iphlpapi")
_iphlp.IcmpCreateFile.restype = wt.HANDLE
_iphlp.IcmpCloseHandle.argtypes = [wt.HANDLE]
_iphlp.IcmpSendEcho.argtypes = [wt.HANDLE, wt.ULONG, ctypes.c_void_p, wt.WORD, ctypes.c_void_p, ctypes.c_void_p,
                                wt.DWORD, wt.DWORD]
_iphlp.IcmpSendEcho.restype = wt.DWORD
INVALID_HANDLE = wt.HANDLE(-1).value


class _IpOptions(ctypes.Structure):
    _fields_ = [("Ttl", ctypes.c_ubyte), ("Tos", ctypes.c_ubyte), ("Flags", ctypes.c_ubyte),
                ("OptionsSize", ctypes.c_ubyte), ("OptionsData", ctypes.c_void_p)]


class _EchoReply(ctypes.Structure):
    _fields_ = [("Address", wt.ULONG), ("Status", wt.ULONG), ("RoundTripTime", wt.ULONG), ("DataSize", wt.USHORT),
                ("Reserved", wt.USHORT), ("Data", ctypes.c_void_p), ("Options", _IpOptions)]


def resolve(target: str) -> str | None:
    """IPv4 address of a name or address, or None."""
    try:
        return socket.getaddrinfo(target, None, socket.AF_INET, socket.SOCK_STREAM)[0][4][0]
    except (OSError, UnicodeError, IndexError):
        return None


def icmp(ip: str, timeout_ms: int = 800) -> int | None:
    """Round-trip time in ms of a ping to ip, or None without an answer."""
    handle = _iphlp.IcmpCreateFile()
    if not handle or handle == INVALID_HANDLE:
        return None
    try:
        payload = b"sshmgr-ping-0123456789abcdef"
        buf = ctypes.create_string_buffer(ctypes.sizeof(_EchoReply) + len(payload) + 64)
        dest = struct.unpack("<I", socket.inet_aton(ip))[0]
        n = _iphlp.IcmpSendEcho(handle, dest, payload, len(payload), None, buf, len(buf), timeout_ms)
        if not n:
            return None
        reply = _EchoReply.from_buffer(buf)
        return int(reply.RoundTripTime) if reply.Status == 0 else None
    except OSError:
        return None
    finally:
        _iphlp.IcmpCloseHandle(handle)


def tcp(ip: str, port: int = 22, timeout: float = 0.8) -> int | None:
    """Time in ms until the computer answered a connection to port (accepted or refused), or None."""
    t0 = time.perf_counter()
    try:
        with socket.create_connection((ip, port), timeout=timeout):
            pass
    except ConnectionRefusedError:
        pass   # refused: the computer is on, nothing listens there
    except OSError:
        return None
    return round((time.perf_counter() - t0) * 1000)


def reach(target: str, port: int = 22) -> tuple[str, int] | None:
    """(IPv4 address, latency ms) when target answers, else None."""
    ip = resolve(target)
    if ip is None:
        return None
    ms = icmp(ip)
    if ms is None:
        ms = tcp(ip, port)
    return (ip, ms) if ms is not None else None
