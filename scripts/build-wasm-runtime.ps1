param([string]$Compiler = '', [string]$Output = '', [switch]$Check)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Compiler) {
    $installed = Get-Command zig -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $Compiler = if ($installed) { $installed.Source } else { Join-Path $repository '.native-build/toolchains/zig-x86_64-windows-0.15.1/zig.exe' }
}
if (-not $Output) { $Output = Join-Path $repository 'AquariusCore/wasm/runtime/runtime.wasm' }
if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Set -Compiler to a Zig 0.15.1 executable.' }
if ((& $Compiler version) -ne '0.15.1') { throw 'The portable runtime requires pinned Zig 0.15.1.' }
if ($Check) { $expectedHash = (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash; $Output = Join-Path $repository '.native-build/runtime-check.wasm' }
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $repository '.native-build/zig-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $repository '.native-build/zig-local-cache'
$runtimeSource = Join-Path $repository 'AquariusCore/wasm/runtime/runtime.c'
$sourceText = Get-Content -LiteralPath $runtimeSource -Raw
$exports = @([regex]::Matches($sourceText, '\b(?:u32|int)\s+((?:aqua_|rt_)\w+)\s*\(') | ForEach-Object { '-Wl,--export=' + $_.Groups[1].Value })
$exports += @('add','subtract','multiply','divide','less','greater','lessEqual','greaterEqual','equal','notEqual','and','or') | ForEach-Object { '-Wl,--export=rt_' + $_ }
& $Compiler cc --target=wasm32-freestanding -O3 -g0 -std=c11 -fno-builtin -mbulk-memory -nostdlib `
    '-Wl,--no-entry' '-Wl,--export-table' '-Wl,--strip-all' @exports `
    '-Wl,-z,stack-size=1048576' '-Wl,--initial-memory=16777216' '-Wl,--max-memory=536870912' `
    $runtimeSource -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Portable Wasm runtime compilation failed.' }
if ($Check -and (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash -ne $expectedHash) { throw 'The checked-in Wasm runtime differs from its source. Rebuild it with pinned Zig 0.15.1.' }
Write-Output ('Built {0}: {1}' -f $Output, (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLowerInvariant())
