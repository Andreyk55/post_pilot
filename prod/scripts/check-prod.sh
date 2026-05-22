#!/usr/bin/env bash
# PostPilot — production smoke check.
#
# Usage on the VPS:
#   /opt/postpilot/prod/scripts/check-prod.sh
#
# Returns non-zero if any check fails so it can be wired into uptime monitors.

set -uo pipefail

green() { printf '\033[32m%s\033[0m\n' "$*"; }
red()   { printf '\033[31m%s\033[0m\n' "$*"; }
cyan()  { printf '\033[36m==> %s\033[0m\n' "$*"; }

fail=0

cyan "Container status"
docker compose -f /opt/postpilot/prod/docker-compose.yml ps || fail=1
echo

cyan "Local API health (loopback — what nginx sees)"
if curl -fsS -o /dev/null -w "  HTTP %{http_code}\n" http://127.0.0.1:5122/health; then
    green "  local /health OK"
else
    red "  local /health FAILED"
    fail=1
fi
echo

cyan "Public API health (via host nginx + TLS)"
if curl -fsS -o /dev/null -w "  HTTP %{http_code}\n" https://post-pilot.cloud-ip.cc/health; then
    green "  public /health OK"
else
    red "  public /health FAILED"
    fail=1
fi
echo

cyan "Listening sockets — 5122 must be 127.0.0.1 only, 5432 must NOT appear"
sudo ss -tulpn | grep -E '5122|5432' || true
echo

if [[ $fail -ne 0 ]]; then
    red "One or more checks failed."
    exit 1
fi

green "All checks passed."
