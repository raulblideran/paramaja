#!/usr/bin/env bash
# Produce cargo-sources.json and nuget-sources.json, so a Flathub build can fetch
# its crates and NuGet packages offline.
#
# The manifest builds with `--share=network` by default, which is the honest
# thing for a local build and is not allowed on Flathub. Running this writes the
# list of everything the build downloads; add each file to its module's
# `sources` (cargo-sources.json to paramaja-torrent, nuget-sources.json to
# paramaja), drop both build-args lines, and add `--source
# /run/build/paramaja/nuget-sources` to the dotnet publish command.
set -euo pipefail

cd "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TOOLS="https://raw.githubusercontent.com/flatpak/flatpak-builder-tools/master"
CACHE="${TMPDIR:-/tmp}/paramaja-flatpak-tools"
mkdir -p "$CACHE"
for f in cargo/flatpak-cargo-generator.py dotnet/flatpak-dotnet-generator.py; do
    [[ -f "$CACHE/$(basename "$f")" ]] || curl -fsSL -o "$CACHE/$(basename "$f")" "$TOOLS/$f"
done

python3 -m pip install --user --quiet aiohttp toml tomlkit 2>/dev/null || true
python3 "$CACHE/flatpak-cargo-generator.py" native/paramaja-torrent/Cargo.lock \
    -o packaging/flatpak/cargo-sources.json
python3 "$CACHE/flatpak-dotnet-generator.py" --dotnet 8 --freedesktop 25.08 --runtime linux-x64 \
    packaging/flatpak/nuget-sources.json src/Paramaja.App/Paramaja.App.csproj

echo "Wrote packaging/flatpak/cargo-sources.json and packaging/flatpak/nuget-sources.json"
