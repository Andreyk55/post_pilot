#!/usr/bin/env bash
# Generates a BCrypt hash for PrivateAccess__PasswordHash.
#
# Usage:
#   ./scripts/gen-private-access-hash.sh
#   (you'll be prompted for the password — it is not echoed)
#
# Requires Python 3 with the "bcrypt" package:
#   pip install bcrypt

set -euo pipefail

read -rsp "Password: " PP_PWD
echo
export PP_PWD

python3 -c 'import os, bcrypt; print(bcrypt.hashpw(os.environ["PP_PWD"].encode("utf-8"), bcrypt.gensalt(rounds=12)).decode("utf-8"))'

unset PP_PWD
