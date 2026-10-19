#!/usr/bin/env bash
# dotnet restore + build + test for the whole solution.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/dotnet-env.sh"
cd "$ROOT"
"$DOTNET" restore
"$DOTNET" build --no-restore -nologo
"$DOTNET" test --project tests/TicketShine.Support.Tests --no-build
