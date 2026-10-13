param([string]$Zig = (Join-Path $PSScriptRoot '../.native-build/toolchains/zig-x86_64-windows-0.15.1/zig.exe'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exports = @('pose_abi','pose_ptr','pose_reset','pose_begin','pose_detector_input','pose_detect','pose_landmark_input','pose_finish') | ForEach-Object { '-Wl,--export=' + $_ }
& $Zig cc --target=wasm32-freestanding -O3 -g0 -std=c11 -fno-builtin -mbulk-memory -nostdlib '-Wl,--no-entry' @exports '-Wl,--strip-all' '-Wl,-z,stack-size=1048576' '-Wl,--initial-memory=16777216' '-Wl,--max-memory=16777216' (Join-Path $root 'AquariusCore/mediapipe/pose.c') -o (Join-Path $root 'AquariusCore/mediapipe/pose.wasm')
if ($LASTEXITCODE -ne 0) { throw 'Pose Wasm compilation failed.' }
