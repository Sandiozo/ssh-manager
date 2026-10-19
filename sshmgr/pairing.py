"""Approve-on-the-other-PC pairing.

The app on this PC (the requester) wants to manage a computer whose app is already connected to this
PC's server (it is a client here). Instead of asking for that computer's Windows password, the app
files a request here; the other computer's app sees it while polling this server, asks its user
Allow / Deny, adds the requester's public key there itself, and answers. The requester's app then logs
in with its key.

Safety: a request only carries a public key, expires after two minutes, there is at most one per
computer, nothing is installed without a click on the other computer, and every step is logged."""
import secrets
import threading
import time

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel, Field

from . import keys

TTL = 120


class PairRequestBody(BaseModel):
    target: str = Field(min_length=1, max_length=64)      # the other computer's name (its COMPUTERNAME)
    public_key: str = Field(min_length=20, max_length=16_000)


class PairAnswerBody(BaseModel):
    machine: str = Field(min_length=1, max_length=64)
    allowed: bool
    user: str = Field("", max_length=104)
    error: str = Field("", max_length=500)


def make_router(activity, host_name, who) -> APIRouter:
    r = APIRouter(prefix="/api/pair")
    lock = threading.Lock()
    requests: dict[str, dict] = {}

    def state(q: dict) -> str:
        if q["answer"] is not None:
            return "allowed" if q["answer"]["allowed"] else "denied"
        return "expired" if time.time() > q["expires"] else "pending"

    def prune():
        now = time.time()
        for k in [k for k, q in requests.items() if now > q["expires"] + 600]:
            del requests[k]

    @r.post("/request")
    def create(request: Request, body: PairRequestBody):
        try:
            key = keys.validate_public_key(body.public_key)
        except keys.KeyError_ as e:
            raise HTTPException(400, str(e))
        target = body.target.strip()
        with lock:
            prune()
            for q in requests.values():   # one open request per computer: a new one replaces it
                if q["target"].lower() == target.lower() and state(q) == "pending":
                    q["expires"] = 0
            rid = secrets.token_urlsafe(12)
            requests[rid] = {"id": rid, "target": target, "from": host_name(), "key": " ".join(body.public_key.split()),
                             "fingerprint": key["fingerprint"], "expires": time.time() + TTL, "answer": None}
        activity.add("setup", action="pair-requested", client=who(request), target=target, fingerprint=key["fingerprint"])
        return {"id": rid, "expires_in": TTL}

    @r.get("/pending")
    def pending(machine: str):
        """Polled by the apps connected here: an open request for that computer, if any."""
        with lock:
            for q in requests.values():
                if q["target"].lower() == machine.strip().lower() and state(q) == "pending":
                    return {"request": {"id": q["id"], "from": q["from"], "public_key": q["key"],
                                        "fingerprint": q["fingerprint"], "expires_in": round(q["expires"] - time.time())}}
        return {"request": None}

    @r.post("/{rid}/answer")
    def answer(request: Request, rid: str, body: PairAnswerBody):
        with lock:
            q = requests.get(rid)
            if q is None or q["target"].lower() != body.machine.strip().lower():
                raise HTTPException(404, "No such request.")
            if state(q) != "pending":
                raise HTTPException(410, "This request has expired.")
            q["answer"] = {"allowed": body.allowed, "user": body.user.strip(), "error": body.error.strip()}
        activity.add("setup", action="pair-allowed" if body.allowed else "pair-denied", client=who(request),
                     target=q["target"], fingerprint=q["fingerprint"])
        return {"ok": True}

    @r.get("/{rid}")
    def status(rid: str):
        with lock:
            q = requests.get(rid)
            if q is None:
                raise HTTPException(404, "No such request.")
            a = q["answer"] or {}
            return {"state": state(q), "user": a.get("user", ""), "error": a.get("error", ""),
                    "expires_in": max(0, round(q["expires"] - time.time()))}

    @r.post("/{rid}/cancel")
    def cancel(rid: str):
        with lock:
            q = requests.get(rid)
            if q is not None and state(q) == "pending":
                q["expires"] = 0
        return {"ok": True}

    return r
