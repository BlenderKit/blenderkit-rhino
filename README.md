# BlenderKit for Rhino 8

Rhino 8 port of the [BlenderKit](https://www.blenderkit.com/) asset browser.

- Search, filters, categories, download, import, rating, bookmarks.
- Comments and notifications open on the website in a browser.
- Assets are downloaded as `.gltf` / `.glb` and imported via Rhino 8's native loader.
- Upload is **not** supported in Rhino — use the Blender addon to upload.

## Status

Early skeleton. See [`../docs/RHINO_PORT_ARCHITECTURE.md`](../docs/RHINO_PORT_ARCHITECTURE.md)
for the architecture and the list of spikes to validate before filling out the feature set.

## Repo layout

```
.
├── src/                   # C# plugin shell (.rhp) — internal namespace
│                          # Blendkit.Rhino, output BlendkitRhino.rhp
├── python/
│   └── blendkit_rhino/    # helper scripts (asset extraction, ScriptEditor dev shim)
├── deploy/
│   ├── deploy_rhino.bat   # Windows: copy built artifacts into Rhino's Plug-ins folder
│   ├── deploy_rhino.sh    # macOS:   same, for Rhinoceros 8.app
│   └── manifest*.yml      # Yak (Rhino Package Manager) metadata
└── tests/                 # xUnit tests (CI-runnable, no Rhino required)
```

The user-visible name (panel title, command names, package title) stays
**BlenderKit** — only internal identifiers (namespace, classes, file
names, assembly) are `Blendkit` to keep the repo self-contained while
preserving brand continuity for end users.

### Where the Go client lives

The Go HTTP client is **shared** with the [Blender add-on](https://github.com/BlenderKit/blenderkit)
and lives there — this repo no longer ships its own copy. The deploy
script locates the client by, in order:

1. `BLENDKIT_CLIENT_DIR` environment variable (explicit override),
2. `../client/` (legacy sibling layout, kept for existing dev checkouts),
3. `../../source_blenderkit_addon/blenderkit/client/` (default sibling-repo layout),
4. `../blenderkit/client/` (Blender addon checked out inside this repo).

Build the client once with `go build` in that folder; the deploy script
will then copy `client.exe` (or the macOS `client` / `client_darwin_arm64`
binary) into Rhino's plug-in directory.

## Dev workflow

1. Install Rhino 8 (Windows or macOS — both targets are supported).
2. Make sure the Blender add-on repo is checked out as a sibling
   (`../source_blenderkit_addon/blenderkit/`) and `client/client.exe`
   has been built there. Or set `BLENDKIT_CLIENT_DIR` to its path.
3. Run `deploy/deploy_rhino.bat` (Windows) or `deploy/deploy_rhino.sh`
   (macOS) to build the .rhp and copy artifacts into Rhino's plug-ins
   folder (e.g. `%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\BlenderKit\`).
4. First install only: Rhino → Tools → Options → Plug-ins → Install...
   → pick the `.rhp`. Subsequent runs: just re-run the deploy script
   and restart Rhino (Rhino does not reload plug-ins cleanly).

Iterating on pure-Python UI code can be done inside Rhino's
ScriptEditor with `importlib.reload(module)` to skip the restart for
small changes.
