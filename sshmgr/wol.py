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


_physical: tuple[float, set[str]] = (0.0, set())


def physical_adapters() -> set[str]:
    """Names of real network cards (not VPN/virtual adapters), from Windows; cached for 10 minutes."""
    global _physical
    if time.time() - _physical[0] > 600:
        try:
            out = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                                  "Get-NetAdapter -Physical | ForEach-Object Name"],
                                 capture_output=True, timeout=20, creationflags=0x08000000).stdout
            _physical = (time.time(), {n.strip() for n in out.decode(errors="replace").splitlines() if n.strip()})
        except Exception:
            _physical = (time.time(), set())
    return _physical[1]


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
            out.append({"adapter": name, "mac": normalize(mac), "ips": ips, "wired": "wi" not in name.lower()})
    return sorted(out, key=lambda a: not a["wired"])   # wired first: Wake-on-LAN usually only works there
