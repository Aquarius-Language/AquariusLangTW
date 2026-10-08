#requires -Version 5.1
param(
    [string]$Dotnet = 'dotnet',
    [string]$Python = 'python',
    [string]$CMake = '',
    [string]$Generator = '',
    [switch]$VerifyGraphics,
    [switch]$Help
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($Help) {
    Write-Host @'
Usage: build-release.bat [-Dotnet path] [-Python path] [-CMake path] [-Generator name] [-VerifyGraphics]
Builds and tests a self-contained Windows x64 aqua compiler, then creates a ZIP
and SHA-256 checksum in dist/releases/AquariusCompiler-win-x64-<build-id>/.
Requires Windows PowerShell 5.1 or PowerShell 7, .NET 8 SDK, Node.js/npm,
Python, CMake 3.20+ and VS C++ tools. User-installed SDK/Python are detected automatically.
-VerifyGraphics also runs the published WebGPU, OpenGL and Processing examples.
Every run uses a new directory; failed runs retain their log under dist/.package-*.
'@
    exit 0
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Invoke-Checked([string]$program, [string[]]$arguments) {
    & $program @arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed (exit $LASTEXITCODE): $program $($arguments -join ' ')" }
}
function Assert-File([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release file is missing: $path" }
}
function Assert-InDirectory([string]$path, [string]$parent) {
    $resolved = [IO.Path]::GetFullPath($path)
    $prefix = [IO.Path]::GetFullPath($parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe workspace path: $resolved" }
    return $resolved
}
function Resolve-Dotnet {
    $candidates = @($Dotnet)
    if (-not $script:explicitDotnet) {
        $candidates += @((Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'), (Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'))
    }
    foreach ($candidate in $candidates | Select-Object -Unique) {
        $command = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $command) { continue }
        $sdks = & $command.Source --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and ($sdks -match '^8\.0\.')) { return $command.Source }
    }
    throw 'Install the .NET 8 SDK (Runtime alone is insufficient), or pass -Dotnet to its dotnet.exe.'
}
function Resolve-Python {
    $candidates = @($Python)
    if (-not $script:explicitPython) {
        $candidates += @(Get-Command python.exe -All -CommandType Application -ErrorAction SilentlyContinue | ForEach-Object Source)
        foreach ($directory in @((Join-Path $env:USERPROFILE '.pyenv/pyenv-win/versions'), (Join-Path $env:LOCALAPPDATA 'Programs/Python'))) {
            $candidates += @(Get-ChildItem -LiteralPath $directory -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'python.exe' })
        }
    }
    foreach ($candidate in $candidates | Select-Object -Unique) {
        $command = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        # Tests launch python as a native process; a batch shim can lose arguments/output.
        if (-not $command -or [IO.Path]::GetExtension($command.Source) -ne '.exe' -or $command.Source -like '*\WindowsApps\*') { continue }
        try { $version = & $command.Source --version 2>$null } catch { continue }
        if ($LASTEXITCODE -eq 0 -and ($version -match '^Python 3\.')) { return $command.Source }
    }
    throw 'Install Python 3, configure its PATH entry, or pass -Python to a working python.exe.'
}
function ConvertTo-WindowsArgument([string]$argument) {
    # ProcessStartInfo.ArgumentList is unavailable on Windows PowerShell/.NET Framework.
    # Double backslashes preceding quotes and the closing quote per Windows argv rules.
    '"' + [regex]::Replace([regex]::Replace($argument, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
function Get-Sha256([string]$path) {
    # Avoid depending on Get-FileHash module autoload across PowerShell editions.
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($path)
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

$stage = $null
$transcribing = $false
$savedEnvironment = @{}
$explicitDotnet = $PSBoundParameters.ContainsKey('Dotnet')
$explicitPython = $PSBoundParameters.ContainsKey('Python')
Push-Location $repository
try {
    Write-Host '[1/8] Checking build tools...'
    $architecture = $env:PROCESSOR_ARCHITEW6432
    if (-not $architecture) { $architecture = $env:PROCESSOR_ARCHITECTURE }
    if ([Environment]::OSVersion.Platform -ne 'Win32NT' -or $architecture -ne 'AMD64') {
        throw 'This release pipeline must run on Windows x64.'
    }
    $dotnetPath = Resolve-Dotnet
    $pythonPath = Resolve-Python
    Write-Host "Using .NET SDK: $dotnetPath"
    Write-Host "Using Python: $pythonPath"
    foreach ($tool in @('node', 'npm.cmd')) { Get-Command $tool -ErrorAction Stop | Out-Null }
    Invoke-Checked 'node' @('--version')
    Invoke-Checked 'npm.cmd' @('--version')
    Invoke-Checked $pythonPath @('--version')
    if ($CMake) { Get-Command $CMake -CommandType Application -ErrorAction Stop | Out-Null }
    # native/build.ps1 discovers the cached CMake/VS generator when no override is supplied.
    $dist = Assert-InDirectory (Join-Path $repository 'dist') $repository
    if (Test-Path -LiteralPath $dist) {
        if ((Get-Item -LiteralPath $dist).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'dist must not be a directory link.' }
    }
    $buildId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $releaseName = "AquariusCompiler-win-x64-$buildId"
    $stage = Assert-InDirectory (Join-Path $dist ".package-$buildId") $dist
    $package = Join-Path $stage 'aqua'
    $reports = Join-Path $stage 'reports'
    New-Item -ItemType Directory -Path $reports -Force | Out-Null
    Start-Transcript -LiteralPath (Join-Path $stage 'build.log') | Out-Null
    $transcribing = $true
    # Ensure spawned .NET test processes use the selected SDK's runtime as well.
    foreach ($name in @('PATH', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_HOST_PATH', 'AQUARIUS_OPENGL_TESTS', 'AQUARIUS_WGPU_TESTS')) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    $env:PATH = [IO.Path]::GetDirectoryName($dotnetPath) + [IO.Path]::PathSeparator + [IO.Path]::GetDirectoryName($pythonPath) + [IO.Path]::PathSeparator + $env:PATH
    $env:DOTNET_ROOT = [IO.Path]::GetDirectoryName($dotnetPath)
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_HOST_PATH = $dotnetPath
    Remove-Item Env:AQUARIUS_OPENGL_TESTS, Env:AQUARIUS_WGPU_TESTS -ErrorAction SilentlyContinue

    Write-Host '[2/8] Restoring pinned browser dependencies and preparing embedded resources...'
    Invoke-Checked 'npm.cmd' @('ci', '--prefix', 'AquariusWebCompiler')
    Invoke-Checked 'npm.cmd' @('run', '--prefix', 'AquariusWebCompiler', 'prepare:browser')
    Assert-File (Join-Path $repository 'AquariusWebCompiler/browser/vendor-jolt.wasm')

    Write-Host '[3/8] Building and installing the native graphics bridge...'
    $nativeOptions = @{ Runtime = 'win-x64' }
    if ($CMake) { $nativeOptions.CMake = $CMake }
    if ($Generator) { $nativeOptions.Generator = $Generator }
    & (Join-Path $repository 'native/build.ps1') @nativeOptions
    Assert-File (Join-Path $repository 'AquariusDesktopVMREPL/runtimes/win-x64/native/aquarius_graphics.dll')

    Write-Host '[4/8] Restoring, building and testing Release...'
    Invoke-Checked $dotnetPath @('restore', 'AquariusLang.sln')
    Invoke-Checked $dotnetPath @('build', 'AquariusLang.sln', '-c', 'Release', '--no-restore', '-m:1', '--nologo')
    Invoke-Checked $dotnetPath @('test', 'AquariusLangVMTesting', '-c', 'Release', '--no-build', '--no-restore', '-m:1', '--logger', 'trx;LogFileName=compiler.trx', '--results-directory', $reports)
    Invoke-Checked 'npm.cmd' @('test', '--prefix', 'AquariusWebCompiler')

    Write-Host '[5/8] Publishing the self-contained compiler...'
    Invoke-Checked $dotnetPath @('publish', 'AquariusCli/AquariusCli.csproj', '-c', 'Release', '-f', 'net8.0', '-r', 'win-x64', '--self-contained', 'true', '-p:UseAppHost=true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $package)
    foreach ($file in @('aqua.exe', 'aqua.dll', 'aqua.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'wgpu_native.dll', 'joltc.dll', 'joltc_double.dll', 'Magick.NET.Core.dll', 'Magick.NET-Q8-AnyCPU.dll', 'Magick.Native-Q8-x64.dll', 'runtimes/win-x64/native/aquarius_graphics.dll', 'examples/increment.aqua', 'licenses/WGPU-NOTICES.md', 'licenses/JOLT-NOTICES.md', 'licenses/MAGICK-NET-LICENSE.txt', 'licenses/MAGICK-NET-NOTICES.txt')) {
        Assert-File (Join-Path $package $file)
    }
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $package
    # Include the .NET distribution notices alongside the bundled runtime.
    $dotnetDirectory = [IO.Path]::GetDirectoryName($dotnetPath)
    Copy-Item -LiteralPath (Join-Path $dotnetDirectory 'LICENSE.txt') -Destination (Join-Path $package 'licenses/DOTNET-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $dotnetDirectory 'ThirdPartyNotices.txt') -Destination (Join-Path $package 'licenses/DOTNET-NOTICES.txt')
    @'
# 星泉正式版編譯器（Windows x64）

本套件包含 .NET 執行階段。請保留整個 aqua 資料夾，在其中開啟 PowerShell：

```powershell
.\aqua.exe --help
.\aqua.exe build .\examples\increment.aqua -o app.bottle
.\aqua.exe run .\app.bottle
.\aqua.exe build .\app.bottle --target web -o web
.\aqua.exe repl
```

編譯多檔專案時列出所有匯入的 .aqua，以 --root 指定根目錄、--assets 封裝資源。
網頁請透過 localhost 或 HTTPS 提供；圖學需要相容的 GPU 與驅動程式。
例如：python -m http.server 8080 --directory web，再開啟 http://localhost:8080。

完整文件：[星泉專案](https://github.com/Aquarius-Language/AquariusLangTW)。
本專案與第三方授權見 LICENSE 及 licenses/。
'@ | Set-Content -LiteralPath (Join-Path $package 'README.md') -Encoding utf8
    foreach ($license in @(
        @{ source = 'native/vendor/GLFW-LICENSE.md'; name = 'GLFW-LICENSE.md' },
        @{ source = 'native/vendor/glad/LICENSE'; name = 'GLAD-LICENSE.txt' },
        @{ source = 'native/vendor/stb/LICENSE'; name = 'STB-LICENSE.txt' }
    )) {
        Copy-Item -LiteralPath (Join-Path $repository $license.source) -Destination (Join-Path $package "licenses/$($license.name)")
    }

    Write-Host '[6/8] Checking the published EXE without installed .NET or original sources...'
    $smoke = Join-Path $stage 'smoke'
    New-Item -ItemType Directory -Path $smoke | Out-Null
    $source = Join-Path $smoke 'source'
    Copy-Item -LiteralPath (Join-Path $repository 'AquariusWebCompiler/tests/fixtures/portable') -Destination $source -Recurse
    $checks = [Collections.Generic.List[object]]::new()
    function Invoke-Published([string]$name, [string[]]$arguments, [string]$expected = '', [string]$capture = '') {
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $package 'aqua.exe'))
        $start.WorkingDirectory = $smoke
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.StandardOutputEncoding = [Text.Encoding]::UTF8
        $start.StandardErrorEncoding = [Text.Encoding]::UTF8
        $start.Arguments = (@($arguments | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join ' ')
        foreach ($nameOfRoot in @('DOTNET_ROOT', 'DOTNET_ROOT_X64')) { $start.EnvironmentVariables[$nameOfRoot] = Join-Path $smoke 'no-installed-dotnet' }
        $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        $start.EnvironmentVariables['AQUARIUS_GRAPHICS_FRAMES'] = '2'
        $start.EnvironmentVariables.Remove('AQUARIUS_GRAPHICS_CAPTURE')
        if ($capture) { $start.EnvironmentVariables['AQUARIUS_GRAPHICS_CAPTURE'] = $capture }
        $process = [Diagnostics.Process]::Start($start)
        try {
            $process.StandardInput.Close()
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(60000)) {
                & "$env:SystemRoot\System32\taskkill.exe" /PID $process.Id /T /F | Out-Null
                throw "Published check timed out: $name"
            }
            $output = $stdout.GetAwaiter().GetResult()
            $errorText = $stderr.GetAwaiter().GetResult()
            $passed = $process.ExitCode -eq 0 -and -not $errorText -and (-not $expected -or $output.Contains($expected))
            if ($capture) { $passed = $passed -and (Test-Path -LiteralPath $capture -PathType Leaf) }
            $checks.Add(@{ name = $name; passed = $passed; output = $output; error = $errorText })
            if (-not $passed) { throw "Published check failed: $name`n$output`n$errorText" }
            Write-Host "PASS published/$name"
        } finally { $process.Dispose() }
    }
    try {
        Invoke-Published 'help' @('--help') 'aqua build'
        @'
變數 images = 匯入("Images");
變數 image = images.Create(1,1,[255,0,0,255]);
變數 formats = ["Png","Jpeg","Bmp","Gif","Tiff"];
迴圈 (變數 i = 0; i < 5; i++) {
    變數 bytes = images.Encode(image,formats[i],{});
    變數 decoded = images.Decode(bytes,0);
    變數 pixels = decoded.Pixels();
    如果 (decoded.width != 1 || decoded.height != 1 || pixels[3] != 255) { images.Decode([],0); }
}
印出("Image codecs OK");
'@ | Set-Content -LiteralPath (Join-Path $smoke 'images.aqua') -Encoding utf8
        Invoke-Published 'compile image codecs' @('build', 'images.aqua', '-o', 'images.bottle') 'Compiled'
        $imageSource = Assert-InDirectory (Join-Path $smoke 'images.aqua') $smoke
        Remove-Item -LiteralPath $imageSource
        Invoke-Published 'native image codecs' @('run', 'images.bottle') 'Image codecs OK'
        $modules = @(Get-ChildItem -LiteralPath $source -Filter '*.aqua' -Recurse | Sort-Object FullName)
        Invoke-Published 'compile' (@('build') + @($modules.FullName) + @('--root', $source, '--assets', $source, '--entry', 'main.aqua', '-o', 'app.bottle')) 'Compiled'
        $source = Assert-InDirectory $source $smoke
        Remove-Item -LiteralPath $source -Recurse -Force
        Invoke-Published 'source-free run' @('run', 'app.bottle') '[40, 42, 封裝成功'
        Invoke-Published 'source-free web export' @('build', 'app.bottle', '--target', 'web', '-o', 'web') 'Website built'
        foreach ($file in @('index.html', 'program.json', 'vendor-jolt.wasm', 'vendor-jolt.mjs', 'vendor-jolt.LICENSE.txt', 'vendor-earcut.LICENSE.txt', 'vendor-gl-matrix.LICENSE.txt')) { Assert-File (Join-Path $smoke "web/$file") }
        $examples = Join-Path $package 'examples'
        $exampleSources = @(Get-ChildItem -LiteralPath $examples -Filter '*.aqua' -Recurse | Sort-Object FullName)
        Invoke-Published 'compile examples' (@('build') + @($exampleSources.FullName) + @('--root', $examples, '--assets', $examples, '--entry', 'increment.aqua', '-o', 'examples.bottle')) 'Compiled'
        Invoke-Published 'native Jolt' @('run', 'examples.bottle', '--entry', 'jolt_physics/main.rius') '真'
        if ($VerifyGraphics) {
            Invoke-Published 'native compute' @('run', 'examples.bottle', '--entry', 'wgpu_compute/main.rius') '真'
            Invoke-Published 'native image' @('run', 'examples.bottle', '--entry', 'wgpu_triangle/main.rius') '真'
            Invoke-Published 'native OpenGL' @('run', 'examples.bottle', '--entry', 'opengl_cube/main.rius') 'OpenGL error: 0' (Join-Path $reports 'opengl.ppm')
            Invoke-Published 'Processing' @('run', 'examples.bottle', '--entry', 'processing_showcase/main.rius') '' (Join-Path $reports 'processing.png')
        }
    } finally {
        $checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $reports 'published-cli.json') -Encoding utf8
    }
    $smoke = Assert-InDirectory $smoke $stage
    Remove-Item -LiteralPath $smoke -Recurse -Force

    Write-Host '[7/8] Creating ZIP and SHA-256 checksum...'
    $archive = Join-Path $stage "$releaseName.zip"
    Compress-Archive -LiteralPath $package -DestinationPath $archive -CompressionLevel Optimal
    $hash = Get-Sha256 $archive
    "$hash  $releaseName.zip" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Write-Host '[8/8] Completing release...'
    Stop-Transcript | Out-Null
    $transcribing = $false
    $releases = Assert-InDirectory (Join-Path $dist 'releases') $dist
    if ((Test-Path -LiteralPath $releases) -and ((Get-Item -LiteralPath $releases).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'dist/releases must not be a directory link.' }
    New-Item -ItemType Directory -Path $releases -Force | Out-Null
    $destination = Assert-InDirectory (Join-Path $releases $releaseName) $releases
    $stage = Assert-InDirectory $stage $dist
    if (Test-Path -LiteralPath $destination) { throw "Release already exists: $destination" }
    Move-Item -LiteralPath $stage -Destination $destination
    Write-Host "Release ready: $destination"
    Write-Host "Compiler: $(Join-Path $destination 'aqua/aqua.exe')"
    Write-Host "Archive: $(Join-Path $destination "$releaseName.zip")"
} catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    if ($stage) { Write-Host "Incomplete build and log retained at: $stage" }
    exit 1
} finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    Pop-Location
}
