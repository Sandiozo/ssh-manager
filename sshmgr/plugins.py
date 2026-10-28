"""Plugins: Python files in <home>/plugins/ that are loaded at startup.

Each plugin defines `setup(ctx)` and can use:
    ctx.config, ctx.home, ctx.monitor, ctx.activity, ctx.app (the FastAPI app, for extra routes)
    ctx.add_decoder(fn)          fn(rec) -> None. Called when a command starts in an SSH session; may
                                 change rec["kind"], rec["summary"] and rec["script"] (e.g. unwrap a
                                 script that a helper sent in an encoded form).
    ctx.on_command_finished(fn)  fn(rec) -> dict | None. Extra fields for the activity entry when the
                                 command ends (e.g. output the command left in a file).
See plugins/example.py in the repository.
"""
import importlib.util
import logging
from pathlib import Path

log = logging.getLogger("sshmgr.plugins")


class PluginContext:
    def __init__(self, config: dict, home: Path, monitor, activity, app):
        self.config, self.home, self.monitor, self.activity, self.app = config, home, monitor, activity, app

    def add_decoder(self, fn):
        self.monitor.decoders.append(fn)

    def on_command_finished(self, fn):
        self.monitor.finish_hooks.append(fn)


def unsafe(folder: Path, files: list[Path]) -> str | None:
    """Why plugins can't be trusted here: the plugins folder (or the home folder around it, or a plugin
    file) can be changed by an account other than this user, Administrators and SYSTEM. The server runs
    plugins with its own (admin) rights, so such an account could take over this PC."""
    from . import winsec
    try:
        trusted = winsec.trusted_sids(winsec.current_user_sid())
    except OSError as e:
        return f"can't tell which account this server runs as ({e})"
    for p in [folder.parent, folder, *files]:
        if sids := winsec.others_with(str(p), trusted, winsec.WRITE_BITS):
            who = ", ".join(winsec.account_name(s) for s in sids)
            return f"{p} can be changed by {who}"
    return None


def load(ctx: PluginContext) -> list[dict]:
    loaded = []
    folder = ctx.home / "plugins"
    files = [p for p in sorted(folder.glob("*.py")) if not p.name.startswith("_")] if folder.is_dir() else []
    if files and (why := unsafe(folder, files)):
        log.error("plugins not loaded: %s. Only this user, Administrators and SYSTEM may be able to change the "
                  "plugins (the server runs them with its rights); fix the folder's permissions and restart.", why)
        return [{"name": p.stem, "ok": False, "description": f"not loaded: {why} (see server.log)"} for p in files]
    for path in files:
        try:
            spec = importlib.util.spec_from_file_location(f"sshmgr_plugin_{path.stem}", path)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            module.setup(ctx)
            loaded.append({"name": path.stem, "ok": True, "description": (module.__doc__ or "").strip().split("\n")[0]})
            log.info("loaded plugin %s", path.name)
        except Exception as e:
            log.exception("plugin %s failed", path.name)
            loaded.append({"name": path.stem, "ok": False, "description": f"failed to load: {e}"})
    return loaded
