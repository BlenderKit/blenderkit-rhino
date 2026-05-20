# test_convert_e2e.ps1 — drive a .blend -> .glb conversion through the
# BlenderKit Go client *without* Rhino in the loop. Same HTTP shape the
# Rhino panel uses (BlenderConvertService + the panel's /report poll),
# so when convert breaks for the user we can reproduce + bisect here
# instead of round-tripping through Rhino startup.
#
# Phases:
#   1. Resolve a Blender exe.
#   2. Make sure a Go client is alive with /run_blender_script
#      (auto-spawn the deployed copy if no candidate port answers).
#   3. Create a minimal sample.blend via `blender -b -P <gen-script>`
#      so the test is self-contained and doesn't depend on a checked-in
#      binary fixture.
#   4. POST /run_blender_script the same way the C# panel does.
#   5. Poll /report until the task hits status=finished | error,
#      streaming each status update so you can see the Blender spawn,
#      the script load, and the GLB export.
#   6. Verify the .glb landed and is non-empty (basic structural check).
#
# Exit 0 on success, non-zero on each named failure (see end).
#
# Usage:
#   pwsh tests/test_convert_e2e.ps1
#   pwsh tests/test_convert_e2e.ps1 -BlenderExe "D:\blenders\5.2.0\blender.exe"
#   pwsh tests/test_convert_e2e.ps1 -ClientExe "<path to client.exe>" -KeepArtifacts

[CmdletBinding()]
param(
    [string] $BlenderExe,
    [string] $ClientExe,
    [int]    $Port = 0,
    [int]    $TimeoutSec = 120,
    [switch] $KeepArtifacts
)

$ErrorActionPreference = 'Stop'

# ---------- 1. Locate Blender ------------------------------------------
function Find-Blender {
    param([string]$Hint)
    if ($Hint -and (Test-Path $Hint)) { return $Hint }
    $cands = @(
        'D:\blenders\5.2.0\blender.exe',
        'C:\Program Files\Blender Foundation\Blender 4.2\blender.exe',
        'C:\Program Files\Blender Foundation\Blender 4.3\blender.exe',
        'C:\Program Files\Blender Foundation\Blender 4.4\blender.exe',
        'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
    )
    foreach ($c in $cands) { if (Test-Path $c) { return $c } }
    # Last resort: probe running blender process — if Blender is open
    # the user clearly has a working install.
    $p = Get-Process -Name 'blender' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p -and $p.Path) { return $p.Path }
    return $null
}

$blender = Find-Blender -Hint $BlenderExe
if (-not $blender) {
    Write-Host 'FAIL: no Blender install found.' -ForegroundColor Red
    Write-Host '      Pass -BlenderExe <path> or install Blender 4.2+ in a standard location.'
    exit 10
}
Write-Host "Blender: $blender"

# ---------- 2. Ensure a Go client with /run_blender_script -------------
$candidatePorts = @(62485, 65425, 55428, 49452, 35452, 25152, 5152, 1234)
if ($Port -gt 0) { $candidatePorts = @($Port) }

function Probe-Capable {
    param([int]$P)
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:$P/run_blender_script" -Method POST -Body '{}' -ContentType 'application/json' -UseBasicParsing -TimeoutSec 2
        return [int]$r.StatusCode
    } catch [System.Net.WebException] {
        $rsp = $_.Exception.Response
        if ($null -eq $rsp) { return -1 }
        return [int]$rsp.StatusCode
    } catch { return -1 }
}

function Find-Capable-Port {
    foreach ($p in $candidatePorts) {
        $s = Probe-Capable -P $p
        # 400 == route registered + complained about empty body. That's
        # what we want. 404 == route not registered (the buggy case).
        if ($s -eq 400) { return $p }
    }
    return 0
}

$activePort = Find-Capable-Port
$ownedClient = $null

if ($activePort -eq 0) {
    # No live client. Spawn the deployed one, or one the user pointed at.
    if (-not $ClientExe) {
        $deployed = 'C:\Users\Intel\AppData\Roaming\McNeel\Rhinoceros\8.0\Plug-ins\BlenderKit\client\client.exe'
        if (Test-Path $deployed) { $ClientExe = $deployed }
    }
    if (-not $ClientExe -or -not (Test-Path $ClientExe)) {
        Write-Host 'FAIL: no live capable Go client and no client.exe to spawn.' -ForegroundColor Red
        Write-Host '      Pass -ClientExe <path> or deploy the plug-in once.'
        exit 11
    }
    Write-Host "Spawning Go client: $ClientExe"
    # Pick a port that's free — start with the first candidate.
    $spawnPort = $candidatePorts[0]
    $args = @('--port', "$spawnPort", '--server', 'https://www.blenderkit.com', '--proxy_which', 'SYSTEM', '--ssl_context', 'ENABLED', '--version', 'e2e-test', '--software', 'TestHarness', '--pid', "$PID")
    $ownedClient = Start-Process -FilePath $ClientExe -ArgumentList $args -PassThru -WindowStyle Hidden
    # Wait up to 10s for it to come up + have the route.
    for ($i = 0; $i -lt 50; $i++) {
        Start-Sleep -Milliseconds 200
        if ((Probe-Capable -P $spawnPort) -eq 400) { $activePort = $spawnPort; break }
    }
    if ($activePort -eq 0) {
        Write-Host "FAIL: spawned client didn't expose /run_blender_script within 10s." -ForegroundColor Red
        if ($ownedClient -and -not $ownedClient.HasExited) { Stop-Process -Id $ownedClient.Id -Force }
        exit 12
    }
}
Write-Host "Go client port: $activePort"

# ---------- 3. Generate a self-contained sample.blend ------------------
# Tiny scene: one default cube, nothing else. ~80 KB. Blender 4.x+ saves
# in the current default format so the resulting file works on any
# 4.0+ runtime.
$workDir = Join-Path $env:TEMP "blendkit-convert-e2e-$(Get-Date -Format yyyyMMdd-HHmmss)"
New-Item -ItemType Directory -Path $workDir | Out-Null
$blendPath = Join-Path $workDir 'sample.blend'
$glbPath   = Join-Path $workDir 'sample.glb'

$genPy = @"
import bpy
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.mesh.primitive_cube_add(size=2)
bpy.ops.wm.save_as_mainfile(filepath=r'$blendPath')
"@
$genPyPath = Join-Path $workDir '_gen.py'
[System.IO.File]::WriteAllText($genPyPath, $genPy)

Write-Host "Generating sample.blend via Blender -b -P ..."
$genProc = Start-Process -FilePath $blender -ArgumentList @('-b', '--python', $genPyPath) -Wait -PassThru -NoNewWindow -RedirectStandardOutput "$workDir\gen.stdout" -RedirectStandardError "$workDir\gen.stderr"
if ($genProc.ExitCode -ne 0 -or -not (Test-Path $blendPath)) {
    Write-Host 'FAIL: sample.blend generation failed.' -ForegroundColor Red
    Write-Host (Get-Content "$workDir\gen.stderr" -Raw)
    if ($ownedClient -and -not $ownedClient.HasExited) { Stop-Process -Id $ownedClient.Id -Force }
    exit 13
}
$blendSize = (Get-Item $blendPath).Length
Write-Host "  sample.blend OK ($blendSize bytes)"

# ---------- 4. POST /run_blender_script (matches C# panel shape) ------
$appId = $PID
$payload = @{
    script_id        = 'export_glb'
    blender_exe_path = $blender
    blend_path       = $blendPath
    output_path      = $glbPath
    status_message   = 'e2e test convert'
    params           = @{ output_path = $glbPath; yup = $true; draco = $false; export_apply = $true }
    app_id           = $appId
    addon_version    = 'e2e-test'
    platform_version = 'TestHarness'
    software         = 'TestHarness'
} | ConvertTo-Json -Depth 5

Write-Host "POST /run_blender_script ..."
$startMs = (Get-Date).Ticks / 10000
try {
    $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$activePort/run_blender_script" -Method POST -Body $payload -ContentType 'application/json' -UseBasicParsing -TimeoutSec 10
} catch {
    Write-Host "FAIL: POST /run_blender_script threw: $($_.Exception.Message)" -ForegroundColor Red
    if ($ownedClient -and -not $ownedClient.HasExited) { Stop-Process -Id $ownedClient.Id -Force }
    exit 14
}
if ($resp.StatusCode -ne 200) {
    Write-Host "FAIL: status $($resp.StatusCode) — expected 200." -ForegroundColor Red
    Write-Host "      body: $($resp.Content)"
    if ($ownedClient -and -not $ownedClient.HasExited) { Stop-Process -Id $ownedClient.Id -Force }
    exit 14
}
$taskId = ($resp.Content | ConvertFrom-Json).task_id
if (-not $taskId) {
    Write-Host 'FAIL: response had no task_id.' -ForegroundColor Red
    exit 14
}
Write-Host "  task_id: $taskId"

# ---------- 5. Poll /report ------------------------------------------
# Mirrors BlendkitPanel's HandleTask dispatcher: read the array, find
# the entry whose task_id matches ours, print its status, stop when
# status is "finished" or "error".
$lastSeen = ''
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$reportBody = @{ app_id = $appId; api_key = ''; addon_version = 'e2e-test'; blender_version = '4.2.0'; platform_version = 'TestHarness'; project_name = '' } | ConvertTo-Json
$finalStatus = ''
$finalMessage = ''
$finalResult = $null

while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:$activePort/report" -Method POST -Body $reportBody -ContentType 'application/json' -UseBasicParsing -TimeoutSec 5
        $tasks = ConvertFrom-Json $r.Content
        $ours = $tasks | Where-Object { $_.task_id -eq $taskId } | Select-Object -First 1
        if ($ours) {
            $line = "[$($ours.status)] $($ours.message)"
            if ($line -ne $lastSeen) { Write-Host "  $line"; $lastSeen = $line }
            if ($ours.status -eq 'finished' -or $ours.status -eq 'error') {
                $finalStatus = $ours.status
                $finalMessage = $ours.message
                $finalResult = $ours.result
                break
            }
        }
    } catch {
        Write-Verbose "report poll error: $($_.Exception.Message)"
    }
    Start-Sleep -Milliseconds 400
}

$elapsedMs = [int](((Get-Date).Ticks / 10000) - $startMs)

# ---------- 6. Verify --------------------------------------------------
if ($finalStatus -eq '') {
    Write-Host "FAIL: timeout ($TimeoutSec s) without final status." -ForegroundColor Red
    if ($ownedClient -and -not $ownedClient.HasExited) { Stop-Process -Id $ownedClient.Id -Force }
    exit 15
}
if ($finalStatus -eq 'error') {
    Write-Host "FAIL: task finished with status=error: $finalMessage" -ForegroundColor Red
    if ($ownedClient -and -not $ownedClient.HasExited) { Stop-Process -Id $ownedClient.Id -Force }
    exit 16
}
if (-not (Test-Path $glbPath)) {
    Write-Host "FAIL: task finished but $glbPath wasn't written." -ForegroundColor Red
    if ($ownedClient -and -not $ownedClient.HasExited) { Stop-Process -Id $ownedClient.Id -Force }
    exit 17
}
$glbSize = (Get-Item $glbPath).Length
$glbBytes = [System.IO.File]::ReadAllBytes($glbPath)
$magic = if ($glbBytes.Length -ge 4) { -join ($glbBytes[0..3] | ForEach-Object { [char]$_ }) } else { '' }
if ($magic -ne 'glTF') {
    Write-Host "FAIL: $glbPath has wrong magic header (got '$magic', want 'glTF')." -ForegroundColor Red
    if ($ownedClient -and -not $ownedClient.HasExited) { Stop-Process -Id $ownedClient.Id -Force }
    exit 18
}

Write-Host ''
Write-Host "PASS: convert pipeline OK." -ForegroundColor Green
Write-Host "      elapsed: $elapsedMs ms"
Write-Host "      output : $glbPath ($glbSize bytes, magic glTF)"
if ($KeepArtifacts) {
    Write-Host "      artifacts kept under $workDir"
} else {
    Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
}
if ($ownedClient -and -not $ownedClient.HasExited) {
    Write-Host "Stopping spawned client (pid $($ownedClient.Id))"
    Stop-Process -Id $ownedClient.Id -Force -ErrorAction SilentlyContinue
}
exit 0
