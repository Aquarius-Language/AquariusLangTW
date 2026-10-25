#!/usr/bin/env bash
# Publishes the aqua distribution layout on Linux (see publish-cli.ps1 for the layout):
#   scripts/publish-cli.sh [output-directory]       default dist/aqua-linux-x64
# Runtime packs come from .native-build/install (scripts/build-runtime.ps1, or cmake --install of runtime/).
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
output=${1:-"$root/dist/aqua-linux-x64"}
case "$output" in /*) ;; *) output="$root/$output" ;; esac
rm -rf "$output"
dotnet publish "$root/compiler/AquariusCli/AquariusCli.csproj" -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false --nologo -o "$output"
packs=$(ls -d "$root/.native-build/install"/*/ 2>/dev/null || true)
[ -n "$packs" ] || { echo "No runtime pack is built: run scripts/build-runtime.ps1 first." >&2; exit 1; }
mkdir -p "$output/runtime" "$output/libraries" "$output/scripts"
for pack in $packs; do cp -r "$pack" "$output/runtime/$(basename "$pack")"; done
cp -r "$root/web-glue" "$output/web-glue"
cp -r "$root/runtime/sdk" "$output/sdk"
cp "$root/scripts/toolchain.json" "$root/scripts/toolchain.ps1" "$root/scripts/install-toolchain.ps1" "$root/scripts/install-toolchain.sh" "$output/scripts/"
for library in "$root"/libraries/*/; do
    [ -f "$library/aqua.toml" ] || continue
    destination="$output/libraries/$(basename "$library")"
    mkdir -p "$destination"
    for file in aqua.toml README.md LICENSE; do [ -f "$library/$file" ] && cp "$library/$file" "$destination/"; done
    [ -d "$library/aqua" ] && cp -r "$library/aqua" "$destination/aqua"
done
cp "$root/LICENSE" "$output/"
"$output/aqua" --help > /dev/null
echo "Published $output"
