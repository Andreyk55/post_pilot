# PostPilot — stop the local dev stack.
#
# Thin wrapper around scripts/stop.ps1. See dev/README.md for why the
# heavy script still lives at the repo root.
#
# Usage:  pwsh -File dev/scripts/stop-local.ps1
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
& (Join-Path $RepoRoot 'scripts/stop.ps1') @args
