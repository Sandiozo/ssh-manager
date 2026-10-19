"""API for the tools: quick actions and the command panel (via Runner), the log viewer, screen preview,
the shared clipboard and Wake-on-LAN."""
import asyncio
import os
import time
from datetime import datetime, timezone
from pathlib import Path

import psutil
from fastapi import APIRouter, HTTPException, Query, Request
from fastapi.responses import FileResponse, Response
from pydantic import BaseModel, Field

from . import agents as agents_mod, desktop, logs, wol
from .activity import ActivityLog
from .runner import Runner


class RunBody(BaseModel):
    script: str = Field(min_length=1, max_length=100_000)
    title: str = Field("", max_length=10_000)     # longer titles are shortened, not rejected
    cwd: str = Field("", max_length=1000)         # working directory (environment variables allowed)
    env: dict[str, str] = {}                      # extra environment variables for this run


class ClipBody(BaseModel):
    text: str = Field(max_length=desktop.MAX_TEXT)
    origin: str = Field("", max_length=64)


class WakeBody(BaseModel):
    mac: str = Field(min_length=12, max_length=32)
    ips: list[str] = []


def make_router(config: dict, activity: ActivityLog, runner: Runner, who, agents: "agents_mod.Agents") -> APIRouter:
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
    async def run_action(request: Request, action_id: str):
        a = action(action_id)
        client = await asyncio.to_thread(who, request)
        agent = agents_mod.agent_of(request)
        if agent and a.get("confirm"):
            await approve(request, client, a)
        run = runner.start(a.get("name", action_id), a.get("script", ""), client, action_id, float(a.get("timeout", 0) or 0),
                           agent=agent)
        return run.view()

    async def approve(request: Request, client: str, a: dict):
        """An action that asks first, started by an AI agent: waits for Allow in an SSH Manager app on this PC
        or on the agent's computer. Refused on Deny, or when nobody answers in time."""
        here = os.environ.get("COMPUTERNAME", "")
        machines = [here, request.headers.get(agents_mod.FROM_HEADER, "").strip()[:64]]
        if not agents.can_ask(machines):
            activity.add("setup", action="approval-unanswered", client=client, title=a.get("name", ""))
            raise HTTPException(403, f"\"{a.get('name')}\" needs a person's approval, but no SSH Manager app that could ask "
                                     "is open (on this PC or the computer the request came from). Ask the owner to run it.")
        q = agents.create(client, machines, {"title": a.get("name", ""), "action_id": a.get("id", ""),
                                             "script": a.get("script", "")[:2000], "pc": monitor_name()})
        activity.add("setup", action="approval-requested", client=client, title=a.get("name", ""))
        answer = await agents.wait(q)
        if answer is None:
            activity.add("setup", action="approval-unanswered", client=client, title=a.get("name", ""))
            raise HTTPException(403, f"\"{a.get('name')}\" was not run: nobody approved it within {agents_mod.APPROVAL_TTL} seconds.")
        if not answer["allowed"]:
            raise HTTPException(403, f"\"{a.get('name')}\" was not run: it was denied on {answer['by']}.")

    def monitor_name() -> str:
        return config.get("name") or os.environ.get("COMPUTERNAME", "this PC")

    @r.post("/run")
    def run_script(request: Request, body: RunBody):
        title = " ".join((body.title.strip() or body.script.strip().splitlines()[0]).split())
        if len(title) > 200:
            title = title[:199] + "…"
        cwd = os.path.expandvars(body.cwd.strip()) if body.cwd.strip() else None
        if cwd and not os.path.isdir(cwd):
            raise HTTPException(400, f"The working directory doesn't exist: {cwd}")
        if len(body.env) > 100 or any(not k or "=" in k or "\0" in k + v for k, v in body.env.items()):
            raise HTTPException(400, "Invalid environment variables.")
        return runner.start(title, body.script, who(request), cwd=cwd, env=body.env or None,
                            agent=agents_mod.agent_of(request)).view()

    @r.get("/runs")
    def get_runs():
        return {"runs": runner.recent()}

    @r.get("/runs/{run_id}")
    def get_run(run_id: str, after: int = 0):
        run = runner.get(run_id)
        if not run:
            raise HTTPException(404, "That run is no longer kept.")
        return run.view(after)

    @r.get("/runs/{run_id}/output")
    def get_run_output(run_id: str):
        """The complete output of a run as a text file (the page keeps only the last lines)."""
        run = runner.get(run_id)
        if not run or not run.output.exists():
            raise HTTPException(404, "That run is no longer kept.")
        return FileResponse(run.output, media_type="text/plain; charset=utf-8", filename=f"run-{run_id}.log",
                            headers={"Cache-Control": "no-store"})

    # ---- wait for something to happen (instead of polling in a loop) ----
    @r.get("/wait")
    async def wait_for(file: str = "", changed_after: str = "", process: str = "", state: str = "started",
                       timeout: float = Query(60, ge=0, le=300)):
        """Waits until `file` exists (and, with `changed_after`, was modified after that time: an ISO time,
        Unix seconds or "now"), or until a program named `process` has started / exited. Returns
        {"met": true/false, ...} after at most `timeout` seconds."""
        if bool(file) == bool(process):
            raise HTTPException(400, "Give either file or process.")
        if state not in ("started", "exited"):
            raise HTTPException(400, "state must be started or exited.")
        t0 = time.time()
        after = None
        if changed_after:
            if changed_after == "now":
                after = t0
            else:
                try:
                    after = float(changed_after)
                except ValueError:
                    try:
                        d = datetime.fromisoformat(changed_after.replace("Z", "+00:00"))
                        after = (d if d.tzinfo else d.replace(tzinfo=timezone.utc)).timestamp()
                    except ValueError:
                        raise HTTPException(400, "changed_after must be an ISO time, Unix seconds or now.")
        name = process.lower().removesuffix(".exe")

        def check() -> dict | None:
            if file:
                p = Path(os.path.expandvars(file))
                try:
                    st = p.stat()
                except OSError:
                    return None
                if after is not None and st.st_mtime <= after:
                    return None
                return {"file": str(p), "size": st.st_size,
                        "mtime": datetime.fromtimestamp(st.st_mtime, timezone.utc).isoformat(timespec="seconds")}
            pids = [p.pid for p in psutil.process_iter(["name"])
                    if (p.info["name"] or "").lower().removesuffix(".exe") == name]
            if (state == "started") == bool(pids):
                return {"process": process, "state": state, "pids": pids}
            return None

        while True:
            found = await asyncio.to_thread(check)
            if found is not None or time.time() - t0 >= timeout:
                return {"met": found is not None, "waited_s": round(time.time() - t0, 1), **(found or {})}
            await asyncio.sleep(0.5)

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

    def app_allowed(app: str) -> bool:
        """With a "screen_apps" list in the settings, only those programs can be listed and viewed."""
        allowed = [a.strip().lower().removesuffix(".exe") for a in config.get("screen_apps", []) if a.strip()]
        return not allowed or app.lower() in allowed

    def monitors_allowed() -> bool:
        return config.get("screen_monitors", True)

    @r.get("/screen/monitors")
    def screen_monitors():
        screen_allowed()
        if not monitors_allowed():
            return {"monitors": [], "whole_screen": False}
        try:
            return {"monitors": desktop.monitors(), "whole_screen": True}
        except desktop.NotInDesktopSession as e:
            raise HTTPException(409, str(e))

    @r.get("/screen/windows")
    def screen_windows():
        screen_allowed()
        try:
            return {"windows": [w for w in desktop.windows() if app_allowed(w["app"])],
                    "filtered": bool(config.get("screen_apps"))}
        except desktop.NotInDesktopSession as e:
            raise HTTPException(409, str(e))

    @r.get("/screen")
    def screen(request: Request, monitor: int = 1, window: int = 0, width: int = Query(1280, ge=0, le=7680),
               quality: int = Query(70, ge=20, le=100), format: str = Query("jpeg", pattern="^(jpeg|jpg|png)$")):
        """A whole screen (monitor), or one app window when `window` is given. `width=0` keeps the full
        size; `format=png` is lossless."""
        screen_allowed()
        fmt = "png" if format == "png" else "jpeg"
        if window and not app_allowed(desktop.window_app(window)):
            raise HTTPException(403, "That app isn't on this PC's list of apps that can be viewed.")
        if not window and not monitors_allowed():
            raise HTTPException(403, "Whole-screen preview is turned off on this PC; pick an app instead.")
        try:
            jpeg = desktop.capture_window(window, width, quality, fmt) if window else desktop.capture(monitor, width, quality, fmt)
        except desktop.NotInDesktopSession as e:
            raise HTTPException(409, str(e))
        except desktop.WindowGone as e:
            raise HTTPException(404, str(e))
        viewer = who(request)
        if time.time() - last_screen_view.get(viewer, 0) > 300:   # log a viewing session, not every frame
            activity.add("setup", action="screen-viewed", client=viewer)
        last_screen_view[viewer] = time.time()
        return Response(jpeg, media_type="image/png" if fmt == "png" else "image/jpeg", headers={"Cache-Control": "no-store"})

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
