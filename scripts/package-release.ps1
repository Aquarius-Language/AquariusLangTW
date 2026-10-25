#requires -Version 5.1
# Builds, tests and packages the Windows x64 aqua distribution into dist/aqua/ (the layout publish-cli.ps1 describes),
# then zips it with a SHA-256 checksum into dist/releases/AquariusCompiler-win-x64-<build-id>.zip.
param(
    [string]$Dotnet = 'dotnet',
    [string[]]$Targets = @('windows-x64', 'web', 'android-arm64-v8a', 'android-x86_64'),
    [switch]$SkipRuntime,
    [switch]$SkipTests,
    [switch]$Help
)
$ErrorActionPreference = 'Stop'
if ($Help) {
    Write-Host @'
Usage: build-release.bat [-Targets windows-x64,web,...] [-SkipRuntime] [-SkipTests] [-Dotnet path]
Builds the runtime packs (scripts/build-runtime.ps1; add linux-x64 to build it in WSL), builds and tests the compiler,
publishes dist/aqua/ (compiler, runtime/<target>/, web-glue/, libraries/, sdk/, scripts/) and checks it without an
installed .NET: help, compile and run, a website and a standalone executable. -SkipRuntime reuses the runtime packs in
.native-build/install. Requires the .NET 8 SDK and the pinned toolchain (scripts/install-toolchain.ps1); Android packs
need ANDROID_HOME.
'@
    exit 0
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Targets = @($Targets | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed (exit $LASTEXITCODE): $Program $($Arguments -join ' ')" }
}
function Get-Sha256([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
# Runs the published aqua (or a program it built) without any installed .NET and checks its output.
function Invoke-Published([string]$Program, [string[]]$Arguments, [string]$Expected, [string]$WorkingDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new($Program)
    $start.Arguments = (@($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false); $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($name in @('DOTNET_ROOT', 'DOTNET_ROOT_X64')) { $start.EnvironmentVariables[$name] = Join-Path $WorkingDirectory 'no-installed-dotnet' }
    $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $process = [Diagnostics.Process]::Start($start)
    $errors = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    if (-not $process.WaitForExit(120000)) { $process.Kill(); throw "Timed out: $Program $($Arguments -join ' ')" }
    if ($process.ExitCode -ne 0 -or -not $output.Contains($Expected)) {
        throw "Release check failed: $Program $($Arguments -join ' ') (exit $($process.ExitCode))`n$output`n$($errors.Result)"
    }
    Write-Host "PASS $([IO.Path]::GetFileName($Program)) $($Arguments -join ' ')"
}

Push-Location $repository
try {
    Write-Host '[1/5] Runtime packs'
    if (-not $SkipRuntime) { & (Join-Path $PSScriptRoot 'build-runtime.ps1') -Targets $Targets }
    foreach ($target in $Targets) {
        if (-not (Test-Path -LiteralPath (Join-Path $repository ".native-build/install/$target"))) { throw "The $target runtime pack is missing." }
    }

    Write-Host '[2/5] Compiler build and tests (Release)'
    Invoke-Checked $Dotnet @('build', 'AquariusLang.sln', '-c', 'Release', '--nologo')
    if (-not $SkipTests) { Invoke-Checked $Dotnet @('test', 'compiler/AquariusTests', '-c', 'Release', '--no-build', '--nologo') }

    Write-Host '[3/5] Publishing dist/aqua'
    $package = Join-Path $repository 'dist/aqua'
    & (Join-Path $PSScriptRoot 'publish-cli.ps1') -Dotnet $Dotnet -Runtime 'win-x64' -Output $package -Packs $Targets

    Write-Host '[4/5] Checking the distribution without an installed .NET'
    $smoke = Join-Path $repository "dist/.release-check-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $smoke | Out-Null
    try {
        $aqua = Join-Path $package 'aqua.exe'
        [IO.File]::WriteAllText((Join-Path $smoke 'main.aqua'), "var tools = import(""tools.aqua"");`nprint(tools.add(40, 2), isWeb());`n", [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $smoke 'tools.aqua'), "var add = function(a, b) { return a + b; };`n", [Text.UTF8Encoding]::new($false))
        Invoke-Published $aqua @('--help') 'aqua build' $smoke
        Invoke-Published $aqua @('build', 'main.aqua', 'tools.aqua', '--root', '.', '-o', 'app.wasm') 'Compiled 2 script(s)' $smoke
        Invoke-Published $aqua @('run', 'app.wasm') '42' $smoke
        Invoke-Published $aqua @('run', (Join-Path $repository 'examples/starship_expedition/main.aqua')) '' $smoke
        Invoke-Published $aqua @('build', 'app.wasm', '--target', 'windows', '-o', 'standalone/app.exe') 'Executable built' $smoke
        Remove-Item -LiteralPath (Join-Path $smoke 'app.wasm')
        Invoke-Published (Join-Path $smoke 'standalone/app.exe') @() '42' $smoke
        if ($Targets -contains 'web') {
            Invoke-Published $aqua @('build', 'main.aqua', 'tools.aqua', '--root', '.', '--target', 'web', '-o', 'web') 'Website built' $smoke
            foreach ($file in @('index.html', 'app.mjs', 'aquarius.json', 'program.wasm', 'glue/index.mjs', 'runtime/aquarius.wasm')) {
                if (-not (Test-Path -LiteralPath (Join-Path $smoke "web/$file"))) { throw "The website lacks $file." }
            }
        }
    } finally { Remove-Item -LiteralPath $smoke -Recurse -Force -ErrorAction SilentlyContinue }

    Write-Host '[5/5] Archive'
    $releaseName = 'AquariusCompiler-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    $releases = Join-Path $repository 'dist/releases'
    New-Item -ItemType Directory -Path $releases -Force | Out-Null
    $archive = Join-Path $releases "$releaseName.zip"
    Compress-Archive -LiteralPath $package -DestinationPath $archive -CompressionLevel Optimal
    "$(Get-Sha256 $archive)  $releaseName.zip" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Write-Host "Distribution: $package"
    Write-Host "Archive: $archive"
} catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally { Pop-Location }
