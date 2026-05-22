# PostPilot — reset the local Postgres database.
#
# Stops the local stack, deletes the postgres data volume, and starts the
# stack again so EF Core migrations re-create a clean schema on API startup.
#
# Usage:  pwsh -File dev/scripts/reset-db.ps1
$ErrorActionPreference = 'Stop'

$RepoRoot  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$DeployDir = Join-Path $RepoRoot 'deploy'
$EnvFile   = Join-Path $DeployDir 'env/local.env'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }

if (-not (Test-Path $EnvFile)) { throw "Env file missing: $EnvFile (copy dev/local.env.example to deploy/env/local.env first)" }

Step 'Stopping local stack'
Push-Location $DeployDir
try {
    docker compose `
        --env-file ./env/local.env `
        -f docker-compose.yml `
        -f docker-compose.local.db.yml `
        -f docker-compose.local.storage.yml `
        down
} finally { Pop-Location }
Ok 'Stack stopped'

Step 'Removing postgres data volume'
# The volume is namespaced by the compose project name, which defaults to
# the folder name (deploy/) — so it's `deploy_postgres_data`.
docker volume rm deploy_postgres_data 2>$null
Ok 'Volume removed (will be recreated empty on next start)'

Step 'Restarting local stack — API will re-run all migrations on startup'
& (Join-Path $RepoRoot 'scripts/start.ps1')
