# Post Pilot — start pgAdmin only.
#
# Brings up just the pgAdmin container from docker-compose.local.db.yml.
# Useful when the database lives elsewhere (e.g. Supabase) and you only need
# a local UI to query it. The local Postgres service is NOT started.
#
# Credentials and port come from deploy/env/local.env:
#   PGADMIN_DEFAULT_EMAIL, PGADMIN_DEFAULT_PASSWORD, PGADMIN_PORT
#
# Usage (from anywhere):  pwsh -File dev/scripts/pgadmin-start.ps1
#                 or      ./dev/scripts/pgadmin-start.ps1   (when run from repo root)
#
# Stop with:              ./dev/scripts/pgadmin-stop.ps1

$ErrorActionPreference = 'Stop'

# ── Resolve paths ───────────────────────────────────────────────────────────
# This script lives at <repo>/dev/scripts/pgadmin-start.ps1 — go up two levels.
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$DeployDir = Join-Path $RepoRoot 'deploy'
$EnvFile   = Join-Path $DeployDir 'env\local.env'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# ── Preconditions ───────────────────────────────────────────────────────────
Step 'Checking prerequisites'

try { docker version --format '{{.Server.Version}}' | Out-Null }
catch { throw 'Docker daemon is not reachable. Start Docker Desktop and retry.' }
Ok 'Docker daemon reachable'

if (-not (Test-Path $EnvFile)) { throw "Env file missing: $EnvFile" }

# Read PGADMIN_PORT from env file for the final summary (default 5050).
$pgadminPort = '5050'
$pgadminEmail = 'admin@postpilot.com'
foreach ($line in Get-Content $EnvFile) {
    if ($line -match '^\s*PGADMIN_PORT\s*=\s*(\S+)')         { $pgadminPort = $Matches[1] }
    if ($line -match '^\s*PGADMIN_DEFAULT_EMAIL\s*=\s*(\S+)') { $pgadminEmail = $Matches[1] }
}

# ── Bring up pgAdmin only ───────────────────────────────────────────────────
Step 'Starting pgAdmin container'
Push-Location $DeployDir
try {
    docker compose `
        --env-file ./env/local.env `
        -f docker-compose.local.db.yml `
        up -d pgadmin
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}
Ok 'pgAdmin container started'

# ── Wait for pgAdmin HTTP to answer ─────────────────────────────────────────
Step "Waiting for pgAdmin to be ready on http://localhost:$pgadminPort"
$deadline = (Get-Date).AddSeconds(60)
$ready = $false
while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$pgadminPort/misc/ping" -TimeoutSec 2 -ErrorAction Stop
        if ($r.StatusCode -eq 200) { $ready = $true; break }
    } catch { Start-Sleep -Milliseconds 800 }
}
if (-not $ready) {
    Warn 'pgAdmin did not respond within 60s. It may still be initializing — check `docker logs postpilot-pgadmin`.'
} else {
    Ok 'pgAdmin ready'
}

Write-Host ''
Write-Host '════════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host '  pgAdmin is up' -ForegroundColor Green
Write-Host '════════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host ("  URL       : http://localhost:$pgadminPort")
Write-Host ("  Email     : $pgadminEmail")
Write-Host ("  Password  : (see PGADMIN_DEFAULT_PASSWORD in deploy/env/local.env)")
Write-Host ''
Write-Host '  Stop with:  ./dev/scripts/pgadmin-stop.ps1' -ForegroundColor DarkGray
Write-Host ''
