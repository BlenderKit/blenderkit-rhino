"""Plugin preferences — persisted via Rhino's PlugIn.Settings and a JSON file.

Rhino's PlugIn.Settings writes to the registry on Windows and a plist on macOS.
That's fine for non-secrets. For the API key we prefer a JSON file under
%APPDATA%\\BlenderKit\\config.json so the same file can be shared with the
Blender addon on the same machine.
"""
from __future__ import annotations

import json
import os
from pathlib import Path

DEFAULT_GLOBAL_DIR = Path.home() / ".blenderkit"
DEFAULT_PORT = 62485


def _config_path() -> Path:
    appdata = os.environ.get("APPDATA")
    root = Path(appdata) if appdata else Path.home()
    return root / "BlenderKit" / "config.json"


def load() -> dict:
    p = _config_path()
    if not p.exists():
        return {}
    try:
        return json.loads(p.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return {}


def save(settings: dict) -> None:
    p = _config_path()
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(settings, indent=2), encoding="utf-8")


def get_api_key() -> str:
    return load().get("api_key", "")


def set_api_key(key: str) -> None:
    s = load()
    s["api_key"] = key
    save(s)


def get_global_dir() -> Path:
    s = load()
    return Path(s.get("global_dir", str(DEFAULT_GLOBAL_DIR)))


def get_client_port() -> int:
    return int(load().get("client_port", DEFAULT_PORT))
