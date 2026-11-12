#!/usr/bin/env bash
# Creates a self-signed code signing certificate for TESTS in .build/signing (never commit it):
#   openappmover-test.pfx + password.txt  used by tools/sign.sh (SIGN_PFX / SIGN_PASSWORD_FILE)
#   openappmover-test.cer                 to install on the test PCs (see README, "Signing")
# Signatures made with it are trusted only on PCs where the .cer is installed.
set -euo pipefail
cd "$(dirname "$0")/.."

DIR=.build/signing
mkdir -p "$DIR"
if [ -f "$DIR/openappmover-test.pfx" ]; then
    echo "$DIR/openappmover-test.pfx already exists (delete $DIR to create a new one)."
    exit 0
fi

openssl rand -base64 24 > "$DIR/password.txt"
openssl req -x509 -newkey rsa:3072 -sha256 -days 1095 -nodes \
    -keyout "$DIR/openappmover-test.key" -out "$DIR/openappmover-test.crt" \
    -subj "/CN=Keep The Faith (test)/O=Keep The Faith" \
    -addext "basicConstraints=critical,CA:FALSE" \
    -addext "keyUsage=critical,digitalSignature" \
    -addext "extendedKeyUsage=codeSigning" 2>/dev/null
openssl pkcs12 -export -inkey "$DIR/openappmover-test.key" -in "$DIR/openappmover-test.crt" \
    -out "$DIR/openappmover-test.pfx" -passout "file:$DIR/password.txt" -name "OpenAppMover test signing"
openssl x509 -in "$DIR/openappmover-test.crt" -outform DER -out "$DIR/openappmover-test.cer"
rm "$DIR/openappmover-test.key"

openssl x509 -in "$DIR/openappmover-test.crt" -noout -subject -enddate -fingerprint -sha1
echo "Sign the build with:"
echo "  SIGN_PFX=$DIR/openappmover-test.pfx SIGN_PASSWORD_FILE=$DIR/password.txt ./build.sh"
