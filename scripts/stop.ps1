# Post Pilot — local dev shutdown.
#
# Stops everything start.ps1 brought up:
#   1. The frontend window (npm run dev) — closes any PowerShell window whose
#      working directory was the frontend folder. Best-effort.
#   2. ngrok (using the PID file written by start.ps1, or by process name).
#   3. The Docker stack: api, publisher, postgres, pgadmin, minio.
#
# By default this preserves Postgres + MinIO volumes (your data survives).
# Pass -PurgeData to also remove the named volumes (destructive — full reset).
#
# Usage:
#   ./scripts/stop.ps1
#   ./scripts/stop.ps1 -PurgeData

param(
    [switch]$PurgeData
)

$ErrorActionPreference = 'Continue'   # don't bail on first failed cleanup step

# ── Resolve paths ───────────────────────────────────────────────────────────
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$DeployDir  = Join-Path $RepoRoot 'deploy'
$FrontendDir = Join-Path $RepoRoot 'frontend'
$RunDir     = Join-Path $RepoRoot '.run'
$NgrokPid   = Join-Path $RunDir   'ngrok.pid'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# ── Stop the frontend window ────────────────────────────────────────────────
# Heuristic: find PowerShell processes whose command line includes the frontend
# directory + "npm run dev". This avoids killing unrelated PowerShell terminals.
Step 'Stopping frontend (Vite)'
$frontendKilled = 0
try {
    $candidates = Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe' OR Name='node.exe'" -ErrorAction Stop
    foreach ($p in $candidates) {
        $cmd = $p.CommandLine
        if (-not $cmd) { continue }
        if ($cmd -match [regex]::Escape($FrontendDir) -or $cmd -match 'vite' -and $cmd -match 'node') {
            try {
                Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
                $frontendKilled++
            } catch { }
        }
    }
} catch { Warn "Could not enumerate processes via CIM: $_" }
if ($frontendKilled -gt 0) { Ok "Stopped $frontendKilled frontend-related process(es)" }
else                       { Warn 'No frontend process found (already stopped, or started outside this script).' }

# ── Stop ngrok ──────────────────────────────────────────────────────────────
Step 'Stopping ngrok'
$ngrokStopped = $false
if (Test-Path $NgrokPid) {
    $pidStr = Get-Content $NgrokPid -ErrorAction SilentlyContinue
    if ($pidStr -and (Get-Process -Id $pidStr -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $pidStr -Force -ErrorAction SilentlyContinue
        Ok "Stopped ngrok (pid=$pidStr)"
        $ngrokStopped = $true
    }
    Remove-Item $NgrokPid -ErrorAction SilentlyContinue
}
# Belt-and-braces: kill any leftover ngrok.exe regardless.
$leftover = Get-Process ngrok -ErrorAction SilentlyContinue
if ($leftover) {
    $leftover | Stop-Process -Force -ErrorAction SilentlyContinue
    Ok ("Stopped {0} stray ngrok process(es)" -f $leftover.Count)
    $ngrokStopped = $true
}
if (-not $ngrokStopped) { Warn 'No ngrok process found.' }

# ── Stop the Docker stack ───────────────────────────────────────────────────
if ($PurgeData) {
    Step 'Stopping Docker stack AND removing volumes (Postgres + MinIO data will be wiped)'
    $downArgs = @('down', '-v')
} else {
    Step 'Stopping Docker stack (volumes preserved)'
    $downArgs = @('down')
}

Push-Location $DeployDir
try {
    docker compose `
        --env-file ./env/local.env `
        -f docker-compose.yml `
        -f docker-compose.local.db.yml `
        -f docker-compose.local.storage.yml `
        @downArgs
    if ($LASTEXITCODE -ne 0) { Warn "docker compose down returned exit $LASTEXITCODE" }
    else                     { Ok 'Docker stack stopped' }
} finally {
    Pop-Location
}

Write-Host ''
Write-Host '════════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host '  Post Pilot is down' -ForegroundColor Green
if ($PurgeData) {
    Write-Host '  (Volumes removed — next start.ps1 will recreate them empty.)' -ForegroundColor Yellow
} else {
    Write-Host '  (Volumes preserved — Postgres + MinIO data still there.)' -ForegroundColor DarkGray
}
Write-Host '════════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host ''
