#requires -Version 5.1
# Builds, tests and packages the aqua distribution for this host into dist/aqua/ (the layout scripts/publish-cli.ps1
# describes) and the VS Code extension with its bundled language server, then archives both with SHA-256 checksums
# into dist/releases/. Written for PowerShell 7 on any OS (pwsh ./build-release.ps1); Windows PowerShell 5.1 runs it too.
param(
    [string]$Dotnet = 'dotnet',
    [string[]]$Targets = @(),
    [switch]$SkipRuntime,
    [switch]$SkipTests,
    [switch]$Help
)
$ErrorActionPreference = 'Stop'
if ($Help) {
    Write-Host @'
Usage: ./build-release.ps1 [-Targets <host>,web,android-arm64-v8a,android-x86_64] [-SkipRuntime] [-SkipTests] [-Dotnet path]
Installs any missing piece of the pinned toolchain and builds the runtime packs (scripts/build-runtime.ps1; on Windows,
add linux-x64 to build it in WSL), builds and tests the compiler, publishes dist/aqua/ (compiler, runtime/<target>/,
web-glue/, libraries/, sdk/, scripts/, examples/) and checks it without an installed .NET (help, compile and run, a
website and a standalone executable), then packages the VS Code extension with the language server (editors/vscode)
and archives everything into dist/releases/. -SkipRuntime reuses the runtime packs in .native-build/install.
Requires the .NET 8 SDK and Node.js; the Android packs need ANDROID_HOME with the pinned NDK.
'@
    exit 0
}
. (Join-Path $PSScriptRoot 'scripts/toolchain.ps1')
# Windows PowerShell 5.1 runs only on Windows and lacks the $Is* variables of PowerShell 7.
if ($PSVersionTable.PSEdition -eq 'Desktop') { $IsWindows = $true }
$repository = $AquaToolchain.Repository
$hostTarget = $AquaToolchain.Platform
if ($AquaTargets -notcontains $hostTarget) { throw "AquariusLang has no runtime target for this host ($hostTarget) yet." }
$Targets = @($Targets | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if (-not $Targets.Count) { $Targets = @($hostTarget, 'web', 'android-arm64-v8a', 'android-x86_64') }
# The .NET runtime identifier and executable suffix of this host, and the aqua build target of a standalone executable.
$dotnetRuntime = $hostTarget.Replace('windows-', 'win-').Replace('macos-', 'osx-')
$exe = if ($IsWindows) { '.exe' } else { '' }
$standaloneTarget = $hostTarget.Split('-')[0]

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed (exit $LASTEXITCODE): $Program $($Arguments -join ' ')" }
}
function Find-Program([string]$Name) {
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { throw "$Name was not found on PATH." }
    return $command.Source
}
function Write-Checksum([string]$Path) {
    "$((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $Path -Leaf)" |
        Set-Content -LiteralPath "$Path.sha256" -Encoding ascii
}
function New-ProcessStart([string]$Program, [string[]]$Arguments, [string]$WorkingDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new($Program)
    $start.Arguments = (@($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false); $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    return $start
}
# Runs the published aqua (or a program it built) without any installed .NET and checks its output.
function Invoke-Published([string]$Program, [string[]]$Arguments, [string]$Expected, [string]$WorkingDirectory) {
    $start = New-ProcessStart $Program $Arguments $WorkingDirectory
    foreach ($name in @('DOTNET_ROOT', 'DOTNET_ROOT_X64')) { $start.EnvironmentVariables[$name] = Join-Path $WorkingDirectory 'no-installed-dotnet' }
    $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $process = [Diagnostics.Process]::Start($start)
    $errors = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    if (-not $process.WaitForExit(120000)) { $process.Kill(); throw "Timed out: $Program $($Arguments -join ' ')" }
    if ($process.ExitCode -ne 0 -or -not $output.Contains($Expected)) {
        throw "Release check failed: $Program $($Arguments -join ' ') (exit $($process.ExitCode))`n$output`n$($errors.Result)"
    }
    Write-Host "PASS $(Split-Path $Program -Leaf) $($Arguments -join ' ')"
}
# Starts the extension's bundled language server and checks that it answers an LSP initialize request.
function Test-LanguageServer([string]$Server) {
    $start = New-ProcessStart $Dotnet @($Server) (Split-Path $Server)
    $start.RedirectStandardInput = $true
    # The request must start with its header: no UTF-8 byte order mark on the server's input. .NET Framework (Windows
    # PowerShell 5.1) takes the input's encoding from the console.
    $utf8 = [Text.UTF8Encoding]::new($false)
    $consoleEncoding = $null
    if ($start.PSObject.Properties['StandardInputEncoding']) { $start.StandardInputEncoding = $utf8 }
    else { try { $consoleEncoding = [Console]::InputEncoding; [Console]::InputEncoding = $utf8 } catch { $consoleEncoding = $null } }
    try { $process = [Diagnostics.Process]::Start($start) }
    finally { if ($consoleEncoding) { try { [Console]::InputEncoding = $consoleEncoding } catch { } } }
    try {
        $body = [Text.Encoding]::UTF8.GetBytes('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"rootUri":null,"capabilities":{}}}')
        $header = [Text.Encoding]::ASCII.GetBytes("Content-Length: $($body.Length)`r`n`r`n")
        $process.StandardInput.BaseStream.Write($header, 0, $header.Length)
        $process.StandardInput.BaseStream.Write($body, 0, $body.Length)
        $process.StandardInput.BaseStream.Flush()
        $read = $process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(60000) -or $read.Result -notmatch '^Content-Length: (\d+)$') { throw "The language server did not answer initialize: $($read.Result)" }
        $length = [int]$Matches[1]
        while ($process.StandardOutput.ReadLine()) { }   # the rest of the header, up to its blank line
        $buffer = [char[]]::new($length); $count = 0
        while ($count -lt $length) {
            $chunk = $process.StandardOutput.Read($buffer, $count, $length - $count)
            if ($chunk -le 0) { break }
            $count += $chunk
        }
        $response = [string]::new($buffer, 0, $count)
        if (-not $response.Contains('"capabilities"')) { throw "Unexpected initialize response: $response" }
        Write-Host 'PASS language server initialize'
    } finally { if (-not $process.HasExited) { $process.Kill() }; $process.Dispose() }
}

Push-Location $repository
try {
    $buildId = Get-Date -Format 'yyyyMMdd-HHmmss'
    $releases = Join-Path $repository 'dist/releases'

    Write-Host '[1/6] Toolchain and runtime packs'
    if (-not $SkipRuntime) { & (Join-Path $PSScriptRoot 'scripts/build-runtime.ps1') -Targets $Targets }
    foreach ($target in $Targets) {
        if (-not (Test-Path -LiteralPath (Join-Path $AquaToolchain.Native "install/$target"))) { throw "The $target runtime pack is missing." }
    }

    Write-Host '[2/6] Compiler build and tests (Release)'
    Invoke-Checked $Dotnet @('build', 'AquariusLang.sln', '-c', 'Release', '--nologo')
    if (-not $SkipTests) { Invoke-Checked $Dotnet @('test', 'compiler/AquariusTests', '-c', 'Release', '--no-build', '--nologo') }

    Write-Host '[3/6] Publishing dist/aqua'
    $package = Join-Path $repository 'dist/aqua'
    & (Join-Path $PSScriptRoot 'scripts/publish-cli.ps1') -Dotnet $Dotnet -Runtime $dotnetRuntime -Output $package -Packs $Targets

    Write-Host '[4/6] Checking the distribution without an installed .NET'
    $smoke = Join-Path $repository "dist/.release-check-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $smoke | Out-Null
    try {
        $aqua = Join-Path $package "aqua$exe"
        [IO.File]::WriteAllText((Join-Path $smoke 'main.aqua'), "var tools = import(""tools.aqua"");`nprint(tools.add(40, 2), isWeb());`n", [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $smoke 'tools.aqua'), "var add = function(a, b) { return a + b; };`n", [Text.UTF8Encoding]::new($false))
        Invoke-Published $aqua @('--help') 'aqua build' $smoke
        Invoke-Published $aqua @('build', 'main.aqua', 'tools.aqua', '--root', '.', '-o', 'app.wasm') 'Compiled 2 script(s)' $smoke
        Invoke-Published $aqua @('run', 'app.wasm') '42' $smoke
        Invoke-Published $aqua @('run', (Join-Path $repository 'examples/starship_expedition/main.aqua')) '' $smoke
        Invoke-Published $aqua @('build', 'app.wasm', '--target', $standaloneTarget, '-o', "standalone/app$exe") 'Executable built' $smoke
        Remove-Item -LiteralPath (Join-Path $smoke 'app.wasm')
        Invoke-Published (Join-Path $smoke "standalone/app$exe") @() '42' $smoke
        if ($Targets -contains 'web') {
            Invoke-Published $aqua @('build', 'main.aqua', 'tools.aqua', '--root', '.', '--target', 'web', '-o', 'web') 'Website built' $smoke
            foreach ($file in @('index.html', 'app.mjs', 'aquarius.json', 'program.wasm', 'glue/index.mjs', 'runtime/aquarius.wasm')) {
                if (-not (Test-Path -LiteralPath (Join-Path $smoke "web/$file"))) { throw "The website lacks $file." }
            }
        }
    } finally { Remove-Item -LiteralPath $smoke -Recurse -Force -ErrorAction SilentlyContinue }

    Write-Host '[5/6] Language server and VS Code extension'
    # The extension's own scripts publish the language server into editors/vscode/server and bundle the client.
    $extension = Join-Path $repository 'editors/vscode'
    $npm = Find-Program 'npm'
    $env:AQUARIUS_DOTNET = Find-Program $Dotnet
    # Package scripts run node by name through cmd on Windows, which loses a PATH that npm's own entries take past 8191
    # characters: there npm runs with only what the extension's scripts need (node, dotnet, the system directories).
    $fullPath = $env:PATH
    if ($IsWindows) {
        $env:PATH = (@((Split-Path (Find-Program 'node')), (Split-Path $env:AQUARIUS_DOTNET), [Environment]::SystemDirectory, $env:SystemRoot) -join [IO.Path]::PathSeparator)
    }
    Push-Location $extension
    try {
        Invoke-Checked $npm @('ci', '--no-audit', '--no-fund')
        Invoke-Checked $npm @('run', 'check')
        Invoke-Checked $npm @('run', 'package')
    } finally { Pop-Location; $env:PATH = $fullPath }
    $manifest = Get-Content -LiteralPath (Join-Path $extension 'package.json') -Raw | ConvertFrom-Json
    $vsix = Join-Path $extension "$($manifest.name)-$($manifest.version).vsix"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($vsix)
    try {
        $entries = @($zip.Entries | ForEach-Object FullName)
        foreach ($entry in @('extension/package.json', 'extension/dist/extension.js', 'extension/server/AquariusLanguageServer.dll')) {
            if ($entries -notcontains $entry) { throw "The VS Code extension lacks $entry." }
        }
    } finally { $zip.Dispose() }
    Test-LanguageServer (Join-Path $extension 'server/AquariusLanguageServer.dll')

    Write-Host '[6/6] Archive'
    New-Item -ItemType Directory -Path $releases -Force | Out-Null
    $releaseName = "AquariusCompiler-$dotnetRuntime-$buildId"
    if ($IsWindows) {
        $archive = Join-Path $releases "$releaseName.zip"
        Compress-Archive -LiteralPath $package -DestinationPath $archive -CompressionLevel Optimal
    } else {
        # tar keeps the executable bits a .zip made by Compress-Archive would lose.
        $archive = Join-Path $releases "$releaseName.tar.gz"
        Invoke-Checked (Find-Program 'tar') @('-czf', $archive, '-C', (Split-Path $package), (Split-Path $package -Leaf))
    }
    Write-Checksum $archive
    $extensionRelease = Join-Path $releases "$($manifest.name)-$($manifest.version)-$buildId.vsix"
    Copy-Item -LiteralPath $vsix -Destination $extensionRelease
    Write-Checksum $extensionRelease
    Write-Host "Distribution: $package"
    Write-Host "Archive: $archive"
    Write-Host "VS Code extension: $extensionRelease"
} catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally { Pop-Location }
