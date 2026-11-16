#!/usr/bin/env bash
# Signs Windows executables (Authenticode) with osslsigncode: SHA-1 signature, then a nested SHA-256 one ("dual
# signing"), since Windows 7 without its SHA-2 code signing update (KB4474419) only checks the SHA-1 one.
#   SIGN_PFX            certificate and private key (.pfx)
#   SIGN_PASSWORD_FILE  file holding the .pfx password (or SIGN_PASSWORD)
#   SIGN_TIMESTAMP_URL  RFC 3161 timestamp server (default: DigiCert); empty to skip the timestamp
#   SIGN_SHA1           0: SHA-256 signature only (no Windows 7 without KB4474419)
# On Windows, use signtool instead (see README, "Signing").
# osslsigncode is used from the PATH, or fetched once from the Debian archive into .build/osslsigncode.
set -euo pipefail
cd "$(dirname "$0")/.."

: "${SIGN_PFX:?SIGN_PFX is not set}"
TIMESTAMP_URL="${SIGN_TIMESTAMP_URL-http://timestamp.digicert.com}"

OSSLSIGNCODE=$(command -v osslsigncode || true)
if [ -z "$OSSLSIGNCODE" ]; then
    LOCAL=.build/osslsigncode/root/usr/bin/osslsigncode
    if [ ! -x "$LOCAL" ]; then
        ARCH=$(dpkg --print-architecture)
        DEB="osslsigncode_2.9-2_${ARCH}.deb"
        mkdir -p .build/osslsigncode
        curl -sSfL "https://deb.debian.org/debian/pool/main/o/osslsigncode/$DEB" -o ".build/osslsigncode/$DEB"
        dpkg -x ".build/osslsigncode/$DEB" .build/osslsigncode/root
    fi
    OSSLSIGNCODE=$LOCAL
fi

PASSWORD_ARGS=()
if [ -n "${SIGN_PASSWORD_FILE:-}" ]; then
    PASSWORD_ARGS=(-readpass "$SIGN_PASSWORD_FILE")
elif [ -n "${SIGN_PASSWORD:-}" ]; then
    PASSWORD_ARGS=(-pass "$SIGN_PASSWORD")
fi
TIMESTAMP_ARGS=()
[ -n "$TIMESTAMP_URL" ] && TIMESTAMP_ARGS=(-ts "$TIMESTAMP_URL")

# sign <file> <digest> [-nest]
sign() {
    "$OSSLSIGNCODE" sign -pkcs12 "$SIGN_PFX" "${PASSWORD_ARGS[@]}" -h "$2" "${@:3}" -n "OpenAppMover" \
        "${TIMESTAMP_ARGS[@]}" -in "$1" -out "$1.signed" >/dev/null
    chmod --reference="$1" "$1.signed"
    mv "$1.signed" "$1"
}

for file in "$@"; do
    if [ "${SIGN_SHA1:-1}" = 0 ]; then
        sign "$file" sha256
        echo "Signed $file (SHA-256)"
    else
        sign "$file" sha1
        sign "$file" sha256 -nest
        echo "Signed $file (SHA-1 + SHA-256)"
    fi
done
