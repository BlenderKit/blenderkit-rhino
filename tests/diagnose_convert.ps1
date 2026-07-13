# diagnose_convert.ps1 — health-check the Blendkit convert pipeline
# from outside Rhino. Run any time the Rhino plug-in's status bar shows
# "Convert request failed: Response status code does not indicate success".
#
# What it does:
#   1. Scans the BlenderKit-Client candidate-port list (same list the
#      Rhino plug-in's ClientLib.DiscoverPortAsync uses).
#   2. For every responding port: identifies the client (PID, version,
#      whether it was spawned by the Blender add-on or by us).
#   3. Probes the routes Rhino actually depends on -
#      /report, /run_blender_script, /search, /refresh_token, /oauth2/logout -
#      and reports the HTTP status of each. The handler at /run_blender_script
#      should return 400 ("either script_id or script_path is required")
#      when poked with {} — a 404 means the route was never registered,
#      which is the unmistakable signature of a too-old client (v1.8.3
#      and earlier never knew about that route).
#   4. Diagnoses common failure modes and prints actionable advice.
#
# Usage:
#   pwsh tests/diagnose_convert.ps1
#   pwsh tests/diagnose_convert.ps1 -Verbose            # show raw bodies
#   pwsh tests/diagnose_convert.ps1 -ProbePort 65425    # check one port only
#
# Exit codes:
#   0 = at least one port has the routes Rhino needs
#   1 = clients found but none support /run_blender_script
#   2 = no client responded on any candidate port

[CmdletBinding()]
param(
    [int[]] $ProbePort = $null
)

$ErrorActionPreference = 'Stop'
$candidatePorts = if ($ProbePort) { $ProbePort } else { @(62485, 65425, 55428, 49452, 35452, 25152, 5152, 1234) }

# Routes Rhino's panel hits in the order they're typed in
# BlendkitPanel.cs / Infra/*. We don't care about the HTTP-level
# response details for every one — only that the route is registered.
# A 404 here is the smoking gun for a missing route.
$probeRoutes = @(
    @{ Path = '/report';              Method = 'POST'; Body = '{"app_id":99999,"api_key":"","addon_version":"diag","blender_version":"4.2.0","platform_version":"diag"}' },
    @{ Path = '/run_blender_script';  Method = 'POST'; Body = '{}' },
    @{ Path = '/search';              Method = 'POST'; Body = '{}' },
    @{ Path = '/refresh_token';       Method = 'GET';  Body = '{}' },
    @{ Path = '/oauth2/logout';       Method = 'GET';  Body = '{}' }
)

function Probe-One {
    param([int]$Port, [string]$Method, [string]$Path, [string]$Body)
    $uri = "http://127.0.0.1:$Port$Path"
    try {
        # Splat-build the param hash dynamically — passing -Body '' or a
        # ContentType on a GET request makes Invoke-WebRequest send a
        # malformed request that the Go client silently drops (looks
        # like a connection timeout from this end). Only attach a body
        # when there's actually one to send.
        $iwr = @{
            Uri = $uri; Method = $Method; UseBasicParsing = $true; TimeoutSec = 3
        }
        if ($null -ne $Body -and $Body -ne '') {
            $iwr.Body = $Body
            $iwr.ContentType = 'application/json'
        }
        $resp = Invoke-WebRequest @iwr
        return @{ Status = [int]$resp.StatusCode; Body = $resp.Content }
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($null -eq $r) { return @{ Status = -1; Body = $_.Exception.Message } }
        $sr = New-Object System.IO.StreamReader($r.GetResponseStream())
        $b = $sr.ReadToEnd(); $sr.Close()
        return @{ Status = [int]$r.StatusCode; Body = $b }
    } catch {
        return @{ Status = -1; Body = $_.Exception.Message }
    }
}

function Identify-Client {
    param([int]$Port)
    $res = Probe-One -Port $Port -Method 'GET' -Path '/' -Body ''
    if ($res.Status -ne 200) { return $null }
    # Identity page is hand-rolled HTML — scrape the labelled <div>s.
    # Stays decoupled from any specific HTML version because we only
    # match on the field labels, not the surrounding markup.
    # $PID is a PowerShell automatic readonly variable (current process
    # PID) — using it as a local would throw. Name the captured value
    # $clientPid instead.
    $clientPid = if ($res.Body -match 'Client PID:\s*(\d+)') { $matches[1] } else { '?' }
    $version = if ($res.Body -match 'Client Version:\s*([^\<]+)') { $matches[1].Trim() } else { '?' }
    $plat    = if ($res.Body -match 'Platform:\s*([^\<]+)') { $matches[1].Trim() } else { '?' }
    $sysId   = if ($res.Body -match 'System ID:\s*([^\<]+)') { $matches[1].Trim() } else { '?' }
    $startedBy = if ($res.Body -match 'Started from Blender add-on:\s*([^\<]+)') {
        "Blender add-on $($matches[1].Trim())"
    } elseif ($res.Body -match 'Started from Rhino plug-in:\s*([^\<]+)') {
        "Rhino plug-in $($matches[1].Trim())"
    } else { '(not declared)' }
    return @{ PID = $clientPid; Version = $version; Platform = $plat; SystemId = $sysId; StartedBy = $startedBy }
}

# ----- run --------------------------------------------------------------

Write-Host ''
Write-Host '=== BlenderKit-Client diagnostic ============================' -ForegroundColor Cyan
Write-Host "candidate ports: $($candidatePorts -join ', ')"
Write-Host ''

$live = @()
foreach ($port in $candidatePorts) {
    $id = Identify-Client -Port $port
    if ($null -eq $id) {
        Write-Verbose "  port $port : no response"
        continue
    }
    Write-Host "* Port $port" -ForegroundColor Yellow
    Write-Host "    PID         : $($id.PID)"
    Write-Host "    Version     : $($id.Version)"
    Write-Host "    Platform    : $($id.Platform)"
    Write-Host "    Started by  : $($id.StartedBy)"
    Write-Host "    System ID   : $($id.SystemId)"
    Write-Host '    Routes:'
    $routeResults = @{}
    foreach ($r in $probeRoutes) {
        $rr = Probe-One -Port $port -Method $r.Method -Path $r.Path -Body $r.Body
        $routeResults[$r.Path] = $rr.Status
        $colour = switch ($rr.Status) {
            -1   { 'Red' }
            404  { 'Red' }
            500  { 'Red' }
            400  { 'Green' }   # expected for bare {} on most routes
            200  { 'Green' }
            default { 'Yellow' }
        }
        $label = "      $($r.Method.PadRight(4)) $($r.Path.PadRight(28)) -> $($rr.Status)"
        Write-Host $label -ForegroundColor $colour
        if ($VerbosePreference -ne 'SilentlyContinue' -and $rr.Body) {
            $oneLine = ($rr.Body -replace "`r?`n", ' ').Substring(0, [Math]::Min(120, $rr.Body.Length))
            Write-Host "          body: $oneLine"
        }
    }
    $live += @{ Port = $port; Id = $id; Routes = $routeResults }
    Write-Host ''
}

if ($live.Count -eq 0) {
    Write-Host 'DIAGNOSIS: no BlenderKit-Client is running on any candidate port.' -ForegroundColor Red
    Write-Host '           - If Rhino is open, the plug-in should be spawning one — check'
    Write-Host "             %APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\Blendkit\client\client.exe"
    Write-Host '           - Or start Blender with the Blendkit add-on enabled.'
    exit 2
}

# Verdict: does ANY responding client have /run_blender_script?
$capable = @($live | Where-Object { $_.Routes['/run_blender_script'] -ne 404 -and $_.Routes['/run_blender_script'] -ne -1 })
# Force-array via @(...) because PowerShell's pipeline unwraps a
# single-item collection — without it $capable[0] indexes into the
# hashtable's keys/values instead of returning the hashtable itself.
if ($capable.Count -gt 0) {
    $good = $capable[0]
    Write-Host "DIAGNOSIS: convert pipeline reachable via port $($good.Port) (client $($good.Id.Version), $($good.Id.StartedBy))." -ForegroundColor Green
    Write-Host '            /run_blender_script answered without 404 — Rhino panel should be able to convert.'
    Write-Host '            If you ARE seeing convert failures despite this, run test_convert_e2e.ps1 to actually'
    Write-Host '            roundtrip a .blend through the pipeline.'
    exit 0
}

# We have at least one client, but none with /run_blender_script.
# This is the user-reported scenario: Blender add-on shipped an older
# client that doesn't have the route, Rhino's port-discovery finds it
# first (lower in the candidate list = higher priority) and gets a 404.
Write-Host 'DIAGNOSIS: clients responded, but NONE expose /run_blender_script (all returned 404).' -ForegroundColor Red
foreach ($l in $live) {
    Write-Host "  - port $($l.Port): $($l.Id.Version) — $($l.Id.StartedBy)"
}
Write-Host ''
Write-Host 'The /run_blender_script route was added in BlenderKit-Client v1.9.0. Clients shipped'
Write-Host 'with older Blender add-on releases (e.g. v1.8.3 from add-on v3.19.x) do not have it,'
Write-Host 'which is exactly what causes "Convert request failed: 404 Not Found" in the Rhino panel.'
Write-Host ''
Write-Host 'Fix options, simplest first:' -ForegroundColor Yellow
Write-Host '  1. Close Blender (or unenable the Blendkit add-on there) — the Rhino plug-in will'
Write-Host '     then spawn its own newer client on the same port.'
Write-Host '  2. Update the Blender Blendkit add-on to a build that ships client >= v1.9.0.'
Write-Host '  3. Long-term plug-in fix: ClientLib.DiscoverPortAsync should reject a discovered'
Write-Host '     client that returns 404 on /run_blender_script and try the next candidate.'
exit 1
