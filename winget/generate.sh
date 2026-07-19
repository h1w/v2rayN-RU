#!/usr/bin/env bash
# Generate winget-pkgs manifests for h1w.v2rayN-RU from a published GitHub release.
#
# Usage:   ./winget/generate.sh <version>          e.g. ./winget/generate.sh 1.2.2
#
# Downloads the Windows portable zips from the h1w/v2rayN-RU release <version>,
# computes their SHA256, and writes the 3 manifest files to:
#     winget/out/manifests/h/h1w/v2rayN-RU/<version>/
# ready to submit to https://github.com/microsoft/winget-pkgs
# (via `wingetcreate submit <that dir>` or a manual PR — see winget/README.md).
#
# x64 is required; arm64 is included only if that asset exists in the release.
set -euo pipefail

VERSION="${1:?usage: winget/generate.sh <version>  (e.g. 1.2.2)}"
REPO="h1w/v2rayN-RU"
PKG="h1w.v2rayN-RU"
SCHEMA="1.9.0"
BASE="https://github.com/${REPO}/releases/download/${VERSION}"

here="$(cd "$(dirname "$0")" && pwd)"
outdir="${here}/out/manifests/h/h1w/v2rayN-RU/${VERSION}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$outdir"

# sha256_of <asset-filename>  -> prints UPPERCASE sha256, or nothing if the asset 404s
sha256_of() {
  local asset="$1" url="${BASE}/$1"
  if curl -fsSL -o "${tmp}/${asset}" "$url"; then
    sha256sum "${tmp}/${asset}" | awk '{print toupper($1)}'
  fi
}

echo "Fetching x64 installer for hashing..."
X64_SHA="$(sha256_of "v2rayN-RU-windows-64.zip")"
[ -n "$X64_SHA" ] || { echo "ERROR: ${BASE}/v2rayN-RU-windows-64.zip not found. Is the release published (non-prerelease) with the new asset names?"; exit 1; }

echo "Checking arm64 installer..."
ARM64_SHA="$(sha256_of "v2rayN-RU-windows-arm64.zip" || true)"

# ---- installer manifest ---------------------------------------------------
{
  echo "# yaml-language-server: \$schema=https://aka.ms/winget-manifest.installer.${SCHEMA}.schema.json"
  echo "PackageIdentifier: ${PKG}"
  echo "PackageVersion: ${VERSION}"
  echo "InstallerType: zip"
  echo "NestedInstallerType: portable"
  echo "NestedInstallerFiles:"
  echo "- RelativeFilePath: v2rayN-RU-windows-64\\v2rayN-RU.exe"
  echo "RequireExplicitUpgrade: true"
  echo "Installers:"
  echo "- Architecture: x64"
  echo "  InstallerUrl: ${BASE}/v2rayN-RU-windows-64.zip"
  echo "  InstallerSha256: ${X64_SHA}"
  echo "  NestedInstallerFiles:"
  echo "  - RelativeFilePath: v2rayN-RU-windows-64\\v2rayN-RU.exe"
  if [ -n "$ARM64_SHA" ]; then
    echo "- Architecture: arm64"
    echo "  InstallerUrl: ${BASE}/v2rayN-RU-windows-arm64.zip"
    echo "  InstallerSha256: ${ARM64_SHA}"
    echo "  NestedInstallerFiles:"
    echo "  - RelativeFilePath: v2rayN-RU-windows-arm64\\v2rayN-RU.exe"
  fi
  echo "ManifestType: installer"
  echo "ManifestVersion: ${SCHEMA}"
} > "${outdir}/${PKG}.installer.yaml"

# ---- default locale manifest ----------------------------------------------
cat > "${outdir}/${PKG}.locale.en-US.yaml" <<EOF
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.defaultLocale.${SCHEMA}.schema.json
PackageIdentifier: ${PKG}
PackageVersion: ${VERSION}
PackageLocale: en-US
Publisher: h1w
PublisherUrl: https://github.com/h1w
PublisherSupportUrl: https://github.com/h1w/v2rayN-RU/issues
PackageName: v2rayN-RU
PackageUrl: https://github.com/h1w/v2rayN-RU
License: GPL-3.0
LicenseUrl: https://github.com/h1w/v2rayN-RU/blob/master/LICENSE
Copyright: Copyright (c) 2dust, h1w
ShortDescription: A GUI proxy client for Windows (fork of v2rayN) with client HWID support and full custom .json core configuration support. Supports Xray and sing-box.
Description: |-
  v2rayN-RU is a fork of 2dust/v2rayN. It adds Happ-compatible client HWID
  headers when updating subscriptions (compatible with Remnawave Panel
  v2.9.0+) and full import/run of custom .json core configurations as-is.
  A GUI client supporting the Xray and sing-box cores.
Moniker: v2rayn-ru
Tags:
- proxy
- vpn
- xray
- sing-box
- v2ray
- vmess
- vless
- trojan
- shadowsocks
ManifestType: defaultLocale
ManifestVersion: ${SCHEMA}
EOF

# ---- version manifest -----------------------------------------------------
cat > "${outdir}/${PKG}.yaml" <<EOF
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.version.${SCHEMA}.schema.json
PackageIdentifier: ${PKG}
PackageVersion: ${VERSION}
DefaultLocale: en-US
ManifestType: version
ManifestVersion: ${SCHEMA}
EOF

echo ""
echo "Wrote manifests to: ${outdir}"
ls -1 "${outdir}"
[ -n "$ARM64_SHA" ] && echo "Included: x64 + arm64" || echo "Included: x64 only (arm64 asset absent in release)"
echo ""
echo "Next: validate & submit (see winget/README.md), e.g.:"
echo "  winget validate --manifest ${outdir}"
echo "  wingetcreate submit --token <PAT> ${outdir}"
