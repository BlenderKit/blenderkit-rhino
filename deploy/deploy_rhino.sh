#!/usr/bin/env bash
# BlenderKit for Rhino 8 — macOS dev deploy.
#
# Mirrors deploy_rhino.bat: kill Rhino → build → copy artefacts into Rhino's
# plug-ins directory → optionally pack a .yak / relaunch.
#
# Usage:
#   ./deploy_rhino.sh            # build + copy + launch Rhino
#   ./deploy_rhino.sh --nobuild  # skip dotnet build
#   ./deploy_rhino.sh --nokill   # don't kill Rhinoceros first
#   ./deploy_rhino.sh --nolaunch # don't open Rhino after deploy
#   ./deploy_rhino.sh --pack     # also build a redistributable .yak

set -euo pipefail

DO_BUILD=1
DO_KILL=1
DO_LAUNCH=1
DO_PACK=0
for arg in "$@"; do
    case "$arg" in
        --nobuild)  DO_BUILD=0 ;;
        --nokill)   DO_KILL=0 ;;
        --nolaunch) DO_LAUNCH=0 ;;
        --pack)     DO_PACK=1 ;;
    esac
done

HERE="$(cd "$(dirname "$0")" && pwd)"
RHINO_DIR="$(cd "$HERE/.." && pwd)"
REPO_ROOT="$(cd "$RHINO_DIR/.." && pwd)"

# Locate the Go client binary. After splitting the Rhino plug-in into
# its own repo, the client/ folder lives in the Blender add-on repo —
# usually checked out as a sibling. Resolution order (first hit wins):
#   1. $BLENDKIT_CLIENT_DIR              — explicit override (env var)
#   2. $RHINO_DIR/../client              — legacy sibling layout
#   3. $RHINO_DIR/../../source_blenderkit_addon/blenderkit/client
#                                        — default sibling-repo layout
#   4. $RHINO_DIR/../blenderkit/client   — Blender repo nested under
#                                          this one
if [ -n "${BLENDKIT_CLIENT_DIR:-}" ]; then
    CLIENT_DIR="$BLENDKIT_CLIENT_DIR"
elif [ -d "$RHINO_DIR/../client" ]; then
    CLIENT_DIR="$RHINO_DIR/../client"
elif [ -d "$RHINO_DIR/../../source_blenderkit_addon/blenderkit/client" ]; then
    CLIENT_DIR="$RHINO_DIR/../../source_blenderkit_addon/blenderkit/client"
elif [ -d "$RHINO_DIR/../blenderkit/client" ]; then
    CLIENT_DIR="$RHINO_DIR/../blenderkit/client"
else
    CLIENT_DIR="$RHINO_DIR/../../source_blenderkit_addon/blenderkit/client"
fi
TARGET="$HOME/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/BlenderKit"
RHINO_APP="/Applications/Rhinoceros 8.app"
YAK_BIN="$RHINO_APP/Contents/Resources/bin/yak"

echo
echo "=================================================="
echo " BlenderKit for Rhino 8 — deploy (macOS)"
echo "=================================================="
echo " rhino dir : $RHINO_DIR"
echo " repo root : $REPO_ROOT"
echo " target    : $TARGET"

if [ "$DO_KILL" = "1" ]; then
    pkill -x Rhinoceros || true
    sleep 1
fi

mkdir -p "$TARGET" "$TARGET/python" "$TARGET/client"

if [ "$DO_BUILD" = "1" ]; then
    if ! command -v dotnet >/dev/null 2>&1; then
        echo "[deploy] dotnet not found on PATH — install the .NET SDK and retry."
        exit 1
    fi
    echo "[deploy] Building C# shell (net7.0 cross-platform target)..."
    # Multi-target project: explicitly pick the cross-platform net7.0
    # target on macOS so we don't accidentally produce a Windows-only
    # build that won't load.
    dotnet build "$RHINO_DIR/src/BlendkitRhino.csproj" -c Release \
        -f net7.0 -o "$RHINO_DIR/build/Release"
    cp -f "$RHINO_DIR/build/Release/BlendkitRhino.rhp" "$TARGET/" || true
fi

# Plug-in tree + helper scripts.
cp -R "$RHINO_DIR/python/blendkit_rhino" "$TARGET/python/" 2>/dev/null || true
cp -f "$RHINO_DIR/python/"*.py "$TARGET/python/" 2>/dev/null || true

# Go client binary. Probe order matches the runtime probe in
# BlendkitPlugIn.EnsureGoClient — first the macOS-style plain `client`,
# then `.exe` for Windows-cross-built binaries that happen to live in
# the same client/ folder. CLIENT_DIR is resolved at the top of this
# script (env override + sibling-repo fallbacks).
if [ -f "$CLIENT_DIR/client" ]; then
    cp -f "$CLIENT_DIR/client" "$TARGET/client/client"
    chmod +x "$TARGET/client/client"
    echo "[deploy] Go client (macOS) copied from $CLIENT_DIR."
elif [ -f "$CLIENT_DIR/client_darwin_arm64" ]; then
    cp -f "$CLIENT_DIR/client_darwin_arm64" "$TARGET/client/client"
    chmod +x "$TARGET/client/client"
    echo "[deploy] Go client (darwin/arm64) copied as client from $CLIENT_DIR."
elif [ -f "$CLIENT_DIR/client.exe" ]; then
    cp -f "$CLIENT_DIR/client.exe" "$TARGET/client/client.exe"
    echo "[deploy] WARNING: only Windows client.exe found in $CLIENT_DIR — install macOS client."
else
    echo "[deploy] WARNING: Go client not found at $CLIENT_DIR/."
    echo "[deploy]          Build it: (cd \"$CLIENT_DIR\" && go build)"
    echo "[deploy]          Or set BLENDKIT_CLIENT_DIR to the folder containing the client binary."
fi

# Bundled Blender-script recipes (tools/). The Go client resolves
# `script_id` -> <exe_dir>/tools/<id>.py at runtime, so the recipes
# need to ship next to the client binary in the deployed plug-in.
# Hosts that call script_id="export_glb" (BlenderConvertService.cs)
# depend on these being present.
if [ -d "$CLIENT_DIR/tools" ]; then
    mkdir -p "$TARGET/client/tools"
    cp -R "$CLIENT_DIR/tools/." "$TARGET/client/tools/"
    echo "[deploy] Bundled Blender-script recipes copied to client/tools/."
else
    echo "[deploy] NOTE: $CLIENT_DIR/tools not found; bundled script_id recipes won't be available."
fi

# Optional deploy-side artefacts (toolbar, manifest, listing icon).
if [ -f "$RHINO_DIR/deploy/BlenderKit.rui" ]; then
    cp -f "$RHINO_DIR/deploy/BlenderKit.rui" "$TARGET/BlenderKit.rui"
fi
if [ -f "$RHINO_DIR/deploy/manifest.yml" ]; then
    cp -f "$RHINO_DIR/deploy/manifest.yml" "$TARGET/manifest.yml"
fi
if [ -f "$RHINO_DIR/src/Resources/blenderkit_logo.png" ]; then
    cp -f "$RHINO_DIR/src/Resources/blenderkit_logo.png" "$TARGET/icon.png"
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
