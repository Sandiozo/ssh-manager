"""Runs PowerShell scripts on this PC for quick actions and the command panel, and keeps their output
so the dashboard can stream it. Runs inherit the server's rights (normally admin, in the signed-in
user's desktop session, so a script can also start programs with a window)."""
import base64
import subprocess
import threading
import time
import uuid
from datetime import datetime, timezone

CREATE_NO_WINDOW = 0x08000000
MAX_LINES = 5000
PREAMBLE = "$ProgressPreference = 'SilentlyContinue'\n[Console]::OutputEncoding = [Text.Encoding]::UTF8\n"


def _iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class Run:
    def __init__(self, title: str, script: str, client: str, action_id: str | None):
        self.id = uuid.uuid4().hex[:12]
        self.title, self.script, self.client, self.action_id = title, script, client, action_id
        self.lines: list[str] = []
        self.status = "running"   # running | done | failed | stopped
        self.exit_code: int | None = None
        self.started = _iso()
        self.ended: str | None = None
        self.t0 = time.time()
        self.proc: subprocess.Popen | None = None
        self.lock = threading.Lock()

    def view(self, after: int = 0) -> dict:
        with self.lock:
            return {"id": self.id, "title": self.title, "status": self.status, "exit_code": self.exit_code,
                    "started": self.started, "ended": self.ended, "client": self.client, "action_id": self.action_id,
                    "duration_s": round((time.time() if not self.ended else self.t1) - self.t0, 1),
                    "lines": self.lines[after:], "next": len(self.lines)}


class Runner:
    def __init__(self, activity):
        self.activity = activity
        self.runs: dict[str, Run] = {}
        self.order: list[str] = []
        self.lock = threading.Lock()

    def start(self, title: str, script: str, client: str, action_id: str | None = None, timeout: float = 0) -> Run:
        run = Run(title, script, client, action_id)
        encoded = base64.b64encode((PREAMBLE + script).encode("utf-16-le")).decode()
        run.proc = subprocess.Popen(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, creationflags=CREATE_NO_WINDOW)
        with self.lock:
            self.runs[run.id] = run
            self.order.append(run.id)
            while len(self.order) > 50:
                self.runs.pop(self.order.pop(0), None)
        threading.Thread(target=self._pump, args=(run, timeout), daemon=True).start()
        return run

    def _pump(self, run: Run, timeout: float):
        if timeout:
            threading.Timer(timeout, lambda: run.status == "running" and self.stop(run.id)).start()
        for raw in iter(run.proc.stdout.readline, b""):
            line = raw.decode("utf-8", errors="replace").rstrip("\r\n")
            with run.lock:
                run.lines.append(line)
                if len(run.lines) > MAX_LINES:
                    del run.lines[: len(run.lines) - MAX_LINES]
        code = run.proc.wait()
        with run.lock:
            run.exit_code = code
            if run.status == "running":
                run.status = "done" if code == 0 else "failed"
            run.ended = _iso()
            run.t1 = time.time()
        tail = "\n".join(run.lines[-200:])
        self.activity.add("action", ts=run.started, client=run.client, title=run.title, script=run.script[:20000],
                          output=tail, exit_code=code, status=run.status, duration_s=round(run.t1 - run.t0, 1))

    def stop(self, run_id: str) -> bool:
        run = self.runs.get(run_id)
        if not run or run.status != "running" or not run.proc:
            return False
        run.status = "stopped"
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(run.proc.pid)], capture_output=True, creationflags=CREATE_NO_WINDOW)
        return True

    def get(self, run_id: str) -> Run | None:
        return self.runs.get(run_id)

    def recent(self) -> list[dict]:
        with self.lock:
            runs = [self.runs[i] for i in self.order if i in self.runs]
        return [{k: v for k, v in r.view().items() if k != "lines"} for r in reversed(runs)]
