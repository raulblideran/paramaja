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
# ffmpeg.exe and ffprobe.exe, which yt-dlp needs to merge the separate video and audio
# streams of a download and to embed its cover art. Gyan's "essentials" release build,
# pinned by version and SHA-256.
FFMPEG_VERSION=9.0.2
FFMPEG_SHA256=4705843ccaaf54257c16ad90f3e952ece33c17df964ecf7bfdbb0f49c7171077
# libVLC plays discs with their menus. Taken from VideoLAN's own VLC release for Windows,
# which (unlike the libVLC NuGet package) has the DVD readers; pinned by version and SHA-256.
LIBVLC_VERSION=3.0.24
LIBVLC_SHA256=1ed59c09152e78aff84663fa76efab26057716576db74332858810e5ef54ae1f
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
    if [[ "${OSTYPE:-}" == msys* || "${OSTYPE:-}" == cygwin* ]]; then
      # On Windows (Git Bash, e.g. GitHub's Windows runners): the native MSVC toolchain.
      # .cargo/config.toml links the C runtime statically, so no VC++ redistributable is needed.
      cargo build --release
      cp target/release/paramaja_torrent.dll "$OUT/"
    else
      # On Linux: cross-compiled with MinGW.
      cargo build --release --target x86_64-pc-windows-gnu
      cp target/x86_64-pc-windows-gnu/release/paramaja_torrent.dll "$OUT/"
    fi
    if [ ! -f "$OUT/libmpv-2.dll" ]; then
      DL="$(mktemp -d)"
      echo "Downloading libmpv ($MPV_ASSET)…"
      curl -fL --retry 3 -o "$DL/mpv-dev.7z" \
        "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$MPV_TAG/$MPV_ASSET"
      # Relative paths: Git Bash on Windows doesn't translate a path glued to -o.
      (cd "$DL" && 7z e -y -ox mpv-dev.7z libmpv-2.dll >/dev/null)
      cp "$DL/x/libmpv-2.dll" "$OUT/"
      rm -rf "$DL"
    fi
    fetch_checked "https://github.com/yt-dlp/yt-dlp/releases/download/$YTDLP_VERSION/yt-dlp.exe" \
      "$YTDLP_SHA256" "$OUT/yt-dlp.exe"
    fetch_checked "https://github.com/quickjs-ng/quickjs/releases/download/$QJS_VERSION/qjs-windows-x86_64.exe" \
      "$QJS_SHA256" "$OUT/qjs.exe"
    if [ ! -f "$OUT/libvlc/libvlc.dll" ]; then
      DL="$(mktemp -d)"
      fetch_checked "https://download.videolan.org/pub/videolan/vlc/$LIBVLC_VERSION/win64/vlc-$LIBVLC_VERSION-win64.7z" \
        "$LIBVLC_SHA256" "$DL/vlc.7z"
      (cd "$DL" && 7z x -y -ox vlc.7z "vlc-$LIBVLC_VERSION/libvlc*.dll" "vlc-$LIBVLC_VERSION/plugins/*" >/dev/null)
      VLC="$DL/x/vlc-$LIBVLC_VERSION"
      mkdir -p "$OUT/libvlc/plugins/access"
      cp "$VLC/libvlc.dll" "$VLC/libvlccore.dll" "$OUT/libvlc/"
      # Only what disc playback uses: no streaming output, visualizations, Lua, network
      # discovery, muxers or archives.
      for d in audio_filter audio_mixer audio_output codec d3d11 d3d9 demux misc packetizer spu \
               stream_filter text_renderer video_chroma video_filter video_output; do
        cp -r "$VLC/plugins/$d" "$OUT/libvlc/plugins/"
      done
      for p in libdvdnav libdvdread liblibbluray libfilesystem; do
        cp "$VLC/plugins/access/${p}_plugin.dll" "$OUT/libvlc/plugins/access/"
      done
      # Encoders and formats discs never carry (FFmpeg's decoder covers the rest).
      (cd "$OUT/libvlc/plugins/codec" && rm -f libx264* libx265* libx26410b* libaom* librav1e* libqsv* libzvbi* \
        libvpx* libtheora* libdaala* libschroedinger* libkate* libspeex* libtwolame* libshine* libfluidsynth*)
      rm -rf "$DL"
    fi
    if [ ! -f "$OUT/ffmpeg.exe" ] || [ ! -f "$OUT/ffprobe.exe" ]; then
      DL="$(mktemp -d)"
      fetch_checked "https://github.com/GyanD/codexffmpeg/releases/download/$FFMPEG_VERSION/ffmpeg-$FFMPEG_VERSION-essentials_build.7z" \
        "$FFMPEG_SHA256" "$DL/ffmpeg.7z"
      (cd "$DL" && 7z e -y -ox ffmpeg.7z "ffmpeg-$FFMPEG_VERSION-essentials_build/bin/ffmpeg.exe" \
        "ffmpeg-$FFMPEG_VERSION-essentials_build/bin/ffprobe.exe" >/dev/null)
      cp "$DL/x/ffmpeg.exe" "$DL/x/ffprobe.exe" "$OUT/"
      rm -rf "$DL"
    fi
    ;;
  linux-x64)
    cargo build --release
    cp target/release/libparamaja_torrent.so "$OUT/"
    ;;
  *) echo "unsupported rid $RID" >&2; exit 1 ;;
esac
ls -la "$OUT"
