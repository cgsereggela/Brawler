#!/usr/bin/env bash
# Makes Brawler's self-signed code signing certificate. Run once; keep the output folder private and out of git.
#
#   tools/new-signing-cert.sh [OUT_DIR]      (default: ~/.brawler-signing)
#
# Writes into OUT_DIR:
#   brawler-signing.pfx         the certificate and its private key (PKCS#12), locked with a generated password
#   brawler-signing.password    that password
#   brawler-signing.pfx.b64     the .pfx as one line of base64, for the BRAWLER_SIGNING_PFX GitHub secret
#   brawler-signing.cer         the public certificate only (safe to share; docs/ has a copy)
#
# Then add two repository secrets (Settings > Secrets and variables > Actions):
#   BRAWLER_SIGNING_PFX       the contents of brawler-signing.pfx.b64
#   BRAWLER_SIGNING_PASSWORD  the contents of brawler-signing.password
# and the release workflow signs Brawler.exe with it (see tools/package.ps1 -SignPfx).
#
# A self-signed certificate is not trusted by Windows, so SmartScreen still warns. What it gives players: the exe
# carries a signature, so tampering is detectable, and every release is signed by the same certificate (check the
# thumbprint printed below against docs/INSTALL.md). Needs openssl (Git Bash, Linux and macOS have it).
set -euo pipefail

out="${1:-$HOME/.brawler-signing}"
name=brawler-signing
mkdir -p "$out"
chmod 700 "$out" 2>/dev/null || true
if [ -e "$out/$name.pfx" ]; then
  echo "$out/$name.pfx already exists; delete it first to make a new certificate." >&2
  exit 1
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cat > "$tmp/cert.cnf" <<'EOF'
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = Brawler
O = Brawler (self-signed)
[ext]
basicConstraints = critical, CA:FALSE
keyUsage = critical, digitalSignature
extendedKeyUsage = codeSigning
subjectKeyIdentifier = hash
EOF

openssl rand -base64 30 | tr -d '\n' > "$out/$name.password"
BRAWLER_SIGNING_PASSWORD="$(cat "$out/$name.password")"
export BRAWLER_SIGNING_PASSWORD
openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes -config "$tmp/cert.cnf" \
  -keyout "$tmp/key.pem" -out "$tmp/cert.pem" 2>/dev/null
# AES and SHA-256 inside the .pfx: what current openssl, osslsigncode and Windows all read.
openssl pkcs12 -export -inkey "$tmp/key.pem" -in "$tmp/cert.pem" -name "Brawler" \
  -keypbe AES-256-CBC -certpbe AES-256-CBC -macalg sha256 \
  -passout env:BRAWLER_SIGNING_PASSWORD -out "$out/$name.pfx"
openssl x509 -in "$tmp/cert.pem" -outform der -out "$out/$name.cer"
base64 < "$out/$name.pfx" | tr -d '\n' > "$out/$name.pfx.b64"
chmod 600 "$out/$name".* 2>/dev/null || true

echo "Made $out/$name.pfx"
openssl x509 -in "$tmp/cert.pem" -noout -subject -enddate
echo "SHA-1 thumbprint (what Windows shows): $(openssl x509 -in "$tmp/cert.pem" -noout -fingerprint -sha1 | cut -d= -f2 | tr -d ':')"
echo "Next: add BRAWLER_SIGNING_PFX and BRAWLER_SIGNING_PASSWORD as repository secrets (see the top of this script)."
