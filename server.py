"""SSH Manager server: a dashboard for the OpenSSH server on this Windows PC (status, sessions,
command activity, file transfer, settings, updates).

It listens on 127.0.0.1 only. Other computers reach it through an SSH port forward (the SSH Manager
app does this), and every request must carry the access token, which clients read over SSH.

    server.py                 run the server
    server.py --client-info   print the version, port and access token (used by the app)
    server.py --print-token   print the access token
    server.py --login-url     print a one-time login URL for a browser on this PC
"""
import hmac
import json
import logging
import os
import socket
import sys

from sshmgr import __version__
from sshmgr import home as home_mod

ROOT = home_mod.install_folder()
RES = home_mod.resources()
HOME = home_mod.resolve(ROOT)
DATA = HOME / "data"
TOKEN = home_mod.token(HOME)
CONFIG_PATH = HOME / "config.json"
config = json.loads(CONFIG_PATH.read_text(encoding="utf-8-sig"))
PORT = config.get("port", 8765)

if "--print-token" in sys.argv:
    print(TOKEN)
    sys.exit(0)
if "--client-info" in sys.argv:
    print(f"SSHMGR-VERSION:{__version__}\nSSHMGR-PORT:{PORT}\nSSHMGR-TOKEN:{TOKEN}")
    sys.exit(0)
if "--login-url" in sys.argv:
    print(f"http://localhost:{PORT}/login?token={TOKEN}")
    sys.exit(0)

# no console (pythonw, or started hidden): keep anything printed in a log instead of losing it
if sys.stdout is None or sys.stderr is None or "--log-console" in sys.argv:
    sys.stdout = sys.stderr = open(DATA / "console.log", "a", encoding="utf-8", buffering=1)

logging.basicConfig(filename=DATA / "server.log", level=logging.INFO,
                    format="%(asctime)s %(levelname)s %(name)s: %(message)s")

import uvicorn  # noqa: E402
from fastapi import FastAPI, HTTPException, Request  # noqa: E402
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse, RedirectResponse  # noqa: E402
from fastapi.staticfiles import StaticFiles  # noqa: E402
from pydantic import BaseModel, Field  # noqa: E402

from sshmgr import agents as agents_mod, plugins  # noqa: E402
from sshmgr.activity import ActivityLog  # noqa: E402
from sshmgr.files import client_label, make_router, tunnel_pid  # noqa: E402
from sshmgr.monitor import Monitor  # noqa: E402
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

activity = ActivityLog(DATA / "activity.jsonl")
monitor = Monitor(config, activity)
updater = Updater(config, DATA, activity)
runner = Runner(activity, DATA)
agents = agents_mod.Agents()
app = FastAPI(title="SSH Manager", docs_url=None, redoc_url=None)

NOT_LOGGED_IN = """<!doctype html><meta charset="utf-8"><title>SSH Manager</title>
<body style="font:15px system-ui;background:#0d1117;color:#e6edf3;display:grid;place-items:center;height:100vh;margin:0">
<div style="max-width:460px;line-height:1.5"><h2>SSH Manager</h2>
<p>Open it with the SSH Manager app.</p></div>"""


def authorized(request: Request) -> bool:
    # one cookie per local port (the app may show several PCs, all on "localhost"), or a header
    supplied = [v for k, v in request.cookies.items() if k.startswith(COOKIE)]
    supplied.append(request.headers.get("x-sshmgr-token") or "")
    return any(hmac.compare_digest(s.encode(), TOKEN.encode()) for s in supplied if s)


@app.middleware("http")
async def guard(request: Request, call_next):
    # Host check stops DNS-rebinding pages; the token keeps out anything on this PC (or a web page)
    # that didn't get it over SSH; the custom header on writes stops cross-site form posts.
    if request.headers.get("host", "").rsplit(":", 1)[0] not in ALLOWED_HOSTNAMES:
        return JSONResponse({"detail": "Forbidden host"}, status_code=403)
    if request.url.path not in OPEN_PATHS and not authorized(request):
        if request.url.path == "/":
            return HTMLResponse(NOT_LOGGED_IN, status_code=401)
        return JSONResponse({"detail": "Not logged in"}, status_code=401)
    if request.method not in ("GET", "HEAD") and request.headers.get("x-sshmgr") != "1":
        return JSONResponse({"detail": "Missing X-SSHMgr header"}, status_code=403)
    if agent := agents_mod.agent_of(request):
        # an AI agent: this PC's AI access setting applies, and its work is labelled with its name
        if reason := agents_mod.refusal(request, config):
            return JSONResponse({"detail": reason}, status_code=403)
        agents_mod.current.set(agent)
        await note_agent(request, agent)
    response = await call_next(request)
    if request.url.path.startswith("/static/"):
        # the page files change with every update: always ask (cheap, ETag) instead of using a stale copy
        response.headers["Cache-Control"] = "no-cache"
    return response


@app.get("/api/ping")
def ping():
    return {"app": "ssh-manager", "version": __version__}


@app.get("/login")
def login(request: Request, token: str = ""):
    if not hmac.compare_digest(token.encode(), TOKEN.encode()):
        return HTMLResponse(NOT_LOGGED_IN, status_code=401)
    port = request.headers.get("host", "").rsplit(":", 1)[-1]
    response = RedirectResponse("/", status_code=303)
    response.set_cookie(f"{COOKIE}_{port}", TOKEN, httponly=True, samesite="strict", max_age=400 * 24 * 3600)
    return response


@app.get("/api/status")
def status():
    s = monitor.status(agents.tunnel_agents())
    return {**s, "plugins": loaded_plugins, "version": __version__, "update": updater.snapshot(),
            "activity_24h": activity.counts_24h(), "agents": agents.recent(), "ai_access": agents_mod.level(config)}


def who(request: Request) -> str:
    """Who is calling: the computer, or "<agent> (<computer>)" for an AI agent."""
    label = client_label(request, PORT, monitor)
    agent = agents_mod.agent_of(request)
    return f"{agent} ({label})" if agent else label


_noted: dict[tuple[str, int], float] = {}


async def note_agent(request: Request, agent: str):
    """Remembers which agent works through which connection (for live sessions), every 15 s at most."""
    import asyncio, time
    key = (agent, request.client.port if request.client else 0)
    if time.time() - _noted.get(key, 0) < 15:
        return
    if len(_noted) > 500:
        _noted.clear()
    _noted[key] = time.time()

    def look():
        pid = tunnel_pid(request, PORT)
        agents.note(agent, client_label(request, PORT, monitor), pid)
    await asyncio.to_thread(look)


@app.get("/api/whoami")
def whoami(request: Request):
    adapters = wol.own_macs()
    return {"viewer": who(request), "host": monitor.host_name, "computer": os.environ.get("COMPUTERNAME", ""), "version": __version__,
            "mac": adapters[0]["mac"] if adapters else None, "ips": [ip for a in adapters for ip in a["ips"]]}


@app.get("/api/activity")
def get_activity(after: int = 0, limit: int = 500, kind: str | None = None):
    return {"items": activity.list(after, limit, kind)}


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


@app.post("/api/clients/report")
def client_report(request: Request, body: ClientStats):
    stats = body.model_dump()
    if (pid := tunnel_pid(request, PORT)) and (ip := monitor.ip_for_pid(pid)):
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


app.include_router(make_router(config, activity, who))
app.include_router(make_settings_router(config, CONFIG_PATH, DATA, activity, monitor, who))
app.include_router(make_tools_router(config, activity, runner, who, agents))
app.include_router(agents_mod.make_router(config, agents, activity, who))
app.include_router(make_pairing_router(activity, lambda: monitor.host_name, who))
loaded_plugins = plugins.load(plugins.PluginContext(config, HOME, monitor, activity, app))
app.mount("/static", StaticFiles(directory=RES / "static"), name="static")


# ---- restart: applies a hand-edited config.json or a new/changed plugin ----
RESTART_SCRIPT = r"""
$ErrorActionPreference = 'SilentlyContinue'
Wait-Process -Id {pid} -Timeout 15            # this server quits right after answering
Start-Process -FilePath '{exe}' -ArgumentList {args} -WorkingDirectory '{cwd}' -WindowStyle Hidden
"""


@app.post("/api/server/restart")
def restart_server(request: Request):
    """Restarts this server (same session and rights). Refuses when config.json isn't valid JSON, since
    the server couldn't start again with it."""
    try:
        json.loads(CONFIG_PATH.read_text(encoding="utf-8-sig"))
    except ValueError as e:
        raise HTTPException(400, f"config.json is not valid JSON, so the server was not restarted: {e}")
    activity.add("setup", action="server-restart", client=who(request))
    exe = sys.executable
    args = ["--log-console"] if home_mod.FROZEN else [str(ROOT / "server.py")]
    script = RESTART_SCRIPT.format(pid=os.getpid(), exe=exe.replace("'", "''"), cwd=str(ROOT).replace("'", "''"),
                                   args=",".join("'" + a.replace("'", "''") + "'" for a in args))

    def later():
        import base64, subprocess, threading, time
        time.sleep(0.5)   # let this answer reach the page first
        subprocess.Popen(["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                          "-EncodedCommand", base64.b64encode(script.encode("utf-16-le")).decode()],
                         creationflags=0x08000000 | 0x00000200)   # no window, own process group
        time.sleep(0.3)
        os._exit(0)

    import threading
    threading.Thread(target=later, daemon=True).start()
    return {"restarting": True}


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
        return s.connect_ex(("127.0.0.1", PORT)) == 0


if __name__ == "__main__":
    if port_in_use():
        print(f"SSH Manager is already running on http://localhost:{PORT}")
        sys.exit(0)
    logging.getLogger("sshmgr").info("starting %s; home = %s", __version__, HOME)
    monitor.start()
    updater.start()
    uvicorn.run(app, host="127.0.0.1", port=PORT, log_level="warning", log_config=None)
