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


def load(ctx: PluginContext) -> list[dict]:
    loaded = []
    folder = ctx.home / "plugins"
    for path in sorted(folder.glob("*.py")) if folder.is_dir() else []:
        if path.name.startswith("_"):
            continue
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
