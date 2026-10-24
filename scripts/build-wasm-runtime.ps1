# Builds AquariusCore/wasm/runtime/runtime.wasm with the pinned LLVM/Clang toolchain (install-wasm-toolchain.ps1).
# -Check rebuilds into .native-build and fails unless the result is byte-identical to the committed runtime.
# scripts/build-wasm-runtime.sh is the Linux/macOS equivalent and must stay flag-for-flag identical.
param([string]$Output = '', [switch]$Check)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'wasm-toolchain.ps1')
Assert-AquaToolchain
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Output) { $Output = Join-Path $repository 'AquariusCore/wasm/runtime/runtime.wasm' }
if ($Check) { $expectedHash = (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash; $Output = Join-Path $repository '.native-build/runtime-check.wasm' }
New-Item -ItemType Directory -Path (Split-Path $Output) -Force | Out-Null
$runtimeSource = Join-Path $repository 'AquariusCore/wasm/runtime/runtime.c'
$sourceText = Get-Content -LiteralPath $runtimeSource -Raw
$sourceText += Get-Content -LiteralPath (Join-Path (Split-Path $runtimeSource) 'threads.h') -Raw
$names = @([regex]::Matches($sourceText, '\b(?:u32|int)\s+((?:aqua_|rt_)\w+)\s*\(') | ForEach-Object { $_.Groups[1].Value })
$names += @('add','subtract','multiply','divide','less','greater','lessEqual','greaterEqual','equal','notEqual','and','or','modulo') | ForEach-Object { 'rt_' + $_ }
$names = [string[]]($names | Select-Object -Unique); [Array]::Sort($names, [StringComparer]::Ordinal)
$exports = $names | ForEach-Object { '-Wl,--export=' + $_ }
& $AquaToolchain.Clang --target=wasm32-unknown-unknown @AquaWasmCpu -mbulk-memory -O3 -g0 -std=c11 -fno-builtin -nostdlib `
    '-Wl,--no-entry' '-Wl,--export-table' '-Wl,--strip-all' @exports `
    '-Wl,-z,stack-size=1048576' '-Wl,--initial-memory=16777216' '-Wl,--max-memory=536870912' `
    $runtimeSource -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Portable Wasm runtime compilation failed.' }
if ($Check -and (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash -ne $expectedHash) { throw 'The committed runtime.wasm differs from runtime.c. Rebuild it with scripts/build-wasm-runtime.ps1 (pinned Clang) and commit the result.' }
Write-Output ('Built {0}: {1}' -f $Output, (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLowerInvariant())
