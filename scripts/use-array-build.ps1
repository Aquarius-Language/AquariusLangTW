param([string]$BuildDirectory = (Join-Path $PSScriptRoot '../.array-build/after'))
$ErrorActionPreference = 'Stop'
$resolvedBuild = (Resolve-Path -LiteralPath $BuildDirectory).Path
if (-not ((Test-Path -LiteralPath (Join-Path $resolvedBuild 'aqua.exe')) -or
          (Test-Path -LiteralPath (Join-Path $resolvedBuild 'aqua')))) {
    throw 'Build AquariusCli into this directory before activating it.'
}
# Process/session only: closing this shell restores the inherited PATH.
$env:PATH = $resolvedBuild + [IO.Path]::PathSeparator + $env:PATH
Get-Command aqua | Select-Object Name, Source
