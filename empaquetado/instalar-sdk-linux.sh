#!/usr/bin/env bash
# Instala el SDK de .NET oficial en /opt/dotnet, comprobado con su sha512, para el workflow de Releases (contenedor Debian 12).
# Lee SDK_VERSION y SDK_SHA512 (el hash de dotnet-sdk-<versión>-linux-x64.tar.gz que publica releases.json del canal 10.0,
# https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json). Con GITHUB_PATH y GITHUB_ENV definidos,
# deja dotnet en el PATH de los pasos siguientes.
set -euo pipefail
: "${SDK_VERSION:?falta SDK_VERSION}" "${SDK_SHA512:?falta SDK_SHA512}"
destino="${DOTNET_DESTINO:-/opt/dotnet}"
fichero="dotnet-sdk-$SDK_VERSION-linux-x64.tar.gz"
tmp="$(mktemp -d)"
curl -fsSL --retry 3 -o "$tmp/$fichero" "https://builds.dotnet.microsoft.com/dotnet/Sdk/$SDK_VERSION/$fichero"
echo "$SDK_SHA512  $tmp/$fichero" | sha512sum -c -
mkdir -p "$destino"
tar -xzf "$tmp/$fichero" -C "$destino"
rm -rf -- "$tmp"
"$destino/dotnet" --version
if [ -n "${GITHUB_PATH:-}" ]; then echo "$destino" >> "$GITHUB_PATH"; fi
if [ -n "${GITHUB_ENV:-}" ]; then echo "DOTNET_ROOT=$destino" >> "$GITHUB_ENV"; fi
