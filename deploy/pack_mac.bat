@echo off
REM Blendkit for Rhino 8 - cross-pack the macOS .yak from Windows.
REM
REM Why this exists:
REM   deploy_rhino.bat packs the Windows yak (client.exe + net7.0-windows .rhp).
REM   On Mac the Go client must be a Mach-O binary, and the C# .rhp must come
REM   from the cross-platform net7.0 target (no Win32 P/Invokes compiled in).
REM   Doing a Mac release used to require running deploy_rhino.sh on a Mac.
REM
REM   This script reproduces the Mac yak from Windows by:
REM     1. cross-compiling the Go client with GOOS=darwin GOARCH=arm64,
REM     2. dotnet build -f net7.0 for the cross-platform .rhp,
REM     3. staging files alongside manifest_mac.yml (renamed to manifest.yml),
REM     4. yak build --platform mac to produce blendkit-X.Y.Z-rh8_0-mac.yak,
REM     5. patching the zip's external_attr on client/client to 0o100755 so
REM        the executable bit survives — yak.exe on Windows defaults to 0,
REM        which would leave the Mach-O non-executable on extract.
REM
REM Usage:
REM   pack_mac.bat            - build everything, produce + post-process .yak
REM   pack_mac.bat --help
REM
REM Output:
REM   build\Release\packages\blendkit-<version>-rh8_0-mac.yak
REM
REM Requires Go on PATH (or at C:\Program Files\Go\bin\go.exe), the .NET 7
REM SDK, and yak.exe (Rhino 8 install).

setlocal EnableDelayedExpansion

if /I "%~1"=="--help" goto usage
if /I "%~1"=="-h"     goto usage
if /I "%~1"=="/?"     goto usage

set RHINO_DIR=%~dp0..
set YAK_EXE=C:\Program Files\Rhino 8\System\Yak.exe

REM ---- Locate the Go client source dir ----------------------------------
REM Mirrors the resolution in deploy_rhino.bat. CLIENT_DIR must contain
REM main.go for `go build` to work.
REM Resolution order:
REM   1. BLENDKIT_CLIENT_DIR env var          - explicit override
REM   2. ..\client                            - legacy "client/ sibling" layout
REM   3. ..\source_addon\blenderkit\client    - new sibling-repo name (per bc9275d)
REM   4. ..\source_blenderkit_addon\blenderkit\client
REM                                           - historical sibling name
REM   5. ..\blenderkit\client                 - Blender repo nested inside this one
REM Sibling layout (one level up from source_rhino_plugin), not two.
set CLIENT_DIR=
if defined BLENDKIT_CLIENT_DIR set CLIENT_DIR=%BLENDKIT_CLIENT_DIR%
if not defined CLIENT_DIR if exist "%RHINO_DIR%\..\client\main.go" set CLIENT_DIR=%RHINO_DIR%\..\client
if not defined CLIENT_DIR if exist "%RHINO_DIR%\..\source_addon\blenderkit\client\main.go" set CLIENT_DIR=%RHINO_DIR%\..\source_addon\blenderkit\client
if not defined CLIENT_DIR if exist "%RHINO_DIR%\..\source_blenderkit_addon\blenderkit\client\main.go" set CLIENT_DIR=%RHINO_DIR%\..\source_blenderkit_addon\blenderkit\client
if not defined CLIENT_DIR if exist "%RHINO_DIR%\..\blenderkit\client\main.go" set CLIENT_DIR=%RHINO_DIR%\..\blenderkit\client
if not defined CLIENT_DIR (
    echo ERROR: could not locate Go client source dir ^(no main.go found^).
    echo Set BLENDKIT_CLIENT_DIR or check out source_blenderkit_addon
    echo as a sibling of source_rhino_plugin.
    exit /b 2
)

REM ---- Locate go.exe ----------------------------------------------------
set GO_EXE=
where go >nul 2>&1 && set GO_EXE=go
if not defined GO_EXE if exist "C:\Program Files\Go\bin\go.exe" set GO_EXE=C:\Program Files\Go\bin\go.exe
if not defined GO_EXE (
    echo ERROR: 'go' not on PATH and not at C:\Program Files\Go\bin\go.exe.
    echo Install Go from https://go.dev/dl/ to enable cross-compilation.
    exit /b 3
)

set STAGING=%RHINO_DIR%\build\Release-mac-pack
set RHP_OUT=%RHINO_DIR%\build\Release-mac
set PKG_OUT=%RHINO_DIR%\build\Release\packages

REM Match the Blender add-on's client naming (decide_client_binary_name
REM in client_lib.py + dev.py). os = {windows,macos,linux}, arch =
REM {x86_64,arm64}. The Rhino plug-in's runtime probe in
REM BlendkitPlugIn.cs:CandidateClientBinaryNames() picks this up first.
set CLIENT_BIN_NAME=blenderkit-client-macos-arm64

REM Read the Go client's own version (separate from the plug-in's
REM AssemblyVersion) from CLIENT_DIR\VERSION. The add-on's dev.py
REM passes this via -ldflags so `--version` reflects the actual
REM client build, not just the host's plug-in version.
set CLIENT_VERSION=
for /f "usebackq delims=" %%V in ("%CLIENT_DIR%\VERSION") do (
    if not defined CLIENT_VERSION set CLIENT_VERSION=%%V
)
if not defined CLIENT_VERSION (
    echo WARNING: could not read CLIENT_DIR\VERSION ^(%CLIENT_DIR%\VERSION^).
    echo          Building without -ldflags client version embed.
)

echo.
echo ==================================================
echo  Blendkit for Rhino 8 - cross-pack macOS .yak
echo ==================================================
echo  rhino dir   : %RHINO_DIR%
echo  client src  : %CLIENT_DIR%
echo  client ver  : %CLIENT_VERSION%
echo  client name : %CLIENT_BIN_NAME%
echo  go          : %GO_EXE%
echo  staging     : %STAGING%
echo.

REM ---- 1) Cross-compile Go client (darwin/arm64) -----------------------
REM Naming + flags mirror the Blendkit add-on's dev.py (one-to-one),
REM so a binary built by `blenderkit_client_build` from there drops in
REM unchanged: GOOS/GOARCH set the target, CGO_ENABLED=0 forces a
REM static build, -ldflags embeds the client version, and the output
REM filename is `blenderkit-client-macos-arm64`.
REM
REM arm64 covers Apple Silicon Macs natively; Intel Macs run it under
REM Rosetta 2 — fine for an HTTP proxy. Universal binary would need
REM `lipo`, which is Mac-only.
REM Wipe the whole staging dir up-front. Re-using a stale staging dir
REM is how we ended up with both `client/client` (from an earlier short-
REM name run) and `client/blenderkit-client-macos-arm64` (from a later
REM run) ending up in the same .yak — yak.exe just zips whatever it
REM finds. A clean staging dir guarantees the .yak's contents match
REM exactly what this script wrote.
if exist "%STAGING%" rmdir /S /Q "%STAGING%"

echo [pack_mac] Cross-compiling Go client (darwin/arm64) → %CLIENT_BIN_NAME%...
if not exist "%STAGING%\client" mkdir "%STAGING%\client"
pushd "%CLIENT_DIR%" >nul
set GOOS=darwin
set GOARCH=arm64
set CGO_ENABLED=0
set GOTOOLCHAIN=auto
if defined CLIENT_VERSION (
    "%GO_EXE%" build -ldflags "-X main.ClientVersion=%CLIENT_VERSION%" -o "%STAGING%\client\%CLIENT_BIN_NAME%" .
) else (
    "%GO_EXE%" build -o "%STAGING%\client\%CLIENT_BIN_NAME%" .
)
set GO_RC=%ERRORLEVEL%
set GOOS=
set GOARCH=
set CGO_ENABLED=
set GOTOOLCHAIN=
popd >nul
if not "%GO_RC%"=="0" (
    echo ERROR: go build failed ^(exit %GO_RC%^).
    exit /b 4
)

REM ---- 2) dotnet build the cross-platform .rhp -------------------------
echo [pack_mac] dotnet build -f net7.0 ^(cross-platform target^)...
dotnet build "%RHINO_DIR%\src\BlendkitRhino.csproj" -c Release -f net7.0 -o "%RHP_OUT%"
if errorlevel 1 (
    echo ERROR: dotnet build -f net7.0 failed.
    exit /b 5
)

REM ---- 3) Stage files alongside manifest_mac.yml -----------------------
echo [pack_mac] Staging files for yak build...
copy /Y "%RHP_OUT%\BlendkitRhino.rhp"            "%STAGING%\BlendkitRhino.rhp"  >nul
copy /Y "%RHINO_DIR%\src\Resources\blenderkit_logo.png" "%STAGING%\icon.png"    >nul
REM yak build looks for manifest.yml in the working dir. The Mac flavour
REM lives under deploy/ as manifest_mac.yml; copy it under the canonical
REM filename inside the staging dir only.
copy /Y "%RHINO_DIR%\deploy\manifest_mac.yml"    "%STAGING%\manifest.yml"       >nul

REM Python tree: same xcopy pattern as deploy_rhino.bat. The /E flag
REM keeps __pycache__ to match the published Windows yak's contents.
if not exist "%STAGING%\python" mkdir "%STAGING%\python"
xcopy /E /Y /I /Q "%RHINO_DIR%\python\blendkit_rhino" "%STAGING%\python\blendkit_rhino\" >nul
copy /Y "%RHINO_DIR%\python\*.py" "%STAGING%\python\" >nul 2>&1

REM Client tools (Blender-script recipes referenced by script_id).
if exist "%CLIENT_DIR%\tools" (
    if not exist "%STAGING%\client\tools" mkdir "%STAGING%\client\tools"
    xcopy /E /Y /I /Q "%CLIENT_DIR%\tools" "%STAGING%\client\tools\" >nul
)

REM Optional toolbar — copy if present in the source tree.
if exist "%RHINO_DIR%\deploy\Blendkit.rui" (
    copy /Y "%RHINO_DIR%\deploy\Blendkit.rui" "%STAGING%\Blendkit.rui" >nul
)

REM Drop any stale .yak from a previous pack so we don't accidentally
REM glob-pick the wrong one when moving the artefact below.
del /Q "%STAGING%\*.yak" >nul 2>&1

REM ---- 4) yak build --platform mac --------------------------------------
echo [pack_mac] yak build --platform mac...
pushd "%STAGING%" >nul
"%YAK_EXE%" build --platform mac
if errorlevel 1 (
    popd >nul
    echo ERROR: yak build failed ^(check manifest_mac.yml^).
    exit /b 6
)
popd >nul

REM Find the produced .yak (yak picks the filename based on platform +
REM rh tag derived from the .rhp's RhinoCommon reference).
set MAC_YAK=
for %%f in ("%STAGING%\*.yak") do set MAC_YAK=%%f
if not defined MAC_YAK (
    echo ERROR: yak build claimed success but produced no .yak.
    exit /b 6
)

REM ---- 5) Patch external_attr on the client binary to 0o100755 --------
REM yak.exe on Windows leaves ExternalAttributes at 0 for every entry,
REM which on Mac extract translates to no executable bit. The Mach-O
REM client binary needs +x or Rhino can't spawn it. Fix in-place via
REM .NET ZipArchive's Update mode — sets the upper 16 bits to 0o100755
REM (regular file, rwxr-xr-x), which standard zip extractors honour.
echo [pack_mac] Patching exec bit on client/%CLIENT_BIN_NAME%...
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.IO.Compression; Add-Type -AssemblyName System.IO.Compression.FileSystem; $z=[System.IO.Compression.ZipFile]::Open('%MAC_YAK%',[System.IO.Compression.ZipArchiveMode]::Update); $found=$false; foreach ($e in $z.Entries) { if ($e.FullName -eq 'client/%CLIENT_BIN_NAME%') { $e.ExternalAttributes = 0x81ED0000; $found=$true } }; $z.Dispose(); if (-not $found) { Write-Error 'client/%CLIENT_BIN_NAME% entry not found in yak'; exit 1 }"
if errorlevel 1 (
    echo ERROR: failed to patch exec bit.
    exit /b 7
)

REM ---- 6) Move .yak into the canonical packages dir -------------------
if not exist "%PKG_OUT%" mkdir "%PKG_OUT%"
for %%f in ("%MAC_YAK%") do (
    move /Y "%%f" "%PKG_OUT%\%%~nxf" >nul
    set MAC_YAK=%PKG_OUT%\%%~nxf
)

echo.
echo ==================================================
echo  Mac .yak packed successfully:
echo  %MAC_YAK%
echo ==================================================
echo.
echo To publish manually: "%YAK_EXE%" push "%MAC_YAK%"
echo ^(release.bat does this for you on a full release.^)

exit /b 0


:usage
echo Usage: pack_mac.bat
echo.
echo Cross-builds the macOS .yak from this Windows machine:
echo   1. go build for darwin/arm64
echo   2. dotnet build -f net7.0 ^(cross-platform .rhp^)
echo   3. yak build --platform mac
echo   4. patches the zip's exec bit on client/client
echo.
echo Output: build\Release\packages\blendkit-^<version^>-rh8_0-mac.yak
echo.
echo Requires Go and the .NET 7 SDK on this machine.
exit /b 1
