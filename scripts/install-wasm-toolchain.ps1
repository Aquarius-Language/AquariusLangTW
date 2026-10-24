# Installs the pinned LLVM/Clang toolchain (versions and SHA-256 in scripts/wasm-toolchain.json) that builds every
# repository Wasm binary (runtime.wasm, audio.wasm, pose.wasm) and the dynamic-linking example's native libraries:
# the official LLVM release for this host (clang, wasm-ld, lld-link, ld.lld; WebAssembly and x86 backends) plus the
# wasi-sdk WASI sysroot and compiler-rt for WASI libc builds. Archives are verified, then unpacked into the gitignored
# .native-build/toolchains. install-wasm-toolchain.sh does the same on Linux/macOS without PowerShell. Prints clang's path.
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'wasm-toolchain.ps1')
$toolchains = $AquaToolchain.Toolchains
New-Item -ItemType Directory -Path $toolchains -Force | Out-Null

function Install-Archive([string]$Url, [string]$Sha256, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { return }
    $archive = Join-Path $toolchains ([IO.Path]::GetFileName([Uri]::UnescapeDataString($Url)))
    if (-not (Test-Path -LiteralPath $archive)) {
        $progress = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
        try { Invoke-WebRequest -Uri $Url -OutFile $archive -UseBasicParsing } finally { $ProgressPreference = $progress }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Sha256) {
        Remove-Item -LiteralPath $archive -Force
        throw "Checksum mismatch for $Url"
    }
    # Each archive holds one top-level directory; unpack beside it, then give it the pinned name.
    $staging = Join-Path $toolchains ('.unpack-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    try {
        & tar -xf $archive -C $staging
        if ($LASTEXITCODE -ne 0) { throw "Could not unpack $archive" }
        $top = @(Get-ChildItem -LiteralPath $staging)
        if ($top.Count -ne 1 -or -not $top[0].PSIsContainer) { throw "Unexpected layout in $archive" }
        Move-Item -LiteralPath $top[0].FullName -Destination $Destination
    } finally { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $archive -Force
}

$llvm = $AquaPins.llvm.($AquaToolchain.Platform)
if (-not $env:AQUARIUS_LLVM) { Install-Archive ($AquaPins.llvm.base + $llvm.file) $llvm.sha256 $AquaToolchain.Llvm }
Install-Archive $AquaPins.sysroot.url $AquaPins.sysroot.sha256 $AquaToolchain.Sysroot
Install-Archive $AquaPins.compilerRt.url $AquaPins.compilerRt.sha256 $AquaToolchain.CompilerRt
# As wasi-sdk documents, the WebAssembly compiler-rt builtins belong in clang's resource directory.
$resource = (& $AquaToolchain.Clang --target=wasm32-wasip1 -print-resource-dir).Trim()
foreach ($target in Get-ChildItem -LiteralPath $AquaToolchain.CompilerRt -Directory) {
    $destination = Join-Path $resource "lib/$($target.Name)"
    if (-not (Test-Path -LiteralPath $destination)) { Copy-Item -LiteralPath $target.FullName -Destination $destination -Recurse }
}
Assert-AquaToolchain
Write-Output $AquaToolchain.Clang
