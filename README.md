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

## Dev workflow

1. Install Rhino 8 (Windows or macOS — both targets are supported).
2. Make sure the Blender add-on repo is checked out as a sibling
   (`../source_addon/blenderkit/`) so the client can be located. The
   macOS script will `go build` it for you; on Windows build
   `client.exe` once with `go build` in that folder. Or set
   `BLENDKIT_CLIENT_DIR` to point at it.
3. Run `deploy/deploy_rhino.bat` (Windows) or `deploy/deploy_rhino.sh`
   (macOS) to build the .rhp and copy artifacts into Rhino's plug-ins
   folder (e.g. `%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\BlenderKit\`).
   On macOS the script also mirrors the deploy into the Yak-installed
   package dir under `~/Library/Application Support/McNeel/Rhinoceros/
   packages/8.0/BlenderKit/<version>/` if one exists, since Rhino
   loads the Yak copy in preference to `Plug-ins/`. Pass `--noyak` to
   skip that mirror.
4. First install only: Rhino → Tools → Options → Plug-ins → Install...
   → pick the `.rhp` (or install the `.yak` via `_PackageManager`).
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
`build\Release\packages\blenderkit-<version>-rh8_0-mac.yak`,
which you can then `yak push` manually. The packer also patches
the zip's `external_attr` on `client/client` to `0o100755` so
the Mach-O binary is executable on the user's Mac — `yak.exe`
running on Windows otherwise leaves it at `0` and the binary
won't run on extract.
