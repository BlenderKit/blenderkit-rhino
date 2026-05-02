# `client_pr.patch` — host-agnostic Blender-script endpoint for the shared Go client

Adds **one** endpoint, `POST /run_blender_script`, that any embedder
(Rhino plug-in, future Houdini port, eventually the Blender add-on's
own `_bg.py` chores) can use to run a Python recipe under headless
Blender. Ships the first bundled recipe, `tools/export_glb.py`, so
the most common case — re-export a `.blend` to Draco-free `.glb` —
is one HTTP call away.

The patch is a **strict superset** of current Blender behavior. It
touches no existing code paths; the new endpoint is simply
registered alongside the existing routes. Nothing in the Blender
add-on calls it yet — only the Rhino plug-in does.

## What's in it

```
 client/main.go               | +6   - register /run_blender_script
 client/run_blender_script.go | new  - endpoint + recipe runner (~280 lines)
 client/tools/export_glb.py   | new  - first bundled recipe
```

Total: 3 files, ~295 added lines, no deletions.

## The endpoint at a glance

`POST /run_blender_script`:

```jsonc
{
  "script_id":        "export_glb",       // EITHER a bundled recipe ID,
  "script_path":      "/abs/path.py",     // OR an absolute caller-shipped path

  "blender_exe_path": "/path/to/blender", // required — caller knows this

  "blend_path":       "/cache/asset.blend",
  "output_path":      "/cache/asset.glb",  // cached + post-run validated
  "params":           { "yup": true, ... }, // free-form JSON, forwarded as-is

  // standard task envelope unchanged
  "app_id": 1, "addon_version": "...", "platform_version": "...", "software": "..."
}
```

- `script_id` resolves to `<exe_dir>/tools/<id>.py` at runtime
  (`$BLENDERKIT_TOOLS_DIR` overrides for dev workflows). For stable,
  broadly-useful recipes that ship with the binary.
- `script_path` is the escape hatch for caller-specific recipes
  (e.g. Rhino's PBR-material extractor — that one stays in the
  Rhino plug-in, not bundled here).
- `params` is serialized to a temp `params.json`; the recipe reads
  it from `sys.argv[-1]`. Callers evolve their schemas freely; the
  Go client never inspects the params.
- `blender_exe_path` is required: callers know where their Blender
  is (Blender add-on uses `bpy.app.binary_path`; external embedders
  do their own platform-specific lookup). Keeps the client out of
  install-path discovery.

## How to apply

From the Blender add-on repo root, on a fresh branch off `main`:

```bash
git checkout main
git pull
git checkout -b client-host-agnostic-endpoint

git apply <path-to>/client_pr.patch

git add client/main.go \
        client/run_blender_script.go \
        client/tools/export_glb.py

git commit -m "client: add /run_blender_script + bundled export_glb recipe

POST /run_blender_script picks a recipe via script_id (bundled in
client/tools/<id>.py, ships with the binary) or script_path (absolute
path to a caller-shipped script). The recipe reads free-form JSON
params from sys.argv[-1]; callers evolve schemas without server
changes. The caller passes blender_exe_path so the client stays out
of install-path discovery.

First bundled recipe: tools/export_glb.py — re-exports the active
scene to Draco-free .glb. Used by the Rhino plug-in to convert
downloaded .blend assets that Rhino can import natively.

No existing endpoint or behavior is modified. Pure addition."

git push -u origin client-host-agnostic-endpoint
```

Then open a PR against `main`.
