"""SSH Manager server: a dashboard for the OpenSSH server on this Windows PC (status, sessions,
command activity, file transfer, settings, updates).

It listens on 127.0.0.1 only. Other computers reach it through an SSH port forward (the SSH Manager
app does this), and every request must carry the access token, which clients read over SSH.

    server.py                 run the server
    server.py --client-info   print the version, port, access token and app secret (used by the app)
    server.py --print-token   print the access token
    server.py --login-url     print a one-time login URL for a browser on this PC
"""
import hmac
import logging
import os
import socket
import sys
import threading
import time
from logging.handlers import RotatingFileHandler

from sshmgr import __version__
from sshmgr import cfgfile
from sshmgr import home as home_mod

ROOT = home_mod.install_folder()
RES = home_mod.resources()
HOME = home_mod.resolve(ROOT)
DATA = HOME / "data"
TOKEN = home_mod.token(HOME)
APP_SECRET = home_mod.app_secret(HOME)
CONFIG_PATH = HOME / "config.json"
config, CONFIG_PROBLEM = cfgfile.load(CONFIG_PATH, RES / "config.example.json")
PORT = config.get("port", 8765)

if "--print-token" in sys.argv:
    print(TOKEN)
    sys.exit(0)
if "--client-info" in sys.argv:
    print(f"SSHMGR-VERSION:{__version__}\nSSHMGR-PORT:{PORT}\nSSHMGR-TOKEN:{TOKEN}\nSSHMGR-APPSECRET:{APP_SECRET}")
    sys.exit(0)
if "--login-url" in sys.argv:
    print(f"http://127.0.0.1:{PORT}/login?token={TOKEN}")
    sys.exit(0)

LOG_MAX = 5 * 1024 * 1024
LOG_BACKUPS = 3


class SafeRotatingHandler(RotatingFileHandler):
    """Rotates server.log at LOG_MAX; if the file can't be renamed (another process has it open), keeps
    writing and tries again later instead of losing log lines."""
    retry_at = 0.0

    def shouldRollover(self, record):
        return time.monotonic() >= self.retry_at and super().shouldRollover(record)

    def doRollover(self):
        try:
            super().doRollover()
        except OSError:
            self.retry_at = time.monotonic() + 600
            if self.stream is None:
                self.stream = self._open()


class RotatingStream:
    """stdout/stderr replacement for a server without a console: console.log, rotated like server.log."""

    def __init__(self, path):
        self.path, self.lock, self.retry_at = path, threading.Lock(), 0.0
        self.f = open(path, "a", encoding="utf-8", errors="replace", buffering=1)

    def write(self, s: str) -> int:
        with self.lock:
            try:
                n = self.f.write(s)
                if self.f.tell() > LOG_MAX and time.monotonic() >= self.retry_at:
                    self._rotate()
                return n
            except (OSError, ValueError):
                return len(s)

    def _rotate(self):
        self.f.close()
        try:
            for i in range(LOG_BACKUPS - 1, 0, -1):
                src = self.path.with_name(f"{self.path.name}.{i}")
                if src.exists():
                    os.replace(src, self.path.with_name(f"{self.path.name}.{i + 1}"))
            os.replace(self.path, self.path.with_name(f"{self.path.name}.1"))
        except OSError:
            self.retry_at = time.monotonic() + 600
        self.f = open(self.path, "a", encoding="utf-8", errors="replace", buffering=1)

    def flush(self):
        try:
            self.f.flush()
        except (OSError, ValueError):
            pass

    def isatty(self) -> bool:
        return False


# no console (pythonw, or started hidden): keep anything printed in a log instead of losing it
if sys.stdout is None or sys.stderr is None or "--log-console" in sys.argv:
    sys.stdout = sys.stderr = RotatingStream(DATA / "console.log")

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s",
                    handlers=[SafeRotatingHandler(DATA / "server.log", maxBytes=LOG_MAX, backupCount=LOG_BACKUPS,
                                                  encoding="utf-8", delay=True)])
log = logging.getLogger("sshmgr")

# the token, config.json and plugins must not be readable or changeable by other accounts (before the
# plugins are checked and loaded below)
try:
    home_mod.secure(HOME, ROOT, log)
except Exception:
    log.exception("checking the home folder's permissions failed")

import asyncio  # noqa: E402

import uvicorn  # noqa: E402
from fastapi import FastAPI, HTTPException, Request  # noqa: E402
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse, RedirectResponse  # noqa: E402
from fastapi.staticfiles import StaticFiles  # noqa: E402
from pydantic import BaseModel, Field  # noqa: E402

from sshmgr import agents as agents_mod, desktop, plugins, winsec  # noqa: E402
from sshmgr.activity import ActivityLog  # noqa: E402
from sshmgr.files import _peer_process, client_label, make_router, transfers_active, tunnel_pid  # noqa: E402
from sshmgr.monitor import Monitor  # noqa: E402
from sshmgr.protect import Protect  # noqa: E402
from sshmgr.settings import make_router as make_settings_router  # noqa: E402
from sshmgr.runner import Runner  # noqa: E402
from sshmgr.tools import make_router as make_tools_router  # noqa: E402
from sshmgr.pairing import make_router as make_pairing_router  # noqa: E402
from sshmgr import wol  # noqa: E402
from sshmgr.updater import Updater  # noqa: E402

# any local port: the app may forward from a different local port than this server's
ALLOWED_HOSTNAMES = {"127.0.0.1", "localhost", "[::1]"}
OPEN_PATHS = {"/api/ping", "/login", "/favicon.ico"}
COOKIE = "sshmgr_token"

agents_mod.configure(APP_SECRET)
activity = ActivityLog(DATA / "activity.jsonl")
monitor = Monitor(config, activity)
updater = Updater(config, DATA, activity, busy=lambda: runner.busy() or transfers_active() > 0)
runner = Runner(activity, DATA)
agents = agents_mod.Agents()
agents.machine_id_of = lambda name: (monitor.clients.get(name.strip().lower()) or {}).get("machine_id") or None
protect = Protect(HOME, ROOT)
app = FastAPI(title="SSH Manager", docs_url=None, redoc_url=None)

NOT_LOGGED_IN = """<!doctype html><meta charset="utf-8"><title>SSH Manager</title>
<body style="font:15px system-ui;background:#0d1117;color:#e6edf3;display:grid;place-items:center;height:100vh;margin:0">
<div style="max-width:460px;line-height:1.5"><h2>SSH Manager</h2>
<p>Open it with the SSH Manager app.</p></div>"""


def split_host(request: Request) -> tuple[str, str]:
    """(host name, port) from the Host header; port "80" when it names none."""
    host = request.headers.get("host", "")
    if host.startswith("["):
        name, _, rest = host.partition("]")
        return name + "]", rest[1:] if rest.startswith(":") else "80"
    name, sep, port = host.rpartition(":")
    return (name, port) if sep else (host, "80")


def authorized(request: Request) -> bool:
    """The token, from the X-SSHMgr-Token header or the cookie for this port. Several PCs' dashboards are
    all on "localhost" (one forwarded port each) and browsers send every localhost cookie to every port,
    so only the cookie named for the port in the Host header counts: another PC's token never does."""
    supplied = [request.headers.get("x-sshmgr-token") or "", request.cookies.get(f"{COOKIE}_{split_host(request)[1]}") or ""]
    return any(hmac.compare_digest(s.encode(), TOKEN.encode()) for s in supplied if s)


@app.middleware("http")
async def guard(request: Request, call_next):
    # Host check stops DNS-rebinding pages; the token keeps out anything on this PC (or a web page)
    # that didn't get it over SSH; the custom header on writes stops cross-site form posts.
    if split_host(request)[0] not in ALLOWED_HOSTNAMES:
        return JSONResponse({"detail": "Forbidden host"}, status_code=403)
    if request.url.path not in OPEN_PATHS and not authorized(request):
        if request.url.path == "/":
            return HTMLResponse(NOT_LOGGED_IN, status_code=401)
        return JSONResponse({"detail": "Not logged in"}, status_code=401)
    if request.method not in ("GET", "HEAD") and request.headers.get("x-sshmgr") != "1":
        return JSONResponse({"detail": "Missing X-SSHMgr header"}, status_code=403)
    if agents_mod.is_agent(request):
        # an AI agent: this PC's AI access setting applies, and its work is labelled with its name
        agent = agents_mod.agent_of(request)
        if reason := agents_mod.refusal(request, config):
            return JSONResponse({"detail": reason}, status_code=403)
        agents_mod.current.set(agent)
        try:
            await note_agent(request, agent)
        except Exception:
            log.exception("noting agent %s failed", agent)
    response = await call_next(request)
    if request.url.path.startswith("/static/"):
        # the page files change with every update: always ask (cheap, ETag) instead of using a stale copy
        response.headers["Cache-Control"] = "no-cache"
    return response


class DrainBody:
    """Reads the rest of a request body (up to DRAIN_MAX) before an answer that didn't need it is sent
    (a refusal, an "already exists"): closing a socket with unread data makes Windows reset the connection,
    and the client would get "connection reset" instead of the error message."""
    DRAIN_MAX = 8 * 1024 * 1024

    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope["type"] != "http":
            return await self.app(scope, receive, send)
        length = dict(scope.get("headers") or []).get(b"content-length", b"0")
        try:
            pending = 0 < int(length) <= self.DRAIN_MAX
        except ValueError:
            pending = False
        state = {"more": pending}

        async def recv():
            msg = await receive()
            if msg["type"] != "http.request" or not msg.get("more_body"):
                state["more"] = False
            return msg

        async def snd(msg):
            if msg["type"] == "http.response.start":
                while state["more"]:
                    await recv()
            await send(msg)

        await self.app(scope, recv, snd)


app.add_middleware(DrainBody)   # outermost: added after the guard


@app.get("/api/ping")
async def ping():
    return {"app": "ssh-manager", "version": __version__}


@app.get("/login")
def login(request: Request, token: str = ""):
    if not hmac.compare_digest(token.encode(), TOKEN.encode()):
        return HTMLResponse(NOT_LOGGED_IN, status_code=401)
    response = RedirectResponse("/", status_code=303)
    response.set_cookie(f"{COOKIE}_{split_host(request)[1]}", TOKEN, httponly=True, samesite="strict",
                        max_age=400 * 24 * 3600, path="/")
    return response


@app.get("/api/status")
def status():
    s = monitor.status(agents.tunnel_agents())
    return {**s, "plugins": loaded_plugins, "version": __version__, "update": updater.snapshot(),
            "activity_24h": activity.counts_24h(), "agents": agents.recent(), "ai_access": agents_mod.level(config),
            "security": {"home_private": home_mod.security["private"], "warnings": home_mod.security["warnings"]},
            "config_problem": CONFIG_PROBLEM}


def who(request: Request) -> str:
    """Who is calling: the computer, or "<agent> (<computer>)" for an AI agent."""
    label = client_label(request, PORT, monitor)
    agent = agents_mod.agent_of(request)
    return f"{agent} ({label})" if agent else label


def address_of(request: Request) -> str | None:
    """IP address of the SSH session this request came through (None for a program on this PC)."""
    pid = tunnel_pid(request, PORT, monitor)
    return monitor.ip_for_pid(pid) if pid else None


_noted: dict[tuple[str, int], float] = {}


async def note_agent(request: Request, agent: str):
    """Remembers which agent works through which connection (for live sessions), every 15 s at most."""
    key = (agent, request.client.port if request.client else 0)
    if time.monotonic() - _noted.get(key, -1e9) < 15:
        return
    if len(_noted) > 500:
        _noted.clear()
    _noted[key] = time.monotonic()

    def look():
        pid = tunnel_pid(request, PORT, monitor)
        agents.note(agent, client_label(request, PORT, monitor), pid)
    await asyncio.to_thread(look)


@app.get("/api/whoami")
def whoami(request: Request):
    adapters = wol.own_macs()
    return {"viewer": who(request), "host": monitor.host_name, "computer": os.environ.get("COMPUTERNAME", ""), "version": __version__,
            "mac": adapters[0]["mac"] if adapters else None, "ips": [ip for a in adapters for ip in a["ips"]]}


@app.get("/api/activity")
def get_activity(after: int = 0, limit: int = 500, kind: str | None = None):
    """Entries after id `after`. `log_id` changes when ids start over (the log was deleted): a page that
    sees a different log_id, or a last_id below its `after`, should start again from after=0."""
    return {"items": activity.list(after, limit, kind), "log_id": activity.log_id, "last_id": activity.last_id()}


class ClientStats(BaseModel):
    machine: str = Field(min_length=1, max_length=64)
    cpu: float | None = None
    ram_total: int | None = None
    ram_used: int | None = None
    disks: list[dict] = []
    battery: int | None = None
    charging: bool | None = None
    uptime_s: int | None = None
    app_version: str = ""
    macs: list[str] = []
    user: str = ""
    features: list[str] = []   # e.g. "pair": its app can approve pairing requests
    machine_id: str = Field("", max_length=64)   # random id per app install (tells same-named computers apart)


@app.post("/api/clients/report")
def client_report(request: Request, body: ClientStats):
    stats = body.model_dump()
    if (pid := tunnel_pid(request, PORT, monitor)) and (ip := monitor.ip_for_pid(pid)):
        stats["ip"] = ip   # so sessions from that address get the app's computer name
    monitor.report_client(stats, who(request))
    return {"ok": True}


@app.get("/api/update")
def update_state():
    return updater.snapshot()


@app.post("/api/update/check")
def update_check():
    return updater.check()


@app.post("/api/update/install")
def update_install(request: Request):
    try:
        return updater.install(reason=f"started by {who(request)}")
    except RuntimeError as e:
        raise HTTPException(400, str(e))


app.include_router(make_router(config, activity, who, protect))
app.include_router(make_settings_router(config, CONFIG_PATH, DATA, activity, monitor, who))
app.include_router(make_tools_router(config, activity, runner, who, agents, protect))
app.include_router(agents_mod.make_router(config, agents, activity, who))
app.include_router(make_pairing_router(activity, lambda: monitor.host_name, who, address_of))
loaded_plugins = plugins.load(plugins.PluginContext(config, HOME, monitor, activity, app))
app.mount("/static", StaticFiles(directory=RES / "static"), name="static")


# ---- restart: applies a hand-edited config.json or a new/changed plugin ----
RESTART_SCRIPT = r"""
$ErrorActionPreference = 'SilentlyContinue'
Wait-Process -Id {pid} -Timeout 15            # this server quits right after answering
Start-Process -FilePath '{exe}' -ArgumentList {args} -WorkingDirectory '{cwd}' -WindowStyle Hidden
"""


def restart_self(delay: float = 0.5):
    """Starts a helper that waits for this process to end and starts the server again (same session and
    rights), then ends this process."""
    exe = sys.executable
    args = ["--log-console"] if home_mod.FROZEN else [str(ROOT / "server.py")]
    script = RESTART_SCRIPT.format(pid=os.getpid(), exe=exe.replace("'", "''"), cwd=str(ROOT).replace("'", "''"),
                                   args=",".join("'" + a.replace("'", "''") + "'" for a in args))

    def later():
        import base64
        import subprocess
        time.sleep(delay)   # let an answer reach the page first
        try:
            subprocess.Popen(["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                              "-EncodedCommand", base64.b64encode(script.encode("utf-16-le")).decode()],
                             creationflags=0x08000000 | 0x00000200)   # no window, own process group
        except OSError:
            log.exception("couldn't start the restart helper")
        time.sleep(0.3)
        logging.shutdown()
        os._exit(0)

    threading.Thread(target=later, daemon=True).start()


@app.post("/api/server/restart")
def restart_server(request: Request):
    """Restarts this server (same session and rights). Refuses when config.json can't be used, since the
    server would then start with the last good settings instead."""
    if problem := cfgfile.problem(CONFIG_PATH):
        raise HTTPException(400, f"config.json can't be used, so the server was not restarted: {problem}")
    activity.add("setup", action="server-restart", client=who(request))
    restart_self()
    return {"restarting": True}


# ---- moving from session 0 to the desktop session (R12) ----
# Started over SSH while nobody was signed in, the server runs in session 0 (no screen, no clipboard).
# When the user signs in, the logon task starts a new instance in the desktop session; it finds the port
# taken, and asks the old instance (with the token, which it reads from the same home folder) to quit.

def _same_program(pid: int) -> bool:
    import psutil
    try:
        p = psutil.Process(pid)
        if os.path.normcase(p.exe()) == os.path.normcase(sys.executable):
            return True
        if not home_mod.FROZEN:
            here = os.path.normcase(str(ROOT / "server.py"))
            return any(os.path.normcase(a) == here for a in p.cmdline())
    except psutil.Error:
        pass
    return False


@app.post("/api/server/handover")
def handover(request: Request):
    """Called by a new instance of this server in the signed-in user's desktop session: this one (in
    session 0) quits so that one can take over. Refused while runs or transfers are going on."""
    from sshmgr.files import transfers_active
    if desktop.in_desktop_session():
        raise HTTPException(409, "This server already runs in a desktop session.")
    found, pid, _ = _peer_process(request, PORT)
    if not found or not pid or not _same_program(pid) or not winsec.session_of(pid):
        raise HTTPException(403, "Only a new SSH Manager server in a desktop session can take over.")
    if runner.busy() or transfers_active():
        raise HTTPException(409, "Busy: a run or a file transfer is still going on.")
    log.info("handing over to the server in session %s (pid %s)", winsec.session_of(pid), pid)
    activity.add("setup", action="server-handover", client=monitor.host_name, to_session=winsec.session_of(pid))

    def later():
        time.sleep(0.5)
        logging.shutdown()
        os._exit(0)
    threading.Thread(target=later, daemon=True).start()
    return {"ok": True}


def listening_pid() -> int | None:
    import psutil
    try:
        for c in psutil.net_connections("tcp"):
            if c.status == psutil.CONN_LISTEN and c.laddr and c.laddr.port == PORT and c.pid:
                return c.pid
    except psutil.Error:
        pass
    return None


def take_over() -> bool:
    """In a desktop session, with a server of ours in session 0 on the port: ask it to quit and wait for the
    port. Retries for up to 10 minutes while it is busy. False when this instance should just exit."""
    import http.client
    import json
    if not desktop.in_desktop_session():
        return False
    pid = listening_pid()
    if not pid or winsec.session_of(pid) != 0:
        return False
    log.info("a server in session 0 (pid %s) has port %s; asking it to hand over to this desktop session", pid, PORT)
    until = time.monotonic() + 600
    while time.monotonic() < until:
        try:
            c = http.client.HTTPConnection("127.0.0.1", PORT, timeout=10)
            c.request("POST", "/api/server/handover", body=b"",
                      headers={"Host": f"127.0.0.1:{PORT}", "X-SSHMgr-Token": TOKEN, "X-SSHMgr": "1"})
            r = c.getresponse()
            body = r.read().decode("utf-8", errors="replace")
            c.close()
        except OSError as e:
            log.warning("handover request failed: %s", e)
            return False
        if r.status == 200:
            for _ in range(60):
                if not port_in_use():
                    log.info("took over from the session-0 server")
                    return True
                time.sleep(0.25)
            log.warning("the session-0 server agreed to hand over but the port is still in use")
            return False
        if r.status != 409:
            try:
                body = json.loads(body).get("detail", body)
            except ValueError:
                pass
            log.warning("the session-0 server refused to hand over (%s): %s", r.status, body)
            return False
        time.sleep(30)   # busy: try again
    return False


@app.get("/favicon.ico", include_in_schema=False)
def favicon():
    return FileResponse(RES / "static" / "favicon.ico", media_type="image/x-icon")


@app.get("/")
def index():
    # the version in the script/style URLs makes the app load the new files right after an update
    html = (RES / "static" / "index.html").read_text(encoding="utf-8")
    for name in ("app.js", "style.css"):
        html = html.replace(f'"/static/{name}"', f'"/static/{name}?v={__version__}"')
    return HTMLResponse(html, headers={"Cache-Control": "no-store"})


def port_in_use() -> bool:
    with socket.socket() as s:
        s.settimeout(3)
        return s.connect_ex(("127.0.0.1", PORT)) == 0


# ---- staying reachable (R1) ----
# With Windows' default (Proactor) event loop, one failed accept() - e.g. WinError 64 when an SSH tunnel
# drops a connection before it is accepted - closes the listening socket for good: the process keeps
# running but nobody can connect. So the server uses the selector event loop (a failed accept is logged and
# ignored there), restarts itself if the loop still reports a failed accept, and a watchdog restarts it
# when it stops answering on its own port.

def _loop_error(loop, context):
    exc = context.get("exception")
    if "Accept failed" in str(context.get("message", "")):
        log.error("the listening socket failed (%s); restarting the server", exc)
        restart_self(delay=0)
        return
    loop.default_exception_handler(context)


def _watchdog():
    import http.client
    failures = 0
    while True:
        time.sleep(30)
        try:
            c = http.client.HTTPConnection("127.0.0.1", PORT, timeout=10)
            c.request("GET", "/api/ping", headers={"Host": f"127.0.0.1:{PORT}"})
            ok = c.getresponse().status == 200
            c.close()
        except OSError:
            ok = False
        failures = 0 if ok else failures + 1
        if failures >= 3:
            log.error("the server stopped answering on port %s; restarting it", PORT)
            restart_self(delay=0)
            return


class Server(uvicorn.Server):
    async def startup(self, sockets=None):
        asyncio.get_running_loop().set_exception_handler(_loop_error)
        await super().startup(sockets)
        if self.started:
            threading.Thread(target=_watchdog, daemon=True, name="watchdog").start()


def serve():
    loop = asyncio.SelectorEventLoop if sys.platform == "win32" else "auto"
    Server(uvicorn.Config(app, host="127.0.0.1", port=PORT, log_level="warning", log_config=None, loop=loop)).run()


if __name__ == "__main__":
    if port_in_use() and not take_over():
        print(f"SSH Manager is already running on http://localhost:{PORT}")
        sys.exit(0)
    log.info("starting %s; home = %s; session %s; elevated %s", __version__, HOME, winsec.session_of(os.getpid()),
             winsec.is_elevated())
    if CONFIG_PROBLEM:
        log.error("%s", CONFIG_PROBLEM)
    else:
        cfgfile.backup(CONFIG_PATH, config)
    monitor.start()
    updater.start()
    serve()
