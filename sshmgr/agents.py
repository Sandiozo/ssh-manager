"""AI agents: who they are, what they may do on this PC, and approvals for actions that ask first.

Any request with an X-SSHMgr-Agent header is an AI agent's request (`sshmgr mcp` and `sshmgr --as <name>`
send it), whatever the header holds; the name is cleaned up, and "AI agent" is used when nothing is left.
Its requests are labelled "<agent> (<computer>)" in the activity log and live sessions, and this PC's
"ai_access" setting applies to them:

    off     nothing (only /api/ping and /api/ai answer)
    read    looking only: status, activity, logs, files and downloads, screenshots, run output (GET requests)
    full    everything a person can do from the dashboard (default)

Whatever the level, agents can never: change settings, SSH keys or pairing; see or answer approvals;
restart the server or install an update; use the clipboard; read or write the server's own files (token,
app secret, config.json, plugins, data folder) or SSH keys (see protect.py). A quick action with
"confirm": true - or a script identical to one - started by an agent waits until someone clicks Allow
in an SSH Manager app (on this PC, or on the computer the agent works from), and is refused after
APPROVAL_TTL seconds or on Deny. Only the app can answer: it proves itself with the app secret
(X-SSHMgr-App header), which agents don't get.

These controls keep an agent that uses SSH Manager as intended within bounds. An agent with full access
can run any script with administrator rights, so full access means trusting it like an administrator."""
import asyncio
import contextvars
import hmac
import re
import secrets
import threading
import time
import unicodedata
import urllib.parse
from datetime import datetime, timezone

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel, Field

HEADER = "x-sshmgr-agent"
FROM_HEADER = "x-sshmgr-from"      # the requesting computer's name (where its app can approve)
APP_HEADER = "x-sshmgr-app"        # the app secret: only the SSH Manager app has it
LEVELS = ("off", "read", "full")
APPROVAL_TTL = 120
SEEN_FOR = 120                      # an agent counts as active this long after its last request
DEFAULT_NAME = "AI agent"

current = contextvars.ContextVar("sshmgr_agent", default="")
_app_secret = {"value": ""}

OWNER = "Ask the owner to do it in SSH Manager."
# requests an agent may never make, whatever the access level: (methods or None for all, path, why)
NEVER = [
    (None, re.compile(r"^/api/approvals(/|$)"),
     "AI agents can't see or answer approvals; a person answers them in the SSH Manager app."),
    (None, re.compile(r"^/api/pair(/|$)"), f"AI agents can't pair computers. {OWNER}"),
    ({"PUT", "POST", "PATCH", "DELETE"}, re.compile(r"^/api/settings(/|$)"), f"AI agents can't change this PC's settings. {OWNER}"),
    ({"PUT", "POST", "PATCH", "DELETE"}, re.compile(r"^/api/keys(/|$)"), f"AI agents can't add or remove SSH keys. {OWNER}"),
    (None, re.compile(r"^/api/server(/|$)"), f"AI agents can't restart or stop the SSH Manager server. {OWNER}"),
    ({"POST"}, re.compile(r"^/api/update/install$"), f"AI agents can't install SSH Manager updates. {OWNER}"),
    (None, re.compile(r"^/api/clipboard(/|$)"), "AI agents can't read or change the clipboard on this PC."),
]
ALWAYS_OPEN = {"/api/ping", "/api/ai"}


def configure(app_secret: str):
    _app_secret["value"] = app_secret


def is_agent(request: Request) -> bool:
    """An AI agent's request: it has the agent header, even an empty or unusable one."""
    return HEADER in request.headers


NAME_PUNCTUATION = set(" -_.()")


def clean_name(raw: str) -> str:
    """Letters, digits and combining marks of any script, spaces and - _ . ( ); at most 40 characters."""
    kept = "".join(ch for ch in raw if ch in NAME_PUNCTUATION or unicodedata.category(ch)[0] in "LMN")
    return " ".join(kept.split())[:40].strip()


def agent_of(request: Request) -> str:
    """The agent's name from the request ("" for a person)."""
    if not is_agent(request):
        return ""
    raw = request.headers.get(HEADER, "")
    try:   # header bytes arrive as Latin-1 text; a client that sent raw UTF-8 meant UTF-8
        raw = raw.encode("latin-1").decode("utf-8")
    except UnicodeError:
        pass
    raw = urllib.parse.unquote(raw)   # sshmgr sends non-ASCII names percent-encoded (UTF-8)
    return clean_name(raw) or DEFAULT_NAME


def is_app(request: Request) -> bool:
    """The request comes from the SSH Manager app itself (it carries the app secret), not from an agent."""
    supplied = request.headers.get(APP_HEADER, "")
    secret = _app_secret["value"]
    return bool(secret and supplied) and not is_agent(request) and hmac.compare_digest(supplied.encode(), secret.encode())


def require_app(request: Request, what: str):
    if is_agent(request):
        raise HTTPException(403, f"AI agents can't {what}.")
    if not is_app(request):
        raise HTTPException(403, f"Only the SSH Manager app can {what} (update the app on the computer you use).")


def level(config: dict) -> str:
    v = config.get("ai_access", "full")
    return v if v in LEVELS else "full"


def refusal(request: Request, config: dict) -> str | None:
    """Why this agent request isn't allowed here, or None when it is."""
    path, method = request.url.path, request.method
    if path in ALWAYS_OPEN:
        return None
    for methods, pattern, why in NEVER:
        if (methods is None or method in methods) and pattern.search(path):
            return why
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
    """Agents seen recently, and approvals waiting for an answer. Expiry uses the monotonic clock, so a
    clock change (time sync, time zone, manual change) can't shorten or stretch an approval."""

    def __init__(self):
        self.lock = threading.Lock()
        self.seen: dict[str, dict] = {}         # label -> {agent, client, last, pid}
        self.approvals: dict[str, dict] = {}
        self.approvers: dict[str, float] = {}   # computer name (lower case) -> last time (monotonic) its app asked
        self.events: dict[str, asyncio.Event] = {}
        # computer name -> machine_id its SSH Manager app reported (set by server.py; tells apart two
        # computers with the same name)
        self.machine_id_of = lambda name: None

    # ---- who is active ----
    def note(self, agent: str, client: str, pid: int | None):
        with self.lock:
            label = f"{agent} ({client})"
            if len(self.seen) > 200:
                self.seen = {k: a for k, a in self.seen.items() if time.monotonic() - a["mono"] < SEEN_FOR}
            self.seen[label] = {"agent": agent, "client": client, "label": label, "last": time.time(),
                                "mono": time.monotonic(), "pid": pid}

    def _active(self) -> list[dict]:
        now = time.monotonic()
        return [a for a in self.seen.values() if now - a["mono"] < SEEN_FOR]

    def recent(self) -> list[dict]:
        with self.lock:
            return [{**{k: v for k, v in a.items() if k not in ("last", "mono")}, "last": _iso(a["last"])}
                    for a in sorted(self._active(), key=lambda a: -a["mono"])]

    def tunnel_agents(self) -> dict[int, list[str]]:
        """sshd PID of an app's port forward -> the agents that used it recently."""
        out: dict[int, list[str]] = {}
        with self.lock:
            for a in self._active():
                if a["pid"]:
                    out.setdefault(a["pid"], []).append(a["agent"])
        return out

    # ---- approvals ----
    def can_ask(self, machines: list[str]) -> bool:
        now = time.monotonic()
        with self.lock:
            return any(now - self.approvers.get(m.lower(), -1e9) < 20 for m in machines if m)

    def create(self, agent_label: str, machines: list[str], what: dict) -> dict:
        with self.lock:
            now = time.monotonic()
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
            await asyncio.wait_for(self.events[q["id"]].wait(), timeout=max(0.0, q["expires"] - time.monotonic()))
        except asyncio.TimeoutError:
            pass
        finally:
            self.events.pop(q["id"], None)
        with self.lock:
            if q["answer"] is None:
                q["expires"] = 0
            return q["answer"]

    def pending(self, machine: str) -> list[dict]:
        now = time.monotonic()
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
            if q["answer"] is not None or time.monotonic() > q["expires"]:
                raise HTTPException(410, "This approval was already answered or has expired.")
            q["answer"] = {"allowed": allowed, "by": by}
            event = self.events.get(qid)
        if event is not None:
            loop.call_soon_threadsafe(event.set)
        return q


class AnswerBody(BaseModel):
    machine: str = Field(min_length=1, max_length=64)
    allowed: bool
    machine_id: str = Field("", max_length=64)   # the answering app's install id


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
        require_app(request, "answer approvals")
        by = await asyncio.to_thread(who, request)
        q = agents.answer(qid, body.machine, body.allowed, f"{body.machine.strip()}", asyncio.get_running_loop())
        activity.add("setup", action="approval-allowed" if body.allowed else "approval-denied", client=by,
                     requested_by=q["agent"], title=q.get("title", ""), answered_on=body.machine.strip(),
                     **({"answered_by_id": body.machine_id} if body.machine_id else {}))
        return {"ok": True}

    return r
