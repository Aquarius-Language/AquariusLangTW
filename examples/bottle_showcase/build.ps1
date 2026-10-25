param([string]$Dotnet = 'dotnet', [string]$Compiler = '', [ValidateSet('wasm', 'web', 'windows', 'linux', 'android')][string]$Target = 'wasm')
$ErrorActionPreference = 'Stop'
if (-not $Compiler) {
    # A repository build of the CLI (dotnet build compiler/AquariusCli).
    $candidates = @('Debug', 'Release') | ForEach-Object { Join-Path $PSScriptRoot "../../compiler/AquariusCli/bin/$_/net8.0/aqua.dll" }
    $Compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $Compiler) { throw 'Build compiler/AquariusCli first, or pass -Compiler with the path of aqua.dll.' }
}
$output = switch ($Target) {
    'wasm' { 'dist/showcase.wasm' }
    'windows' { 'dist/showcase.exe' }
    'linux' { 'dist/showcase' }
    'web' { 'dist/web' }
    'android' { 'dist/showcase.apk' }
}
& $Dotnet $Compiler build $PSScriptRoot --target $Target -o (Join-Path $PSScriptRoot $output)
if ($LASTEXITCODE -ne 0) { throw 'Bottle showcase build failed.' }
if ($Target -eq 'wasm') {
    & $Dotnet $Compiler run (Join-Path $PSScriptRoot $output)
    if ($LASTEXITCODE -ne 0) { throw 'Bottle showcase execution failed.' }
}
