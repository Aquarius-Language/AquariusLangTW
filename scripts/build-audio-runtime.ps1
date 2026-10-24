# Builds AquariusCore/audio/audio.wasm (miniaudio + WASI libc, no WASI imports) with the pinned LLVM/Clang toolchain.
# -Check rebuilds into .native-build and fails unless the result is byte-identical to the committed binary.
param([switch]$Check)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'wasm-toolchain.ps1')
Assert-AquaToolchain
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$header = Join-Path $repository 'native/vendor/miniaudio/miniaudio.h'
if ((Get-FileHash $header).Hash -ne 'AC7AF4DE748B7E26B777F37E01CEE313A308A7296A3EB080E2906B320CC55C89') { throw 'Unexpected miniaudio 0.11.25 source.' }
$source = Join-Path $repository 'AquariusCore/audio/audio.c'
$output = Join-Path $repository 'AquariusCore/audio/audio.wasm'
if ($Check) { $expected = (Get-FileHash $output).Hash; $output = Join-Path $repository '.native-build/audio-check.wasm' }
New-Item -ItemType Directory -Path (Split-Path $output) -Force | Out-Null
$exports = @([regex]::Matches((Get-Content $source -Raw), '\bint\s+(audio_\w+)\s*\(') | ForEach-Object { '-Wl,--export=' + $_.Groups[1].Value })
& $AquaToolchain.Clang --target=wasm32-wasip1 "--sysroot=$($AquaToolchain.Sysroot)" @AquaWasmCpu -O2 -g0 -std=c11 -DNDEBUG -mexec-model=reactor -ffp-contract=off `
    $source -o $output @exports '-Wl,--strip-all' '-Wl,-z,stack-size=1048576' '-Wl,--initial-memory=16777216' '-Wl,--max-memory=268435456'
if ($LASTEXITCODE) { throw 'Audio Wasm build failed.' }
$imports = node -e "const m=new WebAssembly.Module(require('fs').readFileSync(process.argv[1]));process.stdout.write(String(WebAssembly.Module.imports(m).length))" $output
if ($imports -ne '0') { throw 'audio.wasm must not import anything (it decodes only host-provided memory).' }
if ($Check -and (Get-FileHash $output).Hash -ne $expected) { throw 'The committed audio.wasm differs from audio.c. Rebuild it with scripts/build-audio-runtime.ps1 (pinned Clang) and commit the result.' }
Write-Output "Built audio: $((Get-FileHash $output).Hash.ToLowerInvariant())"
