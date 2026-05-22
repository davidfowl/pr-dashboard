#!/usr/bin/env bash
set -euo pipefail

dotnet build pr-timeline-app.slnx
dotnet test pr-timeline-app.slnx --no-build
npm --prefix frontend ci
npm --prefix frontend run build -- --mode development
