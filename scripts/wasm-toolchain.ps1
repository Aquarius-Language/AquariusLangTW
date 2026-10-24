# Locates the pinned LLVM/Clang toolchain (scripts/wasm-toolchain.json) installed by install-wasm-toolchain.ps1/.sh.
# Dot-source it:  . (Join-Path $PSScriptRoot 'wasm-toolchain.ps1'); Assert-AquaToolchain; & $AquaToolchain.Clang ...
# AQUARIUS_LLVM may point at another installation of the same LLVM release.
$aquaRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$AquaPins = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'wasm-toolchain.json') -Raw | ConvertFrom-Json
$aquaWindows = [IO.Path]::DirectorySeparatorChar -eq '\'
$aquaArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$aquaPlatform = if ($aquaWindows) { if ($aquaArchitecture -eq 'X64') { 'windows-x64' } }
    elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) { if ($aquaArchitecture -eq 'Arm64') { 'macos-arm64' } }
    elseif ($aquaArchitecture -eq 'X64') { 'linux-x64' }
if (-not $aquaPlatform) { throw "The pinned LLVM toolchain is published for Windows x64, Linux x64 and macOS arm64, not this host ($aquaArchitecture)." }
$aquaToolchains = Join-Path $aquaRepository '.native-build/toolchains'
$aquaLlvm = if ($env:AQUARIUS_LLVM) { $env:AQUARIUS_LLVM } else { Join-Path $aquaToolchains $AquaPins.llvm.directory.Replace('{platform}', $aquaPlatform) }
$aquaExe = if ($aquaWindows) { '.exe' } else { '' }
$AquaToolchain = [pscustomobject]@{
    Version = $AquaPins.llvm.version
    Platform = $aquaPlatform
    Toolchains = $aquaToolchains
    Llvm = $aquaLlvm
    Clang = Join-Path $aquaLlvm "bin/clang$aquaExe"
    Sysroot = Join-Path $aquaToolchains $AquaPins.sysroot.directory
    CompilerRt = Join-Path $aquaToolchains $AquaPins.compilerRt.directory
}
# Wasm feature baseline of every built binary: lime1 (bulk-memory-opt, call-indirect-overlong, extended-const, multivalue,
# mutable-globals, nontrapping-fptoint, sign-ext), the default of the Zig 0.15.1 builds these replace. Freestanding builds
# that relied on -mbulk-memory still pass it.
$AquaWasmCpu = @('-mcpu=lime1')
function Assert-AquaToolchain {
    if (-not (Test-Path -LiteralPath $AquaToolchain.Clang)) { throw "Pinned clang not found at $($AquaToolchain.Clang). Run scripts/install-wasm-toolchain.ps1." }
    $version = (& $AquaToolchain.Clang --version | Select-Object -First 1)
    if ($version -notmatch "clang version $([regex]::Escape($AquaToolchain.Version))\b") { throw "Expected clang $($AquaToolchain.Version), found: $version" }
}
