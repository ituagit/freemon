#!/usr/bin/env bash
# Build FreeMon: linux-x64 -> folder (no extraction to $HOME/.net), win-x64 -> single exe.
# Usage: ./build.sh [linux-x64] [win-x64]
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
# .NET SDK: from PATH, or from an env file next to the projects folder (e.g. /appdev/env.sh or ~/devbox/env.sh)
if ! command -v dotnet >/dev/null 2>&1; then
  for envf in "$PWD/../../env.sh" "$PWD/../env.sh" "$HOME/devbox/env.sh"; do
    [ -f "$envf" ] && { . "$envf"; break; }
  done
fi
command -v dotnet >/dev/null 2>&1 || { echo "dotnet not found"; exit 1; }

# no telemetry from the build tools (.NET SDK CLI, Avalonia build services)
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

TARGETS=("$@")
[ ${#TARGETS[@]} -eq 0 ] && TARGETS=(linux-x64 win-x64)

COMMON=(-c Release --self-contained true -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -p:EnableCompressionInSingleFile=true)

for rid in "${TARGETS[@]}"; do
  case "$rid" in
    win-*) EXTRA=(-p:IncludeNativeLibrariesForSelfExtract=true) ;;
    *)     EXTRA=(-p:IncludeNativeLibrariesForSelfExtract=false) ;;
  esac
  echo "=== $rid ==="
  dotnet publish FreeMon.csproj -r "$rid" "${COMMON[@]}" "${EXTRA[@]}" -o "dist/$rid"
done
echo
ls -lh dist/*
