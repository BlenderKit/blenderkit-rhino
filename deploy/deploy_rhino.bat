@echo off
REM BlenderKit for Rhino 8 — dev deploy script.
REM
REM Copies the plugin into Rhino's Plug-ins folder so you can iterate.
REM Rhino does NOT reload plugins cleanly — restart Rhino after each deploy.
REM
REM Usage:
REM   deploy_rhino.bat            - build (if dotnet SDK available) + copy
REM   deploy_rhino.bat --nobuild  - skip the C# build (Python-only changes)
REM   deploy_rhino.bat --launch   - start Rhino 8 after deploying

setlocal EnableDelayedExpansion

set RHINO_DIR=%~dp0..

REM Locate the Go client binary. After splitting the Rhino plug-in
REM into its own repo, this script no longer assumes a sibling
REM client folder. Resolution order (first hit wins):
REM   1. BLENDKIT_CLIENT_DIR env var        - explicit override
REM   2. ..\client                          - legacy sibling layout
REM   3. ..\..\source_blenderkit_addon\blenderkit\client
REM                                         - default sibling-repo layout
REM   4. ..\blenderkit\client               - Blender addon nested inside
REM Plain nested IFs (no else-if chains): cmd parses parenthesised
REM blocks once and trips on edge cases when nested deep.
set CLIENT_DIR=
if defined BLENDKIT_CLIENT_DIR set CLIENT_DIR=%BLENDKIT_CLIENT_DIR%
if not defined CLIENT_DIR if exist "%RHINO_DIR%\..\client\client.exe" set CLIENT_DIR=%RHINO_DIR%\..\client
if not defined CLIENT_DIR if exist "%RHINO_DIR%\..\..\source_blenderkit_addon\blenderkit\client\client.exe" set CLIENT_DIR=%RHINO_DIR%\..\..\source_blenderkit_addon\blenderkit\client
if not defined CLIENT_DIR if exist "%RHINO_DIR%\..\blenderkit\client\client.exe" set CLIENT_DIR=%RHINO_DIR%\..\blenderkit\client
if not defined CLIENT_DIR set CLIENT_DIR=%RHINO_DIR%\..\..\source_blenderkit_addon\blenderkit\client

REM REPO_ROOT kept as an alias for any callers that still reference it.
set REPO_ROOT=%RHINO_DIR%\..
set TARGET=%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\BlenderKit
set RHINO_EXE=C:\Program Files\Rhino 8\System\Rhino.exe
set YAK_EXE=C:\Program Files\Rhino 8\System\Yak.exe
set DO_BUILD=1
set DO_LAUNCH=0
set DO_PACK=0

set DO_KILL_RHINO=1
:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--nobuild"  set DO_BUILD=0
if /I "%~1"=="--launch"   set DO_LAUNCH=1
if /I "%~1"=="--nokill"   set DO_KILL_RHINO=0
if /I "%~1"=="--pack"     set DO_PACK=1
shift
goto parse_args
:args_done

REM ---- Kill Rhino + any orphan Go client before touching deployed files ----
REM A previous session that didn't shut down cleanly leaves client.exe alive
REM and holding its own binary open, which silently fails the copy below.
if "%DO_KILL_RHINO%"=="1" (
    taskkill /F /IM Rhino.exe >NUL 2>&1
    taskkill /F /IM client.exe >NUL 2>&1
    REM 2s pause so Windows releases file handles before we try to copy.
    ping -n 3 127.0.0.1 >NUL
)

echo.
echo ==================================================
echo  BlenderKit for Rhino 8 - deploy
echo ==================================================
echo  rhino dir : %RHINO_DIR%
echo  repo root : %REPO_ROOT%
echo  target    : %TARGET%
echo.

if not exist "%TARGET%"         mkdir "%TARGET%"
if not exist "%TARGET%\python"  mkdir "%TARGET%\python"
if not exist "%TARGET%\client"  mkdir "%TARGET%\client"

REM ---- Build C# shell (optional) ----
if "%DO_BUILD%"=="1" (
    where dotnet >nul 2>&1
    if errorlevel 1 (
        echo [deploy] dotnet not found on PATH - skipping C# build.
        echo [deploy] Install the .NET SDK from https://dotnet.microsoft.com/download
        echo [deploy] to be able to build the .rhp.
    ) else (
        dotnet --list-sdks 2>nul | findstr /R "^[0-9]" >nul
        if errorlevel 1 (
            echo [deploy] dotnet runtime present but no SDK - skipping C# build.
            echo [deploy] Install the .NET 7 SDK or .NET Framework 4.8 SDK to build.
        ) else (
            echo [deploy] Building C# shell...
            REM Multi-target build: explicitly pick the Windows target
            REM so the -o flag doesn't fight a same-name .rhp from the
            REM net7.0 (cross-platform) target.
            dotnet build "%RHINO_DIR%\src\BlendkitRhino.csproj" -c Release ^
                -f net7.0-windows -o "%RHINO_DIR%\build\Release" || goto build_failed
            if exist "%RHINO_DIR%\build\Release\BlendkitRhino.rhp" (
                copy /Y "%RHINO_DIR%\build\Release\BlendkitRhino.rhp" "%TARGET%\" >nul
                if errorlevel 1 (
                    echo [deploy] ERROR: failed to copy .rhp - file still locked?
                    exit /b 2
                )
                echo [deploy] .rhp copied.
            ) else (
                echo [deploy] WARNING: no .rhp produced by build - check csproj TargetExt.
            )
        )
    )
) else (
    echo [deploy] --nobuild - skipping C# build.
)

REM ---- Copy Python tree ----
echo [deploy] Copying Python modules...
xcopy /E /Y /I /Q "%RHINO_DIR%\python\blendkit_rhino" "%TARGET%\python\blendkit_rhino\" >nul
REM Standalone helper scripts (e.g. blender_export.py) live directly under python/.
copy /Y "%RHINO_DIR%\python\*.py" "%TARGET%\python\" >nul 2>&1

REM ---- Copy Go client binary (shared with Blender addon) ----
REM CLIENT_DIR resolved at the top — points at the Blender addon's
REM client/ folder (or the legacy sibling client\ for dev setups that
REM still ship them together).
if exist "%CLIENT_DIR%\client.exe" (
    copy /Y "%CLIENT_DIR%\client.exe" "%TARGET%\client\client.exe" >nul
    echo [deploy] Go client binary copied from %CLIENT_DIR%.
) else (
    echo [deploy] WARNING: Go client not found at %CLIENT_DIR%\client.exe
    echo [deploy]          Build it with: pushd "%CLIENT_DIR%" ^&^& go build ^&^& popd
    echo [deploy]          Or set BLENDKIT_CLIENT_DIR to the folder containing client.exe.
)

REM ---- Copy bundled Blender-script recipes (tools\) ----
REM The Go client resolves script_id at runtime by looking up
REM exe_dir\tools\id.py, so the recipes need to live next to
REM client.exe in the deployed plug-in. Hosts calling
REM script_id="export_glb" (BlenderConvertService) depend on these.
if exist "%CLIENT_DIR%\tools" (
    if not exist "%TARGET%\client\tools" mkdir "%TARGET%\client\tools"
    xcopy /E /Y /I /Q "%CLIENT_DIR%\tools" "%TARGET%\client\tools\" >nul
    echo [deploy] Bundled Blender-script recipes copied to client\tools\.
) else (
    echo [deploy] NOTE: %CLIENT_DIR%\tools not found - bundled script_id recipes unavailable.
    echo [deploy]       Update the Blender add-on checkout or set BLENDKIT_CLIENT_DIR.
)

REM ---- Optional: BlenderKit.rui toolbar file ----
REM If a BlenderKit.rui has been authored once via Tools ^> Toolbar Layout
REM in Rhino and saved next to the project, copy it alongside the .rhp.
REM The plugin's OnLoad picks it up on first run via -Toolbar _Open.
if exist "%RHINO_DIR%\deploy\BlenderKit.rui" (
    copy /Y "%RHINO_DIR%\deploy\BlenderKit.rui" "%TARGET%\BlenderKit.rui" >nul
    echo [deploy] Toolbar file BlenderKit.rui copied.
)

REM ---- YAK package metadata (manifest + listing icon) ----
REM Yak.exe scans the package dir for manifest.yml + icon.png. Both
REM live in the deploy folder of the source tree and get copied to
REM the target on every deploy so a subsequent --pack run produces
REM a reproducible .yak archive.
if exist "%RHINO_DIR%\deploy\manifest.yml" (
    copy /Y "%RHINO_DIR%\deploy\manifest.yml" "%TARGET%\manifest.yml" >nul
    echo [deploy] Yak manifest copied.
)
REM Listing icon for Package Manager — reuse the embedded panel logo.
if exist "%RHINO_DIR%\src\Resources\blenderkit_logo.png" (
    copy /Y "%RHINO_DIR%\src\Resources\blenderkit_logo.png" "%TARGET%\icon.png" >nul
    echo [deploy] Listing icon copied.
)

echo.
echo [deploy] Done.
echo.

REM ---- Optional: build a .yak package for Package Manager publish ----
REM Run with `--pack` to produce a redistributable archive. Yak.exe
REM scans the working directory for manifest.yml and zips every
REM sibling file into BlenderKit-<version>-rh8_0-win.yak. The output
REM lands in %RHINO_DIR%\build\Release\packages\ where the launching
REM Bash session can grab it for upload.
if "%DO_PACK%"=="1" (
    if not exist "%YAK_EXE%" (
        echo [deploy] WARNING: Yak.exe not found at %YAK_EXE%; skipping pack.
    ) else (
        echo [deploy] Building .yak package...
        if not exist "%RHINO_DIR%\build\Release\packages" (
            mkdir "%RHINO_DIR%\build\Release\packages"
        )
        REM Run yak from inside the deployed folder so it picks up
        REM manifest.yml and produces a relative-path archive.
        pushd "%TARGET%"
        "%YAK_EXE%" build
        if errorlevel 1 (
            echo [deploy] ERROR: yak build failed; check manifest.yml syntax + version.
            popd
        ) else (
            REM Move the .yak out of the deployed plug-ins dir so it
            REM doesn't ship inside the next package; users would see
            REM a "package within package" otherwise.
            REM Yak emits files as <lowercase-name>-<version>-rh8_0-win.yak.
            REM Glob over *.yak so we don't depend on the exact case.
            for %%f in (*.yak) do (
                move /Y "%%f" "%RHINO_DIR%\build\Release\packages\%%f" >nul
                echo [deploy] Package: %RHINO_DIR%\build\Release\packages\%%f
            )
            popd
            echo.
            echo [deploy] To publish: yak push ^<file^>.yak
            echo [deploy] First-time publishers: yak login  ^(opens browser^)
        )
    )
    echo.
)

echo Next steps:
echo   1. Start Rhino 8 (or use --launch next time).
echo   2. First install only:
echo        Tools ^> Options ^> Plug-ins ^> Install...
echo        select: %TARGET%\BlendkitRhino.rhp
echo   3. Run the command: _BlenderKit
echo      (or enable 'BlenderKit' in the panel list)
echo.

if "%DO_LAUNCH%"=="1" (
    if exist "%RHINO_EXE%" (
        echo [deploy] Launching Rhino...
        start "" "%RHINO_EXE%"
    ) else (
        echo [deploy] Rhino.exe not found at %RHINO_EXE% - start it manually.
    )
)

exit /b 0

:build_failed
echo [deploy] ERROR: dotnet build failed.
exit /b 1
