param(
    [Parameter(Mandatory)][string]$Ndk,
    [string[]]$Abis = @('arm64-v8a','x86_64'),
    [string]$Cargo = 'cargo',
    [switch]$TestOnly
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $TestOnly) {
    $Ndk = [IO.Path]::GetFullPath($Ndk)
    $properties = Join-Path $Ndk 'source.properties'
    if (-not (Test-Path -LiteralPath $properties) -or (Get-Content -LiteralPath $properties -Raw) -notmatch '28\.2\.13676358') {
        throw 'The Android WebGPU build requires pinned NDK 28.2.13676358.'
    }
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cache = Join-Path $repository '.android-build/toolchains'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
function SourceArchive([string]$Name, [string]$Url, [string]$Hash, [string]$Directory) {
    $archive = Join-Path $cache $Name
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest $Url -OutFile $archive }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Hash) { throw "WebGPU source checksum mismatch: $Name" }
    $source = Join-Path $cache $Directory
    if (-not (Test-Path -LiteralPath $source)) {
        & tar -xzf $archive -C $cache
        if ($LASTEXITCODE -ne 0) { throw "Cannot extract $Name" }
    }
    return $source
}
$native = SourceArchive 'wgpu-native-v29.0.1.1.tar.gz' 'https://github.com/gfx-rs/wgpu-native/archive/refs/tags/v29.0.1.1.tar.gz' '6008651E1D10022D4DED15B69405B4BA88577BE5D623938BA98AD241B2CBF818' 'wgpu-native-29.0.1.1'
$hal = SourceArchive 'wgpu-hal-29.0.3.crate' 'https://static.crates.io/crates/wgpu-hal/wgpu-hal-29.0.3.crate' '31F8E1A9E7A8512F276F7C62E018C7FA8D60954303FED2E5750114332049193F' 'wgpu-hal-29.0.3'
$patch = Join-Path $repository 'native/patches/wgpu-hal-29.0.3-timeline-values.patch'
$halRelative = '.android-build/toolchains/wgpu-hal-29.0.3'
& git -C $repository apply --check --reverse "--directory=$halRelative" $patch 2>$null
if ($LASTEXITCODE -ne 0) {
    & git -C $repository apply --check "--directory=$halRelative" $patch
    if ($LASTEXITCODE -ne 0) { throw 'Pinned WebGPU semaphore source does not match the compatibility patch.' }
    & git -C $repository apply "--directory=$halRelative" $patch
    if ($LASTEXITCODE -ne 0) { throw 'Cannot apply WebGPU semaphore compatibility patch.' }
}
# Test the actual patched submission builder without creating a GPU or window.
$tests = Join-Path $cache 'wgpu-semaphore-tests'
New-Item -ItemType Directory -Force -Path $tests | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $tests 'Cargo.toml'), @'
[package]
name = 'aquarius-vulkan-semaphore-tests'
version = '0.1.0'
edition = '2021'
[lib]
path = 'lib.rs'
[dependencies]
ash = { version = '=0.38.0', default-features = false, features = ['std', 'debug'] }
'@, $utf8)
$testSource = (Join-Path $repository 'native/tests/vulkan_semaphore_values.rs').Replace('\','/')
$halSource = (Join-Path $hal 'src/vulkan/semaphore_list.rs').Replace('\','/')
[IO.File]::WriteAllText((Join-Path $tests 'lib.rs'), "extern crate alloc;`n#[path = `"$halSource`"] mod semaphore_list;`ninclude!(`"$testSource`");`n", $utf8)
& $Cargo test --manifest-path (Join-Path $tests 'Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'WebGPU semaphore regression tests failed.' }
if ($TestOnly) { return }
# Keep the upstream lockfile; replace only its registry HAL with the pinned local patch.
$manifest = Join-Path $native 'Cargo.toml'
$text = [IO.File]::ReadAllText($manifest)
if (-not $text.Contains('[patch.crates-io]')) {
    [IO.File]::WriteAllText($manifest, $text + "`n[patch.crates-io]`nwgpu-hal = { path = '../wgpu-hal-29.0.3' }`n", $utf8)
}
$lock = Join-Path $native 'Cargo.lock'
$text = [IO.File]::ReadAllText($lock)
$text = [regex]::Replace($text, '(name = "wgpu-hal"\r?\nversion = "29\.0\.3"\r?\n)source = [^\r\n]+\r?\nchecksum = [^\r\n]+\r?\n', '$1')
[IO.File]::WriteAllText($lock, $text, $utf8)
$headers = Join-Path $native 'ffi/webgpu-headers'
New-Item -ItemType Directory -Force -Path $headers | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'AquariusAndroid/template/app/src/main/cpp/include/webgpu/webgpu.h') -Destination $headers -Force
$hostTag = if ($IsLinux) { 'linux-x86_64' } elseif ($IsMacOS) { 'darwin-x86_64' } else { 'windows-x86_64' }
$llvm = Join-Path $Ndk "toolchains/llvm/prebuilt/$hostTag"
$exe = if ($IsWindows -or $env:OS -eq 'Windows_NT') { '.exe' } else { '' }
$wrapper = if ($exe) { '.cmd' } else { '' }
$env:WGPU_NATIVE_VERSION = 'v29.0.1.1'
foreach ($abi in $Abis) {
    $triple = switch ($abi) { 'arm64-v8a' { 'aarch64-linux-android' } 'x86_64' { 'x86_64-linux-android' } default { throw "Unsupported Android ABI: $abi" } }
    $key = $triple.Replace('-','_')
    [Environment]::SetEnvironmentVariable("CARGO_TARGET_$($key.ToUpperInvariant())_LINKER", (Join-Path $llvm "bin/${triple}29-clang$wrapper"), 'Process')
    [Environment]::SetEnvironmentVariable("CARGO_TARGET_$($key.ToUpperInvariant())_RUSTFLAGS", '-C link-arg=-Wl,-z,max-page-size=16384', 'Process')
    $clangRoot = $llvm.Replace('\','/')
    $env:BINDGEN_EXTRA_CLANG_ARGS = "--target=${triple}29 --sysroot=`"$clangRoot/sysroot`" -resource-dir `"$clangRoot/lib/clang/19`" -I`"$clangRoot/sysroot/usr/include/$triple`""
    & $Cargo build --locked --manifest-path $manifest --target-dir (Join-Path $native 'target') --release --target $triple
    if ($LASTEXITCODE -ne 0) { throw "Patched Android WebGPU build failed: $abi. Install the Rust Android target and libclang." }
    $libraries = Join-Path $repository "AquariusAndroid/template/app/src/main/jniLibs/$abi"
    New-Item -ItemType Directory -Force -Path $libraries | Out-Null
    Copy-Item -LiteralPath (Join-Path $native "target/$triple/release/libwgpu_native.so") -Destination $libraries -Force
}
Write-Output 'Prepared wgpu-native 29.0.1.1 with complete Vulkan timeline values for Android.'
