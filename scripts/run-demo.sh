#!/usr/bin/env bash
# Build and run the demo at http://127.0.0.1:5080
#   ./scripts/run-demo.sh            # Template demo (no API key needed)
#   ./scripts/run-demo.sh --live-ai  # Live AI (requires ANTHROPIC_API_KEY or `ant auth login`)
#   ./scripts/run-demo.sh --reset    # delete the demo database before running
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/dotnet-env.sh"

export Demo__DraftMode=Template
for arg in "$@"; do
  case "$arg" in
    --live-ai) export Demo__DraftMode=LiveAi ;;
    --reset) rm -f "$ROOT"/src/TicketShine.Support.Web/App_Data/support.db*; echo "Demo database removed." ;;
  esac
done

"$DOTNET" build "$ROOT/TicketShine.SupportAgent.sln" -nologo -v q
echo "Starting demo (draft mode: $Demo__DraftMode) at http://127.0.0.1:5080 — Ctrl+C to stop."
"$DOTNET" run --project "$ROOT/src/TicketShine.Support.Web" --no-build --launch-profile demo
