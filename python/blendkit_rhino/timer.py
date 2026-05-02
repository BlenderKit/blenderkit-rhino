"""Background poll loop for the Go client's /report endpoint.

The Blender addon uses `bpy.app.timers`; in Rhino we run a daemon thread that
polls every ~0.2s and dispatches results to handlers on the UI thread via
Eto's `Application.Instance.AsyncInvoke`.
"""
from __future__ import annotations

import logging
import threading
import time
from typing import Callable

from . import client_lib, preferences

log = logging.getLogger(__name__)

POLL_INTERVAL_S = 0.2


class ReportPoller:
    """Background thread pulling /report and dispatching task results.

    Usage:
        p = ReportPoller(on_tasks=handle_tasks)
        p.start()
        ...
        p.stop()
    """

    def __init__(self, on_tasks: Callable[[list[dict]], None], addon_version: str):
        self._on_tasks = on_tasks
        self._addon_version = addon_version
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._loop, name="BlenderKitPoller", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=2.0)

    def _loop(self) -> None:
        while not self._stop.is_set():
            try:
                reports = client_lib.report(
                    api_key=preferences.get_api_key(),
                    addon_version=self._addon_version,
                )
                tasks = reports.get("tasks", []) if isinstance(reports, dict) else []
                if tasks:
                    self._dispatch(tasks)
            except Exception as e:  # pragma: no cover — defensive
                log.debug("Report poll failed: %s", e)
            self._stop.wait(POLL_INTERVAL_S)

    def _dispatch(self, tasks: list[dict]) -> None:
        """Marshal task dispatch to the UI thread via Eto's AsyncInvoke.

        Outside Rhino (e.g. in tests), falls back to direct call.
        """
        try:
            from Eto.Forms import Application  # type: ignore[import-not-found]
            Application.Instance.AsyncInvoke(lambda: self._on_tasks(tasks))
        except ImportError:
            self._on_tasks(tasks)
