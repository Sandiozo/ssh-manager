"""Windows security helpers (ctypes, no extra packages): who this process runs as, file permissions read
and set by SID (so it works on every Windows language), and replacing a file while keeping its permissions.

Permissions are read as SDDL strings, e.g. "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)": owner, then the
access rules (allow/deny; inheritance flags; rights; ...; account)."""
import ctypes
import ctypes.wintypes as wt
import re

advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
shell32 = ctypes.WinDLL("shell32", use_last_error=True)

SE_FILE_OBJECT = 1
OWNER_SECURITY_INFORMATION = 0x1
DACL_SECURITY_INFORMATION = 0x4
PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000
UNPROTECTED_DACL_SECURITY_INFORMATION = 0x20000000
SDDL_REVISION_1 = 1
TOKEN_QUERY = 0x8
TOKEN_USER_CLASS = 1

SYSTEM = "S-1-5-18"
ADMINISTRATORS = "S-1-5-32-544"
TRUSTED_INSTALLER = "S-1-5-80-956008885-3418522649-1831038044-1849952000-2427256617"

PSID = ctypes.c_void_p
PSD = ctypes.c_void_p
PACL = ctypes.c_void_p

advapi32.GetNamedSecurityInfoW.argtypes = [wt.LPCWSTR, ctypes.c_int, wt.DWORD, ctypes.POINTER(PSID), ctypes.POINTER(PSID),
                                           ctypes.POINTER(PACL), ctypes.POINTER(PACL), ctypes.POINTER(PSD)]
advapi32.GetNamedSecurityInfoW.restype = wt.DWORD
advapi32.SetNamedSecurityInfoW.argtypes = [wt.LPWSTR, ctypes.c_int, wt.DWORD, PSID, PSID, PACL, PACL]
advapi32.SetNamedSecurityInfoW.restype = wt.DWORD
advapi32.ConvertSecurityDescriptorToStringSecurityDescriptorW.argtypes = [PSD, wt.DWORD, wt.DWORD,
                                                                          ctypes.POINTER(wt.LPWSTR), ctypes.POINTER(wt.ULONG)]
advapi32.ConvertSecurityDescriptorToStringSecurityDescriptorW.restype = wt.BOOL
advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [wt.LPCWSTR, wt.DWORD, ctypes.POINTER(PSD),
                                                                          ctypes.POINTER(wt.ULONG)]
advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW.restype = wt.BOOL
advapi32.GetSecurityDescriptorDacl.argtypes = [PSD, ctypes.POINTER(wt.BOOL), ctypes.POINTER(PACL), ctypes.POINTER(wt.BOOL)]
advapi32.GetSecurityDescriptorDacl.restype = wt.BOOL
advapi32.GetSecurityDescriptorOwner.argtypes = [PSD, ctypes.POINTER(PSID), ctypes.POINTER(wt.BOOL)]
advapi32.GetSecurityDescriptorOwner.restype = wt.BOOL
advapi32.ConvertSidToStringSidW.argtypes = [PSID, ctypes.POINTER(wt.LPWSTR)]
advapi32.ConvertSidToStringSidW.restype = wt.BOOL
advapi32.ConvertStringSidToSidW.argtypes = [wt.LPCWSTR, ctypes.POINTER(PSID)]
advapi32.ConvertStringSidToSidW.restype = wt.BOOL
advapi32.OpenProcessToken.argtypes = [wt.HANDLE, wt.DWORD, ctypes.POINTER(wt.HANDLE)]
advapi32.OpenProcessToken.restype = wt.BOOL
advapi32.GetTokenInformation.argtypes = [wt.HANDLE, ctypes.c_int, ctypes.c_void_p, wt.DWORD, ctypes.POINTER(wt.DWORD)]
advapi32.GetTokenInformation.restype = wt.BOOL
advapi32.LookupAccountSidW.argtypes = [wt.LPCWSTR, PSID, wt.LPWSTR, ctypes.POINTER(wt.DWORD), wt.LPWSTR,
                                       ctypes.POINTER(wt.DWORD), ctypes.POINTER(ctypes.c_int)]
advapi32.LookupAccountSidW.restype = wt.BOOL
kernel32.LocalFree.argtypes = [ctypes.c_void_p]
kernel32.LocalFree.restype = ctypes.c_void_p
kernel32.GetCurrentProcess.restype = wt.HANDLE
kernel32.CloseHandle.argtypes = [wt.HANDLE]
kernel32.ProcessIdToSessionId.argtypes = [wt.DWORD, ctypes.POINTER(wt.DWORD)]
kernel32.ProcessIdToSessionId.restype = wt.BOOL
kernel32.ReplaceFileW.argtypes = [wt.LPCWSTR, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD, ctypes.c_void_p, ctypes.c_void_p]
kernel32.ReplaceFileW.restype = wt.BOOL


class SecurityError(OSError):
    pass


def _check(ok, what: str):
    if not ok:
        err = ctypes.get_last_error()
        raise SecurityError(err, f"{what} failed: {ctypes.FormatError(err).strip()}")


def is_elevated() -> bool:
    try:
        return bool(shell32.IsUserAnAdmin())
    except Exception:
        return False


def session_of(pid: int) -> int | None:
    """Windows session of a process (0 = services, not a desktop), or None if unknown."""
    s = wt.DWORD()
    return s.value if kernel32.ProcessIdToSessionId(pid, ctypes.byref(s)) else None


def _sid_string(psid) -> str:
    out = wt.LPWSTR()
    _check(advapi32.ConvertSidToStringSidW(psid, ctypes.byref(out)), "ConvertSidToStringSid")
    try:
        return out.value
    finally:
        kernel32.LocalFree(out)


def current_user_sid() -> str:
    token = wt.HANDLE()
    _check(advapi32.OpenProcessToken(kernel32.GetCurrentProcess(), TOKEN_QUERY, ctypes.byref(token)), "OpenProcessToken")
    try:
        size = wt.DWORD()
        advapi32.GetTokenInformation(token, TOKEN_USER_CLASS, None, 0, ctypes.byref(size))
        buf = ctypes.create_string_buffer(size.value)
        _check(advapi32.GetTokenInformation(token, TOKEN_USER_CLASS, buf, size, ctypes.byref(size)), "GetTokenInformation")
        return _sid_string(ctypes.c_void_p.from_buffer(buf).value)   # TOKEN_USER starts with the PSID
    finally:
        kernel32.CloseHandle(token)


_full_sids: dict[str, str] = {}


def full_sid(sid: str) -> str:
    """"BA" -> "S-1-5-32-544" (SDDL writes well-known accounts as two-letter aliases)."""
    if sid.startswith("S-"):
        return sid
    if sid not in _full_sids:
        p = PSID()
        if advapi32.ConvertStringSidToSidW(sid, ctypes.byref(p)):
            try:
                _full_sids[sid] = _sid_string(p)
            finally:
                kernel32.LocalFree(p)
        else:
            _full_sids[sid] = sid
    return _full_sids[sid]


def account_name(sid: str) -> str:
    """Readable account name for logs ("BUILTIN\\Users"), or the SID itself."""
    p = PSID()
    if not advapi32.ConvertStringSidToSidW(sid, ctypes.byref(p)):
        return sid
    try:
        name, domain = ctypes.create_unicode_buffer(256), ctypes.create_unicode_buffer(256)
        n, d, use = wt.DWORD(256), wt.DWORD(256), ctypes.c_int()
        if advapi32.LookupAccountSidW(None, p, name, ctypes.byref(n), domain, ctypes.byref(d), ctypes.byref(use)):
            return f"{domain.value}\\{name.value}" if domain.value else name.value
        return sid
    finally:
        kernel32.LocalFree(p)


def get_sddl(path: str, owner: bool = True) -> str:
    info = DACL_SECURITY_INFORMATION | (OWNER_SECURITY_INFORMATION if owner else 0)
    psd = PSD()
    err = advapi32.GetNamedSecurityInfoW(str(path), SE_FILE_OBJECT, info, None, None, None, None, ctypes.byref(psd))
    if err:
        raise SecurityError(err, f"reading the permissions of {path} failed: {ctypes.FormatError(err).strip()}")
    try:
        out = wt.LPWSTR()
        _check(advapi32.ConvertSecurityDescriptorToStringSecurityDescriptorW(psd, SDDL_REVISION_1, info, ctypes.byref(out), None),
               "ConvertSecurityDescriptorToStringSecurityDescriptor")
        try:
            return out.value
        finally:
            kernel32.LocalFree(out)
    finally:
        kernel32.LocalFree(psd)


def set_sddl(path: str, sddl: str, info: int):
    """Applies the parts of `sddl` named by `info` (DACL/OWNER flags, plus PROTECTED/UNPROTECTED)."""
    psd = PSD()
    _check(advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, ctypes.byref(psd), None),
           "ConvertStringSecurityDescriptorToSecurityDescriptor")
    try:
        dacl, owner = PACL(), PSID()
        present, defaulted = wt.BOOL(), wt.BOOL()
        if info & DACL_SECURITY_INFORMATION:
            _check(advapi32.GetSecurityDescriptorDacl(psd, ctypes.byref(present), ctypes.byref(dacl), ctypes.byref(defaulted)),
                   "GetSecurityDescriptorDacl")
        if info & OWNER_SECURITY_INFORMATION:
            _check(advapi32.GetSecurityDescriptorOwner(psd, ctypes.byref(owner), ctypes.byref(defaulted)),
                   "GetSecurityDescriptorOwner")
        err = advapi32.SetNamedSecurityInfoW(str(path), SE_FILE_OBJECT, info, owner, None, dacl, None)
        if err:
            raise SecurityError(err, f"changing the permissions of {path} failed: {ctypes.FormatError(err).strip()}")
    finally:
        kernel32.LocalFree(psd)


# ---- reading SDDL ----

RIGHTS = {"GA": 0x10000000, "GR": 0x80000000, "GW": 0x40000000, "GX": 0x20000000, "RC": 0x20000, "SD": 0x10000,
          "WD": 0x40000, "WO": 0x80000, "RP": 0x10, "WP": 0x20, "CC": 0x1, "DC": 0x2, "LC": 0x4, "SW": 0x8, "LO": 0x80,
          "DT": 0x40, "CR": 0x100, "FA": 0x1F01FF, "FR": 0x120089, "FW": 0x120116, "FX": 0x1200A0}
# write data / append (add file, add folder) / delete child / delete / change permissions / take ownership
WRITE_BITS = 0x2 | 0x4 | 0x40 | 0x10000 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000
READ_BITS = 0x1 | 0x80000000 | 0x10000000


def _mask(rights: str) -> int:
    if rights.lower().startswith("0x"):
        return int(rights, 16)
    if rights.isdigit():
        return int(rights)
    m = 0
    for i in range(0, len(rights) - 1, 2):
        m |= RIGHTS.get(rights[i:i + 2], 0)
    return m


def parse(sddl: str) -> dict:
    """{"owner": full SID or None, "protected": bool, "null_dacl": bool,
        "aces": [{"type": "A"/"D", "flags": str, "mask": int, "sid": full SID, "inherited": bool}]}"""
    owner = re.search(r"O:(.+?)(?=[GDS]:|$)", sddl)
    dpart = re.search(r"D:(.*?)(?=S:\(|$)", sddl)
    dacl = dpart.group(1) if dpart else ""
    flags = dacl.split("(", 1)[0]
    aces = []
    for ace in re.findall(r"\(([^()]*)\)", dacl):
        parts = ace.split(";")
        if len(parts) < 6:
            continue
        aces.append({"type": parts[0], "flags": parts[1], "mask": _mask(parts[2]), "sid": full_sid(parts[5]),
                     "inherited": "ID" in parts[1]})
    return {"owner": full_sid(owner.group(1)) if owner else None, "protected": "P" in flags,
            "null_dacl": dpart is None or "NO_ACCESS_CONTROL" in dacl, "aces": aces}


def others_with(path: str, trusted: set[str], bits: int) -> list[str]:
    """SIDs outside `trusted` that have any of `bits` on path (an untrusted owner counts too: it can change
    the permissions). ["*unknown*"] when the permissions can't be read (e.g. a FAT/exFAT drive)."""
    try:
        sd = parse(get_sddl(path))
    except OSError:
        return ["*unknown*"]
    if sd["null_dacl"]:
        return ["S-1-1-0"]   # no permissions at all: Everyone
    found = []
    if sd["owner"] and sd["owner"] not in trusted:
        found.append(sd["owner"])
    for ace in sd["aces"]:
        if "IO" in ace["flags"] or not ace["type"].startswith(("A", "XA")) or ace["type"].startswith("AU"):
            continue   # inherit-only, deny or audit rules
        sid = ace["sid"]
        if sid == "S-1-3-4":   # OWNER RIGHTS: the owner, checked above
            continue
        if sid not in trusted and ace["mask"] & bits and sid not in found:
            found.append(sid)
    return found


def trusted_sids(user_sid: str) -> set[str]:
    return {SYSTEM, ADMINISTRATORS, TRUSTED_INSTALLER, user_sid}


def private_dacl(user_sid: str) -> str:
    """Full control for SYSTEM, Administrators and this user only; not inherited from the parent folder."""
    return f"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;{user_sid})"


def make_private(path: str, user_sid: str):
    set_sddl(path, private_dacl(user_sid), DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION)


def inherit_only(path: str):
    """Drops a file's own permission rules so it has just what its folder passes down."""
    set_sddl(path, "D:", DACL_SECURITY_INFORMATION | UNPROTECTED_DACL_SECURITY_INFORMATION)


def set_owner_admins(path: str):
    set_sddl(path, "O:BA", OWNER_SECURITY_INFORMATION)


def copy_dacl(source: str, target: str):
    """Gives target exactly the permissions of source (explicit and inherited rules, protected)."""
    sddl = get_sddl(source, owner=False)
    sd = parse(sddl)
    if sd["null_dacl"]:
        return
    # inherited rules become explicit ones, so the result is the same whatever folder target is in
    body = re.sub(r"(\([^;()]*;[^;()]*)ID", r"\1", sddl.split("D:", 1)[1])
    flags = body.split("(", 1)[0]
    body = "P" + flags.replace("P", "").replace("AI", "").replace("AR", "") + body[len(flags):]
    set_sddl(target, "D:" + body, DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION)


def replace_file(target: str, replacement: str):
    """Atomically replaces target with replacement, keeping target's permissions and attributes."""
    REPLACEFILE_IGNORE_MERGE_ERRORS = 0x2
    _check(kernel32.ReplaceFileW(str(target), str(replacement), None, REPLACEFILE_IGNORE_MERGE_ERRORS, None, None),
           f"replacing {target}")
