#!/usr/bin/env bash
# Requires .NET 8 SDK. Builds the Linux installers (and Windows ones too — cross-publishing works).
set -euo pipefail
mkdir -p dist
for rid in linux-x64 linux-arm64; do
  dotnet publish src/Glance.Linux/Glance.Linux.csproj -c Release -r "$rid" -o "out/$rid"
  cp "out/$rid/Glance" "dist/Glance-$rid" && chmod +x "dist/Glance-$rid"
done
for rid in win-x64 win-x86; do
  dotnet publish src/Glance/Glance.csproj -c Release -r "$rid" -p:EnableWindowsTargeting=true -o "out/$rid"
  cp "out/$rid/Glance.exe" "dist/Glance-$rid.exe"
done
(cd dist && sha256sum Glance-* > SHA256SUMS)
echo "Done: dist/"
