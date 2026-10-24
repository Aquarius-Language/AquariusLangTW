# Builds AquariusCore/graphics/geometry.wasm (the Processing geometry kernel shared by every host) with the pinned
# LLVM/Clang toolchain and WASI libc/libm. -Check rebuilds into .native-build and fails unless the result is
# byte-identical to the committed binary.
param([switch]$Check)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'wasm-toolchain.ps1')
Assert-AquaToolchain
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $repository 'AquariusCore/graphics/geometry.c'
$output = Join-Path $repository 'AquariusCore/graphics/geometry.wasm'
if ($Check) { $expected = (Get-FileHash $output).Hash; $output = Join-Path $repository '.native-build/geometry-check.wasm' }
New-Item -ItemType Directory -Path (Split-Path $output) -Force | Out-Null
$names = @([regex]::Matches((Get-Content $source -Raw), 'EXPORT\s+[\w ]+?\*?\s*(geo_\w+)\s*\(') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$exports = $names | ForEach-Object { '-Wl,--export=' + $_ }
& $AquaToolchain.Clang --target=wasm32-wasip1 "--sysroot=$($AquaToolchain.Sysroot)" @AquaWasmCpu -O2 -g0 -std=c11 -DNDEBUG -mexec-model=reactor -ffp-contract=off `
    $source -o $output @exports '-Wl,--strip-all' '-Wl,-z,stack-size=262144' '-Wl,--initial-memory=2097152' '-Wl,--max-memory=268435456'
if ($LASTEXITCODE) { throw 'Geometry Wasm build failed.' }
$imports = node -e "const m=new WebAssembly.Module(require('fs').readFileSync(process.argv[1]));process.stdout.write(String(WebAssembly.Module.imports(m).length))" $output
if ($imports -ne '0') { throw 'geometry.wasm must not import anything (it computes only from host-provided memory).' }
if ($Check -and (Get-FileHash $output).Hash -ne $expected) { throw 'The committed geometry.wasm differs from geometry.c. Rebuild it with scripts/build-geometry-wasm.ps1 (pinned Clang) and commit the result.' }
Write-Output "Built geometry: $((Get-FileHash $output).Hash.ToLowerInvariant())"
