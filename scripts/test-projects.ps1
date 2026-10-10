param(
    [string]$Dotnet = '', [string]$Configuration = 'Debug', [switch]$SkipBuild,
    [string]$Marble = '', [string]$Painter = '', [string]$Snake = '',
    [switch]$WindowsExecutables
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$parent = Split-Path $repository
if (-not $Dotnet) {
    $localDotnet = Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
}
if (-not $Marble) { $Marble = Join-Path $parent 'AquariusLang_MarbleRun_3D' }
if (-not $Painter) { $Painter = Join-Path $parent 'AquariusLang_Painter' }
if (-not $Snake) { $Snake = Join-Path $parent 'AquariusLang_Snake' }
$output = Join-Path $repository '.web-build/projects'
$cli = Join-Path $repository "AquariusCli/bin/$Configuration/net8.0/aqua.dll"
$results = [Collections.Generic.List[object]]::new()
function Invoke-Program([string]$executable, [string[]]$arguments, [int]$frames = 0, [string]$capture = '') {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.WorkingDirectory = $output
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8; $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment['AQUARIUS_GRAPHICS_FRAMES'] = [string]$frames
    $start.Environment['AQUARIUS_BUILD_TARGETS'] = Join-Path $repository 'build-targets'
    $start.Environment.Remove('AQUARIUS_GRAPHICS_CAPTURE') | Out-Null
    if ($capture) {
        if (Test-Path -LiteralPath $capture) { Remove-Item -LiteralPath $capture -Force }
        $start.Environment['AQUARIUS_GRAPHICS_CAPTURE'] = $capture
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Project execution timed out after 60 seconds.' }
        $row = @{ code = $process.ExitCode; output = $stdout.GetAwaiter().GetResult(); error = $stderr.GetAwaiter().GetResult() }
        if ($row.code -ne 0 -or $row.error) { throw ($row | ConvertTo-Json -Compress) }
        if ($capture -and -not (Test-Path -LiteralPath $capture)) { throw "Missing rendered capture: $capture" }
        return $row
    } finally { $process.Dispose() }
}
Push-Location $repository
try {
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    if (-not $SkipBuild) {
        & $Dotnet build AquariusCli -c $Configuration -m:1 --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Compiler build failed.' }
    }
    foreach ($project in @(
        @{ name = 'marble'; root = $Marble; entry = 'main.aqua'; frames = 2; check = 'smoke.aqua'; expected = '整合驗證通過' },
        @{ name = 'painter'; root = $Painter; entry = '星泉塗鴉室.aqua'; frames = 1 },
        @{ name = 'snake'; root = $Snake; entry = '貪吃蛇.aqua'; frames = 2; check = '規則驗證.aqua'; expected = '規則驗證全部通過，共 236 項' }
    )) {
        $sources = @(Get-ChildItem -LiteralPath $project.root -Filter '*.aqua' -Recurse | Where-Object { $_.FullName -notmatch '[\\/](web|dist|\.git)[\\/]' } | Sort-Object FullName)
        if (-not $sources.Count) { throw "No sources found: $($project.root)" }
        $wasm = Join-Path $output "$($project.name).wasm"
        Invoke-Program $Dotnet (@($cli, 'build') + @($sources.FullName) + @('--root', $project.root, '--entry', $project.entry, '-o', $wasm)) | Out-Null
        Invoke-Program $Dotnet @($cli, 'build', $wasm, '--target', 'web', '-o', (Join-Path $output $project.name)) | Out-Null
        $run = Invoke-Program $Dotnet @($cli, 'run', $wasm) $project.frames (Join-Path $output "$($project.name)-desktop.png")
        $results.Add(@{ project = $project.name; mode = 'wasm'; passed = $true; output = $run.output })
        Write-Host "PASS $($project.name)/desktop-wasm"
        if ($project.check) {
            $run = Invoke-Program $Dotnet @($cli, 'run', $wasm, '--entry', $project.check)
            if (-not $run.output.Contains($project.expected)) { throw "Incomplete $($project.name) assertions: $($run.output)" }
            $results.Add(@{ project = $project.name; mode = 'assertions'; passed = $true; output = $run.output })
            Write-Host "PASS $($project.name)/assertions"
        }
        if ($WindowsExecutables) {
            $exe = Join-Path $output "$($project.name).exe"
            Invoke-Program $Dotnet @($cli, 'build', $wasm, '--target', 'windows', '-o', $exe) | Out-Null
            $run = Invoke-Program $exe @() $project.frames (Join-Path $output "$($project.name)-exe.png")
            $results.Add(@{ project = $project.name; mode = 'exe'; passed = $true; output = $run.output })
            Write-Host "PASS $($project.name)/standalone-exe"
        }
    }
} catch {
    $results.Add(@{ passed = $false; error = $_.Exception.Message }); throw
} finally {
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'desktop-results.json') -Encoding utf8
    Pop-Location
}
