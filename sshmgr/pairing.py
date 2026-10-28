"""Approve-on-the-other-PC pairing.

The app on this PC (the requester) wants to manage a computer whose app is already connected to this
PC's server (it is a client here). Instead of asking for that computer's Windows password, the app
files a request here; the other computer's app sees it while polling this server, asks its user
Allow / Deny, adds the requester's public key there itself, and answers. The requester's app then logs
in with its key.

Safety: a request only carries a public key, expires after two minutes, there is at most one per
computer, nothing is installed without a click on the other computer, and every step is logged. The
request names who really filed it (`from`: the connection it came through, e.g. another computer
connected here, not simply this PC) and that connection's address, so the prompt can't be made to look
like it comes from this PC. Only the SSH Manager app (app secret) can answer, never an AI agent. After a
Deny, the same requester can't ask the same computer again for DENY_BACKOFF seconds."""
import secrets
import threading
import time

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel, Field

from . import agents as agents_mod, keys

TTL = 120
DENY_BACKOFF = 60


class PairRequestBody(BaseModel):
    target: str = Field(min_length=1, max_length=64)      # the other computer's name (its COMPUTERNAME)
    public_key: str = Field(min_length=20, max_length=16_000)
    machine_id: str = Field("", max_length=64)          # the requesting app's install id
    target_machine_id: str = Field("", max_length=64)   # the other computer's app install id, when known


class PairAnswerBody(BaseModel):
    machine: str = Field(min_length=1, max_length=64)
    allowed: bool
    user: str = Field("", max_length=104)
    error: str = Field("", max_length=500)
    machine_id: str = Field("", max_length=64)          # the answering app's install id


def make_router(activity, host_name, who, address_of=lambda request: None) -> APIRouter:
    """who(request): the requesting connection's name; address_of(request): its SSH session's IP (None
    for a program on this PC)."""
    r = APIRouter(prefix="/api/pair")
    lock = threading.Lock()
    requests: dict[str, dict] = {}
    denied: dict[tuple[str, str], float] = {}   # (from, target) -> when (monotonic) it was denied

    def state(q: dict) -> str:
        if q["answer"] is not None:
            return "allowed" if q["answer"]["allowed"] else "denied"
        return "expired" if time.monotonic() > q["expires"] else "pending"

    def prune():
        now = time.monotonic()
        for k in [k for k, q in requests.items() if now > q["expires"] + 600]:
            del requests[k]
        for k in [k for k, t in denied.items() if now - t > DENY_BACKOFF]:
            del denied[k]

    @r.post("/request")
    def create(request: Request, body: PairRequestBody):
        try:
            key = keys.validate_public_key(body.public_key)
        except keys.KeyError_ as e:
            raise HTTPException(400, str(e))
        target = body.target.strip()
        requester = who(request)
        address = address_of(request)
        with lock:
            prune()
            if (when := denied.get((requester.lower(), target.lower()))) is not None:
                wait = round(DENY_BACKOFF - (time.monotonic() - when))
                raise HTTPException(429, f"{target} just denied a request from {requester}. Try again in {max(wait, 1)} seconds.")
            for q in requests.values():   # one open request per computer: a new one replaces it
                if q["target"].lower() == target.lower() and state(q) == "pending":
                    q["expires"] = 0
            rid = secrets.token_urlsafe(12)
            requests[rid] = {"id": rid, "target": target, "from": requester, "address": address,
                             "machine_id": body.machine_id.strip() or None,
                             "target_machine_id": body.target_machine_id.strip() or None,
                             "key": " ".join(body.public_key.split()), "fingerprint": key["fingerprint"],
                             "expires": time.monotonic() + TTL, "answer": None}
        activity.add("setup", action="pair-requested", client=requester, target=target, fingerprint=key["fingerprint"],
                     **({"address": address} if address else {}))
        return {"id": rid, "expires_in": TTL}

    def for_machine(q: dict, machine: str, machine_id: str) -> bool:
        """The request is meant for that computer (by name, and by install id when both sides give one)."""
        if q["target"].lower() != machine.strip().lower():
            return False
        return not (q["target_machine_id"] and machine_id.strip() and q["target_machine_id"] != machine_id.strip())

    @r.get("/pending")
    def pending(machine: str, machine_id: str = ""):
        """Polled by the apps connected here: an open request for that computer, if any."""
        with lock:
            for q in requests.values():
                if for_machine(q, machine, machine_id) and state(q) == "pending":
                    return {"request": {"id": q["id"], "from": q["from"], "address": q["address"],
                                        "machine_id": q["machine_id"], "public_key": q["key"],
                                        "fingerprint": q["fingerprint"], "expires_in": round(q["expires"] - time.monotonic()),
                                        "server": host_name()}}
        return {"request": None}

    @r.post("/{rid}/answer")
    def answer(request: Request, rid: str, body: PairAnswerBody):
        agents_mod.require_app(request, "answer pairing requests")
        with lock:
            q = requests.get(rid)
            if q is None or not for_machine(q, body.machine, body.machine_id):
                raise HTTPException(404, "No such request.")
            if state(q) != "pending":
                raise HTTPException(410, "This request has expired.")
            q["answer"] = {"allowed": body.allowed, "user": body.user.strip(), "error": body.error.strip()}
            if not body.allowed:
                denied[(q["from"].lower(), q["target"].lower())] = time.monotonic()
        activity.add("setup", action="pair-allowed" if body.allowed else "pair-denied", client=who(request),
                     target=q["target"], fingerprint=q["fingerprint"], requested_by=q["from"],
                     **({"answered_by_id": body.machine_id} if body.machine_id else {}))
        return {"ok": True}

    @r.get("/{rid}")
    def status(rid: str):
        with lock:
            q = requests.get(rid)
            if q is None:
                raise HTTPException(404, "No such request.")
            a = q["answer"] or {}
            return {"state": state(q), "user": a.get("user", ""), "error": a.get("error", ""),
                    "expires_in": max(0, round(q["expires"] - time.monotonic()))}

    @r.post("/{rid}/cancel")
    def cancel(rid: str):
        with lock:
            q = requests.get(rid)
            if q is not None and state(q) == "pending":
                q["expires"] = 0
        return {"ok": True}

    return r
