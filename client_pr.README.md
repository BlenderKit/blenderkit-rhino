# `client_pr.patch` — host-agnostic Blender-script endpoint for the shared Go client

This patch adds **one** endpoint, `POST /run_blender_script`, that any
embedder (Rhino plug-in, future Houdini port, eventually the Blender
add-on's own `_bg.py` chores) can use to run a Python recipe under a
headless Blender. It also ships the first bundled recipe
(`tools/export_glb.py`) so the most common case — re-export a `.blend`
to Draco-free `.glb` — is one HTTP call away.

The patch is a strict superset of the current Blender behavior: when
the new fields are empty / not used, the client behaves exactly as
before. No Blender add-on code is touched.

## What's in it

```
 client/blender_convert.go    | rewritten — now a thin /blend_to_glb back-compat shim
                               that delegates to /run_blender_script with
                               script_id="export_glb"
 client/run_blender_script.go | rewritten — accepts script_id (bundled recipe)
                               OR script_path (host-supplied), forwards a
                               JSON params blob, validates output
 client/tools/export_glb.py   | new — first bundled recipe; replaces the
                               Python that used to be embedded in
                               blender_convert.go's Go source
 client/download.go           | +14 / -4   — propagate model_format selector
 client/main.go               | +15 / -1   — register host-agnostic routes
 client/structs.go            |  +4        — DownloadData.ModelFormat field
```

## The unified endpoint at a glance

`POST /run_blender_script` body:

```jsonc
{
  "script_id":      "export_glb",       // EITHER a bundled recipe ID,
  "script_path":    "/abs/path.py",     // OR an absolute host-shipped path

  "blend_path":     "/cache/asset.blend",
  "output_path":    "/cache/asset.glb",  // cached + post-run validated
  "params":         { ... },             // free-form JSON forwarded as a temp file
  "status_message": "Converting…",

  // standard task envelope unchanged
  "app_id": 1, "addon_version": "...", "platform_version": "...", "software": "..."
}
```

- `script_id` resolves to `<exe_dir>/tools/<id>.py` at runtime
  (`$BLENDKIT_TOOLS_DIR` overrides for dev workflows). Use this for
  recipes you'd want any host to be able to call.
- `script_path` is the escape hatch for host-specific recipes (Rhino's
  Cycles-XML / PBR JSON extractor today).
- `params` is serialized to a temp `params.json`; the recipe reads it
  from `sys.argv[-1]`. Hosts evolve their schemas freely without
  server changes.

`/blend_to_glb` stays registered as a thin back-compat shim that
constructs `RunBlenderScriptData{ScriptID:"export_glb", ...}` and
delegates. Existing callers keep working unchanged.

## How to apply

From the Blender add-on repo root, on a fresh branch off `main`:

```bash
git checkout main
git pull
git checkout -b client-host-agnostic-endpoints

git apply <path-to>/client_pr.patch

git add client/blender_convert.go client/run_blender_script.go \
        client/tools/export_glb.py \
        client/download.go client/main.go client/structs.go

git commit -m "client: unify Blender-script endpoint (script_id + params)

POST /run_blender_script picks a recipe via script_id (bundled in
client/tools/<id>.py, ships with the binary) or script_path (absolute
path to a host-supplied script). Recipes read free-form JSON params
from sys.argv[-1]; hosts evolve schemas without server changes.

First bundled recipe: tools/export_glb.py (replaces the Python
that used to be embedded in blender_convert.go). /blend_to_glb is
preserved as a thin back-compat shim that delegates to the unified
endpoint with script_id=\"export_glb\".

Also: thread an optional model_format selector through the
download path so non-Blender hosts can request a specific
AssetFile variant (e.g. \"gltf\") instead of resolving by
resolution alone."

git push -u origin client-host-agnostic-endpoints
```

Then open a PR against `main`.
