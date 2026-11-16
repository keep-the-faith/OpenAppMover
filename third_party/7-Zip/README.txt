7-Zip 26.05, by Igor Pavlov — https://www.7-zip.org
Built by tools/build-7zip.sh from the official source 7z2605-src.tar.xz
(https://github.com/ip7z/7zip/releases/tag/26.05), with MinGW-w64 GCC 14-win32, mingw-w64-x86-64-dev_12.0.0-5 (Debian), Windows x64,
Windows 7 and later (_WIN32_WINNT=0x0601).

Changes to the official source (GNU LGPL, see License.txt):
  - CPP/7zip/Archive/Nsis/NsisIn.h: "#define NSIS_SCRIPT" enabled, so that the script of NSIS
    installers/uninstallers is decompiled to "[NSIS].nsi" (disabled in official builds).
  - Built with DISABLE_RAR=1: no RAR code, hence no unRAR license restriction.

Used by OpenAppMover to read what NSIS uninstallers remove. The source and this script reproduce the binary.

SHA-256:
0b6080b8c3e7a579161d2d495224c0e51c467576cb4c7048b0ab485ef44cc22e  7z2605-src.tar.xz
9248b232a66c4c78bae3bd74b5922e95edaec762ef93da58bfffeae0cda383df  7zz.exe
