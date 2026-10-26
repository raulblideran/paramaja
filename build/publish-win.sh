#!/usr/bin/env bash
# Builds the portable Windows release: dist/Paramaja-portable-win-x64.zip
# Everything (settings.json, cache/, Screenshots/, portable_config/) lives next to Paramaja.exe.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="$HOME/.dotnet:$HOME/.cargo/bin:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

"$ROOT/build/fetch-libs.sh" win-x64

VERSION=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$ROOT/src/Paramaja.App/Paramaja.App.csproj")
# The commit goes into the exe's informational version and the in-app About, so a friend
# can say exactly which build they have. "-dirty" marks a build with uncommitted changes.
COMMIT=$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)
if [ -n "$(git -C "$ROOT" status --porcelain 2>/dev/null)" ]; then COMMIT="$COMMIT-dirty"; fi
ZIP="Paramaja-$VERSION-portable-win-x64.zip"

# The folder inside the zip stays "Paramaja", so unzipping a new version over an old one
# keeps settings.json and the Screenshots folder.
STAGE="$ROOT/dist/Paramaja"
rm -rf "$STAGE" && mkdir -p "$STAGE"
dotnet publish "$ROOT/src/Paramaja.App" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false \
  -p:DebugType=none -p:GenerateDocumentationFile=false -p:SourceRevisionId="$COMMIT" -o "$STAGE"

cp "$ROOT/README.md" "$STAGE/README.txt" 2>/dev/null || true
cp "$ROOT/CHANGELOG.md" "$STAGE/CHANGELOG.txt"
cd "$ROOT/dist"
rm -f "$ZIP"
7z a -tzip -mx=7 "$ZIP" Paramaja >/dev/null
echo "Built $ROOT/dist/$ZIP (version $VERSION, commit $COMMIT)"
ls -la "$STAGE"
