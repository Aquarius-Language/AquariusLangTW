#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
npm ci --prefix AquariusWebCompiler
npm run --prefix AquariusWebCompiler prepare:browser
# --onnx-cuda additionally publishes build-targets/linux-x64-cuda and builds aqua itself with ONNX Runtime's
# CUDA flavour, so 'aqua run' and [onnx] cuda = true projects can use NVIDIA GPUs.
onnx_cuda=false
if [[ ${1:-} == --onnx-cuda ]]; then onnx_cuda=true; shift; fi
bash native/build.sh
[[ $onnx_cuda == true ]] && bash scripts/publish-apphost.sh --onnx-cuda
bash scripts/publish-apphost.sh
output=${1:-dist/aqua-linux-x64}
dotnet publish AquariusCli -c Release -r linux-x64 --self-contained true -m:1 \
    -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:AquariusOnnxCuda=$onnx_cuda" -o "$output"
"$output/aqua" --help
