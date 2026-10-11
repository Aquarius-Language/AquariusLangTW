param([string]$Compiler = '', [switch]$Check)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Compiler) { $Compiler = Join-Path $repository '.native-build/toolchains/zig-x86_64-windows-0.15.1/zig.exe' }
if ((& $Compiler version) -ne '0.15.1') { throw 'Audio requires pinned Zig 0.15.1.' }
$header = Join-Path $repository 'native/vendor/miniaudio/miniaudio.h'
if ((Get-FileHash $header).Hash -ne 'AC7AF4DE748B7E26B777F37E01CEE313A308A7296A3EB080E2906B320CC55C89') { throw 'Unexpected miniaudio 0.11.25 source.' }
$source = Join-Path $repository 'AquariusCore/audio/audio.c'
$output = Join-Path $repository 'AquariusCore/audio/audio.wasm'
if ($Check) { $expected = (Get-FileHash $output).Hash; $output = Join-Path $repository '.native-build/audio-check.wasm' }
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $repository '.native-build/zig-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $repository '.native-build/zig-local-cache'
$exports = @([regex]::Matches((Get-Content $source -Raw), '\bint\s+(audio_\w+)\s*\(') | ForEach-Object { '-Wl,--export=' + $_.Groups[1].Value })
& $Compiler cc --target=wasm32-wasi -O2 -g0 -std=c11 -DNDEBUG -mexec-model=reactor -ffp-contract=off $source -o $output @exports '-Wl,--strip-all' '-Wl,-z,stack-size=1048576' '-Wl,--initial-memory=16777216' '-Wl,--max-memory=268435456'
if ($LASTEXITCODE) { throw 'Audio Wasm build failed.' }
if ($Check -and (Get-FileHash $output).Hash -ne $expected) { throw 'Audio Wasm differs from source.' }
Write-Output "Built audio: $((Get-FileHash $output).Hash.ToLowerInvariant())"
