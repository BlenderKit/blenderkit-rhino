"""Import a downloaded .gltf / .glb file into the active Rhino document.

Rhino 8 imports glTF natively. Two entry points we use:

  1. `import_file(path)` — programmatic: wraps `RhinoDoc.Import(path, opts)`.
     Use this from double-click, "Insert" button, or any code path where the
     user has already chosen a target location (or we accept "at origin").

  2. `drag_source_for(path)` — returns a DataObject suitable for
     `Control.DoDragDrop` so a thumbnail drag ends at the mouse-release point
     in the Rhino viewport. Rhino's native file-drop handler picks up the URI
     and runs its own import flow — we don't implement placement ourselves.

This module is a thin shim over RhinoCommon; it must be run inside Rhino.
For unit tests, stub `Rhino` at import time.
"""
from __future__ import annotations

import logging
from pathlib import Path

log = logging.getLogger(__name__)


def import_file(path: str | Path, batch: bool = True) -> bool:
    """Import a file into the active Rhino document. Returns True on success."""
    import Rhino  # type: ignore[import-not-found]
    import scriptcontext as sc  # type: ignore[import-not-found]

    p = str(Path(path))
    opts = Rhino.FileIO.FileReadOptions()
    opts.BatchMode = batch
    opts.ImportMode = True
    ok = Rhino.RhinoDoc.ActiveDoc.Import(p, opts)
    sc.doc.Views.Redraw()
    if not ok:
        log.warning("RhinoDoc.Import returned False for %s", p)
    return bool(ok)


def drag_source_for(path: str | Path):
    """Build an Eto DataObject for a thumbnail drag.

    Caller is expected to invoke `control.DoDragDrop(data, DragEffects.Copy)`
    on mouse-down. Rhino's viewport drop handler accepts file URIs and runs its
    native importer on release.
    """
    from Eto.Forms import DataObject  # type: ignore[import-not-found]
    from System import Uri  # type: ignore[import-not-found]

    data = DataObject()
    data.Uris = [Uri(str(Path(path).absolute().as_uri()))]
    return data
