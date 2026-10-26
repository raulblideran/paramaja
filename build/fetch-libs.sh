#!/usr/bin/env bash
# Stages native libraries into libs/<rid>:
#   win-x64   : libmpv-2.dll from shinchiro/mpv-winbuild-cmake (pinned) + paramaja_torrent.dll (cross-built)
#   linux-x64 : libparamaja_torrent.so (libmpv comes from the system / Homebrew)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-win-x64}"
MPV_TAG="${MPV_TAG:-20261021}"
MPV_ASSET="${MPV_ASSET:-mpv-dev-x86_64-20261021-git-e471496c56.7z}"
# YouTube and other sites: yt-dlp, plus QuickJS-NG as the JavaScript runtime it needs for
# YouTube. Pinned by version and SHA-256 (yt-dlp's matches its published SHA2-256SUMS).
YTDLP_VERSION=2026.08.19
YTDLP_SHA256=66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a
QJS_VERSION=v0.17.0
QJS_SHA256=2aeabf0092c3262d6b2609824418f7dd7ed1f1df939f73b2b15645230cac0d77
export PATH="$HOME/.cargo/bin:$PATH"

OUT="$ROOT/libs/$RID"

# Downloads a file once and refuses it if the SHA-256 doesn't match.
fetch_checked() {
  local url="$1" sha="$2" dest="$3"
  if [ -f "$dest" ] && echo "$sha  $dest" | sha256sum -c --quiet 2>/dev/null; then return; fi
  echo "Downloading $(basename "$dest")…"
  curl -fL --retry 3 -o "$dest.part" "$url"
  if ! echo "$sha  $dest.part" | sha256sum -c --quiet; then
    rm -f "$dest.part"; echo "checksum mismatch for $url" >&2; exit 1
  fi
  mv "$dest.part" "$dest"
}
mkdir -p "$OUT"
cd "$ROOT/native/paramaja-torrent"

case "$RID" in
  win-x64)
    cargo build --release --target x86_64-pc-windows-gnu
    cp target/x86_64-pc-windows-gnu/release/paramaja_torrent.dll "$OUT/"
    if [ ! -f "$OUT/libmpv-2.dll" ]; then
      DL="$(mktemp -d)"
      echo "Downloading libmpv ($MPV_ASSET)…"
      curl -fL --retry 3 -o "$DL/mpv-dev.7z" \
        "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$MPV_TAG/$MPV_ASSET"
      7z e -y -o"$DL/x" "$DL/mpv-dev.7z" libmpv-2.dll >/dev/null
      cp "$DL/x/libmpv-2.dll" "$OUT/"
      rm -rf "$DL"
    fi
    fetch_checked "https://github.com/yt-dlp/yt-dlp/releases/download/$YTDLP_VERSION/yt-dlp.exe" \
      "$YTDLP_SHA256" "$OUT/yt-dlp.exe"
    fetch_checked "https://github.com/quickjs-ng/quickjs/releases/download/$QJS_VERSION/qjs-windows-x86_64.exe" \
      "$QJS_SHA256" "$OUT/qjs.exe"
    ;;
  linux-x64)
    cargo build --release
    cp target/release/libparamaja_torrent.so "$OUT/"
    ;;
  *) echo "unsupported rid $RID" >&2; exit 1 ;;
esac
ls -la "$OUT"
