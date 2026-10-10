#requires -Version 5.1
param([string]$Dotnet = 'dotnet', [string]$Runtime = 'win-x64', [string]$Output = 'build-targets/win-x64', [string]$VCRuntimeDirectory = '')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repository
$stage = $null
$backup = $null
$committed = $false
try {
    if ($Runtime -ne 'win-x64' -or [Environment]::OSVersion.Platform -ne 'Win32NT') { throw 'The current application runtime pack supports Windows x64 and must be published on Windows.' }
    $bridge = Join-Path $repository "AquariusDesktopVMREPL/runtimes/$Runtime/native/aquarius_graphics.dll"
    if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) { throw 'Build the native graphics bridge with ./native/build.ps1 before publishing the application runtime.' }
    # wgpu-native imports the VC runtime. Bundle the release redistributable DLLs,
    # sourced from Visual Studio's redist directory rather than the system installation.
    if (-not $VCRuntimeDirectory) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
            if ($installation) {
                $redist = Join-Path $installation 'VC/Redist/MSVC'
                $versions = @(Get-ChildItem -LiteralPath $redist -Directory | Where-Object Name -match '^\d+\.' | Sort-Object { [version]$_.Name } -Descending)
                foreach ($version in $versions) {
                    $x64 = Join-Path $version.FullName 'x64'
                    $crt = Get-ChildItem -LiteralPath $x64 -Directory -Filter 'Microsoft.VC*.CRT' -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($crt) { $VCRuntimeDirectory = $crt.FullName; break }
                }
            }
        }
    }
    if (-not $VCRuntimeDirectory -or -not (Test-Path -LiteralPath (Join-Path $VCRuntimeDirectory 'vcruntime140.dll') -PathType Leaf)) {
        throw 'Visual C++ x64 release redistributable DLLs are required. Install VS C++ build tools or pass -VCRuntimeDirectory to its Microsoft.VC*.CRT redist directory.'
    }
    $VCRuntimeDirectory = [IO.Path]::GetFullPath($VCRuntimeDirectory)
    $outputPath = [IO.Path]::GetFullPath($Output).TrimEnd('\', '/')
    $parent = [IO.Path]::GetDirectoryName($outputPath)
    if (-not $parent) { throw 'Runtime pack output must be a named directory.' }
    if (Test-Path -LiteralPath $outputPath) {
        $item = Get-Item -LiteralPath $outputPath
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Runtime pack output must be a directory without links.' }
        foreach ($child in Get-ChildItem -LiteralPath $outputPath -Force) {
            if ($child.PSIsContainer -or $child.Name -notin @('host.exe', 'runtime.json') -or ($child.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Runtime pack output contains unrelated files. Select a dedicated directory.' }
        }
    }
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $stage = Join-Path $parent ('.aquarius-runtime-stage-' + [Guid]::NewGuid().ToString('N'))
    $backup = Join-Path $parent ('.aquarius-runtime-backup-' + [Guid]::NewGuid().ToString('N'))
    $publish = Join-Path $stage 'publish'
    $dotnetLicenseRoot = [IO.Path]::GetDirectoryName((Get-Command $Dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source)
    & $Dotnet publish AquariusAppHost/AquariusAppHost.csproj -c Release -r $Runtime --self-contained true -m:1 -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeAllContentForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:AquariusDotnetLicenseRoot=$dotnetLicenseRoot" "-p:AquariusVCRuntimeDirectory=$VCRuntimeDirectory" -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Application host publishing failed.' }
    $hostPath = Join-Path $publish 'AquariusAppHost.exe'
    if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) { throw 'Published application host is missing.' }
    $files = @(Get-ChildItem -LiteralPath $publish -Recurse -File)
    if ($files.Count -ne 1) { throw 'Application host publish left external files. All dependencies must be bundled.' }
    $start = [Diagnostics.ProcessStartInfo]::new($hostPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Arguments = '--runtime-info'
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            & "$env:SystemRoot/System32/taskkill.exe" /PID $process.Id /T /F | Out-Null
            throw 'Application runtime smoke check timed out.'
        }
        $info = $stdout.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Application runtime smoke check failed: $($stderr.GetAwaiter().GetResult())" }
    } finally { $process.Dispose() }
    $manifest = $info | ConvertFrom-Json
    if ($manifest.runtimeIdentifier -ne $Runtime) { throw 'Published runtime architecture does not match the requested runtime.' }
    $hash = (Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest | Add-Member -NotePropertyName templateSha256 -NotePropertyValue $hash
    Move-Item -LiteralPath $hostPath -Destination (Join-Path $stage 'host.exe')
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'runtime.json') -Encoding UTF8
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $resolvedPublish = [IO.Path]::GetFullPath($publish)
    if (-not $resolvedPublish.StartsWith($resolvedStage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe runtime staging path.' }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
    if (Test-Path -LiteralPath $outputPath) { Move-Item -LiteralPath $outputPath -Destination $backup }
    try { Move-Item -LiteralPath $stage -Destination $outputPath; $committed = $true }
    catch { if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $outputPath }; throw }
    Write-Host "Runtime pack ready: $outputPath"
} finally {
    foreach ($temporary in @($stage, $(if ($committed) { $backup }))) {
        if ($temporary -and (Test-Path -LiteralPath $temporary)) {
            $resolved = [IO.Path]::GetFullPath($temporary)
            if ([IO.Path]::GetDirectoryName($resolved) -ne $parent -or [IO.Path]::GetFileName($resolved) -notmatch '^\.aquarius-runtime-(stage|backup)-[0-9a-f]{32}$') { throw 'Unsafe runtime cleanup path.' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    Pop-Location
}
