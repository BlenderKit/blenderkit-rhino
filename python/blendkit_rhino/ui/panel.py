"""Main dockable panel layout.

The C# shell instantiates a `BlendkitPanel` (Eto.Forms.Panel). This module
supplies the panel body when the Python bridge is wired up.

Layout (top → bottom):
    +-----------------------------------+
    | [Login] / [Profile]               |
    +-----------------------------------+
    | [MODEL | MATERIAL | HDR | PRINT]  |  <- asset type tabs
    +-----------------------------------+
    | Search box [.............] [Go]   |
    | Filters: style / license / ...    |
    +-----------------------------------+
    | Categories (tree)                 |
    +-----------------------------------+
    | Thumbnail grid (scrollable)       |
    |                                   |
    +-----------------------------------+
    | Toasts / status bar               |
    +-----------------------------------+

For v1 we return a placeholder until the C# → Python handoff is working.
"""
from __future__ import annotations


def build_panel_content():
    """Build and return an Eto control to host inside the C# BlendkitPanel.

    Must be called from inside Rhino (Eto is only available there).
    """
    from Eto.Forms import DynamicLayout, Label, TextBox, Button, DropDown  # type: ignore[import-not-found]
    from Eto.Drawing import Padding, Size  # type: ignore[import-not-found]

    layout = DynamicLayout()
    layout.Padding = Padding(8)
    layout.Spacing = Size(0, 6)

    title = Label()
    title.Text = "Blendkit for Rhino — v0.1"
    layout.AddRow(title)

    asset_type = DropDown()
    for t in ("MODEL", "MATERIAL", "HDR", "PRINTABLE"):
        asset_type.Items.Add(t)
    asset_type.SelectedIndex = 0
    layout.AddRow(asset_type)

    search_box = TextBox()
    search_box.PlaceholderText = "Search assets…"
    go = Button()
    go.Text = "Search"

    search_row = DynamicLayout()
    search_row.BeginHorizontal()
    search_row.Add(search_box, True)  # xscale=True
    search_row.Add(go)
    search_row.EndHorizontal()
    layout.AddRow(search_row)

    placeholder = Label()
    placeholder.Text = "[thumbnail grid placeholder]"
    layout.AddRow(placeholder)
    layout.AddRow(None)  # filler
    return layout
