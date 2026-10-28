"""Wake-on-LAN: the "magic packet" that switches on a sleeping or (if the PC's BIOS/network card allow
it) powered-off PC on the same network. Also tells which MAC address other computers should use to
wake this one."""
import re
import socket
import subprocess
import time

import psutil


def normalize(mac: str) -> str:
    digits = re.sub(r"[^0-9A-Fa-f]", "", mac or "")
    if len(digits) != 12:
        raise ValueError(f"\"{mac}\" is not a MAC address.")
    return ":".join(digits[i:i + 2] for i in range(0, 12, 2)).upper()


def send(mac: str, ips: list[str] | None = None) -> None:
    mac = normalize(mac)
    packet = b"\xff" * 6 + bytes.fromhex(mac.replace(":", "")) * 16
    targets = {"255.255.255.255"}
    for ip in ips or []:
        parts = ip.split(".")
        if len(parts) == 4:
            targets.add(".".join(parts[:3] + ["255"]))   # the /24 broadcast of a known address
            targets.add(ip)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        for t in targets:
            for port in (9, 7):
                try:
                    s.sendto(packet, (t, port))
                except OSError:
                    pass


_physical: tuple[float, dict[str, int | None]] = (-1e9, {})
# NDIS_PHYSICAL_MEDIUM values of wireless adapters: wireless LAN, wireless WAN, native 802.11, Bluetooth,
# WiMAX, UWB, IrDA, 802.15.4
WIRELESS_MEDIA = {1, 8, 9, 10, 12, 13, 16, 20}


def physical_adapters() -> dict[str, int | None]:
    """Real network cards (not VPN/virtual adapters) -> their physical medium (NdisPhysicalMedium, a number,
    so it doesn't depend on the Windows language or the adapter's name); cached for 10 minutes."""
    global _physical
    if time.monotonic() - _physical[0] > 600:
        found: dict[str, int | None] = {}
        try:
            out = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                                  "[Console]::OutputEncoding = [Text.Encoding]::UTF8; Get-NetAdapter -Physical | ForEach-Object { \"$($_.NdisPhysicalMedium)`t$($_.Name)\" }"],
                                 capture_output=True, timeout=20, creationflags=0x08000000).stdout
            for line in out.decode("utf-8", errors="replace").splitlines():
                medium, _, name = line.strip("\r\n").partition("\t")
                if name.strip():
                    found[name.strip()] = int(medium) if medium.strip().isdigit() else None
        except Exception:
            pass
        _physical = (time.monotonic(), found)
    return _physical[1]


def _wired(name: str, medium: int | None) -> bool:
    if medium is not None:
        return medium not in WIRELESS_MEDIA
    low = name.lower()
    return not any(w in low for w in ("wi-fi", "wifi", "wlan", "wireless", "802.11"))


def own_macs() -> list[dict]:
    """MAC + IPv4 of this PC's physical network adapters that are up and have a LAN address."""
    stats = psutil.net_if_stats()
    physical = physical_adapters()
    out = []
    for name, addrs in psutil.net_if_addrs().items():
        if not stats.get(name) or not stats[name].isup or (physical and name not in physical):
            continue
        mac = next((a.address for a in addrs if a.family == psutil.AF_LINK), None)
        ips = [a.address for a in addrs if a.family == socket.AF_INET and not a.address.startswith(("127.", "169.254."))]
        if mac and ips and mac.replace("-", "") != "000000000000":
            out.append({"adapter": name, "mac": normalize(mac), "ips": ips, "wired": _wired(name, physical.get(name))})
    return sorted(out, key=lambda a: not a["wired"])   # wired first: Wake-on-LAN usually only works there
