# PostPilot — reset the local Postgres database.
#
# Stops the local stack, deletes the postgres data volume, and starts the
# stack again so EF Core migrations re-create a clean schema on API startup.
#
# Usage:  pwsh -File dev/scripts/reset-db.ps1
$ErrorActionPreference = 'Stop'

$RepoRoot  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$DevDir    = Join-Path $RepoRoot 'dev'
$EnvFile   = Join-Path $DevDir 'local.env'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }

if (-not (Test-Path $EnvFile)) { throw "Env file missing: $EnvFile (copy dev/local.env.example to dev/local.env first)" }

Step 'Stopping local stack'
Push-Location $DevDir
try {
    docker compose `
        --env-file ./local.env `
        -f docker-compose.yml `
        -f docker-compose.local.db.yml `
        -f docker-compose.local.storage.yml `
        -f docker-compose.local.depends.yml `
        down
} finally { Pop-Location }
Ok 'Stack stopped'

Step 'Removing postgres data volume'
# The volume is namespaced by the compose project name (pinned to
# "postpilot" via COMPOSE_PROJECT_NAME in local.env), so the full name
# is `postpilot_postgres_data`.
docker volume rm postpilot_postgres_data 2>$null
Ok 'Volume removed (will be recreated empty on next start)'

Step 'Restarting local stack — API will re-run all migrations on startup'
& (Join-Path $PSScriptRoot 'start.ps1')
