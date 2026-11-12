"""Runs PowerShell scripts on this PC for quick actions and the command panel, and keeps their output
so the dashboard can stream it. Runs inherit the server's rights (normally admin, in the signed-in
user's desktop session, so a script can also start programs with a window).

The last MAX_LINES lines of each run are kept in memory for streaming; the complete output is also
written to <data>/runs/<id>.log, which can be downloaded while the run is among the last KEEP_RUNS.

A run ends when its own PowerShell process ends, also when a program it started (a server, an app) keeps
running: such a program may hold on to the run's output pipe for as long as it runs. What it writes in the
first LINGER seconds after that is kept; later output is read and dropped (so the program never stalls on
a full pipe), and the run says that a program it started is still running. Such a program loses that pipe
when the server restarts or updates (some programs stop then), and on an installed PC stopping the server's
task may end it too, so the note says it may stop then."""
import base64
import ctypes
import html
import math
import os
import re
import subprocess
import threading
import time
import uuid
from ctypes import wintypes
from datetime import datetime, timezone
from pathlib import Path

import psutil

CREATE_NO_WINDOW = 0x08000000
MAX_LINES = 5000
KEEP_RUNS = 50                    # finished runs kept (runs still going are always kept)
MAX_OUTPUT = 50 * 1024 * 1024     # bytes of output written to a run's file; the rest is only streamed
LINGER = 2.0                      # seconds output is still kept after the run's own process has ended
MAX_RUN_SECONDS = 7 * 24 * 3600   # the longest time limit a run can have
STOP_WAIT = 5.0                   # seconds a Stop waits for the run's process to end
READ_SIZE = 64 * 1024             # bytes read from a run's output at a time
PREAMBLE = "$ProgressPreference = 'SilentlyContinue'\n[Console]::OutputEncoding = [Text.Encoding]::UTF8\n"


CLIXML_STRING = re.compile(r'<S S="(?:Error|Warning|Verbose|Debug|Information)">(.*?)</S>', re.S)
CLIXML_ESCAPE = re.compile(r"_x([0-9A-Fa-f]{4})_")


def decode_clixml(line: str) -> list[str] | None:
    """PowerShell started with -EncodedCommand writes its error stream as CLIXML when that stream isn't a
    console: a "#< CLIXML" line, then <Objs ...> with the messages. Returns the readable lines (none for
    the marker line), or None for an ordinary line."""
    if line.strip() == "#< CLIXML":
        return []
    if not line.startswith("<Objs ") or "schemas.microsoft.com/powershell" not in line:
        return None
    text = "".join(CLIXML_ESCAPE.sub(lambda m: chr(int(m.group(1), 16)), html.unescape(s))
                   for s in CLIXML_STRING.findall(line))
    return text.replace("\r\n", "\n").rstrip("\n").split("\n") if text else []


def signed(code: int) -> int:
    """Windows exit codes are unsigned 32-bit (0xC0000005 = 3221225477); report them as signed ints
    (-1073741819), as PowerShell and most tools do."""
    code &= 0xFFFFFFFF
    return code - 0x100000000 if code >= 0x80000000 else code


def time_limit(value) -> float:
    """Seconds a run may take (0: no limit) from a value that may come from a hand-edited config.json: a
    number up to MAX_RUN_SECONDS (a larger one is cut to it); anything else (negative, NaN, text) is no limit."""
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
        return 0.0
    return float(min(value, MAX_RUN_SECONDS))


def _iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
_k32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
_k32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
_k32.Process32FirstW.argtypes = _k32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.c_void_p]
_k32.CloseHandle.argtypes = [wintypes.HANDLE]
_k32.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
_k32.TerminateProcess.restype = wintypes.BOOL


class _ProcessEntry(ctypes.Structure):   # PROCESSENTRY32W
    _fields_ = [("size", wintypes.DWORD), ("usage", wintypes.DWORD), ("pid", wintypes.DWORD),
                ("heap", ctypes.c_size_t), ("module", wintypes.DWORD), ("threads", wintypes.DWORD),
                ("ppid", wintypes.DWORD), ("priority", ctypes.c_long), ("flags", wintypes.DWORD),
                ("exe", ctypes.c_wchar * 260)]


def _processes() -> list[tuple[int, int, str]]:
    """(pid, parent pid, program) of every process, from one snapshot (psutil takes a snapshot for every
    process' parent, which takes seconds on a busy PC)."""
    snap = _k32.CreateToolhelp32Snapshot(2, 0)   # TH32CS_SNAPPROCESS
    if not snap or snap == wintypes.HANDLE(-1).value:
        raise OSError(ctypes.get_last_error(), "no process snapshot")
    try:
        entry = _ProcessEntry(size=ctypes.sizeof(_ProcessEntry))
        out = []
        more = _k32.Process32FirstW(snap, ctypes.byref(entry))
        while more:
            out.append((entry.pid, entry.ppid, entry.exe))
            more = _k32.Process32NextW(snap, ctypes.byref(entry))
        return out
    finally:
        _k32.CloseHandle(snap)


def _children(run: "Run") -> list[tuple[int, str]]:
    """(process id, program) of the programs the run's process started that are still running. Windows keeps
    a program's parent process id after the parent has ended, and the run's process id isn't given to another
    process while run.proc holds its handle. A program started by one that has ended itself isn't found."""
    found = []
    try:
        for pid, ppid, name in _processes():
            if ppid != run.proc.pid:
                continue
            try:
                if psutil.Process(pid).create_time() < run.t0 - 1:
                    continue   # older than the run: an earlier process with the same parent id
            except psutil.NoSuchProcess:
                continue
            except psutil.Error:
                pass
            found.append((pid, name))
    except OSError:
        return []
    return found


def still_running(run: "Run") -> list[str]:
    """Programs the run's process started that are still running, as "name (process N)" (see _children; for
    one started by a program that has ended the run only knows that its output pipe is still open). The
    console's own conhost is left out."""
    found = [f"{name} (process {pid})" for pid, name in _children(run) if name.lower() != "conhost.exe"]
    return found[:5] + ([f"{len(found) - 5} more"] if len(found) > 5 else [])


def end_process(proc: subprocess.Popen) -> bool:
    """Ends a process at once (exit code 1, as taskkill /F gives). True when this ended it. False when it had
    ended or was ending by itself: Windows decides that in one step and doesn't end a process that is already
    exiting, which then keeps its own exit code (also false if it couldn't be ended at all)."""
    return bool(_k32.TerminateProcess(int(proc._handle), 1))


def end_programs(run: "Run"):
    """Ends the programs the run's process started and the ones they started (the rest of its process tree),
    parents first. Called once that process has been ended, so it can't start more."""
    procs = []
    for pid, _ in _children(run):
        try:
            p = psutil.Process(pid)
            procs += [p, *p.children(recursive=True)]
        except psutil.Error:
            pass
    for p in procs:
        try:
            p.kill()   # psutil makes sure the process id still belongs to that process
        except psutil.Error:
            pass


class Run:
    def __init__(self, title: str, script: str, client: str, action_id: str | None, output: Path, agent: str = ""):
        self.id = uuid.uuid4().hex[:12]
        self.title, self.script, self.client, self.action_id, self.agent = title, script, client, action_id, agent
        self.output = output.with_name(f"{self.id}.log")
        self.lines: list[str] = []
        self.dropped = 0          # lines no longer in memory (still in the output file)
        self.status = "running"   # running | done | failed | stopped | timeout
        self.exit_code: int | None = None
        self.left_running = False  # a program the run started was still running when the run ended
        self.started = _iso()
        self.ended: str | None = None
        self.t0 = time.time()
        self.proc: subprocess.Popen | None = None
        self.lock = threading.Lock()
        self.stopping: str | None = None    # the status a Stop or the time limit asked for, set when the run has ended
        self.stop_lock = threading.Lock()   # held by a Stop while it ends the process, and by the end of the run
        self.finished = threading.Event()   # the run has ended: status, exit code and its last output are in

    def view(self, after: int = 0) -> dict:
        """Lines numbered from the start of the run: `after` is the `next` of the previous call."""
        with self.lock:
            start = max(0, after - self.dropped)
            return {"id": self.id, "title": self.title, "status": self.status, "exit_code": self.exit_code,
                    "left_running": self.left_running, "started": self.started, "ended": self.ended, "client": self.client, "action_id": self.action_id,
                    "duration_s": round((time.time() if not self.ended else self.t1) - self.t0, 1),
                    "lines": self.lines[start:], "next": self.dropped + len(self.lines),
                    "skipped": max(0, self.dropped - after),   # lines between `after` and the first returned one
                    "full_output": f"/api/runs/{self.id}/output", "output_file": str(self.output)}


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
              cwd: str | None = None, env: dict[str, str] | None = None, agent: str = "") -> Run:
        run = Run(title, script, client, action_id, self.dir / "x.log", agent)
        encoded = base64.b64encode((PREAMBLE + script).encode("utf-16-le")).decode()
        run.proc = subprocess.Popen(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, creationflags=CREATE_NO_WINDOW,
            cwd=cwd or None, env={**os.environ, **env} if env else None)
        with self.lock:
            self.runs[run.id] = run
            self.order.append(run.id)
        self._prune()
        threading.Thread(target=self._pump, args=(run, time_limit(timeout)), daemon=True).start()
        return run

    def _prune(self):
        """Forgets the oldest finished runs beyond KEEP_RUNS (and deletes their output files). Runs that
        are still going are never dropped."""
        with self.lock:
            finished = [i for i in self.order if self.runs[i].status != "running"]
            gone = finished[:max(0, len(finished) - KEEP_RUNS)]
            for i in gone:
                self.order.remove(i)
                run = self.runs.pop(i)
                try:
                    run.output.unlink(missing_ok=True)
                except OSError:
                    pass

    def busy(self) -> bool:
        """A run is still going (the updater waits for it)."""
        with self.lock:
            return any(r.status == "running" for r in self.runs.values())

    def _pump(self, run: Run, timeout: float):
        timer = None
        if timeout:   # enforced here, so the limit holds even when whoever started the run went away
            timer = threading.Timer(timeout, lambda: self.stop(run.id, "timeout"))
            timer.daemon = True
            timer.start()
        try:
            out = open(run.output, "w", encoding="utf-8", newline="\n")
        except OSError:
            out = None   # no output file (disk full, no access): the run still streams to the page
        sink = {"out": out, "written": 0, "open": True, "part": bytearray()}   # part: a line read up to now
        gate = threading.Lock()   # held while a line is kept; once sink["open"] is False nothing more is

        def keep(line: str):
            f = sink["out"]
            if f is not None:
                try:
                    if sink["written"] + len(line) + 1 > MAX_OUTPUT:
                        f.write(f"[output truncated: only the first {MAX_OUTPUT // (1024 * 1024)} MB are kept in this file]\n")
                        f.close()
                        sink["out"] = None
                    else:
                        f.write(line + "\n")
                        f.flush()
                        sink["written"] += len(line.encode("utf-8", errors="replace")) + 1
                except OSError:
                    sink["out"] = None
            with run.lock:
                run.lines.append(line)
                if len(run.lines) > MAX_LINES:
                    extra = len(run.lines) - MAX_LINES
                    del run.lines[:extra]
                    run.dropped += extra

        def take(raw: bytes):   # one line of output (called holding gate)
            line = raw.decode("utf-8", errors="replace").rstrip("\r")
            decoded = decode_clixml(line)
            for line in [line] if decoded is None else decoded:
                keep(line)

        def read():
            # until every holder of the pipe has closed it: the run's process, and programs it started that
            # got its output (they may run for days). Read in pieces of at most READ_SIZE: once the run has
            # ended they are dropped as they come, also from a program that never writes a line end.
            pipe = run.proc.stdout
            try:
                while chunk := pipe.read1(READ_SIZE):
                    with gate:
                        if not sink["open"]:
                            continue
                        sink["part"] += chunk
                        if b"\n" in chunk:
                            *lines, rest = sink["part"].split(b"\n")
                            sink["part"] = bytearray(rest)
                            for raw in lines:
                                take(raw)
                with gate:
                    if sink["open"] and sink["part"]:   # the last line had no line end
                        take(bytes(sink["part"]))
                        sink["part"] = bytearray()
            except (OSError, ValueError):
                pass
            finally:
                try:
                    run.proc.stdout.close()
                except OSError:
                    pass

        reader = threading.Thread(target=read, daemon=True, name=f"run-{run.id}")
        reader.start()
        code = signed(run.proc.wait())
        # the status is set last, with the exit code, end time and the last output, so whoever stops following
        # the run at its first status other than "running" has all of it; a Stop that is still ending the
        # process tree finishes first
        with run.stop_lock:
            reader.join(LINGER)   # the pipe closes right away, unless a program the run started still has it
            holds_output = reader.is_alive()
            started = still_running(run)
            with gate:
                if sink["part"]:   # written before the end without a line end
                    take(bytes(sink["part"]))
                    sink["part"] = bytearray()
                sink["open"] = False
                if holds_output or started:
                    many = len(started) > 1
                    if many:
                        what = "programs it started are still running: " + ", ".join(started)
                    else:
                        what = "a program it started is still running" + (f": {started[0]}" if started else "")
                    it, writes = ("they", "write") if many else ("it", "writes")
                    # SSH Manager restarting or updating ends its pipe, and on an installed PC its task
                    later = (f"What {it} {writes} from now on isn't shown here, and {it} may stop" if holds_output
                             else f"{it.capitalize()} may stop") + " when SSH Manager restarts or updates."
                    keep(f"[The script {'was stopped' if run.stopping else 'has ended'}, but {what}. {later}]")
                if sink["out"] is not None:
                    try:
                        sink["out"].close()
                    except OSError:
                        pass
                with run.lock:
                    run.exit_code = code
                    run.left_running = holds_output or bool(started)
                    run.status = run.stopping or ("done" if code == 0 else "failed")
                    run.ended = _iso()
                    run.t1 = time.time()
        run.finished.set()
        if timer is not None:
            timer.cancel()
        tail = "\n".join(run.lines[-200:])
        self.activity.add("action", ts=run.started, client=run.client, title=run.title, script=run.script[:20000],
                          output=tail, exit_code=code, status=run.status, duration_s=round(run.t1 - run.t0, 1),
                          **({"agent": run.agent} if run.agent else {}))
        self._prune()

    def stop(self, run_id: str, status: str = "stopped") -> bool:
        """Ends a run's process and the programs it started that still belong to it (its process tree). The
        run gets `status` when it has ended, after its last output (this waits for that). What counts is
        whether this ended the run's own process: one that had ended or was ending by itself (also as Stop
        came) is not stopped and keeps its own status and exit code, and programs it left running stay as they
        are. False when there is nothing to stop, the process couldn't be ended (the run goes on as before),
        or it hasn't ended within STOP_WAIT (it then gets `status` when it has)."""
        run = self.runs.get(run_id)
        if not run or not run.proc:
            return False
        with run.stop_lock:
            with run.lock:
                if run.status != "running" or run.proc.poll() is not None:
                    return False
            if run.stopping is None:   # (else an earlier Stop ended it and Windows is still at it)
                if not end_process(run.proc):   # first, so it can't start more programs
                    return False
                with run.lock:
                    run.stopping = status
            end_programs(run)
            try:
                run.proc.wait(STOP_WAIT)
            except subprocess.TimeoutExpired:
                return False
        run.finished.wait(LINGER + 5)
        return True

    def get(self, run_id: str) -> Run | None:
        return self.runs.get(run_id)

    def recent(self) -> list[dict]:
        with self.lock:
            runs = [self.runs[i] for i in self.order if i in self.runs]
        return [{k: v for k, v in r.view().items() if k != "lines"} for r in reversed(runs)]
