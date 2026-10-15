#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
npm ci --prefix AquariusWebCompiler
npm run --prefix AquariusWebCompiler prepare:browser
bash native/build.sh
bash scripts/publish-apphost.sh
dotnet build AquariusLang.sln -m:1 --nologo
export AQUARIUS_OPENGL_TESTS=1 AQUARIUS_WGPU_TESTS=1 AQUARIUS_WEBCAM_SMOKE=1
dotnet test AquariusTests -m:1 --no-build --logger 'trx;LogFileName=linux.trx'
export AQUARIUS_COMPILER_DLL="$root/AquariusCli/bin/linux/Debug/net8.0/aqua.dll"
npm test --prefix AquariusWebCompiler
if [[ ${AQUARIUS_TEST_PROJECTS:-0} == 1 ]]; then
    python3 scripts/test-linux-projects.py --executables --examples
fi
