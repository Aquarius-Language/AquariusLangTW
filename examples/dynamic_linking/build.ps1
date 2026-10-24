param(
    [string]$Repository = (Join-Path $PSScriptRoot '../..'),
    [string]$Dotnet = '', [string]$Ndk = '',
    [switch]$LibrariesOnly, [switch]$SkipRuntimePacks, [switch]$Apk
)
$ErrorActionPreference = 'Stop'
$project = $PSScriptRoot
if (-not $Dotnet) { $Dotnet = Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'; if (-not (Test-Path -LiteralPath $Dotnet)) { $Dotnet = 'dotnet' } }
if (-not $Ndk) { $Ndk = if ($env:AQUARIUS_ANDROID_NDK) { $env:AQUARIUS_ANDROID_NDK } else { Join-Path $Repository '.android-build/toolchains/android-ndk-r28c' } }
$Repository = [IO.Path]::GetFullPath($Repository)
# The repository's pinned LLVM/Clang (scripts/install-wasm-toolchain.ps1) cross-compiles every non-Android library.
. (Join-Path $Repository 'scripts/wasm-toolchain.ps1')
Assert-AquaToolchain
$clang = $AquaToolchain.Clang
function Run([string]$Tool, [string[]]$Arguments) { & $Tool @Arguments; if ($LASTEXITCODE -ne 0) { throw "Failed: $Tool $Arguments" } }
$include = Join-Path $Repository 'AquariusCore/linking'
$source = Join-Path $project 'library.c'
foreach ($directory in @('libraries/windows','libraries/linux','libraries/portable','libraries/web','native/android/arm64-v8a','native/android/x86_64','dist')) { New-Item -ItemType Directory -Force -Path (Join-Path $project $directory) | Out-Null }
# -fno-builtin keeps -O2 from turning loops into libc calls that -nostdlib leaves unresolved.
Run $clang @('--target=x86_64-pc-windows-msvc','-fuse-ld=lld','-O2','-shared','-nostdlib','-fno-builtin','-I',$include,$source,'-o',(Join-Path $project 'libraries/windows/showcase.dll'))
Run $clang @('--target=x86_64-linux-musl','-fuse-ld=lld','-O2','-shared','-fPIC','-nostdlib','-fno-builtin','-I',$include,$source,'-o',(Join-Path $project 'libraries/linux/libshowcase.so'))
$exports = @('aqua_library_abi','aqua_alloc','aqua_free','echo','greeting','sum','counter','failure','oversized','invalid_utf8') | ForEach-Object { '-Wl,--export=' + $_ }
Run $clang (@('--target=wasm32-unknown-unknown')+$AquaWasmCpu+@('-O2','-nostdlib','-fno-builtin','-Wl,--no-entry','-Wl,--export-memory','-Wl,-z,stack-size=1048576','-Wl,--max-memory=16777216','-I',$include,$source,'-o',(Join-Path $project 'libraries/portable/showcase.wasm')) + $exports)
Copy-Item -LiteralPath (Join-Path $project 'web-library.mjs') -Destination (Join-Path $project 'libraries/web/showcase.mjs') -Force
$llvm = Join-Path $Ndk 'toolchains/llvm/prebuilt/windows-x86_64'
foreach ($spec in @(@{abi='arm64-v8a';triple='aarch64-linux-android29'},@{abi='x86_64';triple='x86_64-linux-android29'})) {
    Run (Join-Path $llvm 'bin/clang.exe') @("--target=$($spec.triple)","--sysroot=$llvm/sysroot",'-O2','-shared','-fPIC','-Wl,-z,max-page-size=16384','-I',$include,$source,'-o',(Join-Path $project "native/android/$($spec.abi)/libshowcase.so"))
}
if ($LibrariesOnly) { return }
Run $Dotnet @('build',(Join-Path $Repository 'AquariusCli'),'-m:1','--no-restore','-v:q','-clp:ErrorsOnly')
$cli = Join-Path $Repository 'AquariusCli/bin/Debug/net8.0/aqua.dll'
if (-not $SkipRuntimePacks) {
    & (Join-Path $Repository 'scripts/publish-apphost.ps1') -Dotnet $Dotnet
    if ($LASTEXITCODE -ne 0) { throw 'Windows runtime publishing failed' }
    Run 'wsl.exe' @('--cd',$Repository,'--','bash','scripts/publish-apphost.sh')
}
$env:AQUARIUS_BUILD_TARGETS = Join-Path $Repository 'build-targets'
$wasm = Join-Path $project 'dist/showcase.wasm'
Run $Dotnet @($cli,'build',(Join-Path $project 'main.aqua'),'--root',$project,'--assets',(Join-Path $project 'libraries'),'--assets',(Join-Path $project 'native'),'--entry','main.aqua','-o',$wasm)
Run $Dotnet @($cli,'run',$wasm)
foreach ($spec in @(@{target='web';name='web'},@{target='windows';name='windows/Showcase.exe'},@{target='linux';name='linux/showcase'})) {
    Run $Dotnet @($cli,'build',$wasm,'--target',$spec.target,'-o',(Join-Path $project "dist/$($spec.name)"))
}
# Keep any edits in a previous generated Android project when rebuilding.
$androidProject = [IO.Path]::GetFullPath((Join-Path $project 'dist/android-project'))
$distRoot = [IO.Path]::GetFullPath((Join-Path $project 'dist'))
if (Test-Path -LiteralPath $androidProject) {
    $previous = Join-Path $distRoot ('android-project-previous-' + [Guid]::NewGuid().ToString('N'))
    if ([IO.Path]::GetDirectoryName($androidProject) -ne $distRoot -or [IO.Path]::GetDirectoryName($previous) -ne $distRoot -or (Get-Item -LiteralPath $androidProject).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe Android project output path' }
    Move-Item -LiteralPath $androidProject -Destination $previous
    Write-Output "Preserved previous Android project / 已保留先前的 Android 專案：$previous"
}
Run $Dotnet @($cli,'build',$wasm,'--target','android','-o',$androidProject)
if ($Apk) { Run $Dotnet @($cli,'build',$wasm,'--target','android','-o',(Join-Path $project 'dist/android/Showcase.apk')) }
Write-Output "Built all platforms / 已完成所有平台：$project/dist"
