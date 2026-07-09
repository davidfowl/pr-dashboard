#!/usr/bin/env bash
#
# Preview the deployed OAuth experience locally.
#
# Builds the SPA into the server's wwwroot and runs the AppHost in production-parity mode:
# the server runs as Production (the local `gh`/token fallback is OFF), OAuth is the only way
# in, and the built frontend is served single-origin from the server at http://localhost:7080 —
# exactly what an end user sees once deployed.
#
# Prerequisites (one-time):
#   1. Register a GitHub OAuth App (https://github.com/settings/developers):
#        Homepage URL:              http://localhost:7080
#        Authorization callback URL: http://localhost:7080/signin-github
#   2. Store its credentials in the AppHost user-secrets store:
#        dotnet user-secrets --id D90E46CD-0B9F-44AF-A419-43107D23677B set "Parameters:github-client-id" "<client id>"
#        dotnet user-secrets --id D90E46CD-0B9F-44AF-A419-43107D23677B set "Parameters:github-client-secret" "<client secret>"
#
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

wwwroot="pr-timeline-app.Server/wwwroot"

echo "Building the frontend into ${wwwroot} ..."
if [ ! -d frontend/node_modules ]; then
  (cd frontend && npm ci)
fi
(cd frontend && npm run build -- --outDir "../${wwwroot}" --emptyOutDir)

echo "Starting the AppHost in production-parity mode (http://localhost:7080) ..."
ProdParity=true exec aspire run
