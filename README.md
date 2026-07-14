# Blendkit for Rhino 8

Rhino 8 port of the [Blendkit](https://www.blendkit.com/) (formerly BlenderKit)
asset browser — search, preview, and drag-drop download + import of thousands of
free and paid 3D models, materials, and HDR environments, right inside Rhino.

![The Blendkit panel in Rhino 8 — searching, importing, and placing a model](docs/screenshot.png)

- Search with the full filter set, categories, ratings, bookmarks, login.
- Drag-drop or point-based placement, with an in-viewport preview box while you drag.
- Assets download as `.gltf` / `.glb` and import via Rhino 8's native loader.
- Materials are converted to Rhino's PBR material on import.
- Comments and notifications open on blendkit.com in a browser.
- Upload is **not** supported in Rhino — use the Blender add-on to upload.

## Install

Blendkit is published on the Rhino **Package Manager** (Yak), so you don't have
to build anything to use it:

1. In Rhino 8, run the `PackageManager` command (or open **Tools ▸ Package
   Manager**).
2. Search for **Blendkit** (searching the old name **BlenderKit** finds it too).
3. Select it, click **Install**, then **restart Rhino**.
4. Open the panel: run the `Blendkit` command, or pick it under
   **Panels ▸ Blendkit**.
5. Click **Login** in the panel to connect your blendkit.com account (this opens
   a browser to authorize).

Rhino for **Windows and macOS** are both supported (Apple Silicon included). The
package bundles the Go helper client and the Blender export recipes, so the first
model import can take a few seconds while the asset is converted to glTF on your
machine.

## Status

Blendkit for Rhino is **experimental / alpha**, but it already covers almost all
of the functionality of the original Blender Blendkit add-on: full-featured
search and filters, categories, login, ratings, bookmarks, per-asset-type
handling (models, materials, HDRIs), an in-viewport drag-drop preview, and
download + import. Upload is the main intentional exception — do that from the
Blender add-on.

### Known issues & caveats

Rendering is the least-finished area, so treat it as a preview rather than a
final result:

- **Materials may not match the original.** Assets are authored for Blender's
  Cycles/EEVEE and converted to Rhino's PBR material on import. The conversion is
  approximate: base color, roughness, metalness, and normal/texture maps usually
  carry over, but procedural (shader-node) materials, displacement, and some
  texture blends won't reproduce faithfully. Expect to touch materials up before
  a final render.
- **Rhino Cycles (Raytraced mode) can be slow to start.** The first time you
  switch a viewport to Raytraced after importing assets, Cycles has to compile
  shaders and upload textures, which can take a while before the image begins to
  refine. It's faster on subsequent runs.
- Assets are converted to `.glb` on your machine the first time you use them, so
  the occasional `.blend` that doesn't convert cleanly may import incompletely.
- **Download / convert can stall.** The download → Blender-convert → import
  pipeline is still flaky: a job sometimes sits in "Converting…" (or another
  intermediate state) and doesn't finish. A cold Blender launch per convert is
  slow, and there is no timeout/retry yet. If a job hangs, cancel it from the
  Downloads popup and retry; the asset usually converts on a second attempt.

Found a bug? Please report it at
<https://github.com/BlenderKit/blendkit-rhino/issues> — it's an alpha and
reports genuinely help.

---

## Building from source (developers)

The rest of this document is for working on the plug-in itself; end users only
need the **Install** section above.

### Repo layout

```
.
├── src/                   # C# plugin shell (.rhp) — internal namespace
│                          # Blendkit.Rhino, output BlendkitRhino.rhp
├── python/
│   └── blendkit_rhino/    # helper scripts (asset extraction, ScriptEditor dev shim)
├── deploy/
│   ├── deploy_rhino.bat   # Windows: copy built artifacts into Rhino's Plug-ins folder
│   ├── deploy_rhino.sh    # macOS:   same, for Rhino 8.app
│   └── manifest*.yml      # Yak (Rhino Package Manager) metadata
├── docs/                  # README assets (screenshot)
└── tests/                 # xUnit tests (CI-runnable, no Rhino required)
```

Everything — the user-visible name (panel title, command names, package title)
and the internal identifiers (namespace `Blendkit.Rhino`, classes, file names,
assembly `BlendkitRhino`) — is **Blendkit**, the current company/brand name
(formerly **BlenderKit**). The plug-in GUID and the shared on-disk interop paths
(`~/blenderkit_data`, `%APPDATA%\BlenderKit\config.json`) deliberately keep the
old spelling so existing installs and the Blender add-on's shared cache/config
keep working.

### Where the Go client lives

The Go HTTP client is **shared** with the [Blender add-on](https://github.com/BlenderKit/blenderkit)
and lives there — this repo no longer ships its own copy. The deploy
script locates the client by, in order:

1. `BLENDKIT_CLIENT_DIR` environment variable (explicit override),
2. `../client/` (legacy sibling layout, kept for existing dev checkouts),
3. `../source_addon/blenderkit/client/` (current sibling-repo layout —
   Blender addon repo named `source_addon`),
4. `../source_blenderkit_addon/blenderkit/client/` (older sibling-repo
   name; kept as a fallback for existing checkouts),
5. `../blenderkit/client/` (Blender addon checked out inside this repo).

The macOS deploy script (`deploy_rhino.sh`) auto-builds the Go client
on each run via `go build`, force-targeting the host architecture
from `uname -m` (so an Apple Silicon Mac with a Rosetta-installed
amd64 Go still produces a native arm64 binary). Pass `--noclient` to
reuse a pre-built binary instead. The Windows script (`deploy_rhino.bat`)
still expects a pre-built `client.exe`.

### Dev workflow

1. Install Rhino 8 (Windows or macOS — both targets are supported).
2. Make sure the Blender add-on repo is checked out as a sibling
   (`../source_addon/blenderkit/`) so the client can be located. The
   macOS script will `go build` it for you; on Windows build
   `client.exe` once with `go build` in that folder. Or set
   `BLENDKIT_CLIENT_DIR` to point at it.
3. Run `deploy/deploy_rhino.bat` (Windows) or `deploy/deploy_rhino.sh`
   (macOS) to build the .rhp and copy artifacts into Rhino's plug-ins
   folder (e.g. `%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\Blendkit\`).
   On macOS the script also mirrors the deploy into the Yak-installed
   package dir under `~/Library/Application Support/McNeel/Rhinoceros/
   packages/8.0/Blendkit/<version>/` if one exists, since Rhino
   loads the Yak copy in preference to `Plug-ins/`. Pass `--noyak` to
   skip that mirror.
4. First install only: Rhino → Tools → Options → Plug-ins → Install...
   → pick the `.rhp` (or install the `.yak` via `PackageManager`).
   Subsequent runs: just re-run the deploy script and restart Rhino
   (Rhino does not reload plug-ins cleanly).
5. macOS-only: if `dotnet` isn't installed, `deploy_rhino.sh` skips
   the .rhp build and reuses whatever .rhp is already on disk (e.g.
   one built on a Windows machine and copied over). The Go client and
   Python helpers still get refreshed each run, which covers most of
   the day-to-day iteration loop.

Iterating on pure-Python UI code can be done inside Rhino's
ScriptEditor with `importlib.reload(module)` to skip the restart for
small changes.

## Releasing to the Yak package server

One-shot release script:

```cmd
deploy\release.bat 0.1.2
```

This refuses to run if the working tree is dirty, then bumps the
version in `BlendkitRhino.csproj` and both `manifest*.yml` files,
commits the bump (`release: 0.1.2`), runs `deploy_rhino.bat` to
build + pack the **Windows** `.yak`, runs `pack_mac.bat` to
cross-build + pack the **macOS** `.yak` (darwin/arm64 client +
`net7.0` `.rhp`), `yak push`es both to https://yak.rhino3d.com,
and tags `v0.1.2` in git (pushed to `origin`).

Requires `yak login` to have run at least once (token cached at
`%APPDATA%\McNeel\yak.yml`) and Go on `PATH` (or at
`C:\Program Files\Go\bin\go.exe`) for the darwin client
cross-compile. If push fails with "error retrieving your cached
token", run `yak login` and re-invoke `release.bat` with the
same version — the local commit is already in place; the build
steps will produce the same `.yak` files.

The Rhino kill behaviour from `deploy_rhino.bat` applies — Rhino
will be closed during the deploy step so the `.rhp` can be copied
into the local plug-ins folder.

If you only need to rebuild the macOS `.yak` (e.g. you fixed
something Mac-specific between releases without bumping the
version), run `deploy\pack_mac.bat` directly. It produces
`build\Release\packages\blendkit-<version>-rh8_0-mac.yak`,
which you can then `yak push` manually. The packer also patches
the zip's `external_attr` on `client/client` to `0o100755` so
the Mach-O binary is executable on the user's Mac — `yak.exe`
running on Windows otherwise leaves it at `0` and the binary
won't run on extract.

On macOS you can also produce the mac `.yak` directly from
`deploy_rhino.sh` (it packs with `yak build --platform mac` by
default; pass `--nopack` to skip).
