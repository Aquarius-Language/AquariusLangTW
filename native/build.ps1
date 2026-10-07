param([string]$CMake = 'cmake', [string]$Generator = '', [string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$nativeRoot = $PSScriptRoot
$buildPath = Join-Path $nativeRoot 'build'
function Read-BuildCache([string]$directory) {
    $values = @{}
    $cachePath = Join-Path $directory 'CMakeCache.txt'
    if (Test-Path -LiteralPath $cachePath) {
        foreach ($line in Get-Content -LiteralPath $cachePath) {
            if ($line -match '^(CMAKE_GENERATOR|CMAKE_COMMAND):[^=]+=(.*)$') {
                $values[$Matches[1]] = $Matches[2]
            }
        }
    }
    return $values
}
$originalCache = Read-BuildCache $buildPath
if ($Generator -and $originalCache['CMAKE_GENERATOR'] -and $Generator -ne $originalCache['CMAKE_GENERATOR']) {
    # Preserve the existing build when explicitly switching generators.
    $generatorDirectory = ($Generator -replace '[^a-zA-Z0-9-]', '-').Trim('-').ToLowerInvariant()
    $buildPath = Join-Path $buildPath "variants/$Runtime-$generatorDirectory"
}
$cache = Read-BuildCache $buildPath
if (-not $Generator -and $cache['CMAKE_GENERATOR']) {
    # Explicit -G also overrides a different CMAKE_GENERATOR in the shell.
    $Generator = $cache['CMAKE_GENERATOR']
}
if (-not $PSBoundParameters.ContainsKey('CMake')) {
    $cachedCMake = $cache['CMAKE_COMMAND']
    if (-not $cachedCMake) { $cachedCMake = $originalCache['CMAKE_COMMAND'] }
    if ($cachedCMake -and (Test-Path -LiteralPath $cachedCMake)) { $CMake = $cachedCMake }
}
$installPath = Join-Path $nativeRoot "../AquariusDesktopInterpretedREPL/runtimes/$Runtime/native"
$arguments = @('-S', $nativeRoot, '-B', $buildPath, '-DCMAKE_BUILD_TYPE=Release')
if ($Generator) { $arguments += @('-G', $Generator) }
& $CMake @arguments
if ($LASTEXITCODE) { throw 'CMake configuration failed.' }
& $CMake --build $buildPath --config Release --parallel
if ($LASTEXITCODE) { throw 'Native build failed.' }
& $CMake --install $buildPath --config Release --prefix $installPath
if ($LASTEXITCODE) { throw 'Native installation failed.' }
