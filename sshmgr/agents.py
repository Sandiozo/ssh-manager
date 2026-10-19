"""AI agents: who they are, what they may do on this PC, and approvals for actions that ask first.

An agent identifies itself with the X-SSHMgr-Agent header (`sshmgr mcp` and `sshmgr --as <name>` send it).
Its requests are then labelled "<agent> (<computer>)" in the activity log and live sessions, and this PC's
"ai_access" setting applies to them:

    off     nothing (only /api/ping and /api/ai answer)
    read    looking only: status, activity, logs, files and downloads, screenshots, run output (GET requests)
    full    everything a person can do from the dashboard (default)

Agents can never change settings, SSH keys or pairing, nor answer approvals. A quick action with
"confirm": true started by an agent waits until someone clicks Allow in an SSH Manager app (on this PC,
or on the computer the agent works from), and is refused after APPROVAL_TTL seconds or on Deny.

These controls keep an agent that uses SSH Manager as intended within bounds. They are not a wall against
a program that is set on getting around them: the access token is available to anything that runs as
the same Windows user on a computer whose app is connected here."""
import asyncio
import contextvars
import re
import secrets
import threading
import time
from datetime import datetime, timezone

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel, Field

HEADER = "x-sshmgr-agent"
FROM_HEADER = "x-sshmgr-from"      # the requesting computer's name (where its app can approve)
LEVELS = ("off", "read", "full")
APPROVAL_TTL = 120
SEEN_FOR = 120                      # an agent counts as active this long after its last request

current = contextvars.ContextVar("sshmgr_agent", default="")

# requests an agent may never make, whatever the access level
NEVER = [("PUT", re.compile(r"^/api/settings$")), ("POST", re.compile(r"^/api/keys/")),
         ("POST", re.compile(r"^/api/pair/")), ("POST", re.compile(r"^/api/approvals/"))]
ALWAYS_OPEN = {"/api/ping", "/api/ai"}


def agent_of(request: Request) -> str:
    """The agent's name from the request, cleaned up ("" for a person)."""
    raw = request.headers.get(HEADER, "")
    name = " ".join(re.sub(r"[^\w .@+\-]", "", raw).split())[:40]
    return name


def level(config: dict) -> str:
    v = config.get("ai_access", "full")
    return v if v in LEVELS else "full"


def refusal(request: Request, config: dict) -> str | None:
    """Why this agent request isn't allowed here, or None when it is."""
    path, method = request.url.path, request.method
    if path in ALWAYS_OPEN:
        return None
    for m, pattern in NEVER:
        if method == m and pattern.search(path):
            return "AI agents can't change settings, SSH keys or pairing, or answer approvals. Ask the owner to do it in SSH Manager."
    lv = level(config)
    if lv == "off":
        return "AI agents are turned off on this PC (Settings → AI agents in SSH Manager)."
    if lv == "read" and method not in ("GET", "HEAD"):
        return ("AI agents may only look on this PC (status, logs, files, screenshots), not run or change anything "
                "(Settings → AI agents in SSH Manager).")
    return None


def _iso(t: float) -> str:
    return datetime.fromtimestamp(t, timezone.utc).isoformat(timespec="seconds")


class Agents:
    """Agents seen recently, and approvals waiting for an answer."""

    def __init__(self):
        self.lock = threading.Lock()
        self.seen: dict[str, dict] = {}         # label -> {agent, client, last, pid}
        self.approvals: dict[str, dict] = {}
        self.approvers: dict[str, float] = {}   # computer name (lower case) -> last time its app asked for approvals
        self.events: dict[str, asyncio.Event] = {}

    # ---- who is active ----
    def note(self, agent: str, client: str, pid: int | None):
        with self.lock:
            label = f"{agent} ({client})"
            self.seen[label] = {"agent": agent, "client": client, "label": label, "last": time.time(), "pid": pid}

    def recent(self) -> list[dict]:
        now = time.time()
        with self.lock:
            return [{**{k: v for k, v in a.items() if k != "last"}, "last": _iso(a["last"])}
                    for a in sorted(self.seen.values(), key=lambda a: -a["last"]) if now - a["last"] < SEEN_FOR]

    def tunnel_agents(self) -> dict[int, list[str]]:
        """sshd PID of an app's port forward -> the agents that used it recently."""
        now = time.time()
        out: dict[int, list[str]] = {}
        with self.lock:
            for a in self.seen.values():
                if a["pid"] and now - a["last"] < SEEN_FOR:
                    out.setdefault(a["pid"], []).append(a["agent"])
        return out

    # ---- approvals ----
    def can_ask(self, machines: list[str]) -> bool:
        now = time.time()
        with self.lock:
            return any(now - self.approvers.get(m.lower(), 0) < 20 for m in machines if m)

    def create(self, agent_label: str, machines: list[str], what: dict) -> dict:
        with self.lock:
            now = time.time()
            for k in [k for k, q in self.approvals.items() if now > q["expires"] + 600]:
                del self.approvals[k]
            q = {"id": secrets.token_urlsafe(9), "agent": agent_label, "machines": [m.lower() for m in machines if m],
                 "created": now, "expires": now + APPROVAL_TTL, "answer": None, **what}
            self.approvals[q["id"]] = q
            self.events[q["id"]] = asyncio.Event()
            return q

    async def wait(self, q: dict) -> dict | None:
        """The answer ({allowed, by}), or None when nobody answered in time."""
        try:
            await asyncio.wait_for(self.events[q["id"]].wait(), timeout=max(0.0, q["expires"] - time.time()))
        except asyncio.TimeoutError:
            pass
        finally:
            self.events.pop(q["id"], None)
        with self.lock:
            if q["answer"] is None:
                q["expires"] = 0
            return q["answer"]

    def pending(self, machine: str) -> list[dict]:
        now = time.time()
        m = machine.strip().lower()
        with self.lock:
            self.approvers[m] = now
            return [{k: v for k, v in q.items() if k not in ("machines", "answer", "created", "expires")}
                    | {"expires_in": round(q["expires"] - now)}
                    for q in self.approvals.values() if q["answer"] is None and now < q["expires"] and m in q["machines"]]

    def answer(self, qid: str, machine: str, allowed: bool, by: str, loop: asyncio.AbstractEventLoop) -> dict:
        with self.lock:
            q = self.approvals.get(qid)
            if q is None or machine.strip().lower() not in q["machines"]:
                raise HTTPException(404, "No such approval.")
            if q["answer"] is not None or time.time() > q["expires"]:
                raise HTTPException(410, "This approval was already answered or has expired.")
            q["answer"] = {"allowed": allowed, "by": by}
            event = self.events.get(qid)
        if event is not None:
            loop.call_soon_threadsafe(event.set)
        return q


class AnswerBody(BaseModel):
    machine: str = Field(min_length=1, max_length=64)
    allowed: bool


def make_router(config: dict, agents: Agents, activity, who) -> APIRouter:
    r = APIRouter(prefix="/api")

    @r.get("/ai")
    def ai(request: Request):
        """This PC's AI access level and the agents active here (also answers agents when access is off)."""
        return {"access": level(config), "agents": agents.recent(), "you": agent_of(request) or None}

    @r.get("/approvals/pending")
    def pending(machine: str):
        """Polled by the SSH Manager apps connected here: approvals they may answer."""
        return {"approvals": agents.pending(machine)}

    @r.post("/approvals/{qid}/answer")
    async def answer(request: Request, qid: str, body: AnswerBody):
        by = await asyncio.to_thread(who, request)
        q = agents.answer(qid, body.machine, body.allowed, f"{body.machine.strip()}", asyncio.get_running_loop())
        activity.add("setup", action="approval-allowed" if body.allowed else "approval-denied", client=by,
                     requested_by=q["agent"], title=q.get("title", ""))
        return {"ok": True}

    return r
