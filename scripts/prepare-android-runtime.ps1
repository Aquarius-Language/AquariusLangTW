param([Parameter(Mandatory)][string]$Ndk, [string[]]$Abis = @('arm64-v8a','x86_64'))
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cache = Join-Path $repository '.android-build/toolchains'
$template = Join-Path $repository 'AquariusAndroid/template/app/src/main'
$hostTag = if ($IsLinux) { 'linux-x86_64' } elseif ($IsMacOS) { 'darwin-x86_64' } else { 'windows-x86_64' }
$llvm = Join-Path $Ndk "toolchains/llvm/prebuilt/$hostTag"
$env:PATH = (Join-Path $llvm 'bin') + [IO.Path]::PathSeparator + $env:PATH
$exe = if ($IsWindows -or $env:OS -eq 'Windows_NT') { '.exe' } else { '' }
$wrapper = if ($exe) { '.cmd' } else { '' }
function SourceArchive([string]$Name, [string]$Url, [string]$Hash, [string]$Directory) {
    $archive = Join-Path $cache $Name
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest $Url -OutFile $archive }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Hash) { throw "Runtime source checksum mismatch: $Name" }
    if (-not (Test-Path -LiteralPath (Join-Path $cache $Directory))) {
        & tar --exclude='*/.claude' -xzf $archive -C $cache
        if ($LASTEXITCODE -ne 0) { throw "Cannot extract $Name" }
    }
    return Join-Path $cache $Directory
}
$quickjs = SourceArchive 'quickjs-v0.17.0.tar.gz' 'https://github.com/quickjs-ng/quickjs/archive/refs/tags/v0.17.0.tar.gz' '559BC4C420475E55C7AB4510ADBC562F55D7524D75E8E89D79CE4BB02F5687D9' 'quickjs-0.17.0'
$wasmtime = SourceArchive 'wasmtime-v48.0.2.tar.gz' 'https://github.com/bytecodealliance/wasmtime/archive/refs/tags/v48.0.2.tar.gz' '22408DF5455721FA49A2163A1D38E4C82E3E6D5D67F6353083B3A4B16D4330E6' 'wasmtime-48.0.2'
foreach ($abi in $Abis) {
    $triple = switch ($abi) { 'arm64-v8a' { 'aarch64-linux-android' } 'x86_64' { 'x86_64-linux-android' } default { throw "Unsupported Android ABI: $abi" } }
    $key = $triple.Replace('-','_')
    $cc = Join-Path $llvm "bin/${triple}29-clang$wrapper"
    [Environment]::SetEnvironmentVariable("CC_$key", $cc, 'Process')
    [Environment]::SetEnvironmentVariable("AR_$key", (Join-Path $llvm "bin/llvm-ar$exe"), 'Process')
    [Environment]::SetEnvironmentVariable("CARGO_TARGET_$($key.ToUpperInvariant())_LINKER", $cc, 'Process')
    [Environment]::SetEnvironmentVariable("CARGO_TARGET_$($key.ToUpperInvariant())_RUSTFLAGS", '-C link-arg=-Wl,-z,max-page-size=16384', 'Process')
    & cargo build --locked --manifest-path "$wasmtime/crates/c-api/artifact/Cargo.toml" --target-dir "$wasmtime/target" --release --no-default-features --features cranelift,gc,gc-null,threads,parallel-compilation --target $triple
    if ($LASTEXITCODE -ne 0) { throw "Wasmtime build failed: $abi. Install Rust and rustup target add $triple." }
    $libraries = Join-Path $template "jniLibs/$abi"
    New-Item -ItemType Directory -Force -Path $libraries | Out-Null
    Copy-Item -LiteralPath "$wasmtime/target/$triple/release/libwasmtime.so" -Destination $libraries
    $headers = @(Get-ChildItem "$wasmtime/target/$triple/release/build/wasmtime-c-api-*/out/include/wasmtime/conf.h" | Sort-Object LastWriteTime -Descending)[0].Directory.Parent.FullName
    $objects = Join-Path $cache "quickjs-$abi"
    New-Item -ItemType Directory -Force -Path $objects | Out-Null
    $common = @("--target=${triple}29", '-O2', '-g0', "--sysroot=$llvm/sysroot", '-resource-dir', "$llvm/lib/clang/19", '-fPIC')
    foreach ($name in @('dtoa','libregexp','libunicode','quickjs')) {
        & "$llvm/bin/clang$exe" @common -std=gnu11 -D_GNU_SOURCE -funsigned-char -c "$quickjs/$name.c" -o "$objects/$name.o"
        if ($LASTEXITCODE -ne 0) { throw "QuickJS build failed: $abi/$name" }
    }
    & "$llvm/bin/clang++$exe" @common -std=c++17 -shared '-Wl,-z,max-page-size=16384' '-Wl,-soname,libaquarius_runtime.so' -I $quickjs -I $headers "$template/cpp/runtime.cpp" "$objects/dtoa.o" "$objects/libregexp.o" "$objects/libunicode.o" "$objects/quickjs.o" -L $libraries -lwasmtime -llog -ldl -lm -lc++_shared -o "$libraries/libaquarius_runtime.so"
    if ($LASTEXITCODE -ne 0) { throw "Wasmtime JNI build failed: $abi" }
}
Copy-Item -LiteralPath "$quickjs/LICENSE" -Destination "$template/assets/aquarius/QUICKJS-LICENSE.txt"
Copy-Item -LiteralPath "$wasmtime/LICENSE" -Destination "$template/assets/aquarius/WASMTIME-LICENSE.txt"
Write-Output 'Prepared Wasmtime 48.0.2 and QuickJS-NG 0.17.0 for Android.'
