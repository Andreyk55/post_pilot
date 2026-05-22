# Post Pilot — local dev startup.
#
# Brings up the full local stack:
#   1. Docker stack: api, publisher, postgres, pgadmin, minio, minio-init
#   2. ngrok HTTP tunnel pointing at the API (port 5122)
#   3. Updates App__PublicUrl in deploy/env/local.env with the live ngrok URL
#      and restarts api + publisher so they pick it up
#   4. Frontend (Vite) in a new PowerShell window: npm run dev
#
# Usage (from anywhere):  pwsh -File scripts/start.ps1
#                or       ./scripts/start.ps1   (when run from repo root)
#
# Stop everything with:   ./scripts/stop.ps1

$ErrorActionPreference = 'Stop'

# ── Resolve paths ───────────────────────────────────────────────────────────
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$DeployDir  = Join-Path $RepoRoot 'deploy'
$EnvFile    = Join-Path $DeployDir 'env\local.env'
$FrontendDir = Join-Path $RepoRoot 'frontend'
$RunDir     = Join-Path $RepoRoot '.run'
$NgrokPid   = Join-Path $RunDir   'ngrok.pid'
$NgrokLog   = Join-Path $RunDir   'ngrok.log'

if (-not (Test-Path $RunDir)) { New-Item -ItemType Directory -Path $RunDir -Force | Out-Null }

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# ── Preconditions ───────────────────────────────────────────────────────────
Step 'Checking prerequisites'

try { docker version --format '{{.Server.Version}}' | Out-Null }
catch { throw 'Docker daemon is not reachable. Start Docker Desktop and retry.' }
Ok  'Docker daemon reachable'

$ngrokCmd = Get-Command ngrok -ErrorAction SilentlyContinue
if (-not $ngrokCmd) { throw 'ngrok not found on PATH. Install ngrok and run `ngrok config add-authtoken <TOKEN>` first.' }
$ngrokExe = $ngrokCmd.Source
Ok  "ngrok at $ngrokExe"

if (-not (Test-Path $EnvFile)) { throw "Env file missing: $EnvFile" }

# ── Bring up the Docker stack ───────────────────────────────────────────────
Step 'Starting Docker stack (api, publisher, postgres, pgadmin, minio)'
Push-Location $DeployDir
try {
    docker compose `
        --env-file ./env/local.env `
        -f docker-compose.yml `
        -f docker-compose.local.db.yml `
        -f docker-compose.local.storage.yml `
        up -d --build
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

# Wait for the API to answer.
Step 'Waiting for API to be ready on http://localhost:5122'
$deadline = (Get-Date).AddSeconds(120)
$apiReady = $false
while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost:5122/api/media/constraints' -TimeoutSec 2 -ErrorAction Stop
        if ($r.StatusCode -eq 200) { $apiReady = $true; break }
    } catch { Start-Sleep -Milliseconds 800 }
}
if (-not $apiReady) { throw 'API did not become ready within 120s. Check `docker logs deploy-api-1`.' }
Ok 'API ready'

# ── ngrok ───────────────────────────────────────────────────────────────────
Step 'Starting ngrok HTTP tunnel for port 5122'

# If a previous run left an ngrok process, kill it before starting a new one.
if (Test-Path $NgrokPid) {
    $oldPid = Get-Content $NgrokPid -ErrorAction SilentlyContinue
    if ($oldPid -and (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)) {
        Warn "Killing previous ngrok (pid=$oldPid)"
        Stop-Process -Id $oldPid -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $NgrokPid -ErrorAction SilentlyContinue
}

# Also catch the case where ngrok is running but we don't have its PID file —
# port 4040 (ngrok's local web UI) being open is the tell.
try {
    Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:4040/api/tunnels' -TimeoutSec 1 -ErrorAction Stop | Out-Null
    Warn 'ngrok is already running on :4040. Stopping it.'
    Get-Process ngrok -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
} catch { }

$ngrokProc = Start-Process -FilePath $ngrokExe `
    -ArgumentList @('http', '5122', '--log=stdout', '--log-format=logfmt') `
    -RedirectStandardOutput $NgrokLog `
    -WindowStyle Hidden `
    -PassThru
$ngrokProc.Id | Out-File -FilePath $NgrokPid -Encoding ascii
Ok "ngrok started (pid=$($ngrokProc.Id))"

# Wait for ngrok's local API to register the tunnel.
# Strategy: keep polling until either (a) we see an https tunnel, or (b) we see
# any tunnel at all and the deadline is close — better some URL than failing.
Step 'Reading public URL from ngrok'
$deadline = (Get-Date).AddSeconds(30)
$publicUrl = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    try {
        $resp = Invoke-RestMethod -Uri 'http://127.0.0.1:4040/api/tunnels' -TimeoutSec 2 -ErrorAction Stop
    } catch { continue }
    if (-not $resp.tunnels -or $resp.tunnels.Count -eq 0) { continue }

    # Prefer the https tunnel; fall back to the first one we see if only http exists.
    $httpsTunnel = $resp.tunnels | Where-Object { $_.proto -eq 'https' } | Select-Object -First 1
    if ($httpsTunnel) { $publicUrl = $httpsTunnel.public_url; break }

    # Some ngrok versions report just the URL under public_url and don't always
    # list both http and https. If only one tunnel exists, take it.
    $anyTunnel = $resp.tunnels | Select-Object -First 1
    if ($anyTunnel -and $anyTunnel.public_url) {
        $publicUrl = $anyTunnel.public_url
        # If we got an http URL, rewrite to https — the ngrok edge serves both.
        if ($publicUrl -like 'http://*') { $publicUrl = $publicUrl -replace '^http://', 'https://' }
        break
    }
}

if (-not $publicUrl) {
    # Last-ditch fallback: parse the URL from the ngrok log directly.
    # The log line looks like: started tunnel ... url=https://xxx.ngrok-free.dev
    $logTail = Get-Content $NgrokLog -Tail 40 -ErrorAction SilentlyContinue
    foreach ($line in $logTail) {
        if ($line -match 'url=(https://[^\s"]+)') { $publicUrl = $Matches[1]; break }
    }
    if ($publicUrl) {
        Warn "ngrok :4040 API was unreachable; parsed URL from log instead: $publicUrl"
    }
}

if (-not $publicUrl) {
    Warn 'Failed to read ngrok URL. Last 20 lines of ngrok log:'
    Get-Content $NgrokLog -Tail 20 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    throw 'Could not determine ngrok public URL. Check that authtoken is configured: ngrok config add-authtoken <TOKEN>'
}
Ok "Public URL: $publicUrl"

# ── Patch App__PublicUrl in local.env and restart api + publisher ───────────
Step 'Updating App__PublicUrl in deploy/env/local.env'
$envLines = Get-Content $EnvFile
$found    = $false
$newLines = foreach ($line in $envLines) {
    if ($line -match '^\s*App__PublicUrl\s*=') {
        $found = $true
        "App__PublicUrl=$publicUrl"
    } else {
        $line
    }
}
if (-not $found) { $newLines = $newLines + "App__PublicUrl=$publicUrl" }
# Write atomically with no trailing newline change.
[System.IO.File]::WriteAllLines($EnvFile, $newLines, [System.Text.UTF8Encoding]::new($false))
Ok 'local.env updated'

Step 'Restarting api + publisher so they pick up the new App__PublicUrl'
Push-Location $DeployDir
try {
    docker compose `
        --env-file ./env/local.env `
        -f docker-compose.yml `
        -f docker-compose.local.db.yml `
        -f docker-compose.local.storage.yml `
        up -d api publisher
    if ($LASTEXITCODE -ne 0) { throw "docker compose restart failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}
Ok 'api + publisher restarted'

# ── Helper: launch a payload in a new Windows Terminal tab ──────────────────
# Why this is a function: we now open three tabs from the script (frontend +
# two log tails), all with the same wt.exe quirks to navigate.
#
# Quirks worked around:
#   • wt.exe parses its own command line and uses ';' as a tab separator, so
#     passing a multi-line PowerShell payload directly fragments it across
#     multiple tabs. We encode the payload as Base64-UTF16LE and pass it via
#     -EncodedCommand, which contains no special characters wt cares about.
#   • The `--title` flag in `wt new-tab` is parsed brittlely — a title with
#     spaces eats the next argument (the executable). We set the tab title
#     from inside the PowerShell payload via an ANSI escape (ESC ]0;...BEL),
#     which Windows Terminal honors at runtime.
#   • We tag each tab's host process with a known `$Host.UI.RawUI.WindowTitle`
#     prefix ("postpilot:...") so stop.ps1 can find and kill exactly those
#     hosts without touching unrelated PowerShell windows.
function Start-PostPilotTab {
    param(
        [Parameter(Mandatory)] [string]$Tag,        # e.g. "frontend", "log:api"
        [Parameter(Mandatory)] [string]$DisplayName,# shown in the tab title
        [Parameter(Mandatory)] [string]$Payload,    # the PowerShell to run
        [switch]$ExitOnDone                         # if set, tab closes when payload finishes
    )

    $hostTitle = "postpilot:$Tag"
    $header = @"
`$Host.UI.RawUI.WindowTitle = '$hostTitle'
[Console]::Write([char]27 + ']0;$DisplayName' + [char]7)
Write-Host '== $DisplayName ==' -ForegroundColor Cyan
"@
    $fullPayload = $header + "`n" + $Payload
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($fullPayload))
    $wtCmd   = Get-Command wt.exe -ErrorAction SilentlyContinue
    $psArgs  = if ($ExitOnDone) { @('-EncodedCommand', $encoded) } else { @('-NoExit', '-EncodedCommand', $encoded) }

    if ($wtCmd) {
        if (-not $env:WT_SESSION) {
            Warn 'Not running inside Windows Terminal — new tab will open in the most-recent WT window, or create one.'
        }
        $wtArgs = @('-w', '0', 'new-tab', 'powershell.exe') + $psArgs
        Start-Process -FilePath $wtCmd.Source -ArgumentList $wtArgs | Out-Null
    } else {
        Start-Process -FilePath 'powershell.exe' `
            -ArgumentList $psArgs `
            -WindowStyle Normal | Out-Null
    }
}

# ── Frontend tab ─────────────────────────────────────────────────────────────
Step 'Starting frontend (Vite) — npm run dev'
$fePayload = @"
Set-Location -LiteralPath '$FrontendDir'
npm run dev
"@
Start-PostPilotTab -Tag 'frontend' -DisplayName 'post_pilot frontend' -Payload $fePayload
Ok 'Frontend tab opened'

# ── Container log tabs (api + publisher) ────────────────────────────────────
# The payload starts `docker logs -f` as a child process, then watches for a
# kill-flag file in .run/. When restart.ps1 creates that flag, the child is
# killed and the tab exits (closing the WT tab). Restart.ps1 then opens fresh
# tabs pointing at the new containers.
Step 'Starting container log tabs (api + publisher)'
$RunDirEsc = $RunDir.Replace("'", "''")
foreach ($svc in @('api', 'publisher')) {
    $containerName = "deploy-$svc-1"
    $killFlag = Join-Path $RunDir "log-$svc.kill"
    if (Test-Path $killFlag) { Remove-Item $killFlag -Force -ErrorAction SilentlyContinue }
    $killFlagEsc = $killFlag.Replace("'", "''")
    $logPayload = @"
`$killFlag = '$killFlagEsc'
`$proc = Start-Process -FilePath 'docker' -ArgumentList @('logs','-f','--tail','50','$containerName') -NoNewWindow -PassThru
while (-not `$proc.HasExited) {
    if (Test-Path `$killFlag) {
        try { Stop-Process -Id `$proc.Id -Force -ErrorAction SilentlyContinue } catch { }
        Remove-Item `$killFlag -Force -ErrorAction SilentlyContinue
        break
    }
    Start-Sleep -Milliseconds 500
}
exit
"@
    Start-PostPilotTab -Tag "log:$svc" -DisplayName "post_pilot $svc logs" -Payload $logPayload -ExitOnDone
}
Ok 'Log tabs opened for api + publisher'

# ── Final summary ───────────────────────────────────────────────────────────
Write-Host ''
Write-Host '════════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host '  Post Pilot is up' -ForegroundColor Green
Write-Host '════════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host ("  Frontend         : http://localhost:5173")
Write-Host ("  API (local)      : http://localhost:5122")
Write-Host ("  API (public)     : $publicUrl")
Write-Host ("  Swagger          : http://localhost:5122/swagger")
Write-Host ("  pgAdmin          : http://localhost:5050   (admin@postpilot.com / admin)")
Write-Host ("  MinIO console    : http://localhost:9001   (postpilot / postpilot-password)")
Write-Host ("  ngrok inspector  : http://localhost:4040")
Write-Host ''
Write-Host '  Open tabs in this WT window:' -ForegroundColor DarkGray
Write-Host '    • post_pilot frontend    (Vite dev server)'   -ForegroundColor DarkGray
Write-Host '    • post_pilot api logs    (docker logs -f api)' -ForegroundColor DarkGray
Write-Host '    • post_pilot publisher logs (docker logs -f publisher)' -ForegroundColor DarkGray
Write-Host ''
Write-Host '  Stop everything with:  ./scripts/stop.ps1' -ForegroundColor DarkGray
Write-Host ''
