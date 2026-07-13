"""HTTP wrapper around the local Go client.

The Go client listens on 127.0.0.1:<port>. We try a list of candidate ports
(matching the Blender addon so a single client can serve both hosts) and
remember the first one that answers.

All calls are synchronous here. Callers that must not block the UI should run
them on a worker thread and marshal results back to the UI via Eto's
`Application.Instance.AsyncInvoke`.
"""
from __future__ import annotations

import json
import logging
import urllib.error
import urllib.request
from typing import Any

log = logging.getLogger(__name__)

# Same port order the Blender addon uses — see client_lib.py in the Blender side.
CANDIDATE_PORTS: tuple[int, ...] = (62485, 65425, 55428, 49452, 35452, 25152, 5152, 1234)

# Filled in on first successful ping.
_active_port: int | None = None
_app_id: str | None = None


def set_app_id(app_id: str) -> None:
    """Set the unique id for this Rhino instance (used by /report routing)."""
    global _app_id
    _app_id = app_id


def _url(path: str, port: int | None = None) -> str:
    p = port or _active_port
    if p is None:
        raise RuntimeError("Go client port not discovered yet — call discover_port() first.")
    return f"http://127.0.0.1:{p}{path}"


def discover_port(timeout: float = 0.5) -> int | None:
    """Try each candidate port, return the first one where the client answers.

    Sets `_active_port` as a side effect. Returns None if no client is reachable.
    """
    global _active_port
    for port in CANDIDATE_PORTS:
        try:
            req = urllib.request.Request(f"http://127.0.0.1:{port}/", method="GET")
            with urllib.request.urlopen(req, timeout=timeout):
                _active_port = port
                log.info("Blendkit client found on port %d", port)
                return port
        except (urllib.error.URLError, OSError):
            continue
    return None


def _request(method: str, path: str, payload: dict[str, Any] | None = None,
             timeout: float = 30.0) -> dict[str, Any]:
    if _active_port is None:
        if discover_port() is None:
            raise RuntimeError("No Blendkit client reachable on any candidate port.")
    body = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(
        _url(path), data=body, method=method,
        headers={"Content-Type": "application/json"} if body else {},
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read()
    return json.loads(raw) if raw else {}


# ---------------------------------------------------------------------------
# Endpoint wrappers. Paths are prefixed /blender/... today for historical
# reasons — the Go client treats them as host-agnostic, so we reuse them. If
# the Go client later adds /rhino/... aliases, swap them in here.
# ---------------------------------------------------------------------------

def report(api_key: str, addon_version: str = "0.1.0",
           project_name: str = "") -> dict[str, Any]:
    """Drain pending task results for this host instance."""
    params = {
        "app_id": _app_id or "",
        "api_key": api_key,
        "addon_version": addon_version,
        "software": "rhino",
        "project_name": project_name,
    }
    qs = "&".join(f"{k}={urllib.parse.quote(str(v))}" for k, v in params.items())
    return _request("GET", f"/report?{qs}", timeout=5.0)


def asset_search(search_data: dict[str, Any]) -> dict[str, Any]:
    return _request("POST", "/blender/asset_search", search_data)


def asset_download(download_data: dict[str, Any]) -> dict[str, Any]:
    return _request("POST", "/blender/asset_download", download_data)


def cancel_download(task_id: str) -> dict[str, Any]:
    return _request("GET", f"/blender/cancel_download?task_id={task_id}")


def get_user_profile(api_key: str) -> dict[str, Any]:
    return _request("GET", f"/profiles/get_user_profile?api_key={api_key}")


def get_rating(asset_id: str) -> dict[str, Any]:
    return _request("GET", f"/ratings/get_rating?asset_id={asset_id}")


def send_rating(asset_id: str, rating_type: str, rating_value: float) -> dict[str, Any]:
    return _request("POST", "/ratings/send_rating", {
        "asset_id": asset_id,
        "rating_type": rating_type,
        "rating_value": rating_value,
    })


def get_bookmarks(api_key: str) -> dict[str, Any]:
    return _request("GET", f"/ratings/get_bookmarks?api_key={api_key}")


# (comments, notifications endpoints intentionally omitted for v1 — we link
#  to the website instead; see RHINO_PORT_ARCHITECTURE.md §Scope.)
