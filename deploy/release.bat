@echo off
REM Blendkit for Rhino 8 - one-shot release.
REM
REM Usage:
REM   release.bat <version>      e.g. release.bat 0.1.2
REM   release.bat --help
REM
REM Performs:
REM   1. Validates working tree is clean (no uncommitted changes).
REM   2. Bumps <Version> in BlendkitRhino.csproj + version: in both
REM      manifest.yml and manifest_mac.yml.
REM   3. Commits the bump as a single commit ("release: X.Y.Z").
REM   4. Runs deploy_rhino.bat  (builds + packs the Windows .yak).
REM   5. Runs pack_mac.bat      (cross-builds + packs the macOS .yak,
REM                              including patching the Mach-O exec bit
REM                              that yak.exe drops on Windows).
REM   6. yak push both .yak files to https://yak.rhino3d.com.
REM   7. git tag vX.Y.Z and push the tag to origin.
REM
REM Requires `yak login` to have run at least once on this machine
REM (token is cached in %APPDATA%\McNeel\yak.yml). If push fails with
REM "There was an error retrieving your cached token", run yak login
REM and retry.
REM
REM Requires Go on PATH (or at C:\Program Files\Go\bin\go.exe) for the
REM darwin/arm64 client cross-compile that pack_mac.bat does.

setlocal EnableDelayedExpansion

if "%~1"=="" goto usage
if /I "%~1"=="--help" goto usage
if /I "%~1"=="-h" goto usage
if /I "%~1"=="/?" goto usage

set NEW_VERSION=%~1

REM ---- Validate semver pattern (X.Y.Z) ---------------------------------
powershell -NoProfile -Command "if (-not ('%NEW_VERSION%' -match '^\d+\.\d+\.\d+$')) { exit 1 }"
if errorlevel 1 (
    echo ERROR: version must be of the form X.Y.Z, got '%NEW_VERSION%'.
    exit /b 1
)

set RHINO_DIR=%~dp0..
set CSPROJ=%RHINO_DIR%\src\BlendkitRhino.csproj
set MANIFEST_WIN=%RHINO_DIR%\deploy\manifest.yml
set MANIFEST_MAC=%RHINO_DIR%\deploy\manifest_mac.yml
set YAK_EXE=C:\Program Files\Rhino 8\System\Yak.exe
REM csproj is now NuGet-pinned to RhinoCommon 8.0, so yak tags both
REM builds as rh8_0 (down from rh8_<build-machine-SR>). Filenames lock
REM in here so we know exactly what to push after pack.
set YAK_FILE_WIN=%RHINO_DIR%\build\Release\packages\blendkit-%NEW_VERSION%-rh8_0-any.yak
set YAK_FILE_MAC=%RHINO_DIR%\build\Release\packages\blendkit-%NEW_VERSION%-rh8_0-mac.yak

echo.
echo ==================================================
echo  Blendkit for Rhino 8 - release %NEW_VERSION%
echo ==================================================
echo  csproj    : %CSPROJ%
echo  manifests : %MANIFEST_WIN%
echo              %MANIFEST_MAC%
echo  yak (win) : %YAK_FILE_WIN%
echo  yak (mac) : %YAK_FILE_MAC%
echo.
echo This will commit the version bump, build + pack BOTH .yak files
echo (win + mac), push them to yak.rhino3d.com, and tag v%NEW_VERSION%.
echo The Windows deploy step kills Rhino if it is running so the .rhp
echo can be copied locally. Cancel now (Ctrl+C) if that's a problem.
echo.

set /p CONFIRM=Continue? (y/N):
if /I not "!CONFIRM!"=="y" (
    echo Aborted.
    exit /b 0
)

REM ---- Refuse to release with a dirty tree ----------------------------
REM Catches the common foot-gun of an accidental ad-hoc edit getting
REM swept into the release commit. The user can stash / commit /
REM discard before retrying.
pushd "%RHINO_DIR%" >nul
for /f "delims=" %%S in ('git status --porcelain') do (
    echo ERROR: working tree has uncommitted changes:
    git status --short
    popd >nul
    exit /b 2
)
popd >nul

REM ---- Step 1/5: bump version strings -----------------------------------
echo [release] Bumping versions to %NEW_VERSION%...
powershell -NoProfile -Command "$f='%CSPROJ%'; $t=Get-Content $f -Raw; $u8=New-Object System.Text.UTF8Encoding($false); [System.IO.File]::WriteAllText($f, ($t -replace '<Version>[0-9]+\.[0-9]+\.[0-9]+</Version>', '<Version>%NEW_VERSION%</Version>'), $u8)"
if errorlevel 1 ( echo ERROR: csproj rewrite failed. & exit /b 3 )

powershell -NoProfile -Command "$f='%MANIFEST_WIN%'; $t=Get-Content $f -Raw; $u8=New-Object System.Text.UTF8Encoding($false); [System.IO.File]::WriteAllText($f, ($t -replace 'version:\s*[0-9]+\.[0-9]+\.[0-9]+', 'version: %NEW_VERSION%'), $u8)"
if errorlevel 1 ( echo ERROR: manifest.yml rewrite failed. & exit /b 3 )

powershell -NoProfile -Command "$f='%MANIFEST_MAC%'; $t=Get-Content $f -Raw; $u8=New-Object System.Text.UTF8Encoding($false); [System.IO.File]::WriteAllText($f, ($t -replace 'version:\s*[0-9]+\.[0-9]+\.[0-9]+', 'version: %NEW_VERSION%'), $u8)"
if errorlevel 1 ( echo ERROR: manifest_mac.yml rewrite failed. & exit /b 3 )

REM ---- Step 2/5: commit ------------------------------------------------
echo [release] Committing version bump...
pushd "%RHINO_DIR%" >nul
git add src\BlendkitRhino.csproj deploy\manifest.yml deploy\manifest_mac.yml
if errorlevel 1 ( echo ERROR: git add failed. & popd >nul & exit /b 4 )
git commit -m "release: %NEW_VERSION%"
if errorlevel 1 ( echo ERROR: git commit failed. & popd >nul & exit /b 4 )
popd >nul

REM ---- Step 3/6: build + pack Windows yak via deploy_rhino.bat --------
echo [release] Running deploy_rhino.bat (Windows build + pack)...
call "%~dp0deploy_rhino.bat"
if errorlevel 1 ( echo ERROR: deploy_rhino.bat failed. & exit /b 5 )

if not exist "%YAK_FILE_WIN%" (
    echo ERROR: deploy completed but expected Windows .yak not found:
    echo   %YAK_FILE_WIN%
    echo Check deploy output above for clues.
    exit /b 5
)

REM ---- Step 4/6: cross-pack macOS yak via pack_mac.bat ----------------
REM Does the darwin/arm64 cross-compile + net7.0 .rhp build + yak build
REM --platform mac + Mach-O exec bit patch. Builds before either yak push
REM so we don't end up with a "Win-only release" if Mac packing breaks.
echo [release] Running pack_mac.bat (macOS cross-build + pack)...
call "%~dp0pack_mac.bat"
if errorlevel 1 ( echo ERROR: pack_mac.bat failed. & exit /b 5 )

if not exist "%YAK_FILE_MAC%" (
    echo ERROR: pack_mac completed but expected macOS .yak not found:
    echo   %YAK_FILE_MAC%
    echo Check pack output above for clues.
    exit /b 5
)

REM ---- Step 5/6: yak push (Windows then macOS) ------------------------
REM Push order doesn't really matter — yak treats them as independent
REM artifacts. We push Windows first so a token failure surfaces before
REM the second push attempt repeats the same error.
REM cmd parses the body of an if-block at scan time even when the block
REM is not entered. Bare ( and ) inside echo strings are read as block
REM delimiters and crash the parser with ". was unexpected at this time."
REM AFTER the previous step has already run and printed output. Every
REM ( and ) in echo strings inside an if-block must be escaped as ^( ^).
echo [release] Pushing Windows .yak to yak.rhino3d.com...
"%YAK_EXE%" push "%YAK_FILE_WIN%"
if errorlevel 1 (
    echo ERROR: yak push ^(Windows^) failed.
    echo If the message is "error retrieving your cached token", run:
    echo   "%YAK_EXE%" login
    echo and retry release.bat with the same version. The local commit
    echo is already in place; deploy will produce the same .yak.
    exit /b 6
)

echo [release] Pushing macOS .yak to yak.rhino3d.com...
"%YAK_EXE%" push "%YAK_FILE_MAC%"
if errorlevel 1 (
    echo ERROR: yak push ^(macOS^) failed.
    echo Windows yak is already up. To finish the release manually:
    echo   "%YAK_EXE%" push "%YAK_FILE_MAC%"
    echo Then run: git tag v%NEW_VERSION% ^&^& git push origin v%NEW_VERSION%
    exit /b 6
)

REM ---- Step 6/6: tag + push tag --------------------------------------
echo [release] Tagging v%NEW_VERSION% and pushing to origin...
pushd "%RHINO_DIR%" >nul
git tag v%NEW_VERSION%
if errorlevel 1 ( echo WARNING: git tag failed - tag may already exist. & popd >nul & exit /b 7 )
git push origin v%NEW_VERSION%
if errorlevel 1 ( echo WARNING: git push tag failed - tag created locally only. & popd >nul & exit /b 7 )
popd >nul

echo.
echo ==================================================
echo  Released Blendkit %NEW_VERSION%.
echo  - win .yak: %YAK_FILE_WIN%
echo  - mac .yak: %YAK_FILE_MAC%
echo  - tag     : v%NEW_VERSION% (pushed to origin)
echo  - live    : https://yak.rhino3d.com/ (search "Blendkit")
echo ==================================================

exit /b 0


:usage
echo Usage: release.bat ^<version^>
echo.
echo   release.bat 0.1.2
echo.
echo Performs the full release dance:
echo   1. Refuses to run with a dirty git tree.
echo   2. Bumps ^<Version^> in BlendkitRhino.csproj and version: in both manifest yml files.
echo   3. Commits the bump as "release: X.Y.Z".
echo   4. Runs deploy_rhino.bat (builds + packs the Windows .yak).
echo   5. Runs pack_mac.bat     (cross-builds + packs the macOS .yak).
echo   6. yak push both .yak files to yak.rhino3d.com.
echo   7. git tag v^<version^> + push the tag to origin.
echo.
echo Requires `yak login` to have run at least once on this machine,
echo and Go on PATH (or at C:\Program Files\Go\bin\go.exe) for the
echo darwin/arm64 client cross-compile.
exit /b 1
