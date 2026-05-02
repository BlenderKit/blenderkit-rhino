"""Download orchestration.

Each download is a /blender/asset_download call that returns a task_id.
We then watch /report for the matching task_id; when it reports `finished`,
we read `file_paths` and hand the first entry to `import_gltf.import_file()`.

For Rhino we prefer `.glb` over `.gltf` (self-contained, easier to move) and
set the `file_format` field on the request accordingly. The Go client needs to
honor this; if it currently only serves `.blend`, a server-side conversion is
a prerequisite (see RHINO_PORT_ARCHITECTURE.md §Open questions).
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from . import client_lib


@dataclass
class DownloadRequest:
    asset_id: str
    asset_type: str
    resolution: str = "2K"  # 512 | 1K | 2K | 4K | 8K | ORIGINAL
    file_format: str = "glb"  # glb | gltf
    target_point: tuple[float, float, float] | None = None


def start(req: DownloadRequest, api_key: str, addon_version: str) -> str:
    """Queue a download. Returns a task_id the caller can watch on /report."""
    payload: dict[str, Any] = {
        "PREFS": {
            "api_key": api_key,
            "addon_version": addon_version,
            "software": "rhino",
        },
        "asset_data": {
            "id": req.asset_id,
            "asset_type": req.asset_type,
        },
        "resolution": req.resolution,
        "file_format": req.file_format,
        "import_method": "IMPORT",  # Rhino-specific hint
    }
    if req.target_point is not None:
        payload["target_point"] = list(req.target_point)
    resp = client_lib.asset_download(payload)
    return resp.get("task_id", "")


def cancel(task_id: str) -> None:
    client_lib.cancel_download(task_id)
