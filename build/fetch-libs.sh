#!/usr/bin/env bash
# Stages native libraries into libs/<rid>:
#   win-x64   : libmpv-2.dll from shinchiro/mpv-winbuild-cmake (pinned) + negs_torrent.dll (cross-built)
#   linux-x64 : libnegs_torrent.so (libmpv comes from the system / Homebrew)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-win-x64}"
MPV_TAG="${MPV_TAG:-20261021}"
MPV_ASSET="${MPV_ASSET:-mpv-dev-x86_64-20261021-git-e471496c56.7z}"
export PATH="$HOME/.cargo/bin:$PATH"

OUT="$ROOT/libs/$RID"
mkdir -p "$OUT"
cd "$ROOT/native/negs-torrent"

case "$RID" in
  win-x64)
    cargo build --release --target x86_64-pc-windows-gnu
    cp target/x86_64-pc-windows-gnu/release/negs_torrent.dll "$OUT/"
    if [ ! -f "$OUT/libmpv-2.dll" ]; then
      DL="$(mktemp -d)"
      echo "Downloading libmpv ($MPV_ASSET)…"
      curl -fL --retry 3 -o "$DL/mpv-dev.7z" \
        "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$MPV_TAG/$MPV_ASSET"
      7z e -y -o"$DL/x" "$DL/mpv-dev.7z" libmpv-2.dll >/dev/null
      cp "$DL/x/libmpv-2.dll" "$OUT/"
      rm -rf "$DL"
    fi
    ;;
  linux-x64)
    cargo build --release
    cp target/release/libnegs_torrent.so "$OUT/"
    ;;
  *) echo "unsupported rid $RID" >&2; exit 1 ;;
esac
ls -la "$OUT"
