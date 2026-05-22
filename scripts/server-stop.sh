#!/usr/bin/env bash
# Post Pilot — stop the VPS stack.
#
# Stops and removes containers but keeps the postgres_data and minio_data
# volumes so DB rows and uploaded media survive a restart.
#
# Usage from the repo root:
#   ./scripts/server-stop.sh

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEPLOY_DIR="$REPO_ROOT/deploy"

cd "$DEPLOY_DIR"
docker compose \
    --env-file ./env/server.env \
    -f docker-compose.yml \
    -f docker-compose.server.db.yml \
    -f docker-compose.server.storage.yml \
    -f docker-compose.server.proxy.yml \
    down

echo "Stopped. Volumes preserved (postgres_data, minio_data)."
