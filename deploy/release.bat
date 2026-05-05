@echo off
REM BlenderKit for Rhino 8 - one-shot release.
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
REM   4. Runs deploy_rhino.bat (builds + packs the .yak).
REM   5. yak push the new .yak to https://yak.rhino3d.com.
REM   6. git tag vX.Y.Z and push the tag to origin.
REM
REM Requires `yak login` to have run at least once on this machine
REM (token is cached in %APPDATA%\McNeel\yak.yml). If push fails with
REM "There was an error retrieving your cached token", run yak login
REM and retry.

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
set YAK_FILE=%RHINO_DIR%\build\Release\packages\blenderkit-%NEW_VERSION%-rh8_30-any.yak

echo.
echo ==================================================
echo  BlenderKit for Rhino 8 - release %NEW_VERSION%
echo ==================================================
echo  csproj   : %CSPROJ%
echo  manifests: %MANIFEST_WIN%
echo             %MANIFEST_MAC%
echo  yak file : %YAK_FILE%
echo.
echo This will commit the version bump, build + pack a .yak, push to
echo yak.rhino3d.com, and tag v%NEW_VERSION%. The deploy step will
echo also kill Rhino if it is running so the .rhp can be copied
echo locally. Cancel now (Ctrl+C) if that's a problem.
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

REM ---- Step 3/5: build + pack via deploy_rhino.bat --------------------
echo [release] Running deploy (build + pack)...
call "%~dp0deploy_rhino.bat"
if errorlevel 1 ( echo ERROR: deploy_rhino.bat failed. & exit /b 5 )

if not exist "%YAK_FILE%" (
    echo ERROR: deploy completed but expected .yak not found:
    echo   %YAK_FILE%
    echo Check deploy output above for clues.
    exit /b 5
)

REM ---- Step 4/5: yak push ---------------------------------------------
echo [release] Pushing to yak.rhino3d.com...
"%YAK_EXE%" push "%YAK_FILE%"
if errorlevel 1 (
    echo ERROR: yak push failed.
    echo If the message is "error retrieving your cached token", run:
    echo   "%YAK_EXE%" login
    echo and retry release.bat with the same version (the local commit
    echo is already in place; deploy will produce the same .yak).
    exit /b 6
)

REM ---- Step 5/5: tag + push tag --------------------------------------
echo [release] Tagging v%NEW_VERSION% and pushing to origin...
pushd "%RHINO_DIR%" >nul
git tag v%NEW_VERSION%
if errorlevel 1 ( echo WARNING: git tag failed (already exists?). & popd >nul & exit /b 7 )
git push origin v%NEW_VERSION%
if errorlevel 1 ( echo WARNING: git push tag failed - tag created locally only. & popd >nul & exit /b 7 )
popd >nul

echo.
echo ==================================================
echo  Released BlenderKit %NEW_VERSION%.
echo  - .yak: %YAK_FILE%
echo  - tag : v%NEW_VERSION% (pushed to origin)
echo  - live: https://yak.rhino3d.com/ (search "BlenderKit")
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
echo   4. Runs deploy_rhino.bat (builds + packs a .yak).
echo   5. yak push the .yak to yak.rhino3d.com.
echo   6. git tag v^<version^> + push the tag to origin.
echo.
echo Requires `yak login` to have run at least once on this machine.
exit /b 1
