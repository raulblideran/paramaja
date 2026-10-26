#!/usr/bin/env bash
# Builds the portable Windows release: dist/NegsPlayer-portable-win-x64.zip
# Everything (settings.json, cache/, Screenshots/, portable_config/) lives next to NegsPlayer.exe.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="$HOME/.dotnet:$HOME/.cargo/bin:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

"$ROOT/build/fetch-libs.sh" win-x64

STAGE="$ROOT/dist/NegsPlayer"
rm -rf "$STAGE" && mkdir -p "$STAGE"
dotnet publish "$ROOT/src/NegsPlayer.App" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false \
  -p:DebugType=none -p:GenerateDocumentationFile=false -o "$STAGE"

cp "$ROOT/README.md" "$STAGE/README.txt" 2>/dev/null || true
cd "$ROOT/dist"
rm -f NegsPlayer-portable-win-x64.zip
7z a -tzip -mx=7 NegsPlayer-portable-win-x64.zip NegsPlayer >/dev/null
echo "Built $ROOT/dist/NegsPlayer-portable-win-x64.zip"
ls -la "$STAGE"
