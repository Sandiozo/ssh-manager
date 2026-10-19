"""Watches the SSH server on this PC.

- sshd service state and port 22
- logins/logouts/failures from the OpenSSH/Operational event log (polled with wevtutil)
- live sessions (established TCP connections on port 22) and the commands they run, sampled from
  the process table: every process whose parent is an sshd process is one command sent over SSH
- reachability of the configured computers (ping) and, if installed, the Desktop Commander device status
"""
import base64
import ctypes
import html
import os
import logging
import re
import subprocess
import threading
import time
from collections import deque
from datetime import datetime, timedelta, timezone
from pathlib import Path

import psutil

from .activity import ActivityLog

log = logging.getLogger("sshmgr")

CREATE_NO_WINDOW = 0x08000000
SSHD_NAMES = {"sshd.exe", "sshd-session.exe", "sshd-auth.exe"}
IGNORED_CHILDREN = {"conhost.exe"}
MAX_SCRIPT = 20000

EVENT_RE = re.compile(
    r"<TimeCreated SystemTime='([^']+)'/>.*?<EventRecordID>(\d+)</EventRecordID>.*?"
    r"<Execution ProcessID='(\d+)'.*?<Data Name='payload'>(.*?)</Data>", re.S)
ACCEPT_RE = re.compile(r"Accepted (\S+) for (\S+) from (\S+) port (\d+)(?: ssh2)?(?:: (\S+) (\S+))?")
DISCONNECT_RE = re.compile(r"(?:Disconnected from|Connection closed by|Connection reset by)"
                           r"(?: (?:authenticating |invalid )?user \S+)? (\S+) port (\d+)")
RECEIVED_DISCONNECT_RE = re.compile(r"Received disconnect from (\S+) port (\d+):\d+: (.*)")
FAILED_RE = re.compile(r"Failed (\S+) for (?:invalid user )?(\S+) from (\S+) port (\d+)")
INVALID_RE = re.compile(r"Invalid user (\S*) from (\S+) port (\d+)")
ENCODED_RE = re.compile(r"-(?:EncodedCommand|enc|ec|e)\s+\"?([A-Za-z0-9+/=]{8,})", re.I)


def run(args: list[str], timeout: float = 15) -> str:
    r = subprocess.run(args, capture_output=True, timeout=timeout, creationflags=CREATE_NO_WINDOW)
    return r.stdout.decode("utf-8", errors="replace")


def iso(dt: datetime) -> str:
    return dt.astimezone(timezone.utc).isoformat(timespec="seconds")


def parse_event_time(s: str) -> datetime:
    return datetime.fromisoformat(s[:19]).replace(tzinfo=timezone.utc)


def norm_ip(ip: str) -> str:
    return ip[7:] if ip.startswith("::ffff:") else ip


def summarize(script: str) -> str:
    """First meaningful line of a script, for one-line lists."""
    summary = next((ln.strip() for ln in script.splitlines()
                    if ln.strip() and not ln.strip().startswith("#")), script.strip())
    return summary[:157] + "..." if len(summary) > 160 else summary


def decode_command(cmdline: list[str], name: str) -> dict:
    """Turn a process command line into {kind, summary, script}. `powershell -EncodedCommand <base64>`
    (how scripts are usually sent over SSH without quoting trouble) is decoded to the script itself."""
    raw = subprocess.list2cmdline(cmdline) if cmdline else name
    if name.lower() == "sftp-server.exe":
        return {"kind": "sftp", "summary": "SFTP / SCP file session", "script": raw}
    kind, script = "shell", raw
    if m := ENCODED_RE.search(raw):
        try:
            script = base64.b64decode(m.group(1)).decode("utf-16-le")
            kind = "powershell"
        except ValueError:
            pass
    else:
        script = re.sub(r'^\S*cmd(?:\.exe)?"?\s+/[cC]\s+', "", raw)
    return {"kind": kind, "summary": summarize(script), "script": script[:MAX_SCRIPT]}


class Monitor:
    def __init__(self, config: dict, activity: ActivityLog):
        self.config = config
        self.activity = activity
        self.lock = threading.RLock()
        self.last_record = 0
        self.conns: dict[tuple[str, int], dict] = {}
        self.conn_list: deque[dict] = deque(maxlen=5000)
        self.failures: deque[dict] = deque(maxlen=500)
        self.server_events: deque[dict] = deque(maxlen=50)
        self.procs: dict[int, tuple[str, int, float]] = {}   # pid -> (name, ppid, create_time)
        self.running: dict[int, dict] = {}                   # pid -> command record
        self.decoders: list = []                             # plugin hooks, see plugins.py
        self.finish_hooks: list = []
        self.clients: dict[str, dict] = {}                   # machine name -> stats the SSH Manager app sent
        self.peers: dict[str, dict] = {}
        self.reload_peers()
        self.dashboard_port = config.get("port", 8765)
        psutil.cpu_percent(None)

    def reload_peers(self):
        """Rebuild the computer list from the config, keeping the last ping result of unchanged entries."""
        with self.lock:
            old = self.peers
            self.peers = {}
            for p in self.config.get("peers", []):
                prev = old.get(p["name"], {})
                same = (prev.get("host"), prev.get("ip")) == (p.get("host"), p.get("ip"))
                keep = {k: prev[k] for k in ("online", "latency_ms", "resolved_ip", "checked", "via") if k in prev} if same else {}
                self.peers[p["name"]] = {**p, "online": None, **keep}
        threading.Thread(target=self._poll_peers, daemon=True).start()

    @property
    def host_name(self) -> str:
        return self.config.get("name") or os.environ.get("COMPUTERNAME", "this PC")

    # ---- lifecycle ----------------------------------------------------------------------------
    def start(self):
        self._poll_events(initial=True)
        for target, interval in ((self._poll_events, 3), (self._poll_peers, 10)):
            threading.Thread(target=self._loop, args=(target, interval), daemon=True).start()
        threading.Thread(target=self._loop, args=(self._sample_processes, 0.4), daemon=True).start()

    @staticmethod
    def _loop(fn, interval):
        while True:
            try:
                fn()
            except Exception:
                log.exception("%s failed", fn.__name__)
            time.sleep(interval)

    # ---- labels -------------------------------------------------------------------------------
    def label_ip(self, ip: str) -> str:
        if ip in ("127.0.0.1", "::1"):
            return self.host_name
        for p in self.peers.values():
            if ip in (p.get("ip"), p.get("resolved_ip")):
                return p["name"]
        # not in the computer list, but its SSH Manager app reported in from that address: use its name
        for c in list(self.clients.values()):
            if c.get("ip") == ip:
                return c["machine"]
        return ip

    def label_key(self, fp: str | None) -> str | None:
        return self.config.get("keys", {}).get(fp, fp) if fp else None

    # ---- event log ----------------------------------------------------------------------------
    def _poll_events(self, initial: bool = False):
        if initial:
            out = run(["wevtutil", "qe", "OpenSSH/Operational", "/c:3000", "/rd:true", "/f:xml"])
        else:
            q = f"*[System[EventRecordID>{self.last_record}]]"
            out = run(["wevtutil", "qe", "OpenSSH/Operational", f"/q:{q}", "/f:xml"])
        events = [(int(rid), parse_event_time(ts), int(pid), html.unescape(payload))
                  for ts, rid, pid, payload in EVENT_RE.findall(out)]
        events.sort()
        with self.lock:
            for rid, ts, pid, msg in events:
                if rid > self.last_record:
                    self._handle_event(ts, pid, msg)
                    self.last_record = rid

    def _handle_event(self, ts: datetime, pid: int, msg: str):
        if m := ACCEPT_RE.search(msg):
            method, user, ip, port, key_type, fp = m.groups()
            conn = {"ip": norm_ip(ip), "port": int(port), "user": user, "method": method,
                    "key_type": key_type, "fp": fp, "start": iso(ts), "end": None, "pid": pid, "reason": None}
            self.conns[(conn["ip"], conn["port"])] = conn
            self.conn_list.append(conn)
        elif m := RECEIVED_DISCONNECT_RE.search(msg):
            if conn := self.conns.get((norm_ip(m.group(1)), int(m.group(2)))):
                conn["reason"] = m.group(3).strip()
        elif m := DISCONNECT_RE.search(msg):
            if (conn := self.conns.get((norm_ip(m.group(1)), int(m.group(2))))) and not conn["end"]:
                conn["end"] = iso(ts)
        elif m := FAILED_RE.search(msg):
            method, user, ip, port = m.groups()
            self.failures.append({"ts": iso(ts), "ip": norm_ip(ip), "user": user, "method": method, "message": msg})
        elif m := INVALID_RE.search(msg):
            user, ip, port = m.groups()
            self.failures.append({"ts": iso(ts), "ip": norm_ip(ip), "user": user, "method": "invalid user", "message": msg})
        elif msg.startswith("Server listening") or msg.startswith("Received signal"):
            self.server_events.append({"ts": iso(ts), "message": msg})
            if msg.startswith("Received signal"):
                for conn in self.conns.values():
                    conn["end"] = conn["end"] or iso(ts)

    # ---- sockets and processes ----------------------------------------------------------------
    def _ssh_sockets(self) -> list:
        return [c for c in psutil.net_connections("tcp")
                if c.status == psutil.CONN_ESTABLISHED and c.laddr and c.laddr.port == 22 and c.raddr]

    def _sample_processes(self):
        pids = set(psutil.pids())
        with self.lock:
            for pid in [p for p in self.procs if p not in pids]:
                del self.procs[pid]
            new = []
            for pid in pids - self.procs.keys():
                try:
                    p = psutil.Process(pid)
                    self.procs[pid] = (p.name(), p.ppid(), p.create_time())
                    new.append(p)
                except (psutil.NoSuchProcess, psutil.AccessDenied, ProcessLookupError):
                    self.procs[pid] = ("?", 0, 0)
            for pid in [p for p in self.running if p not in self.procs]:
                self._finish(self.running.pop(pid))
            for p in new:
                name, ppid, ctime = self.procs[p.pid]
                parent = self.procs.get(ppid, ("",))[0].lower()
                if parent in SSHD_NAMES and name.lower() not in SSHD_NAMES | IGNORED_CHILDREN:
                    self._start_command(p, name, ppid, ctime)

    def _start_command(self, p: psutil.Process, name: str, ppid: int, ctime: float):
        try:
            cmdline = p.cmdline()
            hidden = False
        except (psutil.Error, OSError):
            # SSH sessions of an admin account run elevated; a non-elevated server can't read them
            cmdline, hidden = [], True
        chain, pid = [], ppid
        while pid and pid not in chain:
            chain.append(pid)
            pid = self.procs.get(pid, ("", 0, 0))[1]
        rec = {"pid": p.pid, "name": name, "start": iso(datetime.fromtimestamp(ctime, timezone.utc)),
               "chain": chain, "ip": None, "client_port": None, "hidden": hidden, **decode_command(cmdline, name)}
        if hidden:
            rec["summary"] = f"{name} (contents hidden: SSH Manager is not running as admin)"
        if "ensure-running" in rec["script"]:
            rec["kind"], rec["summary"] = "dashboard", "SSH Manager app connection"
        else:
            for decoder in self.decoders:
                try:
                    before = rec["script"]
                    decoder(rec)
                    if rec["script"] != before:
                        rec["script"] = rec["script"][:MAX_SCRIPT]
                        rec["summary"] = summarize(rec["script"])
                except Exception:
                    log.exception("plugin decoder failed")
        self._resolve(rec)
        self.running[p.pid] = rec

    def _resolve(self, rec: dict):
        """Attach the client address: the command's ancestors include the per-connection sshd
        process, whose PID the "Accepted ..." event log entry recorded."""
        if rec["ip"]:
            return
        for conn in reversed(self.conn_list):
            if conn["pid"] in rec["chain"]:
                rec["ip"], rec["client_port"] = conn["ip"], conn["port"]
                return

    def _finish(self, rec: dict):
        self._resolve(rec)
        if not rec["ip"]:
            # short commands can end before the 3 s event-log poll has seen their login
            self._poll_events()
            self._resolve(rec)
        start = datetime.fromisoformat(rec["start"])
        duration = max(0.0, (datetime.now(timezone.utc) - start).total_seconds())
        extra = {}
        for hook in self.finish_hooks:
            try:
                extra.update(hook(rec) or {})
            except Exception:
                log.exception("plugin finish hook failed")
        if isinstance(extra.get("output"), str):
            extra["output"] = extra["output"][:MAX_SCRIPT]
        self.activity.add("command", ts=rec["start"], client=self.label_ip(rec["ip"]) if rec["ip"] else "unknown",
                          ip=rec["ip"], pid=rec["pid"], process=rec["name"], type=rec["kind"],
                          summary=rec["summary"], script=rec["script"], duration_s=round(duration, 1), **extra)

    # ---- peers and Desktop Commander ----------------------------------------------------------
    def _poll_peers(self):
        for peer in list(self.peers.values()):
            res = {"online": False, "latency_ms": None, "checked": iso(datetime.now(timezone.utc))}
            for target in [t for t in (peer.get("host"), peer.get("ip")) if t]:
                out = run(["ping", "-4", "-n", "1", "-w", "800", target], timeout=5)
                if "TTL=" in out:
                    m = re.search(r"from ([\d.]+):.*?time[=<](\d+)ms", out)
                    res.update(online=True, latency_ms=int(m.group(2)) if m else None,
                               resolved_ip=m.group(1) if m else peer.get("ip"), via=target)
                    break
            with self.lock:
                peer.update(res)

    @staticmethod
    def desktop_commander() -> dict:
        d = Path.home() / ".desktop-commander-device"
        logs = sorted(d.glob("remote-*.log"), key=lambda p: p.stat().st_mtime) if d.exists() else []
        if not logs:
            return {"installed": d.exists(), "online": False, "state": "not set up"}
        last = logs[-1]
        with last.open("rb") as f:
            f.seek(max(0, last.stat().st_size - 65536))
            lines = f.read().decode("utf-8", errors="replace").splitlines()
        online, changed, heartbeat, last_line = None, None, None, ""
        for line in lines:
            ts, _, msg = line.partition("  ")
            if "marked as online" in msg or "Status: Online" in msg:
                online, changed = True, ts
            elif "marked as offline" in msg or "set to offline" in msg:
                online, changed = False, ts
            elif "last_seen bookkeeping write ok" in msg:
                heartbeat = ts
            if msg.strip():
                last_line = line
        age = time.time() - last.stat().st_mtime
        alive = bool(online) and age < 7 * 60
        return {"installed": True, "online": alive, "state": "online" if alive else "offline",
                "since": changed, "heartbeat": heartbeat, "log": last.name, "last_line": last_line[:200]}

    # ---- snapshot -----------------------------------------------------------------------------
    def _partitions(self):
        """The drive list. Listing it is slow on Windows (it asks every drive), and it rarely changes."""
        now = time.monotonic()
        if now - getattr(self, "_parts_at", -1e9) > 60:
            self._parts, self._parts_at = psutil.disk_partitions(), now
        return self._parts

    def status(self, tunnel_agents: dict[int, list[str]] | None = None) -> dict:
        """`tunnel_agents`: sshd PID of a port forward -> AI agents that used it recently (see agents.py)."""
        now = datetime.now(timezone.utc)
        with self.lock:
            sockets = self._ssh_sockets()
            tunnel_pids = {c.pid for c in psutil.net_connections("tcp")
                           if c.raddr and c.raddr.port == self.dashboard_port and c.status == psutil.CONN_ESTABLISHED}
            for rec in self.running.values():
                self._resolve(rec)
            sessions = []
            for s in sockets:
                ip, port = norm_ip(s.raddr.ip), s.raddr.port
                conn = self.conns.get((ip, port), {})
                cmds = []
                for rec in self.running.values():
                    if (rec["ip"], rec["client_port"]) == (ip, port):
                        cmds.append({**{k: rec[k] for k in ("pid", "name", "kind", "summary", "start", "hidden")},
                                     "children": self._children(rec["pid"])})
                # every client socket is owned by the listening sshd; the per-connection sshd
                # process (PID from the event log) is what parents the session's work
                conn_pid = conn.get("pid")
                tree = ({conn_pid} | self._descendants(conn_pid)) if conn_pid else set()
                sessions.append({
                    "ip": ip, "port": port, "client": self.label_ip(ip), "user": conn.get("user"),
                    "key": self.label_key(conn.get("fp")), "key_type": conn.get("key_type"),
                    "start": conn.get("start"), "pid": conn_pid, "commands": sorted(cmds, key=lambda c: c["start"]),
                    "tunnel": bool(tunnel_pids & tree),
                    "agents": sorted({a for pid in tree for a in (tunnel_agents or {}).get(pid, [])}),
                })
            day_ago = now - timedelta(hours=24)
            recent = [c for c in self.conn_list if datetime.fromisoformat(c["start"]) >= day_ago]
            hours = []
            for h in range(23, -1, -1):
                lo = (now - timedelta(hours=h)).replace(minute=0, second=0, microsecond=0)
                hi = lo + timedelta(hours=1)
                hours.append({"hour": iso(lo), "count": sum(lo <= datetime.fromisoformat(c["start"]) < hi for c in recent)})
            history = [{**c, "client": self.label_ip(c["ip"]), "key": self.label_key(c["fp"])}
                       for c in list(self.conn_list)[-200:]]
            peers = []
            for p in self.peers.values():
                ips = {p.get("ip"), p.get("resolved_ip")} - {None}
                last = next((c for c in reversed(self.conn_list) if c["ip"] in ips), None)
                peers.append({**p, "connected": any(s["ip"] in ips for s in sessions),
                              "last_login": last["start"] if last else None, "stats": self._client_for(p)})
            known_machines = {(p.get(k) or "").lower() for p in self.peers.values() for k in ("host", "name")}
            other_clients = [{k: v for k, v in c.items() if k != "ts"} for m, c in self.clients.items()
                             if m not in known_machines and time.time() - c["ts"] < 30]
            failures = [f for f in self.failures if datetime.fromisoformat(f["ts"]) >= day_ago]
            server_events = list(self.server_events)[-5:]
            # addresses that logged in but aren't in the computer list: offered as "add this computer"
            # known: in the computer list, its SSH Manager app reports in from that address, or it logged
            # in with a key that has a label here (keys allowed through the app get one)
            known = {ip for p in self.peers.values() for ip in (p.get("ip"), p.get("resolved_ip")) if ip}
            known |= {c.get("ip") for c in list(self.clients.values()) if c.get("ip")}
            labelled = {fp for fp, label in self.config.get("keys", {}).items() if label}
            unknown: dict[str, dict] = {}
            for c in self.conn_list:
                if c["ip"] not in known and c["ip"] not in ("127.0.0.1", "::1") and c["fp"] not in labelled:
                    u = unknown.setdefault(c["ip"], {"ip": c["ip"], "count": 0, "last": None, "user": c["user"], "fp": c["fp"]})
                    u["count"] += 1
                    u["last"] = c["start"]

        try:
            svc = psutil.win_service_get("sshd").as_dict()
            sshd = {"installed": True, "status": svc["status"], "start_type": svc["start_type"], "pid": svc["pid"]}
        except Exception:
            sshd = {"installed": False, "status": "missing", "start_type": None, "pid": None}
        sshd["listening"] = any(c.status == psutil.CONN_LISTEN and c.laddr.port == 22
                                for c in psutil.net_connections("tcp"))
        vm = psutil.virtual_memory()
        disks = []
        for part in self._partitions():
            try:
                u = psutil.disk_usage(part.mountpoint)
                disks.append({"mount": part.mountpoint, "total": u.total, "free": u.free, "percent": u.percent})
            except OSError:
                pass
        ips = [a.address for addrs in psutil.net_if_addrs().values() for a in addrs
               if a.family.name == "AF_INET" and not a.address.startswith(("127.", "169.254."))]
        return {
            "now": iso(now),
            "host": {"name": self.host_name, "computer_name": os.environ.get("COMPUTERNAME", ""), "ips": ips,
                     "uptime_s": int(time.time() - psutil.boot_time()), "cpu": psutil.cpu_percent(None),
                     "ram": {"total": vm.total, "used": vm.used, "percent": vm.percent}, "disks": disks},
            "elevated": bool(ctypes.windll.shell32.IsUserAnAdmin()),
            "sshd": sshd, "sessions": sessions, "peers": peers,
            "desktop_commander": self.desktop_commander(),
            "logins_24h": len(recent), "failures_24h": failures[-50:], "hours": hours,
            "history": history, "server_events": server_events,
            "unknown_clients": sorted(unknown.values(), key=lambda u: u["last"], reverse=True),
            "other_apps": other_clients,
        }

    def report_client(self, stats: dict, via: str):
        """Stats an SSH Manager app sends about the computer it runs on."""
        with self.lock:
            self.clients[stats["machine"].lower()] = {**stats, "via": via, "ts": time.time()}

    def _client_for(self, peer: dict) -> dict | None:
        now = time.time()
        for key in (peer.get("host"), peer.get("name")):
            c = self.clients.get((key or "").lower())
            if c and now - c["ts"] < 30:
                return {k: v for k, v in c.items() if k != "ts"}
        return None

    def _conn_for_pid(self, pid: int) -> dict | None:
        """The open SSH session that owns process pid (e.g. the sshd end of a tunnel)."""
        with self.lock:
            chain, p = [], pid
            while p and p not in chain:
                chain.append(p)
                p = self.procs.get(p, ("", 0, 0))[1]
            for attempt in range(2):
                for conn in reversed(self.conn_list):
                    if conn["pid"] in chain and not conn["end"]:
                        return conn
                if attempt == 0:
                    self._poll_events()   # a brand-new session's login may not be read from the log yet
        return None

    def label_for_pid(self, pid: int) -> str | None:
        """Name of the SSH client whose session owns process pid."""
        conn = self._conn_for_pid(pid)
        return self.label_ip(conn["ip"]) if conn else None

    def ip_for_pid(self, pid: int) -> str | None:
        conn = self._conn_for_pid(pid)
        return conn["ip"] if conn else None

    def key_usage(self) -> dict[str, dict]:
        """fingerprint -> last login with it, and whether a session using it is open right now."""
        open_ports = {(norm_ip(s.raddr.ip), s.raddr.port) for s in self._ssh_sockets()}
        out: dict[str, dict] = {}
        with self.lock:
            for c in self.conn_list:
                if c["fp"]:
                    u = out.setdefault(c["fp"], {"last": None, "client": None, "open": False})
                    u["last"], u["client"] = c["start"], self.label_ip(c["ip"])
                    u["open"] = u["open"] or (c["ip"], c["port"]) in open_ports
        return out

    def _descendants(self, pid: int) -> set[int]:
        out, stack = set(), [pid]
        children: dict[int, list[int]] = {}
        for p, (_, ppid, _) in self.procs.items():
            children.setdefault(ppid, []).append(p)
        while stack:
            for c in children.get(stack.pop(), []):
                if c not in out:
                    out.add(c)
                    stack.append(c)
        return out

    def _children(self, pid: int) -> list[str]:
        names = [self.procs[p][0] for p in self._descendants(pid) if p in self.procs]
        return sorted({n for n in names if n.lower() not in IGNORED_CHILDREN})
