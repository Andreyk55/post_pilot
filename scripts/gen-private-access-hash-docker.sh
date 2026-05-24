#!/usr/bin/env bash
# Generates a BCrypt hash for PrivateAccess__PasswordHash using only Docker.
# Use this on the VPS — no Python/.NET install needed.
#
# Usage:
#   ./scripts/gen-private-access-hash-docker.sh
#   (you'll be prompted for the password — it is not echoed)
#
# Prints:
#   1) The raw hash         — single $, the form BCrypt.Verify expects.
#   2) The escaped hash     — double $$, the form to paste into
#                             /opt/postpilot/server.env so docker compose
#                             does not eat the $-signs during interpolation.

set -euo pipefail

read -rsp "Password: " PP_PWD
echo
export PP_PWD

HASH=$(docker run --rm -e PP_PWD python:3-slim sh -c \
  'pip install -q bcrypt && python -c "import os,bcrypt; print(bcrypt.hashpw(os.environ[\"PP_PWD\"].encode(), bcrypt.gensalt(12)).decode())"')

unset PP_PWD

echo
echo "Raw hash (for reference):"
echo "  $HASH"
echo
echo "Paste this line into /opt/postpilot/server.env:"
echo "  PrivateAccess__PasswordHash=${HASH//\$/\$\$}"
