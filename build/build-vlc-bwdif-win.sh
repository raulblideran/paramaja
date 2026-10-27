#!/usr/bin/env bash
# Builds packaging/vlc/win64/libdeinterlace_plugin.dll: VLC 3.0.24's deinterlace plugin with
# packaging/vlc/vlc-bwdif.patch (BWDIF), for the Windows build's libVLC. The Flatpak applies the
# same patch to its own VLC build. Cross-compiled with MinGW-w64 as a release build (no asserts,
# as VideoLAN builds it) against the msvcrt.dll runtime VideoLAN's libvlccore.dll uses, and linked
# to that libvlccore.dll (from ./build/fetch-libs.sh).
# The DLL is committed, so building the Windows zip doesn't need a C compiler:
#   ./build/build-vlc-bwdif-win.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VLC_VERSION=3.0.24
VLC_SHA256=e7cab503d1d7d5849b89d2cf0e1ee60d0ef6d012407791b644b9cfc0cc225fdf
CC="${CC:-x86_64-w64-mingw32-gcc}"
CORE="$ROOT/libs/win-x64/libvlc/libvlccore.dll"
OUT="$ROOT/packaging/vlc/win64/libdeinterlace_plugin.dll"
[ -f "$CORE" ] || { echo "run ./build/fetch-libs.sh win-x64 first (it brings libvlccore.dll)" >&2; exit 1; }
command -v "$CC" >/dev/null || { echo "needs $CC (MinGW-w64)" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
TARBALL="$WORK/vlc.tar.xz"
curl -fsSL --retry 3 -o "$TARBALL" "https://download.videolan.org/pub/videolan/vlc/$VLC_VERSION/vlc-$VLC_VERSION.tar.xz"
echo "$VLC_SHA256  $TARBALL" | sha256sum -c --quiet
tar -C "$WORK" -xf "$TARBALL" "vlc-$VLC_VERSION/include" "vlc-$VLC_VERSION/modules/video_filter/deinterlace"
SRC="$WORK/vlc-$VLC_VERSION"
(cd "$SRC" && git apply -p1 "$ROOT/packaging/vlc/vlc-bwdif.patch")

# What VLC's configure finds for a MinGW-w64 build, as far as this plugin needs.
cat > "$WORK/config.h" <<'H'
#define __LIBVLC__ 1
#define PACKAGE "vlc"
#define VERSION "3.0.24"
#define PACKAGE_VERSION "3.0.24"
#define _FILE_OFFSET_BITS 64
#define _UNICODE 1
#define UNICODE 1
#define __USE_MINGW_ANSI_STDIO 1
#define CAN_COMPILE_MMX 1
#define CAN_COMPILE_MMXEXT 1
#define CAN_COMPILE_SSE 1
#define CAN_COMPILE_SSE2 1
#define HAVE_STRUCT_TIMESPEC 1
#define HAVE_MAX_ALIGN_T 1
#define HAVE_ATOF 1
#define HAVE_ATOLL 1
#define HAVE_GETENV 1
#define HAVE_GETPID 1
#define HAVE_GETTIMEOFDAY 1
#define HAVE_LLDIV 1
#define HAVE_NANF 1
#define HAVE_REWIND 1
#define HAVE_SEARCH_H 1
#define HAVE_STATIC_ASSERT 1
#define HAVE_STRDUP 1
#define HAVE_STRNLEN 1
#define HAVE_STRTOF 1
#define HAVE_STRTOLL 1
#define HAVE_SWAB 1
#define HAVE_FSYNC 1
#include <vlc_fixups.h>
H

D="$SRC/modules/video_filter/deinterlace"
mkdir -p "$(dirname "$OUT")"
"$CC" -std=gnu11 -O2 -shared -mcrtdll=msvcrt-os -static-libgcc \
  -DNDEBUG -DHAVE_CONFIG_H -D__PLUGIN__ -DDLL_EXPORT -DMODULE_STRING='"deinterlace"' -DMODULE_NAME=deinterlace \
  -I"$WORK" -I"$SRC/include" -I"$D" \
  "$D/deinterlace.c" "$D/algo_basic.c" "$D/algo_x.c" "$D/algo_yadif.c" "$D/algo_phosphor.c" \
  "$D/algo_ivtc.c" "$D/common.c" "$D/helpers.c" "$D/merge.c" \
  "$CORE" -o "$OUT" -s
# libVLC 3.0's entry points are exported, and it uses the same C runtime as libvlccore.
HEADERS="$(x86_64-w64-mingw32-objdump -p "$OUT")"
grep -q "vlc_entry__3_0_0f" <<<"$HEADERS" || { echo "no VLC 3.0 entry point" >&2; exit 1; }
grep -q "DLL Name: msvcrt.dll" <<<"$HEADERS" || { echo "not linked to msvcrt.dll, as libvlccore is" >&2; exit 1; }
grep "DLL Name" <<<"$HEADERS"
echo "Built $OUT"
