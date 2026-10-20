#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
runtime=linux-x64
# --onnx-cuda publishes the optional NVIDIA CUDA/TensorRT flavour (default output build-targets/linux-x64-cuda).
# Projects select it with [onnx] cuda = true in aqua.toml or 'aqua build ... --onnx-cuda true'.
onnx_cuda=false
if [[ ${1:-} == --onnx-cuda ]]; then onnx_cuda=true; shift; fi
[[ $(uname -s) == Linux && $(uname -m) == x86_64 ]] || { echo 'Publish this runtime on Linux x86_64.' >&2; exit 1; }
for library in graphics audio; do
    [[ -f AquariusDesktop/runtimes/$runtime/native/libaquarius_$library.so ]] || { echo 'Run bash native/build.sh first.' >&2; exit 1; }
done
suffix=; [[ $onnx_cuda == true ]] && suffix=-cuda
output=${1:-build-targets/$runtime$suffix}
[[ ! -L "$output" ]] || { echo 'Runtime pack output must not be a link.' >&2; exit 1; }
mkdir -p -- "$output"
output=$(cd -- "$output" && pwd)
python3 - "$output" <<'PY'
import pathlib, sys
for item in pathlib.Path(sys.argv[1]).iterdir():
    if item.name not in ('host', 'runtime.json') or not item.is_file() or item.is_symlink():
        raise SystemExit('Runtime pack output contains unrelated files. Choose a dedicated directory.')
PY
# Stage next to the destination so replacement and rollback use directory renames.
stage=$(mktemp -d "$(dirname -- "$output")/.aquarius-runtime-stage-XXXXXXXX")
trap 'rm -rf -- "$stage"' EXIT
dotnet_path=$(readlink -f -- "$(command -v dotnet)")
cuda_runtime_directory=
if [[ $onnx_cuda == true ]]; then
    cuda_runtime_directory="$root/build-targets/cuda-runtime/$runtime"
    python3 scripts/prepare-cuda-runtime.py --runtime "$runtime" --output "$cuda_runtime_directory"
fi
dotnet publish AquariusAppHost/AquariusAppHost.csproj -c Release -r "$runtime" --self-contained true -m:1 \
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeAllContentForSelfExtract=true \
    -p:DebugType=None -p:DebugSymbols=false "-p:AquariusDotnetLicenseRoot=$(dirname -- "$dotnet_path")" \
    "-p:AquariusOnnxCuda=$onnx_cuda" "-p:AquariusCudaRuntimeDirectory=$cuda_runtime_directory" -o "$stage/publish"
[[ $(find "$stage/publish" -type f | wc -l) == 1 ]] || { echo 'Application host has unbundled dependencies.' >&2; exit 1; }
"$stage/publish/AquariusAppHost" --runtime-info > "$stage/info.json"
python3 - "$stage" "$runtime" "$onnx_cuda" <<'PY'
import hashlib, json, pathlib, sys
stage, runtime, onnx_cuda = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3] == 'true'
manifest = json.loads((stage / 'info.json').read_text())
if manifest['runtimeIdentifier'] != runtime:
    raise SystemExit('Published runtime architecture does not match the requested runtime.')
if bool(manifest.get('onnxCuda', False)) != onnx_cuda:
    raise SystemExit('Published runtime ONNX CUDA flavour does not match the request. Run dotnet restore and publish again.')
if onnx_cuda and not manifest.get('onnxCudaRuntime', False):
    raise SystemExit('Published CUDA runtime is missing its NVIDIA redistributables.')
with (stage / 'publish/AquariusAppHost').open('rb') as host:
    digest = hashlib.sha256()
    for block in iter(lambda: host.read(1024 * 1024), b''):
        digest.update(block)
manifest['templateSha256'] = digest.hexdigest()
(stage / 'runtime.json').write_text(json.dumps(manifest, indent=2) + '\n')
PY
mkdir -- "$stage/pack"
mv -- "$stage/publish/AquariusAppHost" "$stage/pack/host"
mv -- "$stage/runtime.json" "$stage/pack/runtime.json"
python3 - "$stage" "$output" <<'PY'
import os, pathlib, sys
stage, output = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
os.rename(output, stage / 'previous')
try:
    os.rename(stage / 'pack', output)
except BaseException:
    os.rename(stage / 'previous', output)
    raise
PY
echo "Runtime pack ready: $output"
