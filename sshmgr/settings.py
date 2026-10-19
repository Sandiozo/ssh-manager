"""Settings API: the computer list, key labels, file shortcuts and display name (config.json), plus the
SSH keys allowed to log in (administrators_authorized_keys)."""
import ipaddress
import json
import os
from pathlib import Path

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel, Field

from . import keys
from .activity import ActivityLog


class Computer(BaseModel):
    name: str = Field(min_length=1, max_length=40)
    host: str = Field("", max_length=100)
    ip: str = Field("", max_length=45)


class Shortcut(BaseModel):
    name: str = Field(min_length=1, max_length=60)
    path: str = Field(min_length=1, max_length=500)


class SettingsBody(BaseModel):
    name: str = Field("", max_length=40)
    peers: list[Computer]
    keys: dict[str, str]
    bookmarks: list[Shortcut]
    default_upload: str = Field("", max_length=500)
    auto_update: bool = True


class AddKeyBody(BaseModel):
    public_key: str = Field(min_length=20, max_length=20000)
    label: str = Field("", max_length=60)


class RemoveKeyBody(BaseModel):
    fingerprint: str


def make_router(config: dict, config_path: Path, data_dir: Path, activity: ActivityLog, monitor,
                who) -> APIRouter:
    r = APIRouter(prefix="/api")

    def save():
        tmp = config_path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(config, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        os.replace(tmp, config_path)

    def current() -> dict:
        return {"name": config.get("name", ""), "computer_name": os.environ.get("COMPUTERNAME", ""),
                "peers": config.get("peers", []), "keys": config.get("keys", {}),
                "bookmarks": config.get("bookmarks", []), "default_upload": config.get("default_upload", ""),
                "auto_update": config.get("auto_update", True), "port": config.get("port", 8765)}

    @r.get("/settings")
    def get_settings():
        return current()

    @r.put("/settings")
    def put_settings(request: Request, body: SettingsBody):
        names = [p.name.strip() for p in body.peers]
        if len(set(n.lower() for n in names)) != len(names):
            raise HTTPException(400, "Two computers have the same name.")
        for p in body.peers:
            if not (p.host.strip() or p.ip.strip()):
                raise HTTPException(400, f"{p.name}: enter a computer name or an IP address.")
            if p.ip.strip():
                try:
                    ipaddress.ip_address(p.ip.strip())
                except ValueError:
                    raise HTTPException(400, f"{p.name}: \"{p.ip}\" is not an IP address.")
        before = current()
        config["name"] = body.name.strip()
        config["peers"] = [{"name": p.name.strip(), "host": p.host.strip(), "ip": p.ip.strip()} for p in body.peers]
        config["keys"] = {fp: label.strip() for fp, label in body.keys.items() if label.strip()}
        config["bookmarks"] = [{"name": b.name.strip(), "path": b.path.strip()} for b in body.bookmarks]
        config["default_upload"] = body.default_upload.strip()
        config["auto_update"] = body.auto_update
        save()
        monitor.reload_peers()
        changed = [k for k in ("name", "peers", "keys", "bookmarks", "default_upload", "auto_update") if before[k] != current()[k]]
        if changed:
            activity.add("setup", action="settings", client=who(request), changed=changed)
        return current()

    @r.get("/keys")
    def get_keys():
        data = keys.list_keys()
        usage = monitor.key_usage()
        labels = config.get("keys", {})
        for k in data["keys"]:
            u = usage.get(k["fingerprint"], {})
            k.update(label=labels.get(k["fingerprint"], ""), last_used=u.get("last"),
                     last_client=u.get("client"), in_use=u.get("open", False))
        return data

    @r.post("/keys/add")
    def add_key(request: Request, body: AddKeyBody):
        try:
            k = keys.add_key(body.public_key, data_dir)
        except keys.KeyError_ as e:
            raise HTTPException(400, str(e))
        if body.label.strip():
            config.setdefault("keys", {})[k["fingerprint"]] = body.label.strip()
            save()
        activity.add("setup", action="key-added", client=who(request), fingerprint=k["fingerprint"],
                     label=body.label.strip() or k["comment"])
        return k

    @r.post("/keys/remove")
    def remove_key(request: Request, body: RemoveKeyBody):
        if monitor.key_usage().get(body.fingerprint, {}).get("open"):
            raise HTTPException(409, "This key is being used by an open SSH session right now (possibly the "
                                     "connection you are using). Removing it would cut that computer off.")
        try:
            keys.remove_key(body.fingerprint, data_dir)
        except keys.KeyError_ as e:
            raise HTTPException(400, str(e))
        label = config.get("keys", {}).pop(body.fingerprint, "")
        save()
        activity.add("setup", action="key-removed", client=who(request), fingerprint=body.fingerprint, label=label)
        return {"removed": body.fingerprint}

    return r
