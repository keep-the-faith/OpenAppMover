#!/usr/bin/env bash
# Builds the 7-Zip command line (7zz, standalone with every format) from the official source, with the NSIS script
# decompiler enabled (NSIS_SCRIPT, disabled in official builds) and without RAR code (DISABLE_RAR=1: pure LGPL).
#   - third_party/7-Zip/7zz.exe : Windows x64, embedded in OpenAppMover to read the script of NSIS uninstallers
#   - .build/7zip-build/linux/7zz : build machine version, to check the decompiled scripts during development
# The Windows build uses the MinGW-w64 GCC cross compiler of Debian, extracted into .build (no root needed): it links
# with msvcrt.dll, present on every Windows, so that 7zz.exe runs on Windows 7 (llvm-mingw for an arm64 build machine
# only exists in its ucrt variant, whose Universal C Runtime is missing there by default).
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"

VERSION="${SEVENZIP_VERSION:-26.05}"
TAG="${VERSION//./}"
DEBIAN_SUITE="${DEBIAN_SUITE:-trixie}"
WORK="$ROOT/.build/7zip-build"
TARGET="$ROOT/third_party/7-Zip"
JOBS="$(nproc)"

case "$(uname -m)" in
  x86_64) HOST=x86_64 ;;
  aarch64|arm64) HOST=aarch64 ;;
  *) echo "Unsupported build machine: $(uname -m)" >&2; exit 1 ;;
esac

# --- MinGW-w64 GCC (win32 threads: no winpthread DLL), from the Debian archive of the build machine's architecture.
#     Package files are verified with the SHA-256 of the archive index.
MINGW="$ROOT/.build/mingw-gcc"
CROSS="$MINGW/sysroot/usr/bin/x86_64-w64-mingw32"
if [ ! -x "$CROSS-g++-win32" ]; then
  ARCH="$(dpkg --print-architecture)"
  mkdir -p "$MINGW/debs"
  curl -sSfL "https://deb.debian.org/debian/dists/$DEBIAN_SUITE/main/binary-$ARCH/Packages.xz" | xz -d > "$MINGW/Packages"
  python3 -I - "$MINGW/Packages" g++-mingw-w64-x86-64-win32 gcc-mingw-w64-x86-64-win32 gcc-mingw-w64-x86-64-win32-runtime \
      gcc-mingw-w64-base binutils-mingw-w64-x86-64 mingw-w64-x86-64-dev mingw-w64-common > "$MINGW/packages.txt" <<'PY'
import re, sys
index = {}
for block in open(sys.argv[1]).read().split("\n\n"):
    fields = dict(m.groups() for m in re.finditer(r"^(\S+): (.*)$", block, re.M))
    if "Package" in fields:
        index[fields["Package"]] = fields
for name in sys.argv[2:]:
    print(index[name]["Filename"], index[name]["SHA256"])
PY
  rm -rf "$MINGW/sysroot"
  while read -r file sha256; do
    deb="$MINGW/debs/$(basename "$file")"
    curl -sSfL "https://deb.debian.org/debian/$file" -o "$deb"
    echo "$sha256  $deb" | sha256sum -c --quiet
    # Hard links copied: the file system of .build may not support them.
    python3 -I "$ROOT/tools/extract-deb.py" "$MINGW/sysroot" "$deb"
  done < "$MINGW/packages.txt"
fi
# "gcc-mingw-w64-x86-64-win32 14.2.0-19+27+b1, mingw-w64-x86-64-dev 12.0.0-5"
MINGW_VERSION="$(grep -oE '/(gcc-mingw-w64-x86-64-win32|mingw-w64-x86-64-dev)_[^_]*' "$MINGW/packages.txt" | sed 's|^/||; s|_| |' | paste -sd, | sed 's/,/, /')"

# --- 7-Zip source, NSIS_SCRIPT enabled
SRC_ARCHIVE="7z$TAG-src.tar.xz"
rm -rf "$WORK" && mkdir -p "$WORK/src"
curl -sSfL "https://github.com/ip7z/7zip/releases/download/$VERSION/$SRC_ARCHIVE" -o "$WORK/$SRC_ARCHIVE"
tar -xJf "$WORK/$SRC_ARCHIVE" -C "$WORK/src"
NSIS_H="$WORK/src/CPP/7zip/Archive/Nsis/NsisIn.h"
grep -q '^// #define NSIS_SCRIPT' "$NSIS_H"
sed -i 's|^// #define NSIS_SCRIPT|#define NSIS_SCRIPT|' "$NSIS_H"

BUNDLE="$WORK/src/CPP/7zip/Bundles/Alone2"
# Linux (build machine)
make -C "$BUNDLE" -j"$JOBS" -f makefile.gcc DISABLE_RAR=1 O="$WORK/linux" >"$WORK/linux.log" 2>&1 \
  || { tail -30 "$WORK/linux.log"; exit 1; }
# Windows x64, Windows 7 and later
WINVER="-D_WIN32_WINNT=0x0601 -DWINVER=0x0601"
make -C "$BUNDLE" -j"$JOBS" -f makefile.gcc DISABLE_RAR=1 IS_MINGW=1 \
  CC="$CROSS-gcc-win32 $WINVER" CXX="$CROSS-g++-win32 $WINVER" RC="$CROSS-windres" \
  O="$WORK/windows" >"$WORK/windows.log" 2>&1 \
  || { tail -30 "$WORK/windows.log"; exit 1; }

# Windows 7: no Universal C Runtime nor API set (api-ms-win-*), no DLL of the compiler (libgcc, libstdc++, winpthread).
if "$CROSS-objdump" -p "$WORK/windows/7zz.exe" | grep -iE 'DLL Name: (api-ms-win-|lib)'; then
  echo "7zz.exe imports DLLs missing on Windows 7" >&2; exit 1
fi

rm -rf "$TARGET" && mkdir -p "$TARGET"
cp "$WORK/windows/7zz.exe" "$TARGET/7zz.exe"
cp "$WORK/src/DOC/License.txt" "$TARGET/License.txt"
cat > "$TARGET/README.txt" <<README
7-Zip $VERSION, by Igor Pavlov — https://www.7-zip.org
Built by tools/build-7zip.sh from the official source $SRC_ARCHIVE
(https://github.com/ip7z/7zip/releases/tag/$VERSION), with the MinGW-w64 GCC of Debian ($MINGW_VERSION), Windows x64,
Windows 7 and later (_WIN32_WINNT=0x0601).

Changes to the official source (GNU LGPL, see License.txt):
  - CPP/7zip/Archive/Nsis/NsisIn.h: "#define NSIS_SCRIPT" enabled, so that the script of NSIS
    installers/uninstallers is decompiled to "[NSIS].nsi" (disabled in official builds).
  - Built with DISABLE_RAR=1: no RAR code, hence no unRAR license restriction.

Used by OpenAppMover to read what NSIS uninstallers remove. The source and this script reproduce the binary.

SHA-256:
$(cd "$WORK" && sha256sum "$SRC_ARCHIVE")
$(cd "$TARGET" && sha256sum 7zz.exe)
README
cat "$TARGET/README.txt"
