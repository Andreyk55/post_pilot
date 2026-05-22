# PostPilot — start the local dev stack.
#
# Thin wrapper around scripts/start.ps1 (which is the heavy script — it does
# Docker, ngrok, tab management, and patches App__PublicUrl). Kept at the
# repo root for now because moving it would break the WT tab-logging code
# that hardcodes the `deploy-*-1` container names. See dev/README.md.
#
# Usage:  pwsh -File dev/scripts/start-local.ps1
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
& (Join-Path $RepoRoot 'scripts/start.ps1') @args
