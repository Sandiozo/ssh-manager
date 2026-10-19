"""Runs PowerShell scripts on this PC for quick actions and the command panel, and keeps their output
so the dashboard can stream it. Runs inherit the server's rights (normally admin, in the signed-in
user's desktop session, so a script can also start programs with a window).

The last MAX_LINES lines of each run are kept in memory for streaming; the complete output is also
written to <data>/runs/<id>.log, which can be downloaded while the run is among the last KEEP_RUNS."""
import base64
import os
import subprocess
import threading
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

CREATE_NO_WINDOW = 0x08000000
MAX_LINES = 5000
KEEP_RUNS = 50
PREAMBLE = "$ProgressPreference = 'SilentlyContinue'\n[Console]::OutputEncoding = [Text.Encoding]::UTF8\n"


def _iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class Run:
    def __init__(self, title: str, script: str, client: str, action_id: str | None, output: Path):
        self.id = uuid.uuid4().hex[:12]
        self.title, self.script, self.client, self.action_id = title, script, client, action_id
        self.output = output.with_name(f"{self.id}.log")
        self.lines: list[str] = []
        self.dropped = 0          # lines no longer in memory (still in the output file)
        self.status = "running"   # running | done | failed | stopped
        self.exit_code: int | None = None
        self.started = _iso()
        self.ended: str | None = None
        self.t0 = time.time()
        self.proc: subprocess.Popen | None = None
        self.lock = threading.Lock()

    def view(self, after: int = 0) -> dict:
        """Lines numbered from the start of the run: `after` is the `next` of the previous call."""
        with self.lock:
            start = max(0, after - self.dropped)
            return {"id": self.id, "title": self.title, "status": self.status, "exit_code": self.exit_code,
                    "started": self.started, "ended": self.ended, "client": self.client, "action_id": self.action_id,
                    "duration_s": round((time.time() if not self.ended else self.t1) - self.t0, 1),
                    "lines": self.lines[start:], "next": self.dropped + len(self.lines),
                    "skipped": max(0, self.dropped - after),   # lines between `after` and the first returned one
                    "full_output": f"/api/runs/{self.id}/output"}


class Runner:
    def __init__(self, activity, data_dir: Path):
        self.activity = activity
        self.dir = data_dir / "runs"
        self.dir.mkdir(parents=True, exist_ok=True)
        self.runs: dict[str, Run] = {}
        self.order: list[str] = []
        self.lock = threading.Lock()
        for old in self.dir.glob("*.log"):   # runs from before a restart can't be looked up any more
            try:
                old.unlink()
            except OSError:
                pass

    def start(self, title: str, script: str, client: str, action_id: str | None = None, timeout: float = 0,
              cwd: str | None = None, env: dict[str, str] | None = None) -> Run:
        run = Run(title, script, client, action_id, self.dir / "x.log")
        encoded = base64.b64encode((PREAMBLE + script).encode("utf-16-le")).decode()
        run.proc = subprocess.Popen(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, creationflags=CREATE_NO_WINDOW,
            cwd=cwd or None, env={**os.environ, **env} if env else None)
        with self.lock:
            self.runs[run.id] = run
            self.order.append(run.id)
            while len(self.order) > KEEP_RUNS:
                gone = self.runs.pop(self.order.pop(0), None)
                if gone and gone.status != "running":
                    try:
                        gone.output.unlink(missing_ok=True)
                    except OSError:
                        pass
        threading.Thread(target=self._pump, args=(run, timeout), daemon=True).start()
        return run

    def _pump(self, run: Run, timeout: float):
        if timeout:
            threading.Timer(timeout, lambda: run.status == "running" and self.stop(run.id)).start()
        with open(run.output, "w", encoding="utf-8", newline="\n") as out:
            for raw in iter(run.proc.stdout.readline, b""):
                line = raw.decode("utf-8", errors="replace").rstrip("\r\n")
                out.write(line + "\n")
                out.flush()
                with run.lock:
                    run.lines.append(line)
                    if len(run.lines) > MAX_LINES:
                        extra = len(run.lines) - MAX_LINES
                        del run.lines[:extra]
                        run.dropped += extra
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
