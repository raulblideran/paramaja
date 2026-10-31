#!/usr/bin/env bash
# Builds packaging/dvd/win64/libdvdcss-2.dll and libdvdread-8.dll: libdvdread with libdvdcss, which
# the DVD ripper's decrypted VIDEO_TS copy calls (Paramaja.Core/Ripping/Dvd/DvdRead.cs). VLC's
# Windows build has both only inside its plugins, so they're built here: the same releases as the
# Flatpak's, cross-compiled with MinGW-w64 (meson from a throwaway virtualenv), libgcc linked in.
# The DLLs are committed, so building the Windows zip doesn't need a C compiler:
#   ./build/build-dvdread-win.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DVDCSS_VERSION=1.6.0
DVDCSS_SHA256=7ea556c846b7bfc32d47b41cae56d1863a6b6d5f706bb162778d6f298490977c
DVDREAD_VERSION=7.1.1
DVDREAD_SHA256=a0d47876548bec806774bbf8dbf20bb19ba139464383156b32eb8e59915b90a9
HOST=x86_64-w64-mingw32
OUT="$ROOT/packaging/dvd/win64"
command -v "$HOST-gcc" >/dev/null || { echo "needs $HOST-gcc (MinGW-w64)" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
python3 -m venv "$WORK/venv"
"$WORK/venv/bin/pip" install --quiet meson==1.9.1 ninja==1.13.0
export PATH="$WORK/venv/bin:$PATH"

cat > "$WORK/cross.ini" <<INI
[binaries]
c = '$HOST-gcc'
ar = '$HOST-ar'
strip = '$HOST-strip'
windres = '$HOST-windres'
pkg-config = 'pkg-config'

[host_machine]
system = 'windows'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'

[built-in options]
c_link_args = ['-static-libgcc']
INI

PREFIX="$WORK/prefix"
export PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig" PKG_CONFIG_PATH=""
build() { # name version sha256 meson-options…
  local name="$1" version="$2" sha="$3"; shift 3
  local tarball="$WORK/$name.tar.xz"
  curl -fsSL --retry 3 -o "$tarball" "https://download.videolan.org/pub/videolan/$name/$version/$name-$version.tar.xz"
  echo "$sha  $tarball" | sha256sum -c --quiet
  tar -C "$WORK" -xf "$tarball"
  meson setup "$WORK/$name-build" "$WORK/$name-$version" --cross-file "$WORK/cross.ini" --prefix "$PREFIX" \
    --buildtype release -Ddefault_library=shared -Denable_docs=false "$@"
  ninja -C "$WORK/$name-build" install
}
build libdvdcss "$DVDCSS_VERSION" "$DVDCSS_SHA256"
build libdvdread "$DVDREAD_VERSION" "$DVDREAD_SHA256" -Dlibdvdcss=enabled

mkdir -p "$OUT"
for dll in libdvdcss-2.dll libdvdread-8.dll; do
  "$HOST-strip" -o "$OUT/$dll" "$PREFIX/bin/$dll"
  # Only Windows' own DLLs (and libdvdread's libdvdcss): nothing from MinGW to ship beside them.
  imports="$("$HOST-objdump" -p "$OUT/$dll" | sed -n 's/.*DLL Name: //p')"
  echo "$dll imports: $(echo $imports)"
  if grep -v -i -E '^(kernel32|advapi32|msvcrt|ucrtbase|api-ms-win-.*|user32|shell32|ws2_32|libdvdcss-2)\.dll$' <<<"$imports"; then
    echo "$dll needs a DLL that isn't Windows' own" >&2; exit 1
  fi
done
exports="$("$HOST-objdump" -p "$OUT/libdvdread-8.dll")"
for f in DVDOpen DVDOpenFile DVDReadBlocks DVDReadBytes DVDFileSize DVDCloseFile DVDClose; do
  grep -q " $f\$" <<<"$exports" || { echo "libdvdread-8.dll doesn't export $f" >&2; exit 1; }
done
ls -la "$OUT"
