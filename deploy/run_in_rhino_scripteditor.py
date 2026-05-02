"""Paste-and-run smoke test for Rhino 8 ScriptEditor.

Until the C# .rhp is compiled and installed, this script lets you poke the
Python modules from inside a running Rhino 8 without a full plugin install.

What it does:
  1. Adds the plugin's python/ folder to sys.path.
  2. Calls `ui.panel.build_panel_content()` to construct an Eto layout.
  3. Shows it in a modeless Eto.Forms.Form (docked-style window).
  4. Prints whether the Go client is reachable on any candidate port.

Usage:
  - Open Rhino 8.
  - Tools > Python 3 Script... (ScriptEditor).
  - Open this file, Run (F5).
  - Iterate on Python modules, then `importlib.reload(...)` as needed.
"""
# ruff: noqa — the top-level imports depend on Rhino being live.
import sys
from pathlib import Path

HERE = Path(__file__).resolve()
PYTHON_DIR = HERE.parents[1] / "python"
if str(PYTHON_DIR) not in sys.path:
    sys.path.insert(0, str(PYTHON_DIR))

from blendkit_rhino import client_lib
from blendkit_rhino.ui import panel

import Rhino  # type: ignore
from Eto.Forms import Form, Application  # type: ignore
from Eto.Drawing import Size  # type: ignore


def main():
    port = client_lib.discover_port()
    if port is None:
        Rhino.RhinoApp.WriteLine(
            "[BlenderKit] No Go client reachable. Start one manually from "
            "../client/client.exe, or install the .rhp which spawns it."
        )
    else:
        Rhino.RhinoApp.WriteLine(f"[BlenderKit] Go client on port {port}.")

    form = Form()
    form.Title = "BlenderKit (dev window)"
    form.ClientSize = Size(400, 600)
    form.Content = panel.build_panel_content()
    form.Owner = Rhino.UI.RhinoEtoApp.MainWindow
    form.Show()


main()
