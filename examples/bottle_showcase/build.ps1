param([string]$Dotnet = '', [string]$Compiler = '', [ValidateSet('wasm', 'web', 'windows', 'linux', 'android')][string]$Target = 'wasm')
$ErrorActionPreference = 'Stop'
if (-not $Dotnet) {
    $local = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.dotnet/dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $local) { $local } else { 'dotnet' }
}
if (-not $Compiler) {
    $candidates = @(
        (Join-Path $PSScriptRoot '../../AquariusCli/bin/Debug/net8.0/aqua.dll'),
        (Join-Path $PSScriptRoot '../AquariusLangTW/AquariusCli/bin/Debug/net8.0/aqua.dll')
    )
    $Compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $Compiler) { throw 'Build AquariusCli first, or pass -Compiler with an absolute path to aqua.dll.' }
}
$output = switch ($Target) {
    'wasm' { 'dist/showcase.wasm' }
    'windows' { 'dist/showcase.exe' }
    'linux' { 'dist/showcase' }
    'web' { 'dist/web' }
    'android' { 'dist/android-project' }
}
& $Dotnet $Compiler build $PSScriptRoot --target $Target -o (Join-Path $PSScriptRoot $output)
if ($LASTEXITCODE -ne 0) { throw 'Bottle showcase build failed.' }
if ($Target -eq 'wasm') {
    & $Dotnet $Compiler run (Join-Path $PSScriptRoot $output)
    if ($LASTEXITCODE -ne 0) { throw 'Bottle showcase execution failed.' }
}
