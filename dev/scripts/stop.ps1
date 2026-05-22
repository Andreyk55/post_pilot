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
#   ./dev/scripts/stop.ps1
#   ./dev/scripts/stop.ps1 -PurgeData

param(
    [switch]$PurgeData
)

$ErrorActionPreference = 'Continue'   # don't bail on first failed cleanup step

# ── Resolve paths ───────────────────────────────────────────────────────────
# This script lives at <repo>/dev/scripts/stop.ps1 — go up two levels.
$RepoRoot   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$DevDir     = Join-Path $RepoRoot 'dev'
$FrontendDir = Join-Path $RepoRoot 'frontend'
$RunDir     = Join-Path $RepoRoot '.run'
$NgrokPid   = Join-Path $RunDir   'ngrok.pid'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# ── Stop the WT tabs the start script opened ────────────────────────────────
# start.ps1 tags every tab's PowerShell host with a window title prefix
# "postpilot:..." (e.g. postpilot:frontend, postpilot:log:api). We find those
# hosts by title and kill them — that closes their WT tab. Other PowerShell
# terminals on the machine are untouched.
#
# Also catch any orphaned npm/vite node.exe processes (they outlive their
# PowerShell parent if it was killed ungracefully).
Step 'Stopping frontend + log tabs'
$tabsKilled = 0
try {
    $allPs = Get-Process powershell, pwsh -ErrorAction SilentlyContinue
    foreach ($p in $allPs) {
        $title = $null
        try { $title = $p.MainWindowTitle } catch { }
        if ($title -and $title -like 'postpilot:*') {
            try {
                Stop-Process -Id $p.Id -Force -ErrorAction Stop
                $tabsKilled++
            } catch { }
        }
    }
} catch { Warn "Could not enumerate PowerShell hosts: $_" }

# Belt-and-braces: catch leftover npm/vite node.exe whose parent PS host died.
$nodeKilled = 0
try {
    $candidates = Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction Stop
    foreach ($p in $candidates) {
        $cmd = $p.CommandLine
        if (-not $cmd) { continue }
        if ($cmd -match [regex]::Escape($FrontendDir) -or ($cmd -match 'vite' -and $cmd -match 'node')) {
            try {
                Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
                $nodeKilled++
            } catch { }
        }
    }
} catch { Warn "Could not enumerate node processes via CIM: $_" }

if ($tabsKilled -gt 0)  { Ok "Closed $tabsKilled WT tab(s) opened by start.ps1" }
if ($nodeKilled -gt 0)  { Ok "Cleaned up $nodeKilled orphan node.exe process(es)" }
if ($tabsKilled -eq 0 -and $nodeKilled -eq 0) {
    Warn 'No frontend/log tabs found (already stopped, or started outside this script).'
}

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

Push-Location $DevDir
try {
    docker compose `
        --env-file ./local.env `
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
