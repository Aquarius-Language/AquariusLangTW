# Publishes the aqua distribution layout: the self-contained compiler and everything it builds applications from.
#   dist/aqua/
#     aqua(.exe) + compiler files   the CLI (self-contained .NET publish)
#     runtime/<target>/              runtime packs from .native-build/install (scripts/build-runtime.ps1)
#     web-glue/                      the web glue (the JavaScript of exported sites)
#     libraries/<name>/              each default library's aqua.toml, aqua/ sources and README (what the compiler reads)
#     sdk/                           runtime/sdk: headers, CMake modules and toolchain files for external library authors
#     scripts/                       the toolchain pins and installers the SDK's toolchain files use
#   scripts/publish-cli.ps1 [-Runtime win-x64|linux-x64] [-Output dist/aqua] [-Packs windows-x64,web,...]
param([string]$Dotnet = 'dotnet', [string]$Runtime = 'win-x64', [string]$Output = 'dist/aqua', [string[]]$Packs = @())
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Packs = @($Packs | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if (-not [IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $repository $Output }
$Output = [IO.Path]::GetFullPath($Output)
if (-not $Output.StartsWith($repository, [StringComparison]::OrdinalIgnoreCase) -or $Output -eq $repository) { throw "Publish inside the repository (dist/...), not $Output." }
if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }

& $Dotnet publish (Join-Path $repository 'compiler/AquariusCli/AquariusCli.csproj') -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false --nologo -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Publishing the compiler failed.' }

function Copy-Folder([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Path (Split-Path $Destination) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}
# Runtime packs: every installed target (and flavour such as windows-x64-cuda), or the ones named.
$installed = Join-Path $repository '.native-build/install'
$available = if (Test-Path -LiteralPath $installed) { @(Get-ChildItem -LiteralPath $installed -Directory | ForEach-Object Name) } else { @() }
if ($Packs.Count) {
    foreach ($pack in $Packs) { if ($available -notcontains $pack) { throw "The $pack runtime pack is not built (scripts/build-runtime.ps1 -Targets $pack)." } }
} else { $Packs = $available }
if (-not $Packs.Count) { throw 'No runtime pack is built: run scripts/build-runtime.ps1 first.' }
foreach ($pack in $Packs) { Copy-Folder (Join-Path $installed $pack) (Join-Path $Output "runtime/$pack") }

Copy-Folder (Join-Path $repository 'web-glue') (Join-Path $Output 'web-glue')
Copy-Folder (Join-Path $repository 'runtime/sdk') (Join-Path $Output 'sdk')
New-Item -ItemType Directory -Path (Join-Path $Output 'scripts') -Force | Out-Null
foreach ($file in @('toolchain.json', 'toolchain.ps1', 'install-toolchain.ps1', 'install-toolchain.sh')) {
    Copy-Item -LiteralPath (Join-Path $repository "scripts/$file") -Destination (Join-Path $Output 'scripts')
}
foreach ($library in Get-ChildItem -LiteralPath (Join-Path $repository 'libraries') -Directory) {
    if (-not (Test-Path -LiteralPath (Join-Path $library.FullName 'aqua.toml'))) { continue }
    $destination = Join-Path $Output "libraries/$($library.Name)"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($file in @('aqua.toml', 'README.md', 'LICENSE')) {
        $path = Join-Path $library.FullName $file
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $destination }
    }
    if (Test-Path -LiteralPath (Join-Path $library.FullName 'aqua')) { Copy-Folder (Join-Path $library.FullName 'aqua') (Join-Path $destination 'aqua') }
}
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $Output

$aqua = Join-Path $Output $(if ($Runtime.StartsWith('win-')) { 'aqua.exe' } else { 'aqua' })
& $aqua --help | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The published aqua does not start.' }
Write-Host "Published $Output (runtime packs: $($Packs -join ', '))"
