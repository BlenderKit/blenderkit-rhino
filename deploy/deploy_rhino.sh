#!/usr/bin/env bash
# BlenderKit for Rhino 8 — macOS dev deploy.
#
# Mirrors deploy_rhino.bat: kill Rhino → build → copy artefacts into Rhino's
# plug-ins directory → optionally pack a .yak / relaunch.
#
# Usage:
#   ./deploy_rhino.sh                # build + copy + pack .yak + launch Rhino
#   ./deploy_rhino.sh --nobuild      # skip dotnet build of the .rhp
#   ./deploy_rhino.sh --noclient     # skip `go build` of the Go client
#   ./deploy_rhino.sh --nokill       # don't kill Rhinoceros first
#   ./deploy_rhino.sh --nolaunch     # don't open Rhino after deploy
#   ./deploy_rhino.sh --nopack       # skip the .yak pack (fast local iteration)
#   ./deploy_rhino.sh --noyak        # don't mirror into the Yak-installed
#                                    # package dir (packages/8.0/BlenderKit/...)

set -euo pipefail

DO_BUILD=1
DO_BUILD_CLIENT=1
DO_KILL=1
DO_LAUNCH=1
# .yak now packs by default — tester / publish cycle wants it produced
# every run. Pass --nopack for fast local iteration.
DO_PACK=1
DO_YAK_MIRROR=1
for arg in "$@"; do
    case "$arg" in
        --nobuild)  DO_BUILD=0 ;;
        --noclient) DO_BUILD_CLIENT=0 ;;
        --nokill)   DO_KILL=0 ;;
        --nolaunch) DO_LAUNCH=0 ;;
        --pack)     DO_PACK=1 ;;
        --nopack)   DO_PACK=0 ;;
        --noyak)    DO_YAK_MIRROR=0 ;;
    esac
done

HERE="$(cd "$(dirname "$0")" && pwd)"
RHINO_DIR="$(cd "$HERE/.." && pwd)"
REPO_ROOT="$(cd "$RHINO_DIR/.." && pwd)"

# Locate the Go client binary. The client/ folder lives in the Blender
# add-on repo; the Rhino and Blender repos are usually checked out as
# siblings under a shared parent (e.g. .../blenderkit/source_addon and
# .../blenderkit/source_rhino_addon). Resolution order (first hit wins):
#   1. $BLENDKIT_CLIENT_DIR              — explicit override (env var)
#   2. $RHINO_DIR/../client              — legacy "client/ is a
#                                          sibling of the Rhino repo"
#                                          layout
#   3. $RHINO_DIR/../source_addon/blenderkit/client
#                                        — current sibling-repo layout
#                                          (Blender add-on repo named
#                                          source_addon)
#   4. $RHINO_DIR/../source_blenderkit_addon/blenderkit/client
#                                        — historical sibling-repo
#                                          name; kept for older checkouts
#   5. $RHINO_DIR/../blenderkit/client   — Blender repo nested under
#                                          this one
if [ -n "${BLENDKIT_CLIENT_DIR:-}" ]; then
    CLIENT_DIR="$BLENDKIT_CLIENT_DIR"
elif [ -d "$RHINO_DIR/../client" ]; then
    CLIENT_DIR="$RHINO_DIR/../client"
elif [ -d "$RHINO_DIR/../source_addon/blenderkit/client" ]; then
    CLIENT_DIR="$RHINO_DIR/../source_addon/blenderkit/client"
elif [ -d "$RHINO_DIR/../source_blenderkit_addon/blenderkit/client" ]; then
    CLIENT_DIR="$RHINO_DIR/../source_blenderkit_addon/blenderkit/client"
elif [ -d "$RHINO_DIR/../blenderkit/client" ]; then
    CLIENT_DIR="$RHINO_DIR/../blenderkit/client"
else
    CLIENT_DIR="$RHINO_DIR/../source_addon/blenderkit/client"
fi
# Normalise so log output and error messages don't show ../.. paths.
CLIENT_DIR="$(cd "$CLIENT_DIR" 2>/dev/null && pwd || echo "$CLIENT_DIR")"
TARGET="$HOME/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/BlenderKit"
# Rhino 8 on macOS ships as "Rhino 8.app" (the older "Rhinoceros 8.app"
# bundle name from earlier installers is also accepted as a fallback).
# The Mach-O binary inside is still called `Rhinoceros`, so `pkill -x
# Rhinoceros` works for both.
if [ -d "/Applications/Rhino 8.app" ]; then
    RHINO_APP="/Applications/Rhino 8.app"
elif [ -d "/Applications/Rhinoceros 8.app" ]; then
    RHINO_APP="/Applications/Rhinoceros 8.app"
else
    RHINO_APP="/Applications/Rhino 8.app"  # nominal default for warnings
fi
YAK_BIN="$RHINO_APP/Contents/Resources/bin/yak"

# Yak-installed package mirror. When the user installed via PackageManager
# (or `_PackageManager` in Rhino) the plug-in lives under
# packages/<rhino-version>/<name>/<version>/, NOT under Plug-ins/. Rhino
# loads it from there and the dev Plug-ins/ copy is ignored, so a deploy
# that only writes to Plug-ins/ silently does nothing — the symptom the
# user sees is "client.exe still in there, my arm64 client never appears".
# We mirror into the highest-version Yak install of "BlenderKit" we find.
YAK_PKG_PARENT="$HOME/Library/Application Support/McNeel/Rhinoceros/packages/8.0/BlenderKit"
YAK_PKG_DIR=""
if [ -d "$YAK_PKG_PARENT" ]; then
    # Highest version dir wins (sort -V handles 0.1.0 < 0.10.0). Filter
    # to *directories* only — Yak drops a manifest.txt next to the
    # version dirs and we'd otherwise pick that up as the "version".
    YAK_PKG_DIR="$(find "$YAK_PKG_PARENT" -mindepth 1 -maxdepth 1 -type d 2>/dev/null \
        | sort -V | tail -n1)"
fi

echo
echo "=================================================="
echo " BlenderKit for Rhino 8 — deploy (macOS)"
echo "=================================================="
echo " rhino dir  : $RHINO_DIR"
echo " repo root  : $REPO_ROOT"
echo " client dir : $CLIENT_DIR"
echo " plugins/   : $TARGET"
if [ -n "$YAK_PKG_DIR" ] && [ "$DO_YAK_MIRROR" = "1" ]; then
    echo " yak pkg    : $YAK_PKG_DIR"
fi
echo " rhino app  : $RHINO_APP"

if [ "$DO_KILL" = "1" ]; then
    pkill -x Rhinoceros || true
    sleep 1
fi

# --- 1) Build the Go client (native arm64 / amd64) -----------------
#
# The client is shipped as a single platform-native binary alongside
# the .rhp. On macOS we always rebuild it before deploying because the
# checked-in copy may be stale or for a different host arch — symptom:
# old x86_64 binary on an Apple Silicon Mac runs under Rosetta and we
# want the native variant. `go build` is fast (a couple of seconds) so
# unconditional rebuild is fine; opt out with --noclient.
#
# go.mod pins `go 1.25`, which is newer than what most distros ship.
# We rely on Go's GOTOOLCHAIN=auto behaviour (default since 1.21) to
# auto-fetch the required toolchain — so even an older `go` on PATH
# will produce a 1.25 build.
if [ "$DO_BUILD_CLIENT" = "1" ]; then
    # Map `uname -m` to GOARCH. We force this rather than letting Go
    # default to its own host arch because a Rosetta-installed amd64
    # Go on an Apple Silicon Mac will silently produce x86_64 binaries
    # (so the deployed `client` runs under Rosetta — works, but slow,
    # and confusing when the user expected native arm64).
    HOST_ARCH="$(uname -m)"
    case "$HOST_ARCH" in
        arm64|aarch64) BUILD_GOARCH=arm64 ;;
        x86_64|amd64)  BUILD_GOARCH=amd64 ;;
        *)             BUILD_GOARCH="" ;;  # let Go decide
    esac

    if [ ! -d "$CLIENT_DIR" ]; then
        echo "[deploy] WARNING: client source dir $CLIENT_DIR does not exist — skipping client build."
    elif ! command -v go >/dev/null 2>&1; then
        echo "[deploy] WARNING: 'go' not on PATH — skipping client build (existing $CLIENT_DIR/client will be used if present)."
    else
        if [ -n "$BUILD_GOARCH" ]; then
            echo "[deploy] Building Go client (darwin/$BUILD_GOARCH, host $HOST_ARCH)..."
            ( cd "$CLIENT_DIR" && GOTOOLCHAIN=auto GOOS=darwin GOARCH="$BUILD_GOARCH" go build -o client . ) || {
                echo "[deploy] ERROR: go build failed in $CLIENT_DIR — see output above."
                exit 1
            }
        else
            echo "[deploy] Building Go client (host default, uname -m=$HOST_ARCH)..."
            ( cd "$CLIENT_DIR" && GOTOOLCHAIN=auto go build -o client . ) || {
                echo "[deploy] ERROR: go build failed in $CLIENT_DIR — see output above."
                exit 1
            }
        fi
    fi
fi

# --- 2) Build the C# .rhp shell (optional on macOS) ----------------
#
# dotnet SDK isn't always installed on macOS dev boxes; if absent we
# fall through and reuse whatever .rhp is already deployed (e.g. one
# built on Windows and copied over, or a Yak-installed one). The user
# only needs to rebuild the shell when C# code in src/ changes — the
# Go client + Python helpers are the parts that iterate fastest.
RHP_BUILT=""
EXISTING_RHP="$RHINO_DIR/build/Release/BlendkitRhino.rhp"
if [ "$DO_BUILD" = "1" ]; then
    if ! command -v dotnet >/dev/null 2>&1; then
        # If src/*.cs is newer than the on-disk .rhp, loud-warn — this
        # is exactly the "I changed C# code but the running plug-in
        # still has the old behaviour" trap. Silent skip wastes a
        # debug session.
        cs_newer="no"
        if [ -f "$EXISTING_RHP" ]; then
            if find "$RHINO_DIR/src" -name '*.cs' -newer "$EXISTING_RHP" 2>/dev/null \
                | grep -q .; then
                cs_newer="yes"
            fi
        else
            cs_newer="yes"  # no .rhp at all → definitely stale
        fi

        if [ "$cs_newer" = "yes" ]; then
            echo
            echo "[deploy] !! WARNING: dotnet not on PATH AND C# sources are newer than"
            echo "[deploy] !!          $EXISTING_RHP"
            echo "[deploy] !!          Your C# changes WILL NOT take effect this deploy."
            echo "[deploy] !!"
            echo "[deploy] !!          Fix one of:"
            echo "[deploy] !!            • brew install dotnet      (then re-run this script)"
            echo "[deploy] !!            • build BlendkitRhino.rhp on another machine and"
            echo "[deploy] !!              drop it at the path above before re-running"
            echo "[deploy] !!            • pass --nobuild to acknowledge & silence this"
            echo
        else
            echo "[deploy] dotnet not found on PATH — skipping .rhp build."
            echo "[deploy]   (no C# sources newer than the existing .rhp, so this is fine.)"
        fi
    else
        echo "[deploy] Building C# shell (net7.0 cross-platform target)..."
        # Multi-target project: explicitly pick the cross-platform net7.0
        # target on macOS so we don't accidentally produce a Windows-only
        # build that won't load.
        dotnet build "$RHINO_DIR/src/BlendkitRhino.csproj" -c Release \
            -f net7.0 -o "$RHINO_DIR/build/Release"
        RHP_BUILT="$RHINO_DIR/build/Release/BlendkitRhino.rhp"
    fi
fi
# If we didn't build but a previous build is on disk, still treat it
# as the source-of-truth .rhp to mirror into target dirs.
if [ -z "$RHP_BUILT" ] && [ -f "$RHINO_DIR/build/Release/BlendkitRhino.rhp" ]; then
    RHP_BUILT="$RHINO_DIR/build/Release/BlendkitRhino.rhp"
fi

# --- 3) Copy artefacts into a deploy target -----------------------
#
# Same payload to potentially several locations: the dev Plug-ins/
# folder and any Yak-installed package dir. Wrapping it in a function
# keeps the two paths in sync — easy to silently drift otherwise.
deploy_to() {
    local dest="$1"
    echo "[deploy] --> $dest"
    mkdir -p "$dest" "$dest/python" "$dest/client"

    # Purge stale .rhp files from previous builds. Pre-rename builds
    # (BlenderKitRhino.rhp) and the current build (BlendkitRhino.rhp)
    # share the same plug-in GUID, so if both end up in the folder
    # Rhino loads whichever it indexed first — leading to the new
    # code silently not running. Delete every .rhp that isn't ours.
    shopt -s nullglob
    for rhp in "$dest"/*.rhp; do
        base="$(basename "$rhp")"
        if [ "$base" != "BlendkitRhino.rhp" ]; then
            if rm -f "$rhp"; then
                echo "[deploy]    removed stale $base"
            else
                echo "[deploy]    WARNING: could not delete stale $base — is Rhino still running?"
            fi
        fi
    done
    shopt -u nullglob

    # Fresh .rhp (if we have one).
    if [ -n "$RHP_BUILT" ] && [ -f "$RHP_BUILT" ]; then
        cp -f "$RHP_BUILT" "$dest/BlendkitRhino.rhp"
    fi

    # Plug-in tree + helper scripts.
    cp -R "$RHINO_DIR/python/blendkit_rhino" "$dest/python/" 2>/dev/null || true
    cp -f "$RHINO_DIR/python/"*.py "$dest/python/" 2>/dev/null || true

    # Go client binary. Probe order matches the runtime probe in
    # BlendkitPlugIn.EnsureGoClient — first the macOS-style plain
    # `client`, then `.exe` for Windows-cross-built binaries that
    # happen to live in the same client/ folder. We also delete any
    # stale client.exe in the destination when we have a native
    # macOS binary, otherwise both end up side-by-side and the user
    # has to wonder which one Rhino actually launches. (The C#
    # probe order is `client.exe` first, so a leftover .exe on a
    # Mac would actively break things.)
    if [ -f "$CLIENT_DIR/client" ]; then
        cp -f "$CLIENT_DIR/client" "$dest/client/client"
        chmod +x "$dest/client/client"
        rm -f "$dest/client/client.exe"
        echo "[deploy]    Go client (macOS) copied from $CLIENT_DIR."
    elif [ -f "$CLIENT_DIR/client_darwin_arm64" ]; then
        cp -f "$CLIENT_DIR/client_darwin_arm64" "$dest/client/client"
        chmod +x "$dest/client/client"
        rm -f "$dest/client/client.exe"
        echo "[deploy]    Go client (darwin/arm64) copied as client from $CLIENT_DIR."
    elif [ -f "$CLIENT_DIR/client.exe" ]; then
        cp -f "$CLIENT_DIR/client.exe" "$dest/client/client.exe"
        echo "[deploy]    WARNING: only Windows client.exe found in $CLIENT_DIR — install macOS client."
    else
        echo "[deploy]    WARNING: Go client not found at $CLIENT_DIR/."
        echo "[deploy]             Build it: (cd \"$CLIENT_DIR\" && go build)"
        echo "[deploy]             Or set BLENDKIT_CLIENT_DIR to the folder containing the client binary."
    fi

    # Bundled Blender-script recipes (tools/). The Go client resolves
    # `script_id` -> <exe_dir>/tools/<id>.py at runtime, so the
    # recipes need to ship next to the client binary in the deployed
    # plug-in. Hosts that call script_id="export_glb"
    # (BlenderConvertService.cs) depend on these being present.
    if [ -d "$CLIENT_DIR/tools" ]; then
        mkdir -p "$dest/client/tools"
        cp -R "$CLIENT_DIR/tools/." "$dest/client/tools/"
        echo "[deploy]    Bundled Blender-script recipes copied to client/tools/."
    else
        echo "[deploy]    NOTE: $CLIENT_DIR/tools not found; bundled script_id recipes won't be available."
    fi

    # Optional deploy-side artefacts (toolbar, manifest, listing icon).
    if [ -f "$RHINO_DIR/deploy/BlenderKit.rui" ]; then
        cp -f "$RHINO_DIR/deploy/BlenderKit.rui" "$dest/BlenderKit.rui"
    fi
    if [ -f "$RHINO_DIR/deploy/manifest.yml" ]; then
        cp -f "$RHINO_DIR/deploy/manifest.yml" "$dest/manifest.yml"
    fi
    if [ -f "$RHINO_DIR/src/Resources/blenderkit_logo.png" ]; then
        cp -f "$RHINO_DIR/src/Resources/blenderkit_logo.png" "$dest/icon.png"
    fi
}

deploy_to "$TARGET"
if [ "$DO_YAK_MIRROR" = "1" ] && [ -n "$YAK_PKG_DIR" ] && [ -d "$YAK_PKG_DIR" ]; then
    deploy_to "$YAK_PKG_DIR"
fi

# --pack: produce a redistributable .yak. Yak's macOS binary lives
# inside the Rhino app bundle. The Yak CLI scans the working dir for
# manifest.yml and zips every sibling — same flow as the Windows
# script.
if [ "$DO_PACK" = "1" ]; then
    if [ ! -x "$YAK_BIN" ]; then
        echo "[deploy] WARNING: Yak not found at $YAK_BIN — skipping pack."
    else
        echo "[deploy] Building .yak package..."
        mkdir -p "$RHINO_DIR/build/Release/packages"
        ( cd "$TARGET" && "$YAK_BIN" build ) || {
            echo "[deploy] ERROR: yak build failed; check manifest.yml syntax + version."
        }
        for yak in "$TARGET"/*.yak; do
            [ -f "$yak" ] || continue
            mv -f "$yak" "$RHINO_DIR/build/Release/packages/$(basename "$yak")"
            echo "[deploy] Package: $RHINO_DIR/build/Release/packages/$(basename "$yak")"
        done
        echo "[deploy] To publish: yak push <file>.yak"
        echo "[deploy] First-time publishers: yak login  (opens browser)"
    fi
fi

echo "[deploy] Done."
if [ "$DO_LAUNCH" = "1" ] && [ -d "$RHINO_APP" ]; then
    open "$RHINO_APP"
fi
