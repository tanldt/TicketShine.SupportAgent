#!/usr/bin/env bash
# Find a dotnet with the .NET 10 SDK: prefer ~/.dotnet or %LOCALAPPDATA%\Microsoft\dotnet, then dotnet on PATH.
DOTNET=""
for c in "$HOME/.dotnet/dotnet" "${LOCALAPPDATA:-}/Microsoft/dotnet/dotnet.exe" "$(command -v dotnet || true)"; do
  if [ -n "$c" ] && [ -x "$c" ] && "$c" --list-sdks 2>/dev/null | grep -q '^10\.'; then DOTNET="$c"; break; fi
done
if [ -z "$DOTNET" ]; then
  echo ".NET 10 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0" >&2
  exit 1
fi
export DOTNET DOTNET_ROOT="$(dirname "$DOTNET")" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
echo "Using $DOTNET"
