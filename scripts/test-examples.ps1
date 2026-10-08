param([string]$Dotnet = 'dotnet', [string]$Configuration = 'Debug', [switch]$SkipBuild, [switch]$RetryFailed)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build = Join-Path $repository '.web-build'
$results = [Collections.Generic.List[object]]::new()
$retryKeys = @{}
if ($RetryFailed) {
    $previous = Get-Content -LiteralPath (Join-Path $build 'desktop-example-results.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($row in $previous) {
        if ($row.passed) { $results.Add($row) }
        else { $retryKeys["$($row.project)/$($row.mode)/$($row.entry)"] = $true }
    }
}
function Invoke-Aqua([string]$assembly, [string[]]$arguments, [string]$capture = '') {
    $start = [Diagnostics.ProcessStartInfo]::new($Dotnet)
    $start.WorkingDirectory = $build
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $start.ArgumentList.Add($assembly)
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment['AQUARIUS_GRAPHICS_FRAMES'] = '2'
    if ($arguments.Count -gt 0 -and $arguments[-1] -match 'smoke\.(aqua|rius)$') { $start.Environment['AQUARIUS_GRAPHICS_FRAMES'] = '12' }
    if ($arguments.Count -gt 0 -and $arguments[-1] -match 'color_mapping[\\/]main\.(aqua|rius)$') { $start.Environment['AQUARIUS_GRAPHICS_FRAMES'] = '1' }
    $start.Environment.Remove('AQUARIUS_GRAPHICS_CAPTURE') | Out-Null
    if ($capture) { $start.Environment['AQUARIUS_GRAPHICS_CAPTURE'] = $capture }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Example timed out after 60 seconds.' }
        return @{ code = $process.ExitCode; output = $stdout.GetAwaiter().GetResult(); error = $stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}
Push-Location $repository
try {
    New-Item -ItemType Directory -Path $build -Force | Out-Null
    if (-not $SkipBuild) {
        & $Dotnet build AquariusLang.sln -c $Configuration -m:1 --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    }
    $cli = Join-Path $repository "AquariusCli/bin/$Configuration/net8.0/aqua.dll"
    $desktop = Join-Path $repository "AquariusDesktopVMREPL/bin/$Configuration/net8.0/AquariusDesktopVMREPL.dll"
    foreach ($project in @(
        @{ name = 'examples'; source = 'examples'; entry = 'increment.aqua' },
        @{ name = 'desktop-examples'; source = 'AquariusDesktopVMREPL/examples'; entry = 'increment.aqua' },
        @{ name = 'marble'; source = 'AquariusWebCompiler/tests/fixtures/marble-run'; entry = 'main.aqua' },
        @{ name = 'portable'; source = 'AquariusWebCompiler/tests/fixtures/portable'; entry = 'main.aqua' }
    )) {
        $name = $project.name
        $inputDirectory = Join-Path $build ("example-inputs-" + [Guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath (Join-Path $repository $project.source) -Destination $inputDirectory -Recurse
        $bottle = Join-Path $build "$name.bottle"
        $sources = @(Get-ChildItem -LiteralPath $inputDirectory -Filter '*.aqua' -Recurse | Sort-Object FullName)
        $arguments = @('build') + @($sources.FullName) + @('--root', $inputDirectory, '--assets', $inputDirectory, '--entry', $project.entry, '-o', $bottle)
        $compiled = Invoke-Aqua $cli $arguments
        if ($compiled.code -ne 0) { throw "Bottle build failed: $($compiled.error)" }
        $entries = @($sources | ForEach-Object { [IO.Path]::GetRelativePath($inputDirectory, $_.FullName).Replace('\', '/') })
        # Sources are run in a disposable copy so example output does not modify the repository.
        foreach ($entry in $entries) {
            if ($RetryFailed -and -not $retryKeys.ContainsKey("$name/source/$entry")) { continue }
            $capture = if ($entry -eq 'opengl_cube/main.aqua') { Join-Path $build "$name-source-opengl.ppm" } elseif ($entry -match '^(processing_|color_mapping/|multilingual_input/)') { Join-Path $build "$name-source-$($entry.Replace('/','-')).png" } else { '' }
            try {
                $run = Invoke-Aqua $desktop @((Join-Path $inputDirectory $entry)) $capture
                $expectedError = $entry.StartsWith('generate_errors/')
                $passed = if ($expectedError) { $run.code -eq 1 -and $run.output.Contains('Identifier not found: array') } else { $run.code -eq 0 -and -not $run.error }
                if ($capture) { $passed = $passed -and (Test-Path -LiteralPath $capture) }
                $results.Add(@{ project = $name; mode = 'source'; entry = $entry; passed = $passed; expectedFailure = $expectedError; output = $run.output; error = $run.error })
                Write-Host "$(if ($passed) {'PASS'} else {'FAIL'}) $name/source/$entry"
            } catch { $results.Add(@{ project = $name; mode = 'source'; entry = $entry; passed = $false; error = $_.Exception.Message }) }
        }
        # Resolve and check the exact absolute target before deleting only our disposable source copy.
        $resolvedInput = [IO.Path]::GetFullPath($inputDirectory)
        if (-not $resolvedInput.StartsWith([IO.Path]::GetFullPath($build) + [IO.Path]::DirectorySeparatorChar) -or -not ([IO.Path]::GetFileName($resolvedInput)).StartsWith('example-inputs-')) { throw 'Unsafe temporary input path.' }
        Remove-Item -LiteralPath $resolvedInput -Recurse -Force
        foreach ($entry in $entries) {
            if ($RetryFailed -and -not $retryKeys.ContainsKey("$name/bottle/$entry")) { continue }
            $capture = if ($entry -eq 'opengl_cube/main.aqua') { Join-Path $build "$name-bottle-opengl.ppm" } elseif ($entry -match '^(processing_|color_mapping/|multilingual_input/)') { Join-Path $build "$name-bottle-$($entry.Replace('/','-')).png" } else { '' }
            try {
                $run = Invoke-Aqua $cli @('run', $bottle, '--entry', $entry.Replace('.aqua','.rius')) $capture
                $expectedError = $entry.StartsWith('generate_errors/')
                $passed = if ($expectedError) { $run.code -eq 1 -and $run.output.Contains('Identifier not found: array') } else { $run.code -eq 0 -and -not $run.error }
                if ($entry -eq 'smoke.aqua') { $passed = $passed -and $run.output.Contains('整合驗證通過') }
                if ($capture) { $passed = $passed -and (Test-Path -LiteralPath $capture) }
                $results.Add(@{ project = $name; mode = 'bottle'; entry = $entry; passed = $passed; expectedFailure = $expectedError; output = $run.output; error = $run.error })
                Write-Host "$(if ($passed) {'PASS'} else {'FAIL'}) $name/bottle/$entry"
            } catch { $results.Add(@{ project = $name; mode = 'bottle'; entry = $entry; passed = $false; error = $_.Exception.Message }) }
        }
        $exported = Invoke-Aqua $cli @('build', $bottle, '--target', 'web', '-o', (Join-Path $build "$name-bottle"))
        if ($exported.code -ne 0) { throw "Source-free web export failed: $($exported.error)" }
    }
} finally {
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $build 'desktop-example-results.json') -Encoding utf8
    Pop-Location
}
$failed = @($results | Where-Object { -not $_.passed })
Write-Host "Desktop example executions: $($results.Count); passed: $($results.Count - $failed.Count); failed: $($failed.Count)"
if ($failed.Count -ne 0) { throw "$($failed.Count) example executions failed; see .web-build/desktop-example-results.json." }
