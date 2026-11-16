#!/usr/bin/env bash
# Builds, tests and publishes into dist/:
#   - openappmover.exe (CLI) and openappmover-gui.exe (WPF): Windows 10/11 x64, .NET 8 self-contained single files
#   - openappmover-legacy.exe and openappmover-gui-legacy.exe (+ .exe.config): Windows 7 SP1, 8.1 and later x64, .NET Framework 4.8
#     (dependencies merged into the exe by Costura; the .config makes Windows ask for .NET Framework 4.8 when missing)
# The .NET Framework tests run on Windows, or with Mono elsewhere (skipped when it is missing; the CI runs both).
# The .NET 8 SDK is installed locally in .build/dotnet when missing; NuGet packages go to .build/nuget (nuget.config).
# third_party/7-Zip/7zz.exe (embedded) is produced by tools/build-7zip.sh.
set -euo pipefail
cd "$(dirname "$0")"

DOTNET_DIR="$PWD/.build/dotnet"
if [ ! -x "$DOTNET_DIR/dotnet" ]; then
    mkdir -p .build
    curl -sSL https://dot.net/v1/dotnet-install.sh -o .build/dotnet-install.sh
    bash .build/dotnet-install.sh --channel 8.0 --install-dir "$DOTNET_DIR"
fi

export DOTNET_ROOT="$DOTNET_DIR" PATH="$DOTNET_DIR:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# Not configurable in nuget.config.
export NUGET_HTTP_CACHE_PATH="$PWD/.build/nuget-http-cache"

# Authenticode signature, when a certificate is given (tools/make-test-cert.sh creates a test one): 7zz.exe is signed
# before being embedded (a copy: third_party keeps the reproducible build), the executables after publication.
SEVENZIP=()
if [ -n "${SIGN_PFX:-}" ]; then
    mkdir -p .build/signed
    cp third_party/7-Zip/7zz.exe .build/signed/7zz.exe
    bash tools/sign.sh .build/signed/7zz.exe
    SEVENZIP=("-p:SevenZipExe=$PWD/.build/signed/7zz.exe")
fi

dotnet build -c Release "${SEVENZIP[@]}"
dotnet test -c Release --no-build
# .NET Framework 4.8 tests outside Windows: with Mono when it is installed, through the xUnit console runner (fetched
# once into .build; "dotnet test" does not run .NET Framework assemblies there).
if [[ "$OSTYPE" != msys* && "$OSTYPE" != cygwin* ]] && command -v mono > /dev/null; then
    XUNIT=.build/xunit-console/tools/net48/xunit.console.exe
    if [ ! -f "$XUNIT" ]; then
        mkdir -p .build/xunit-console
        curl -sSfL https://api.nuget.org/v3-flatcontainer/xunit.runner.console/2.9.2/xunit.runner.console.2.9.2.nupkg \
            -o .build/xunit-console/runner.nupkg
        python3 -I -c 'import base64, hashlib, sys
digest = base64.b64encode(hashlib.sha512(open(sys.argv[1], "rb").read()).digest()).decode()
sys.exit(digest != "Zdbdw2/HHI/yCy0wlFYodiHSz110kSRNtciUQ/iHptQbSiyoDFVtO8IiBj+5Z8Y6AruDY51fTvYvfgYR8EO/7A==")' \
            .build/xunit-console/runner.nupkg
        python3 -I -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' \
            .build/xunit-console/runner.nupkg .build/xunit-console
    fi
    dotnet build tests/OpenAppMover.Tests -c Release -f net48 -p:TargetFrameworks='"net8.0;net48"' "${SEVENZIP[@]}"
    mono "$XUNIT" tests/OpenAppMover.Tests/bin/Release/net48/OpenAppMover.Tests.dll -noshadow
else
    echo "Mono not found: .NET Framework 4.8 tests skipped (they run in the CI)."
fi
dotnet publish src/OpenAppMover.Cli -c Release -f net8.0 -o dist "${SEVENZIP[@]}"
dotnet publish src/OpenAppMover.Gui -c Release -f net8.0-windows -o dist "${SEVENZIP[@]}"
rm -rf .build/publish-legacy
dotnet publish src/OpenAppMover.Cli -c Release -f net48 -o .build/publish-legacy "${SEVENZIP[@]}"
dotnet publish src/OpenAppMover.Gui -c Release -f net48 -o .build/publish-legacy "${SEVENZIP[@]}"
cp .build/publish-legacy/openappmover-legacy.exe* .build/publish-legacy/openappmover-gui-legacy.exe* dist/
# License of OpenAppMover, and notices of the embedded third-party binaries (7-Zip, GNU LGPL).
cp LICENSE dist/LICENSE.txt
mkdir -p dist/third-party/7-Zip
cp third_party/7-Zip/License.txt third_party/7-Zip/README.txt dist/third-party/7-Zip/

if [ -n "${SIGN_PFX:-}" ]; then
    bash tools/sign.sh dist/openappmover.exe dist/openappmover-gui.exe dist/openappmover-legacy.exe dist/openappmover-gui-legacy.exe
fi

ls -lh dist/openappmover.exe dist/openappmover-gui.exe dist/openappmover-legacy.exe* dist/openappmover-gui-legacy.exe*
