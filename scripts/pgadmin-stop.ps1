# Post Pilot — stop pgAdmin only.
#
# Stops the pgAdmin container brought up by pgadmin-start.ps1.
# The local Postgres service (if running) is left alone.
#
# Usage:
#   ./scripts/pgadmin-stop.ps1

$ErrorActionPreference = 'Continue'   # don't bail on first failed cleanup step

# ── Resolve paths ───────────────────────────────────────────────────────────
$RepoRoot  = Split-Path -Parent $PSScriptRoot
$DeployDir = Join-Path $RepoRoot 'deploy'
$EnvFile   = Join-Path $DeployDir 'env\local.env'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

if (-not (Test-Path $EnvFile)) { throw "Env file missing: $EnvFile" }

Step 'Stopping pgAdmin container'
Push-Location $DeployDir
try {
    docker compose `
        --env-file ./env/local.env `
        -f docker-compose.local.db.yml `
        stop pgadmin
    if ($LASTEXITCODE -ne 0) { Warn "docker compose stop returned exit $LASTEXITCODE" }

    docker compose `
        --env-file ./env/local.env `
        -f docker-compose.local.db.yml `
        rm -f pgadmin
    if ($LASTEXITCODE -ne 0) { Warn "docker compose rm returned exit $LASTEXITCODE" }
    else                     { Ok 'pgAdmin container removed' }
} finally {
    Pop-Location
}

Write-Host ''
Write-Host '════════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host '  pgAdmin is down' -ForegroundColor Green
Write-Host '════════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host ''
