"""SSH Manager server: a dashboard for the OpenSSH server on this Windows PC (status, sessions,
command activity, file transfer, settings).

It listens on 127.0.0.1 only. Other computers reach it through an SSH port forward (the SSH Manager
app does this), and every request must carry the access token, which clients read over SSH.

    python server.py                 run the server
    python server.py --print-token   print the access token
    python server.py --client-info   print port and token for the app (ensure-running.ps1)
    python server.py --login-url     print a one-time login URL for a browser on this PC
"""
import hmac
import json
import logging
import socket
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

from sshmgr import home as home_mod  # noqa: E402

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
    print(f"SSHMGR-PORT:{PORT}\nSSHMGR-TOKEN:{TOKEN}")
    sys.exit(0)
if "--login-url" in sys.argv:
    print(f"http://localhost:{PORT}/login?token={TOKEN}")
    sys.exit(0)

# pythonw (no console) leaves these as None, which crashes uvicorn's logging setup
if sys.stdout is None or sys.stderr is None:
    sys.stdout = sys.stderr = open(DATA / "console.log", "a", encoding="utf-8", buffering=1)

logging.basicConfig(filename=DATA / "server.log", level=logging.INFO,
                    format="%(asctime)s %(levelname)s %(name)s: %(message)s")

import uvicorn  # noqa: E402
from fastapi import FastAPI, Request  # noqa: E402
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse, RedirectResponse  # noqa: E402
from fastapi.staticfiles import StaticFiles  # noqa: E402

from sshmgr import plugins  # noqa: E402
from sshmgr.activity import ActivityLog  # noqa: E402
from sshmgr.files import client_label, make_router  # noqa: E402
from sshmgr.monitor import Monitor  # noqa: E402
from sshmgr.settings import make_router as make_settings_router  # noqa: E402

# any local port: the app may forward from a different local port than this server's
ALLOWED_HOSTNAMES = {"127.0.0.1", "localhost", "[::1]"}
OPEN_PATHS = {"/api/ping", "/login", "/favicon.ico"}
COOKIE = "sshmgr_token"

activity = ActivityLog(DATA / "activity.jsonl")
monitor = Monitor(config, activity)
app = FastAPI(title="SSH Manager", docs_url=None, redoc_url=None)

NOT_LOGGED_IN = """<!doctype html><meta charset="utf-8"><title>SSH Manager</title>
<body style="font:15px system-ui;background:#0d1117;color:#e6edf3;display:grid;place-items:center;height:100vh;margin:0">
<div style="max-width:460px;line-height:1.5"><h2>SSH Manager</h2>
<p>Open it with the SSH Manager app, or on this PC run <code>start.bat</code>, which logs you in.</p></div>"""


def authorized(request: Request) -> bool:
    supplied = request.cookies.get(COOKIE) or request.headers.get("x-sshmgr-token") or ""
    return hmac.compare_digest(supplied.encode(), TOKEN.encode())


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
    return await call_next(request)


@app.get("/api/ping")
def ping():
    return {"app": "ssh-manager"}


@app.get("/login")
def login(token: str = ""):
    if not hmac.compare_digest(token.encode(), TOKEN.encode()):
        return HTMLResponse(NOT_LOGGED_IN, status_code=401)
    response = RedirectResponse("/", status_code=303)
    response.set_cookie(COOKIE, TOKEN, httponly=True, samesite="strict", max_age=400 * 24 * 3600)
    return response


@app.get("/api/status")
def status():
    return {**monitor.status(), "plugins": loaded_plugins}


def who(request: Request) -> str:
    return client_label(request, PORT, monitor)


@app.get("/api/whoami")
def whoami(request: Request):
    return {"viewer": who(request), "host": monitor.host_name}


@app.get("/api/activity")
def get_activity(after: int = 0, limit: int = 500, kind: str | None = None):
    return {"items": activity.list(after, limit, kind)}


app.include_router(make_router(config, activity, monitor))
app.include_router(make_settings_router(config, CONFIG_PATH, DATA, activity, monitor, who))
loaded_plugins = plugins.load(plugins.PluginContext(config, HOME, monitor, activity, app))
app.mount("/static", StaticFiles(directory=ROOT / "static"), name="static")


@app.get("/")
def index():
    return FileResponse(ROOT / "static" / "index.html", headers={"Cache-Control": "no-store"})


def port_in_use() -> bool:
    with socket.socket() as s:
        return s.connect_ex(("127.0.0.1", PORT)) == 0


if __name__ == "__main__":
    if port_in_use():
        print(f"SSH Manager is already running on http://localhost:{PORT}")
        sys.exit(0)
    logging.getLogger("sshmgr").info("starting; home = %s", HOME)
    monitor.start()
    uvicorn.run(app, host="127.0.0.1", port=PORT, log_level="warning")
