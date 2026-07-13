"""Blendkit for Rhino 8 — Python business logic.

Module layout:
    client_lib       HTTP wrapper for the local Go client.
    client_process   Manage the Go client subprocess lifecycle.
    preferences      Read/write plugin settings (api_key, global_dir, port).
    search           Build search payloads, cache results, track pagination.
    download         Queue downloads, poll progress, dispatch import.
    import_gltf      Import downloaded .gltf / .glb into the active Rhino doc.
    categories       Fetch and cache the category tree.
    ratings          Quality + working-hours ratings.
    bookmarks        Favorite toggle + list.
    timer            Background poll of the client's /report endpoint.
    ui.panel         Eto panel layout — the thing the user sees.
    ui.thumbnail_grid  Scrollable grid of asset thumbnails.
    ui.filters       Search filter controls.
    ui.reports       Transient toast messages.

Everything in this package avoids Blender imports — it's meant to be testable
outside Rhino where possible, and to serve as the template for other
host ports.
"""

__version__ = "0.1.0"
