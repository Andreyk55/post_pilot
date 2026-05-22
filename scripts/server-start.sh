#!/usr/bin/env bash
# Post Pilot — bring up the full stack on a VPS.
#
# Containers:
#   nginx     — reverse proxy on :80 (routes / → api, /storage/ → minio)
#   api       — ASP.NET Core, runs EF migrations on startup
#   publisher — background worker
#   postgres  — local database (volume: postgres_data)
#   minio     — S3-compatible storage (volume: minio_data)
#   minio-init — one-shot bucket creation
#
# Prerequisites on the VPS:
#   - Docker Engine + the docker compose plugin installed
#   - deploy/env/server.env filled in (no REPLACE_ME left)
#   - Inbound port 80 open in the firewall
#
# Usage from the repo root:
#   ./scripts/server-start.sh

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEPLOY_DIR="$REPO_ROOT/deploy"
ENV_FILE="$DEPLOY_DIR/env/server.env"

cyan()  { printf '\033[36m==> %s\033[0m\n' "$*"; }
green() { printf '\033[32m    %s\033[0m\n' "$*"; }
warn()  { printf '\033[33m    %s\033[0m\n' "$*"; }
fail()  { printf '\033[31m!! %s\033[0m\n' "$*" >&2; exit 1; }

[[ -f "$ENV_FILE" ]] || fail "Env file missing: $ENV_FILE"

# Block the obvious "I forgot to fill in the env file" footgun.
if grep -E '^[^#]*REPLACE_ME' "$ENV_FILE" >/dev/null; then
    warn "deploy/env/server.env still contains REPLACE_ME values:"
    grep -nE '^[^#]*REPLACE_ME' "$ENV_FILE" | sed 's/^/      /'
    fail "Fill those in before deploying."
fi

if grep -E '^[^#]*VPS_PUBLIC_HOST' "$ENV_FILE" >/dev/null; then
    fail "Replace VPS_PUBLIC_HOST in $ENV_FILE with your VPS IP or domain."
fi

cyan "Checking Docker"
docker version --format '{{.Server.Version}}' >/dev/null || fail "Docker daemon not reachable."
green "Docker reachable"

cyan "Building images (api, publisher)"
cd "$DEPLOY_DIR"
docker compose \
    --env-file ./env/server.env \
    -f docker-compose.yml \
    -f docker-compose.server.db.yml \
    -f docker-compose.server.storage.yml \
    -f docker-compose.server.proxy.yml \
    build

cyan "Starting full stack"
docker compose \
    --env-file ./env/server.env \
    -f docker-compose.yml \
    -f docker-compose.server.db.yml \
    -f docker-compose.server.storage.yml \
    -f docker-compose.server.proxy.yml \
    up -d

cyan "Waiting for API"
deadline=$(( $(date +%s) + 120 ))
ready=false
while [[ $(date +%s) -lt $deadline ]]; do
    if curl -fsS -o /dev/null http://localhost/api/media/constraints 2>/dev/null; then
        ready=true
        break
    fi
    sleep 2
done

if [[ "$ready" != true ]]; then
    warn "API did not become ready in 120s. Recent api logs:"
    docker compose -f docker-compose.yml -f docker-compose.server.db.yml -f docker-compose.server.storage.yml -f docker-compose.server.proxy.yml logs --tail 80 api || true
    fail "API did not start. Check logs above."
fi
green "API ready"

echo
printf '\033[32m════════════════════════════════════════════════════════════════\033[0m\n'
printf '\033[32m  Post Pilot is up\033[0m\n'
printf '\033[32m════════════════════════════════════════════════════════════════\033[0m\n'
public_host=$(grep -E '^App__PublicUrl=' "$ENV_FILE" | head -n1 | cut -d= -f2-)
echo  "  API              : $public_host"
echo  "  Swagger          : $public_host/swagger    (only if ASPNETCORE_ENVIRONMENT=Development)"
echo  "  MinIO console    : $public_host/storage-console/"
echo  "  Storage endpoint : $public_host/storage/"
echo
echo  "  Stop with:  ./scripts/server-stop.sh"
echo  "  Logs:       docker compose -f deploy/docker-compose.yml \\"
echo  "                            -f deploy/docker-compose.server.db.yml \\"
echo  "                            -f deploy/docker-compose.server.storage.yml \\"
echo  "                            -f deploy/docker-compose.server.proxy.yml logs -f api publisher"
echo
