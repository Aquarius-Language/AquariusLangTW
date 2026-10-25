# Locates the pinned native toolchain (scripts/toolchain.json) installed by install-toolchain.ps1/.sh.
# Dot-source it:  . (Join-Path $PSScriptRoot 'toolchain.ps1'); Assert-AquaToolchain; & $AquaToolchain.Clang ...
# AQUARIUS_LLVM may point at another installation of the same LLVM release; AQUARIUS_NATIVE_BUILD moves .native-build.
$aquaRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$AquaPins = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'toolchain.json') -Raw | ConvertFrom-Json
$aquaWindows = [IO.Path]::DirectorySeparatorChar -eq '\'
$aquaArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$aquaPlatform = if ($aquaWindows) { if ($aquaArchitecture -eq 'X64') { 'windows-x64' } }
    elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) { if ($aquaArchitecture -eq 'Arm64') { 'macos-arm64' } }
    elseif ($aquaArchitecture -eq 'X64') { 'linux-x64' }
if (-not $aquaPlatform) { throw "The pinned LLVM toolchain is published for Windows x64, Linux x64 and macOS arm64, not this host ($aquaArchitecture)." }
$aquaNative = if ($env:AQUARIUS_NATIVE_BUILD) { $env:AQUARIUS_NATIVE_BUILD } else { Join-Path $aquaRepository '.native-build' }
$aquaToolchains = Join-Path $aquaNative 'toolchains'
$aquaLlvm = if ($env:AQUARIUS_LLVM) { $env:AQUARIUS_LLVM } else { Join-Path $aquaToolchains $AquaPins.llvm.directory.Replace('{platform}', $aquaPlatform) }
$aquaExe = if ($aquaWindows) { '.exe' } else { '' }
$AquaToolchain = [pscustomobject]@{
    Version = $AquaPins.llvm.version
    Platform = $aquaPlatform
    Repository = $aquaRepository
    Native = $aquaNative
    Toolchains = $aquaToolchains
    Sdks = Join-Path $aquaNative 'sdk'
    Llvm = $aquaLlvm
    Clang = Join-Path $aquaLlvm "bin/clang$aquaExe"
    Sysroot = Join-Path $aquaToolchains $AquaPins.sysroot.directory
    CompilerRt = Join-Path $aquaToolchains $AquaPins.compilerRt.directory
    MinGW = Join-Path $aquaToolchains $AquaPins.mingw.directory
}
# Wasm feature baseline of every built binary: lime1 (bulk-memory-opt, call-indirect-overlong, extended-const, multivalue,
# mutable-globals, nontrapping-fptoint, sign-ext). Freestanding builds that relied on -mbulk-memory still pass it.
$AquaWasmCpu = @('-mcpu=lime1')
# Every target the runtime and libraries build for. Adding a platform adds a row here, a CMake toolchain file in
# runtime/sdk/cmake/toolchains/, a runtime/platform/<os> layer and a build target in compiler/AquariusBuild/Targets.
$AquaTargets = @('windows-x64', 'linux-x64', 'android-arm64-v8a', 'android-x86_64', 'web')
function Assert-AquaToolchain {
    if (-not (Test-Path -LiteralPath $AquaToolchain.Clang)) { throw "Pinned clang not found at $($AquaToolchain.Clang). Run scripts/install-toolchain.ps1." }
    $version = (& $AquaToolchain.Clang --version | Select-Object -First 1)
    if ($version -notmatch "clang version $([regex]::Escape($AquaToolchain.Version))\b") { throw "Expected clang $($AquaToolchain.Version), found: $version" }
}
# The unpacked SDK directory (include/, lib/) for a pinned SDK and target, e.g. Get-AquaSdk wasmtime windows-x64.
function Get-AquaSdk([string]$Name, [string]$Target) {
    $pin = $AquaPins.sdks.$Name
    return Join-Path $AquaToolchain.Sdks "$Name-$($pin.version)-$Target"
}
