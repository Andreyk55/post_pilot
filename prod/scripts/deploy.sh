#!/usr/bin/env bash
# PostPilot — pull latest images from GHCR and bring the stack up on the VPS.
#
# Assumes the compose file lives at /opt/postpilot/prod/docker-compose.yml
# and secrets at /opt/postpilot/server.env (see prod/README.md).
#
# Usage on the VPS:
#   /opt/postpilot/prod/scripts/deploy.sh
#
# GitHub Actions calls this via SSH after pushing new images.

set -euo pipefail

cd /opt/postpilot

echo "==> docker compose pull"
docker compose -f prod/docker-compose.yml pull

echo "==> docker compose up -d"
docker compose -f prod/docker-compose.yml up -d

echo "==> docker compose ps"
docker compose -f prod/docker-compose.yml ps
