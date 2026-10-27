# Builds runtime packs: configures, builds and installs runtime/ (the host core, the target's platform layer and the
# default libraries under libraries/) for each target into .native-build/install/<target>, where the compiler of this
# checkout finds them (compiler/AquariusCompiler/AquariusInstallation.cs).
#   scripts/build-runtime.ps1 -Targets windows-x64,web,android-x86_64,android-arm64-v8a,linux-x64
#   scripts/build-runtime.ps1 -Targets windows-x64 -Libraries graphics,application   # a subset of the default libraries
#   scripts/build-runtime.ps1 -Targets windows-x64 -OnnxCuda                          # .native-build/install/windows-x64-cuda
# It first runs scripts/install-toolchain.ps1 for the targets, which downloads (SHA-256 checked) any pinned toolchain
# piece or SDK missing from .native-build, so a partial .native-build never breaks a build. Android needs ANDROID_HOME
# with the pinned NDK, platform and build-tools; linux-x64 builds in WSL on Windows (Ubuntu-22.04 with the Linux LLVM
# release in $HOME/aquarius-toolchains, see install-toolchain.sh). CMake and Ninja are the pinned ones unless
# -CMake/-Ninja name others.
# Library downloads verify TLS: -CaBundle (default: Git for Windows' ca-bundle.crt when present) gives CMake builds
# without a CA store one. Prebuilt third-party DLLs some libraries install (ONNX Runtime) need the Visual C++ 2015-2022
# runtime: windows-x64 packs that contain DLLs get its redistributable DLLs from -VCRuntimeDirectory or the installed
# Visual Studio's VC/Redist (otherwise target machines need the VC++ redistributable installed).
param(
    [string[]]$Targets = @('windows-x64'),
    [string[]]$Libraries = @(),
    [switch]$OnnxCuda,
    [ValidateSet('Release', 'Debug', 'RelWithDebInfo')][string]$Configuration = 'Release',
    [string]$CMake = '',
    [string]$Ninja = '',
    [string]$Distribution = 'Ubuntu-22.04',
    [string]$CaBundle = '',
    [string]$VCRuntimeDirectory = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'toolchain.ps1')

# Comma-separated values arrive as one string from cmd.exe and other shells.
$Targets = @($Targets | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Libraries = @($Libraries | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
foreach ($target in $Targets) {
    if ($AquaTargets -notcontains $target) { throw "Unknown target $target; use $($AquaTargets -join ', ')." }
}
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed (exit $LASTEXITCODE): $Program $($Arguments -join ' ')" }
}
& (Join-Path $PSScriptRoot 'install-toolchain.ps1') -Targets $Targets | Out-Null
$cmakePath = if ($CMake) { $CMake } else { $AquaToolchain.CMake }
$ninjaPath = if ($Ninja) { $Ninja } else { $AquaToolchain.Ninja }
$repository = $AquaToolchain.Repository
$runtime = Join-Path $repository 'runtime'
$libraryList = $Libraries -join ';'
$flavour = ''
if ($OnnxCuda) { $flavour = '-cuda' }
if (-not $CaBundle -and $aquaWindows) {
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    # git.exe sits in <Git>/cmd, <Git>/bin or <Git>/mingw64/bin; the bundle is <Git>/mingw64/etc/ssl/certs/ca-bundle.crt.
    $directory = if ($git) { Split-Path $git.Source } else { '' }
    for ($level = 0; $directory -and $level -lt 3 -and -not $CaBundle; $level++) {
        foreach ($relative in @('mingw64/etc/ssl/certs/ca-bundle.crt', 'etc/ssl/certs/ca-bundle.crt')) {
            $candidate = Join-Path $directory $relative
            if (-not $CaBundle -and (Test-Path -LiteralPath $candidate)) { $CaBundle = $candidate }
        }
        $directory = Split-Path $directory
    }
}
# The Visual C++ runtime DLLs of the installed Visual Studio (redistributable copies only).
function Find-VCRuntime {
    if ($VCRuntimeDirectory) { return $VCRuntimeDirectory }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { return '' }
    $studio = & $vswhere -latest -products '*' -property installationPath
    if (-not $studio) { return '' }
    $crt = Get-ChildItem -Path (Join-Path $studio 'VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT') -Directory -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($crt) { return $crt.FullName }
    return ''
}

foreach ($target in $Targets) {
    $pack = Join-Path $AquaToolchain.Native "install/$target$flavour"
    Write-Host "== $target$flavour -> $pack"
    if ($target -eq 'linux-x64' -and $aquaWindows) {
        # Linux builds run in WSL with the Linux release of the pinned LLVM; the pack is installed back into this checkout.
        $wslRepository = '/mnt/' + $repository.Substring(0, 1).ToLowerInvariant() + $repository.Substring(2).Replace('\', '/')
        $wslPack = '/mnt/' + $pack.Substring(0, 1).ToLowerInvariant() + $pack.Substring(2).Replace('\', '/')
        $options = "-DCMAKE_BUILD_TYPE=$Configuration"
        if ($libraryList) { $options += " -DAQUA_LIBRARIES='$libraryList'" }
        if ($OnnxCuda) { $options += ' -DAQUA_ONNX_CUDA=ON' }
        $script = @"
set -e
export AQUARIUS_LLVM=`${AQUARIUS_LLVM:-`$HOME/aquarius-toolchains/llvm-$($AquaPins.llvm.version)-linux-x64}
build=`$HOME/aquarius-build/runtime/linux-x64$flavour
cmake -S '$wslRepository/runtime' -B "`$build" -G 'Unix Makefiles' -DCMAKE_TOOLCHAIN_FILE='$wslRepository/runtime/sdk/cmake/toolchains/linux-x64.cmake' $options
cmake --build "`$build" -j`$(nproc)
rm -rf '$wslPack'
cmake --install "`$build" --prefix '$wslPack'
"@
        $file = Join-Path $AquaToolchain.Native "build/linux-x64$flavour.sh"
        New-Item -ItemType Directory -Path (Split-Path $file) -Force | Out-Null
        [IO.File]::WriteAllText($file, $script.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
        $wslScript = '/mnt/' + $file.Substring(0, 1).ToLowerInvariant() + $file.Substring(2).Replace('\', '/')
        $env:MSYS_NO_PATHCONV = '1'
        Invoke-Checked 'wsl.exe' @('-d', $Distribution, 'bash', $wslScript)
        continue
    }
    if ($target -like 'android-*' -and -not $env:ANDROID_HOME -and -not $env:ANDROID_NDK_HOME) { throw 'Android runtime packs need ANDROID_HOME (with the pinned NDK).' }
    $build = Join-Path $AquaToolchain.Native "build/$target$flavour"
    $arguments = @('-S', $runtime, '-B', $build, '-G', 'Ninja', "-DCMAKE_MAKE_PROGRAM=$ninjaPath",
        "-DCMAKE_TOOLCHAIN_FILE=$(Join-Path $runtime "sdk/cmake/toolchains/$target.cmake")", "-DCMAKE_BUILD_TYPE=$Configuration",
        "-DAQUA_LIBRARIES=$libraryList")
    if ($OnnxCuda) { $arguments += '-DAQUA_ONNX_CUDA=ON' }
    # Forward slashes: CMake writes the value into generated scripts, where backslashes would start escapes.
    if ($CaBundle) { $arguments += "-DCMAKE_TLS_CAINFO=$($CaBundle.Replace('\', '/'))" }
    Invoke-Checked $cmakePath $arguments
    Invoke-Checked $cmakePath @('--build', $build)
    if (Test-Path -LiteralPath $pack) { Remove-Item -LiteralPath $pack -Recurse -Force }
    Invoke-Checked $cmakePath @('--install', $build, '--prefix', $pack)
    if ($target -eq 'windows-x64' -and (Get-ChildItem -LiteralPath $pack -Filter '*.dll' -File)) {
        $crt = Find-VCRuntime
        if ($crt -and (Test-Path -LiteralPath (Join-Path $crt 'vcruntime140.dll'))) {
            Copy-Item -Path (Join-Path $crt '*.dll') -Destination $pack
            New-Item -ItemType Directory -Path (Join-Path $pack 'licenses') -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $pack 'licenses/VC-RUNTIME-NOTICES.md'), @'
# Microsoft Visual C++ runtime

The release Visual C++ 2015-2022 runtime DLLs beside the program come unmodified from the licensed Visual Studio
installation's VC/Redist/MSVC/<version>/x64/Microsoft.VC*.CRT folder. Prebuilt third-party DLLs (ONNX Runtime) need
them. Copyright Microsoft Corporation. Redistribution is subject to the Microsoft Software License Terms of that
Visual Studio installation: https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files
'@, [Text.UTF8Encoding]::new($false))
        } else {
            Write-Warning "$pack holds prebuilt DLLs that need the Visual C++ 2015-2022 runtime, and no redistributable copy was found (pass -VCRuntimeDirectory). Machines running exported applications need the VC++ redistributable installed."
        }
    }
}
