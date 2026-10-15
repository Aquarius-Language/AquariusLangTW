#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
npm ci --prefix AquariusWebCompiler
npm run --prefix AquariusWebCompiler prepare:browser
bash native/build.sh
bash scripts/publish-apphost.sh
output=${1:-dist/aqua-linux-x64}
dotnet publish AquariusCli -c Release -r linux-x64 --self-contained true -m:1 \
    -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$output"
"$output/aqua" --help
