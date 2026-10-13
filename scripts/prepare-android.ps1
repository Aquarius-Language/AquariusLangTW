param([string]$Ndk = $env:AQUARIUS_ANDROID_NDK)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cache = Join-Path $repository '.android-build/toolchains'
$template = Join-Path $repository 'AquariusAndroid/template/app/src/main'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
function Download([string]$Url, [string]$Path, [string]$Hash) {
    if (-not (Test-Path -LiteralPath $Path)) { Invoke-WebRequest -Uri $Url -OutFile $Path }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash) { throw "Pinned Android dependency checksum mismatch: $Path" }
}
if (-not $Ndk) {
    $sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { $env:ANDROID_SDK_ROOT }
    if ($sdk) { $Ndk = Join-Path $sdk 'ndk/28.2.13676358' }
}
if (-not $Ndk -or -not (Test-Path -LiteralPath (Join-Path $Ndk 'source.properties'))) { throw 'Install NDK 28.2.13676358, then pass -Ndk or set AQUARIUS_ANDROID_NDK.' }
if ((Get-Content -LiteralPath (Join-Path $Ndk 'source.properties') -Raw) -notmatch '28\.2\.13676358') { throw 'The Android bridge requires pinned NDK 28.2.13676358.' }
$hostTag = if ($IsLinux) { 'linux-x86_64' } elseif ($IsMacOS) { 'darwin-x86_64' } else { 'windows-x86_64' }
$llvm = Join-Path $Ndk "toolchains/llvm/prebuilt/$hostTag"
$compiler = Join-Path $llvm $(if ($hostTag.StartsWith('windows')) { 'bin/clang++.exe' } else { 'bin/clang++' })
foreach ($spec in @(
    @{ abi='arm64-v8a'; arch='aarch64'; triple='aarch64-linux-android'; hash='721741F1B05A20C1738166BEDF7A5EFB2BA4B382DA689526D3FC33DE22BDD573' },
    @{ abi='x86_64'; arch='x86_64'; triple='x86_64-linux-android'; hash='EF16FC0644BF0E308A39AC4516742DA8E22D8C201D3A542CC5BAF533D272C491' }
)) {
    $archive = Join-Path $cache "wgpu-$($spec.abi).zip"
    Download "https://github.com/gfx-rs/wgpu-native/releases/download/v29.0.1.1/wgpu-android-$($spec.arch)-release.zip" $archive $spec.hash
    $unpacked = Join-Path $cache "wgpu-$($spec.abi)"
    if (-not (Test-Path -LiteralPath $unpacked)) { Expand-Archive -LiteralPath $archive -DestinationPath $unpacked }
    $libraries = Join-Path $template "jniLibs/$($spec.abi)"
    New-Item -ItemType Directory -Force -Path $libraries | Out-Null
    Copy-Item -LiteralPath (Join-Path $unpacked 'lib/libwgpu_native.so') -Destination $libraries
    Copy-Item -LiteralPath (Join-Path $llvm "sysroot/usr/lib/$($spec.triple)/libc++_shared.so") -Destination $libraries
    & $compiler "--target=$($spec.triple)29" -O2 -g0 "--sysroot=$llvm/sysroot" -resource-dir "$llvm/lib/clang/19" -std=c++17 -shared -fPIC '-Wl,-z,max-page-size=16384' -I "$template/cpp/include" "$template/cpp/gpu.cpp" -L $libraries -lwgpu_native -landroid -llog -lc++_shared -o "$libraries/libaquarius_android.so"
    if ($LASTEXITCODE -ne 0) { throw "Android JNI compilation failed: $($spec.abi)" }
}
foreach ($license in @('LICENSE.APACHE','LICENSE.MIT')) {
    Invoke-WebRequest "https://raw.githubusercontent.com/gfx-rs/wgpu-native/v29.0.1.1/$license" -OutFile (Join-Path $template "assets/aquarius/WGPU-$license.txt")
}
$icu = Join-Path $template 'assets/aquarius/icudtl.dat'
if (-not (Test-Path -LiteralPath $icu)) {
    $encoded = Join-Path $cache 'icudtl.base64'
    Invoke-WebRequest 'https://chromium.googlesource.com/chromium/deps/icu/+/d578f2e8b7bd5938e21cfb6bf15c079e0aa5b738/common/icudtl.dat?format=TEXT' -OutFile $encoded
    [IO.File]::WriteAllBytes($icu,[Convert]::FromBase64String([IO.File]::ReadAllText($encoded)))
}
if ((Get-FileHash -LiteralPath $icu -Algorithm SHA256).Hash -ne '9F48C7F9C7C94D516A14870707E910AB94D75AE640FF6842C4AF53276CD26EBE') { throw 'V8 15.2.124.17 ICU checksum mismatch.' }
Write-Output 'Prepared native Android WebGPU libraries and Unicode data for arm64-v8a and x86_64.'
