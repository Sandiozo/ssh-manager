"""API for the tools: quick actions and the command panel (via Runner), the log viewer, screen preview,
the shared clipboard and Wake-on-LAN."""
import time

from fastapi import APIRouter, HTTPException, Query, Request
from fastapi.responses import Response
from pydantic import BaseModel, Field

from . import desktop, logs, wol
from .activity import ActivityLog
from .runner import Runner


class RunBody(BaseModel):
    script: str = Field(min_length=1, max_length=100_000)
    title: str = Field("", max_length=200)


class ClipBody(BaseModel):
    text: str = Field(max_length=desktop.MAX_TEXT)
    origin: str = Field("", max_length=64)


class WakeBody(BaseModel):
    mac: str = Field(min_length=12, max_length=32)
    ips: list[str] = []


def make_router(config: dict, activity: ActivityLog, runner: Runner, who) -> APIRouter:
    r = APIRouter(prefix="/api")
    clip = desktop.ClipboardWatch()
    last_screen_view: dict[str, float] = {}

    def action(action_id: str) -> dict:
        a = next((a for a in config.get("actions", []) if a.get("id") == action_id), None)
        if not a:
            raise HTTPException(404, "No such action.")
        return a

    # ---- quick actions and the command panel ----
    @r.get("/actions")
    def get_actions():
        return {"actions": config.get("actions", []), "runs": runner.recent()}

    @r.post("/actions/{action_id}/run")
    def run_action(request: Request, action_id: str):
        a = action(action_id)
        run = runner.start(a.get("name", action_id), a.get("script", ""), who(request), action_id, float(a.get("timeout", 0) or 0))
        return run.view()

    @r.post("/run")
    def run_script(request: Request, body: RunBody):
        title = body.title.strip() or body.script.strip().splitlines()[0][:80]
        return runner.start(title, body.script, who(request)).view()

    @r.get("/runs")
    def get_runs():
        return {"runs": runner.recent()}

    @r.get("/runs/{run_id}")
    def get_run(run_id: str, after: int = 0):
        run = runner.get(run_id)
        if not run:
            raise HTTPException(404, "That run is no longer kept.")
        return run.view(after)

    @r.post("/runs/{run_id}/stop")
    def stop_run(run_id: str):
        return {"stopped": runner.stop(run_id)}

    # ---- log viewer ----
    @r.get("/logs")
    def get_logs():
        out = []
        for i, entry in enumerate(config.get("logs", [])):
            path = logs.resolve(entry.get("path", ""))
            out.append({"index": i, "name": entry.get("name", ""), "pattern": entry.get("path", ""),
                        "file": str(path) if path else None, "size": path.stat().st_size if path else None})
        return {"logs": out}

    @r.get("/logs/read")
    def read_log(index: int | None = None, path: str = "", offset: int = -1, file: str = ""):
        if index is not None:
            entries = config.get("logs", [])
            if not 0 <= index < len(entries):
                raise HTTPException(404, "No such log.")
            path = entries[index].get("path", "")
        if not path:
            raise HTTPException(400, "Choose a log.")
        return logs.read(path, offset, file)

    # ---- screen preview ----
    def screen_allowed():
        if not config.get("screen_preview", True):
            raise HTTPException(403, "Screen preview is turned off in this PC's settings.")

    @r.get("/screen/monitors")
    def screen_monitors():
        screen_allowed()
        try:
            return {"monitors": desktop.monitors()}
        except desktop.NotInDesktopSession as e:
            raise HTTPException(409, str(e))

    @r.get("/screen/windows")
    def screen_windows():
        screen_allowed()
        try:
            return {"windows": desktop.windows()}
        except desktop.NotInDesktopSession as e:
            raise HTTPException(409, str(e))

    @r.get("/screen")
    def screen(request: Request, monitor: int = 1, window: int = 0, width: int = Query(1280, ge=160, le=7680),
               quality: int = Query(70, ge=20, le=95)):
        """A whole screen (monitor), or one app window when `window` is given."""
        screen_allowed()
        try:
            jpeg = desktop.capture_window(window, width, quality) if window else desktop.capture(monitor, width, quality)
        except desktop.NotInDesktopSession as e:
            raise HTTPException(409, str(e))
        except desktop.WindowGone as e:
            raise HTTPException(404, str(e))
        viewer = who(request)
        if time.time() - last_screen_view.get(viewer, 0) > 300:   # log a viewing session, not every frame
            activity.add("setup", action="screen-viewed", client=viewer)
        last_screen_view[viewer] = time.time()
        return Response(jpeg, media_type="image/jpeg", headers={"Cache-Control": "no-store"})

    # ---- shared clipboard (text) ----
    @r.get("/clipboard")
    def get_clipboard(since: int = 0):
        if not desktop.in_desktop_session():
            raise HTTPException(409, "The clipboard isn't reachable: SSH Manager is running outside your desktop session.")
        return clip.since(since)

    @r.post("/clipboard")
    def put_clipboard(body: ClipBody):
        try:
            clip.put(body.text, body.origin)
        except desktop.NotInDesktopSession as e:
            raise HTTPException(409, str(e))
        return {"seq": clip.seq}

    # ---- Wake-on-LAN ----
    @r.get("/wol/self")
    def wol_self():
        return {"adapters": wol.own_macs()}

    @r.post("/wol")
    def wake(request: Request, body: WakeBody):
        try:
            wol.send(body.mac, body.ips)
        except ValueError as e:
            raise HTTPException(400, str(e))
        activity.add("setup", action="wake", client=who(request), mac=wol.normalize(body.mac))
        return {"sent": True}

    return r
