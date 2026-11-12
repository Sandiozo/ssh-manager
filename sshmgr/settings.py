"""Settings API: the computer list, key labels, file shortcuts and display name (config.json), plus the
SSH keys allowed to log in (administrators_authorized_keys)."""
import ipaddress
import os
import re
from pathlib import Path

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel, Field

from . import cfgfile, keys, wol
from .activity import ActivityLog
from .runner import MAX_RUN_SECONDS, time_limit


class Computer(BaseModel):
    name: str = Field(min_length=1, max_length=40)
    host: str = Field("", max_length=100)
    ip: str = Field("", max_length=45)
    mac: str = Field("", max_length=32)


class Action(BaseModel):
    id: str = Field("", max_length=200)   # saved cut to 40 (new_action_id): an older too-long id doesn't block saving
    name: str = Field(min_length=1, max_length=60)
    script: str = Field(min_length=1, max_length=100_000)
    group: str = Field("", max_length=40)
    confirm: bool = False
    timeout: float = Field(0, ge=0, le=MAX_RUN_SECONDS, allow_inf_nan=False)   # seconds, 0: no limit


class LogEntry(BaseModel):
    name: str = Field(min_length=1, max_length=60)
    path: str = Field(min_length=1, max_length=500)


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
    actions: list[Action] = []
    logs: list[LogEntry] = []
    screen_preview: bool = True
    screen_apps: list[str] | None = None        # None (older dashboards): keep what is there
    screen_monitors: bool | None = None
    ai_access: str | None = Field(None, pattern="^(off|read|full)$")   # None (older dashboards): keep it


class AddShortcutBody(BaseModel):
    name: str = Field("", max_length=500)    # cut to 60; empty: the folder's name
    path: str = Field(min_length=1, max_length=500)


class RemoveShortcutBody(BaseModel):
    path: str = Field(min_length=1, max_length=500)


def actions_shown(actions: list) -> list[dict]:
    """The quick actions as answers show them: a time limit in config.json that can't be used (negative, NaN,
    not a number) is left out and one over the maximum is cut to it, so a hand-edited file can't break the
    answers (NaN can't be sent as JSON) or stop an action the moment it starts."""
    out = []
    for a in actions:
        if isinstance(a, dict) and "timeout" in a and (t := time_limit(a["timeout"])) != a["timeout"]:
            a = {k: v for k, v in a.items() if k != "timeout"} | ({"timeout": t} if t else {})
        out.append(a)
    return out


def folder_key(path: str) -> str:
    """Where a folder path leads, for comparing: environment variables expanded, / and \\ alike, "." and ".."
    worked out, no trailing separator, any case (C:\\Windows\\, C:/Windows and %WINDIR% are one folder)."""
    p = os.path.expandvars(path.strip())
    return os.path.normcase(os.path.normpath(p)) if p else ""


def same_folder(a: str, b: str) -> bool:
    return bool(folder_key(a)) and folder_key(a) == folder_key(b)


def has_shortcut(config: dict, path: str) -> bool:
    """A folder shortcut leads to this folder (the star in Files)."""
    return any(same_folder(b.get("path", ""), path) for b in config.get("bookmarks", []))


ACTION_ID_MAX = 40


def new_action_id(wanted: str, name: str, used: set) -> str:
    """A quick action's id: `wanted` (the id the page sent back) or one made from the name, cut to
    ACTION_ID_MAX characters, with -2, -3, ... when it is taken. Never longer than ACTION_ID_MAX, so an id
    that is too long in config.json (older versions could make one) is shortened when the settings are saved."""
    base = wanted or re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-") or "action"
    aid, n = base[:ACTION_ID_MAX], 2
    while aid in used:
        suffix = f"-{n}"
        aid = base[:ACTION_ID_MAX - len(suffix)].rstrip("-") + suffix
        n += 1
    return aid


class AddKeyBody(BaseModel):
    public_key: str = Field(min_length=20, max_length=20000)
    label: str = Field("", max_length=60)


class RemoveKeyBody(BaseModel):
    fingerprint: str


def make_router(config: dict, config_path: Path, data_dir: Path, activity: ActivityLog, monitor,
                who) -> APIRouter:
    r = APIRouter(prefix="/api")

    def save():
        cfgfile.save(config, config_path)   # callers hold cfgfile.lock around their change + save

    def store(changes: dict):
        """Writes config.json with these changes and only then uses them, so a write that fails (config.json
        held open by another program, a full disk) changes nothing and trying again does it again. Callers
        hold cfgfile.lock."""
        try:
            cfgfile.save({**config, **changes}, config_path)
        except OSError as e:
            reason = (e.strerror or str(e)).strip().rstrip(".")
            raise HTTPException(503, f"Couldn't save the settings ({reason}). Nothing was changed; try again.")
        config.update(changes)

    def current() -> dict:
        return {"name": config.get("name", ""), "computer_name": os.environ.get("COMPUTERNAME", ""),
                "peers": config.get("peers", []), "keys": config.get("keys", {}),
                "bookmarks": config.get("bookmarks", []), "default_upload": config.get("default_upload", ""),
                "auto_update": config.get("auto_update", True), "port": config.get("port", 8765),
                "actions": actions_shown(config.get("actions", [])), "logs": config.get("logs", []),
                "screen_preview": config.get("screen_preview", True), "screen_apps": config.get("screen_apps", []),
                "screen_monitors": config.get("screen_monitors", True), "ai_access": config.get("ai_access", "full")}

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
            if p.mac.strip():
                try:
                    wol.normalize(p.mac)
                except ValueError as e:
                    raise HTTPException(400, f"{p.name}: {e}")
        with cfgfile.lock:
            before = current()
            store(changes(body))
            after = current()
        monitor.reload_peers()
        changed = [k for k in ("name", "peers", "keys", "bookmarks", "default_upload", "auto_update", "actions", "logs",
                               "screen_preview", "screen_apps", "screen_monitors", "ai_access") if before[k] != after[k]]
        if changed:
            activity.add("setup", action="settings", client=who(request), changed=changed)
        return after

    def changes(body: SettingsBody) -> dict:
        """The settings a full save sets."""
        new = {"name": body.name.strip(),
               "peers": [{"name": p.name.strip(), "host": p.host.strip(), "ip": p.ip.strip(),
                          **({"mac": wol.normalize(p.mac)} if p.mac.strip() else {})} for p in body.peers],
               "keys": {fp: label.strip() for fp, label in body.keys.items() if label.strip()},
               "bookmarks": [{"name": b.name.strip(), "path": b.path.strip()} for b in body.bookmarks],
               "default_upload": body.default_upload.strip(),
               "auto_update": body.auto_update}
        # ids that can stay are kept first, so an id that has to change (new, taken or too long) can't take one
        kept, used = {}, set()
        for i, a in enumerate(body.actions):
            if a.id and len(a.id) <= ACTION_ID_MAX and a.id not in used:
                kept[i] = a.id
                used.add(a.id)
        actions = []
        for i, a in enumerate(body.actions):
            aid = kept.get(i) or new_action_id(a.id, a.name, used)
            used.add(aid)
            actions.append({"id": aid, "name": a.name.strip(), "script": a.script, "group": a.group.strip(),
                            "confirm": a.confirm, **({"timeout": a.timeout} if a.timeout else {})})
        new["actions"] = actions
        new["logs"] = [{"name": l.name.strip(), "path": l.path.strip()} for l in body.logs]
        new["screen_preview"] = body.screen_preview
        if body.screen_apps is not None:
            new["screen_apps"] = sorted({a.strip().removesuffix(".exe") for a in body.screen_apps if a.strip()}, key=str.lower)
        if body.screen_monitors is not None:
            new["screen_monitors"] = body.screen_monitors
        if body.ai_access is not None:
            new["ai_access"] = body.ai_access
        return new

    # ---- one item at a time (the star in Files, "Save as quick action"): changes only that item, on top
    # of the settings as they are now, so changes made meanwhile elsewhere are kept ----
    def noted(request: Request, what: str):
        activity.add("setup", action="settings", client=who(request), changed=[what])

    @r.post("/settings/bookmarks/add")
    def add_shortcut(request: Request, body: AddShortcutBody):
        """Adds a folder shortcut (nothing changes when one leads to that folder already). `shortcut`: the one
        added (None when nothing changed); `starred`: a shortcut leads to the folder now."""
        path = body.path.strip()
        if not path:
            raise HTTPException(400, "Choose a folder.")
        name = (body.name.strip() or path.rstrip("\\/").replace("/", "\\").split("\\")[-1] or path)[:60].strip()
        with cfgfile.lock:
            shortcut = None if has_shortcut(config, path) else {"name": name, "path": path}
            if shortcut:
                store({"bookmarks": [*config.get("bookmarks", []), shortcut]})
            after = current()
        if shortcut:
            noted(request, "bookmarks")
        return {"settings": after, "changed": bool(shortcut), "shortcut": shortcut, "starred": True}

    @r.post("/settings/bookmarks/remove")
    def remove_shortcut(request: Request, body: RemoveShortcutBody):
        """Removes every shortcut that leads to a folder (nothing changes when none does). `removed`: those
        shortcuts, as they were saved."""
        with cfgfile.lock:
            shortcuts = config.get("bookmarks", [])
            removed = [b for b in shortcuts if same_folder(b.get("path", ""), body.path)]
            if removed:
                store({"bookmarks": [b for b in shortcuts if not same_folder(b.get("path", ""), body.path)]})
            after = current()
        if removed:
            noted(request, "bookmarks")
        return {"settings": after, "changed": bool(removed), "removed": removed, "starred": False}

    @r.post("/settings/actions/add")
    def add_action(request: Request, body: Action):
        """Adds a quick action at the end of the list, with a new id."""
        if not body.name.strip():
            raise HTTPException(400, "Give the quick action a name.")
        if not body.script.strip():
            raise HTTPException(400, "The script is empty.")
        with cfgfile.lock:
            actions = config.get("actions", [])
            a = {"id": new_action_id("", body.name, {x.get("id") for x in actions}), "name": body.name.strip(),
                 "script": body.script, "group": body.group.strip(), "confirm": body.confirm,
                 **({"timeout": body.timeout} if body.timeout else {})}
            store({"actions": [*actions, a]})
            after = current()
        noted(request, "actions")
        return {"settings": after, "action": a}

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
            with cfgfile.lock:
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
        with cfgfile.lock:
            label = config.get("keys", {}).pop(body.fingerprint, "")
            save()
        activity.add("setup", action="key-removed", client=who(request), fingerprint=body.fingerprint, label=label)
        return {"removed": body.fingerprint}

    return r
