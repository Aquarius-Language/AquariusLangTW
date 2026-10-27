# Installs the pinned native toolchain (versions and SHA-256 in scripts/toolchain.json): the official LLVM release
# for this host (clang, wasm-ld, ld.lld; WebAssembly and x86 backends), the wasi-sdk WASI sysroot and compiler-rt,
# the llvm-mingw MinGW-w64 sysroot on Windows hosts, CMake and Ninja, a JDK for Android targets, and the prebuilt Wasmtime
# C API SDK for each requested target. Archives are verified, then unpacked into the gitignored .native-build; what is
# already installed is kept (unpacking goes through a staging folder, so an installed folder is complete), so running it
# again only fetches what is missing. build-runtime.ps1 runs it for the targets it builds. install-toolchain.sh does
# the same on Linux/macOS without PowerShell. Prints clang's path.
#   -Targets windows-x64,android-arm64-v8a   SDK targets to install (default: the desktop target of this host).
param([string[]]$Targets = @())
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'toolchain.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$toolchains = $AquaToolchain.Toolchains
New-Item -ItemType Directory -Path $toolchains, $AquaToolchain.Sdks -Force | Out-Null
if ($Targets.Count -eq 0) { $Targets = @($AquaToolchain.Platform) }
$Targets = @($Targets | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

# Unpacks a verified archive into $Destination. An archive with one top-level directory becomes that directory.
function Install-Archive([string]$Url, [string]$Sha256, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { return }
    $archive = Join-Path $toolchains ([IO.Path]::GetFileName([Uri]::UnescapeDataString($Url)))
    if (-not (Test-Path -LiteralPath $archive)) {
        $progress = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
        try { Invoke-WebRequest -Uri $Url -OutFile $archive -UseBasicParsing }
        catch {
            Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
            throw "Could not download $Url for $Destination ($($_.Exception.Message)). Check the network, or download the file into $toolchains yourself and run this script again."
        } finally { $ProgressPreference = $progress }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Sha256) {
        Remove-Item -LiteralPath $archive -Force
        throw "Checksum mismatch for $Url"
    }
    $staging = Join-Path $toolchains ('.unpack-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    try {
        # .NET reads .zip on every host (tar only does where it is bsdtar), far faster than Expand-Archive.
        if ($archive.EndsWith('.zip')) { [IO.Compression.ZipFile]::ExtractToDirectory($archive, $staging) }
        else { & tar -xf $archive -C $staging; if ($LASTEXITCODE -ne 0) { throw "Could not unpack $archive" } }
        $top = @(Get-ChildItem -LiteralPath $staging)
        $source = if ($top.Count -eq 1 -and $top[0].PSIsContainer) { $top[0].FullName } else { $staging }
        New-Item -ItemType Directory -Path (Split-Path $Destination) -Force | Out-Null
        if ($source -eq $staging) { Move-Item -LiteralPath $staging -Destination $Destination; $staging = $null }
        else { Move-Item -LiteralPath $source -Destination $Destination }
    } finally { if ($staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue } }
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
foreach ($tool in @('cmake', 'ninja')) {
    $pin = $AquaPins.$tool.($AquaToolchain.Platform)
    Install-Archive ($AquaPins.$tool.base + $pin.file) $pin.sha256 (Join-Path $toolchains $AquaPins.$tool.directory.Replace('{platform}', $AquaToolchain.Platform))
}
if (-not $aquaWindows) { & chmod +x $AquaToolchain.Ninja }   # .zip archives carry no permissions
if ($Targets -contains 'windows-x64') { Install-Archive $AquaPins.mingw.url $AquaPins.mingw.sha256 $AquaToolchain.MinGW }
if ($Targets -match '^android-') {
    $jdk = $AquaPins.jdk.($AquaToolchain.Platform)
    Install-Archive ($AquaPins.jdk.base + $jdk.file) $jdk.sha256 $AquaToolchain.Jdk
}
foreach ($target in $Targets) {
    if ($target -eq 'web') { continue }
    if ($target -notin $AquaTargets) { throw "Unknown target: $target (known: $($AquaTargets -join ', '))" }
    foreach ($name in $AquaPins.sdks.PSObject.Properties.Name) {   # every prebuilt SDK the runtime links
        $pin = $AquaPins.sdks.$name.$target
        if (-not $pin) { throw "No pinned $name SDK for $target." }
        Install-Archive ($AquaPins.sdks.$name.base + $pin.file) $pin.sha256 (Get-AquaSdk $name $target)
    }
}
Assert-AquaToolchain
Write-Output $AquaToolchain.Clang
