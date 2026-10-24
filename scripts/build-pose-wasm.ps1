# Builds AquariusCore/mediapipe/pose.wasm with the pinned LLVM/Clang toolchain (install-wasm-toolchain.ps1).
# -Check rebuilds into .native-build and fails unless the result is byte-identical to the committed binary.
param([switch]$Check)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'wasm-toolchain.ps1')
Assert-AquaToolchain
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $root 'AquariusCore/mediapipe/pose.wasm'
if ($Check) { $expected = (Get-FileHash $output).Hash; $output = Join-Path $root '.native-build/pose-check.wasm' }
New-Item -ItemType Directory -Path (Split-Path $output) -Force | Out-Null
$exports = @('pose_abi','pose_ptr','pose_reset','pose_begin','pose_detector_input','pose_detect','pose_landmark_input','pose_finish') | ForEach-Object { '-Wl,--export=' + $_ }
& $AquaToolchain.Clang --target=wasm32-unknown-unknown @AquaWasmCpu -mbulk-memory -O3 -g0 -std=c11 -fno-builtin -nostdlib '-Wl,--no-entry' @exports '-Wl,--strip-all' `
    '-Wl,-z,stack-size=1048576' '-Wl,--initial-memory=16777216' '-Wl,--max-memory=16777216' (Join-Path $root 'AquariusCore/mediapipe/pose.c') -o $output
if ($LASTEXITCODE -ne 0) { throw 'Pose Wasm compilation failed.' }
if ($Check -and (Get-FileHash $output).Hash -ne $expected) { throw 'The committed pose.wasm differs from pose.c. Rebuild it with scripts/build-pose-wasm.ps1 (pinned Clang) and commit the result.' }
Write-Output "Built pose: $((Get-FileHash $output).Hash.ToLowerInvariant())"
