# CLAUDE.md — working notes for this repo

Blendkit for Rhino 8 (formerly BlenderKit). C# `.rhp` plugin shell + Python
helpers + a shared Go client (lives in the Blender add-on repo). See `README.md`
for the user-facing overview; this file is the developer/agent quick-start.

## Environment (macOS dev machine)

- **dotnet**: installed via Homebrew (`/opt/homebrew/bin/dotnet`, .NET 10 SDK).
  The project targets `net7.0`; NuGet reference-packs make it build fine on the
  10 SDK. `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`.
- **go**: `/Users/vilemduha/go/bin/go` (1.22 host, amd64 under Rosetta). The
  deploy script forces `GOARCH` from `uname -m` so the client builds native
  arm64. `GOTOOLCHAIN=auto` pulls the go.mod-pinned toolchain (1.25).
- **Rhino**: `/Applications/Rhino 8.app` (note: *not* "Rhinoceros 8.app"). Inner
  binary is still `Rhinoceros`, so `pkill -x Rhinoceros` works.
- **Running tests** needs a runtime roll-forward (tests are `net8.0`, only the
  10 runtime is installed):
  ```bash
  export PATH="/opt/homebrew/bin:$PATH" DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
  export DOTNET_ROLL_FORWARD=LatestMajor DOTNET_ROLL_FORWARD_TO_PRERELEASE=1
  dotnet test tests/BlendkitRhino.Tests.csproj -c Release   # 93 tests
  ```

## Build / deploy / test loop

```bash
export PATH="/opt/homebrew/bin:/Users/vilemduha/go/bin:$PATH"
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
bash deploy/deploy_rhino.sh --nopack            # build client+rhp, deploy, launch Rhino
#   --noclient  skip go build   --nobuild skip dotnet   --nolaunch  --nokill  --nopack  --noyak
```

`deploy_rhino.sh` kills Rhino, `go build`s the client, `dotnet build`s the
`.rhp`, then copies both into **two** places:
1. `~/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Blendkit/`
2. the Yak-installed package dir (currently
   `.../packages/8.0/BlenderKit/0.1.3/` — the locally-installed OLD-named
   package). **Rhino loads the Yak copy in preference**, so a deploy that misses
   it silently runs stale code — always confirm the `.rhp` timestamp under
   `packages/8.0/.../` after deploying.

## Naming / interop — things that deliberately KEEP the old spelling

The rebrand is BlenderKit → **Blendkit** (brand + user-visible text + API domain
`blenderkit.com` → `blendkit.com`, which is live). But these intentionally stay:
- **Plug-in GUID** `3f1c9d20-…` — never change; Rhino keys installs on it.
- **`~/blenderkit_data`** — shared runtime/cache dir with the Go client + Blender
  add-on. Not branding; don't rename.
- **`%APPDATA%\BlenderKit\config.json`** (python/blendkit_rhino/preferences.py) —
  shared config with the Blender add-on.
- **`public.blenderkit.com`** CDN URLs returned by the server (not in our code).
- **GitHub org** `github.com/BlenderKit/…` (repo itself renamed to `blendkit-rhino`).
- **`.3dm` UserDictionary keys** `blenderkit.asset_id` etc. — persisted, write-only,
  no reader; leave to avoid a migration.
- Assembly/`.rhp` name `BlendkitRhino`, namespace `Blendkit.Rhino` — already Blendkit.

## Drag-drop / download lifecycle (where the bugs live)

- One `_drops.Add` (StartDrag). Each drop = a `DragSession` resolved by
  `OnDrop`/`OnCancel`. Convert results route by `task_id` through
  `_pendingConvertActions` / `_orphanedConvertResults` (race: "finished" can beat
  registration — all 3 registration sites now drain the orphan map).
- Preview box = `DragPreviewConduit`. Teardown MUST go through
  `ClearDropPreview(drop)` (disable conduit **and** `Views.Redraw()` on the UI
  thread) — a bare `Enabled=false` leaves the green box painted until the next
  repaint.
- Preview box size comes from `BBoxFromHit` (meters → doc units) + `UnitFloor()`
  (≈1 mm floor, unit-scaled) so boxes read true size in any unit system.

## Open bugs / TODO (as of last session)

- **Download/convert stalls** — jobs often stick in "Converting…" and never
  finish. Suspect: cold Blender launch per convert is slow + no timeout/retry +
  possibly the Go client `run_blender_script` hanging. Next step: instrument the
  convert task (client `rhino.log` under `~/blenderkit_data/client/`), add a
  timeout/reaper + a manual "clear stuck drop" affordance. This is the top
  user-facing complaint.
- **Publishing**: `yak push build/Release/packages/blendkit-0.1.3-rh8_0-mac.yak`
  still pending (needs `yak login`). Windows `.yak` not built here. When the new
  **Blendkit** Yak package is installed locally, uninstall the old **BlenderKit**
  one (same GUID → Rhino loads whichever it indexed first).
- **rhino-mcp**: bridge plugin (rhino-mcp 0.6.0) runs in Rhino on `127.0.0.1:4242`
  (Mac-compatible). A `.mcp.json` for Claude Code was written to the session cwd
  (`../source_addon/.mcp.json`) pointing `uvx rhino3dm-mcp` at the bridge; needs a
  fresh Claude Code session to load (no `claude` CLI installed — this is the
  in-app Claude Code). `uv` is installed at `/opt/homebrew/bin/uvx`.

## Git

Two repos, both pushed to their own remotes:
- Rhino plugin: `github.com/BlenderKit/blendkit-rhino` (this repo, branch `main`).
- Blender add-on / Go client: `../source_addon/blenderkit`, branch
  `client-rhino-changes` (holds the shared client + `tools/export_glb.py`).
