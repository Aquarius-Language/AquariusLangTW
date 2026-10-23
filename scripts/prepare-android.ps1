param([string]$Ndk = $env:AQUARIUS_ANDROID_NDK)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cache = Join-Path $repository '.android-build/toolchains'
$template = Join-Path $repository 'AquariusAndroid/template/app/src/main'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
if (-not $Ndk) {
    $sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { $env:ANDROID_SDK_ROOT }
    if ($sdk) { $Ndk = Join-Path $sdk 'ndk/28.2.13676358' }
}
if (-not $Ndk -or -not (Test-Path -LiteralPath (Join-Path $Ndk 'source.properties'))) { throw 'Install NDK 28.2.13676358, then pass -Ndk or set AQUARIUS_ANDROID_NDK.' }
if ((Get-Content -LiteralPath (Join-Path $Ndk 'source.properties') -Raw) -notmatch '28\.2\.13676358') { throw 'The Android bridge requires pinned NDK 28.2.13676358.' }
$hostTag = if ($IsLinux) { 'linux-x86_64' } elseif ($IsMacOS) { 'darwin-x86_64' } else { 'windows-x86_64' }
$llvm = Join-Path $Ndk "toolchains/llvm/prebuilt/$hostTag"
$compiler = Join-Path $llvm $(if ($hostTag.StartsWith('windows')) { 'bin/clang++.exe' } else { 'bin/clang++' })
& (Join-Path $PSScriptRoot 'prepare-android-wgpu.ps1') -Ndk $Ndk
foreach ($spec in @(
    @{ abi='arm64-v8a'; triple='aarch64-linux-android' },
    @{ abi='x86_64'; triple='x86_64-linux-android' }
)) {
    $libraries = Join-Path $template "jniLibs/$($spec.abi)"
    New-Item -ItemType Directory -Force -Path $libraries | Out-Null
    Copy-Item -LiteralPath (Join-Path $llvm "sysroot/usr/lib/$($spec.triple)/libc++_shared.so") -Destination $libraries
    & $compiler "--target=$($spec.triple)29" -O2 -g0 "--sysroot=$llvm/sysroot" -resource-dir "$llvm/lib/clang/19" -std=c++17 -shared -fPIC '-Wl,-z,max-page-size=16384' -I "$template/cpp/include" "$template/cpp/gpu.cpp" "$template/cpp/dynamic_library.cpp" -L $libraries -lwgpu_native -landroid -llog -ldl -lc++_shared -o "$libraries/libaquarius_android.so"
    if ($LASTEXITCODE -ne 0) { throw "Android JNI compilation failed: $($spec.abi)" }
}
foreach ($license in @('LICENSE.APACHE','LICENSE.MIT')) {
    Invoke-WebRequest "https://raw.githubusercontent.com/gfx-rs/wgpu-native/v29.0.1.1/$license" -OutFile (Join-Path $template "assets/aquarius/WGPU-$license.txt")
}
& (Join-Path $PSScriptRoot 'prepare-android-runtime.ps1') -Ndk $Ndk
Write-Output 'Prepared native Android WebGPU and Wasmtime for arm64-v8a and x86_64.'
