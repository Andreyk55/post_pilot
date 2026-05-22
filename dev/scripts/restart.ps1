# Post Pilot — rebuild and restart api + publisher containers only.
#
# Leaves Postgres, pgAdmin, MinIO, ngrok, and the frontend untouched.
# Closes the stale log tabs opened by start.ps1 (via kill-flag files) and
# opens fresh ones pointing at the rebuilt containers.
#
# Usage (from anywhere):  pwsh -File scripts/restart.ps1
#                or       ./scripts/restart.ps1   (when run from repo root)

$ErrorActionPreference = 'Stop'

# ── Resolve paths ───────────────────────────────────────────────────────────
$RepoRoot  = Split-Path -Parent $PSScriptRoot
$DeployDir = Join-Path $RepoRoot 'deploy'
$RunDir    = Join-Path $RepoRoot '.run'

if (-not (Test-Path $RunDir)) { New-Item -ItemType Directory -Path $RunDir -Force | Out-Null }

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# ── Preconditions ───────────────────────────────────────────────────────────
Step 'Checking prerequisites'
try { docker version --format '{{.Server.Version}}' | Out-Null }
catch { throw 'Docker daemon is not reachable. Start Docker Desktop and retry.' }
Ok 'Docker daemon reachable'

# ── Signal old log tabs to close ────────────────────────────────────────────
# Each tab from start.ps1 watches for a kill-flag file. Touch the flag and
# the tab's docker logs child is killed and its powershell host exits, which
# closes the WT tab.
Step 'Signaling old api + publisher log tabs to close'
foreach ($svc in @('api', 'publisher')) {
    $killFlag = Join-Path $RunDir "log-$svc.kill"
    New-Item -ItemType File -Path $killFlag -Force | Out-Null
}
# Give the tabs up to 5s to consume the flags and exit.
$deadline = (Get-Date).AddSeconds(5)
while ((Get-Date) -lt $deadline) {
    $stillThere = $false
    foreach ($svc in @('api', 'publisher')) {
        if (Test-Path (Join-Path $RunDir "log-$svc.kill")) { $stillThere = $true; break }
    }
    if (-not $stillThere) { break }
    Start-Sleep -Milliseconds 200
}
# Belt-and-braces: remove any lingering flags so they don't kill the new tabs.
foreach ($svc in @('api', 'publisher')) {
    Remove-Item (Join-Path $RunDir "log-$svc.kill") -Force -ErrorAction SilentlyContinue
}
Ok 'Old log tabs signaled'

# ── Rebuild and restart api + publisher ─────────────────────────────────────
# Only docker-compose.yml defines api + publisher — no db/storage files needed,
# which avoids triggering minio-init or other one-shot containers.
Step 'Rebuilding and restarting api + publisher'
Push-Location $DeployDir
try {
    docker compose `
        --env-file ./env/local.env `
        -f docker-compose.yml `
        up -d --build api publisher
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}
Ok 'api + publisher rebuilt and restarted'

# ── Wait for API to be ready ─────────────────────────────────────────────────
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

# ── Open fresh log tabs (mirrors start.ps1's Start-PostPilotTab) ─────────────
Step 'Opening fresh log tabs for api + publisher'

function Start-PostPilotTab {
    param(
        [Parameter(Mandatory)] [string]$Tag,
        [Parameter(Mandatory)] [string]$DisplayName,
        [Parameter(Mandatory)] [string]$Payload,
        [switch]$ExitOnDone
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
            Warn 'Not running inside Windows Terminal — new tab will open in the most-recent WT window.'
        }
        $wtArgs = @('-w', '0', 'new-tab', 'powershell.exe') + $psArgs
        Start-Process -FilePath $wtCmd.Source -ArgumentList $wtArgs | Out-Null
    } else {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $psArgs -WindowStyle Normal | Out-Null
    }
}

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
Ok 'Fresh log tabs opened for api + publisher'

# ── Final summary ────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '════════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host '  api + publisher restarted' -ForegroundColor Green
Write-Host '════════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host ("  API (local) : http://localhost:5122")
Write-Host ("  Swagger     : http://localhost:5122/swagger")
Write-Host ''
