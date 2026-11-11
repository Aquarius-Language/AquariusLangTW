# Locates the pinned native toolchain (scripts/toolchain.json) installed by install-toolchain.ps1/.sh.
# Dot-source it:  . (Join-Path $PSScriptRoot 'toolchain.ps1'); Assert-AquaToolchain; & $AquaToolchain.Clang ...
# AQUARIUS_LLVM may point at another installation of the same LLVM release; AQUARIUS_NATIVE_BUILD moves .native-build.
$aquaRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$AquaPins = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'toolchain.json') -Raw | ConvertFrom-Json
$aquaWindows = [IO.Path]::DirectorySeparatorChar -eq '\'
# The host's architecture (X64, Arm64, ...): Windows names it in its environment (PROCESSOR_ARCHITEW6432 from a 32-bit
# PowerShell); elsewhere .NET or uname does. RuntimeInformation alone is not enough: some Windows PowerShell 5.1
# sessions resolve it to an assembly without OSArchitecture.
$aquaArchitecture = if ($aquaWindows) { @($env:PROCESSOR_ARCHITEW6432, $env:PROCESSOR_ARCHITECTURE) | Where-Object { $_ } | Select-Object -First 1 }
if (-not $aquaArchitecture) { $aquaArchitecture = [string][Runtime.InteropServices.RuntimeInformation]::OSArchitecture }
if (-not $aquaArchitecture -and -not $aquaWindows) { $aquaArchitecture = (& uname -m) }
$aquaArchitecture = switch -Regex ([string]$aquaArchitecture) { '^(AMD64|X64|x86_64)$' { 'X64' } '^(ARM64|Arm64|aarch64|arm64)$' { 'Arm64' } default { $_ } }
$aquaPlatform = if ($aquaWindows) { if ($aquaArchitecture -eq 'X64') { 'windows-x64' } }
    elseif ((& uname -s) -eq 'Darwin') { if ($aquaArchitecture -eq 'Arm64') { 'macos-arm64' } }
    elseif ($aquaArchitecture -eq 'X64') { 'linux-x64' }
if (-not $aquaPlatform) { throw "The pinned LLVM toolchain is published for Windows x64, Linux x64 and macOS arm64, not this host ($aquaArchitecture)." }
$aquaNative = if ($env:AQUARIUS_NATIVE_BUILD) { $env:AQUARIUS_NATIVE_BUILD } else { Join-Path $aquaRepository '.native-build' }
$aquaToolchains = Join-Path $aquaNative 'toolchains'
$aquaLlvm = if ($env:AQUARIUS_LLVM) { $env:AQUARIUS_LLVM } else { Join-Path $aquaToolchains $AquaPins.llvm.directory.Replace('{platform}', $aquaPlatform) }
$aquaExe = if ($aquaWindows) { '.exe' } else { '' }
# The Android SDK: ANDROID_HOME, else where Android Studio installs it on this host.
$aquaAndroidSdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME }
    elseif ($aquaWindows) { Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
    elseif ($aquaPlatform -like 'macos-*') { Join-Path $HOME 'Library/Android/sdk' }
    else { Join-Path $HOME 'Android/Sdk' }
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
    Jdk = Join-Path $aquaToolchains $AquaPins.jdk.directory.Replace('{platform}', $aquaPlatform)
    CMake = Join-Path $aquaToolchains "$($AquaPins.cmake.directory.Replace('{platform}', $aquaPlatform))/$($AquaPins.cmake.$aquaPlatform.bin)/cmake$aquaExe"
    Ninja = Join-Path $aquaToolchains "$($AquaPins.ninja.directory.Replace('{platform}', $aquaPlatform))/ninja$aquaExe"
    Wabt = Join-Path $aquaToolchains $AquaPins.wabt.directory.Replace('{platform}', $aquaPlatform)
    # Test-only tools (install-toolchain.ps1 -Tools).
    Rust = Join-Path $aquaToolchains $AquaPins.rust.directory.Replace('{platform}', $aquaPlatform)
    Kotlin = Join-Path $aquaToolchains $AquaPins.kotlin.directory
    # Suffixes of this host's programs and of the Android build-tools' wrapper scripts (apksigner.bat on Windows).
    Exe = $aquaExe
    Script = if ($aquaWindows) { '.bat' } else { '' }
    AndroidSdk = $aquaAndroidSdk
    # The PowerShell running this script, for scripts that start another one (powershell.exe or pwsh).
    # pwsh installed as a .NET tool runs as dotnet, which cannot start a script.
    PowerShell = if ((Split-Path -Leaf (Get-Process -Id $PID).Path) -match '^(pwsh|powershell)') { (Get-Process -Id $PID).Path } else { 'pwsh' }
}
# Wasm feature baseline of every built binary: lime1 (bulk-memory-opt, call-indirect-overlong, extended-const, multivalue,
# mutable-globals, nontrapping-fptoint, sign-ext). Freestanding builds that relied on -mbulk-memory still pass it.
$AquaWasmCpu = @('-mcpu=lime1')
# The targets of scripts/toolchain.json (targets.list): name, platform (windows, linux, android, web, ...), kind (desktop,
# mobile, web), the hosts that can build it and planned (reserved, not built yet). docs/adding-a-target.md adds one.
$AquaTargetTable = @($AquaPins.targets.list | Where-Object { -not $_.planned })
# Every target the runtime and libraries build for, and the ones only reserved so far.
$AquaTargets = @($AquaTargetTable | ForEach-Object name)
$AquaPlannedTargets = @($AquaPins.targets.list | Where-Object { $_.planned } | ForEach-Object name)
# The desktop targets: their native binaries build and run only on a host of the same platform.
$AquaDesktopTargets = @($AquaTargetTable | Where-Object kind -eq 'desktop' | ForEach-Object name)
# A target's row of the table; planned and unknown names are errors that say which targets exist.
function Get-AquaTarget([string]$Name) {
    $row = $AquaTargetTable | Where-Object name -eq $Name
    if ($row) { return $row }
    if ($AquaPlannedTargets -contains $Name) { throw "The $Name target is planned but not built yet (docs/adding-a-target.md)." }
    throw "Unknown target $Name; use $($AquaTargets -join ', ')."
}
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
