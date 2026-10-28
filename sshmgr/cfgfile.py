"""config.json: reading it tolerantly and saving it safely.

- Saves hold one lock around "change the settings + write the file", write a uniquely named temp file
  and swap it in, so two saves at once can't mix or lose each other's changes.
- config.json.bak is a copy of the last settings that were read or saved successfully.
- If config.json can't be used at startup (invalid JSON, not an object, unreadable; files saved as UTF-16
  by Notepad or PowerShell are read fine), the server logs why, keeps a copy as config.json.broken, and
  runs with config.json.bak (or the defaults) instead of not starting at all."""
import codecs
import json
import logging
import os
import secrets
import shutil
import threading
from pathlib import Path

log = logging.getLogger("sshmgr")
lock = threading.RLock()


def decode(data: bytes) -> str:
    if data.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE)):
        return data.decode("utf-16")
    if data.startswith(codecs.BOM_UTF8):
        return data[3:].decode("utf-8")
    if len(data) >= 4 and data[1] == 0 and data[3] == 0:   # UTF-16 LE without a BOM
        return data.decode("utf-16-le")
    return data.decode("utf-8")


def read(path: Path) -> dict:
    """The settings in path; ValueError (or OSError) when they can't be used."""
    value = json.loads(decode(path.read_bytes()))
    if not isinstance(value, dict):
        raise ValueError("it must contain a JSON object ({ ... })")
    return value


def problem(path: Path) -> str | None:
    """Why path can't be used as config.json, or None when it can."""
    try:
        read(path)
        return None
    except (OSError, ValueError) as e:
        return str(e)


def _write(path: Path, text: str):
    tmp = path.with_name(f"{path.name}.{os.getpid()}-{secrets.token_hex(4)}.tmp")
    try:
        tmp.write_text(text, encoding="utf-8")
        os.replace(tmp, path)
    finally:
        tmp.unlink(missing_ok=True)


def _text(config: dict) -> str:
    return json.dumps(config, indent=2, ensure_ascii=False) + "\n"


def backup(path: Path, config: dict):
    """Keeps config.json.bak = these (good) settings."""
    bak = path.with_name(path.name + ".bak")
    text = _text(config)
    try:
        if not bak.exists() or bak.read_text(encoding="utf-8", errors="replace") != text:
            _write(bak, text)
    except OSError as e:
        log.warning("couldn't write %s: %s", bak, e)


def load(path: Path, example: Path) -> tuple[dict, str | None]:
    """(settings, problem): problem says what was wrong with config.json and what is used instead."""
    if not path.exists():
        shutil.copyfile(example, path)
    try:
        return read(path), None
    except (OSError, ValueError) as e:
        why = f"config.json can't be used ({e})"
    broken = path.with_name(path.name + ".broken")
    try:
        shutil.copyfile(path, broken)
    except OSError:
        pass
    bak = path.with_name(path.name + ".bak")
    try:
        return read(bak), f"{why}; using {bak.name} (the last good settings) until it is fixed or saved again"
    except (OSError, ValueError):
        pass
    return read(example), f"{why}; using the default settings until it is fixed or saved again"


def save(config: dict, path: Path):
    """Writes config.json (call it while holding `lock`, together with the change)."""
    with lock:
        _write(path, _text(config))
        backup(path, config)
