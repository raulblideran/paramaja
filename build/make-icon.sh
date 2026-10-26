#!/usr/bin/env bash
# Renders the aubergine SVG into the PNG + multi-size ICO used by the app (needs ImageMagick).
set -euo pipefail
A="$(cd "$(dirname "$0")/.." && pwd)/src/NegsPlayer.App/Assets"
T="$(mktemp -d)"
magick -background none -density 384 "$A/negs.svg" -resize 256x256 "$A/negs-256.png"
for s in 16 24 32 48 64 128; do magick "$A/negs-256.png" -resize ${s}x${s} "$T/$s.png"; done
magick "$T"/{16,24,32,48,64,128}.png "$A/negs-256.png" -define icon:format=png "$A/negs.ico"
rm -rf "$T"
echo "wrote $A/negs.ico and $A/negs-256.png"
