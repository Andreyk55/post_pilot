#!/usr/bin/env bash
# PostPilot — stop all containers and bring them back up.
#
# Usage on the VPS:
#   /opt/postpilot/prod/scripts/restart.sh

set -euo pipefail

cd /opt/postpilot

echo "==> docker compose down"
docker compose -f prod/docker-compose.yml down

echo "==> docker compose up -d"
docker compose -f prod/docker-compose.yml up -d

echo "==> docker compose ps"
docker compose -f prod/docker-compose.yml ps
