param([string]$Dotnet = 'dotnet', [string]$Configuration = 'Debug', [switch]$SkipBuild, [switch]$RetryFailed, [string[]]$ProjectNames = @())
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
function Invoke-Aqua([string]$assembly, [string[]]$arguments, [string]$capture = '', [string]$workingDirectory = $build) {
    $start = [Diagnostics.ProcessStartInfo]::new($Dotnet)
    $start.WorkingDirectory = $workingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $start.ArgumentList.Add($assembly)
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment['AQUARIUS_GRAPHICS_FRAMES'] = '2'
    if ($arguments.Count -gt 0 -and $arguments[-1] -match 'smoke\.aqua$') { $start.Environment['AQUARIUS_GRAPHICS_FRAMES'] = '12' }
    if ($arguments.Count -gt 0 -and $arguments[-1] -match 'color_mapping[\\/]main\.aqua$') { $start.Environment['AQUARIUS_GRAPHICS_FRAMES'] = '1' }
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
    $desktop = Join-Path $repository "AquariusDesktop/bin/$Configuration/net8.0/AquariusDesktop.dll"
    foreach ($project in @(
        @{ name = 'examples'; source = 'examples'; entry = 'increment.aqua' },
        @{ name = 'desktop-examples'; source = 'AquariusDesktop/examples'; entry = 'increment.aqua' },
        @{ name = 'marble'; source = 'AquariusWebCompiler/tests/fixtures/marble-run'; entry = 'main.aqua' },
        @{ name = 'portable'; source = 'AquariusWebCompiler/tests/fixtures/portable'; entry = 'main.aqua' }
    )) {
        $name = $project.name
        if ($ProjectNames.Count -and $name -notin $ProjectNames) { continue }
        $inputDirectory = Join-Path $build ("example-inputs-" + [Guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath (Join-Path $repository $project.source) -Destination $inputDirectory -Recurse
        # The raw WGPU demo normally waits for an explicit window close. Bound
        # only the disposable test copy so both source and Wasm runs finish.
        $windowDemo = Join-Path $inputDirectory 'wgpu_window/main.aqua'
        if (Test-Path -LiteralPath $windowDemo) {
            $windowSource = [IO.File]::ReadAllText($windowDemo)
            $frameAdvance = '    影格++;'
            if (-not $windowSource.Contains($frameAdvance)) { throw 'WGPU window demo frame marker changed; update its bounded test copy.' }
            $windowSource = $windowSource.Replace($frameAdvance, $frameAdvance + ' 如果 (影格 >= 2) { 窗.Close(); }')
            [IO.File]::WriteAllText($windowDemo, $windowSource, [Text.UTF8Encoding]::new($false))
        }
        # Reuse prepared models in the disposable example copy; no download is needed.
        $models = Join-Path $inputDirectory 'mediapipe/模型'
        if (Test-Path -LiteralPath $models) {
            foreach ($model in @('人物偵測.onnx', '姿態.onnx')) {
                $prepared = Join-Path (Split-Path $repository) "AquariusLangTW_MediaPipeExamples/模型/$model"
                if (-not (Test-Path -LiteralPath (Join-Path $models $model)) -and (Test-Path -LiteralPath $prepared)) {
                    Copy-Item -LiteralPath $prepared -Destination $models
                }
            }
        }
        $wasm = Join-Path $build "$name.wasm"
        $isProject = Test-Path -LiteralPath (Join-Path $inputDirectory 'aqua.toml')
        $sources = @(Get-ChildItem -LiteralPath $inputDirectory -Filter '*.aqua' -Recurse | Where-Object {
            if ($isProject) { return $true }
            # TOML projects have their own dependency graph and platform contract.
            for ($directory = $_.Directory; $directory; $directory = $directory.Parent) {
                if (Test-Path -LiteralPath (Join-Path $directory.FullName 'aqua.toml')) { return $false }
                if ($directory.FullName -eq $inputDirectory) { break }
            }
            return $_.FullName -notmatch '[\\/]android_smoke[\\/]'
        } | Sort-Object FullName)
        $arguments = if ($isProject) { @('build', $inputDirectory, '--platform', 'windows', '-o', $wasm) } else {
            @('build') + @($sources.FullName) + @('--root', $inputDirectory, '--assets', $inputDirectory, '--entry', $project.entry, '-o', $wasm)
        }
        $compiled = Invoke-Aqua $cli $arguments
        if ($compiled.code -ne 0) { throw "Wasm build failed: $($compiled.error)" }
        if ($isProject) {
            $exported = Invoke-Aqua $cli @('build', $inputDirectory, '--target', 'web', '-o', (Join-Path $build "$name-wasm"))
            if ($exported.code -ne 0) { throw "Project web build failed: $($exported.error)" }
        }
        $entries = @($sources | ForEach-Object { [IO.Path]::GetRelativePath($inputDirectory, $_.FullName).Replace('\', '/') })
        # Sources are run in a disposable copy so example output does not modify the repository.
        foreach ($entry in $entries) {
            if ($isProject) { continue } # Project imports require the compiled dependency graph.
            if ($entry -match '^webcam/(main|影格驗證)\.aqua$' -and $env:AQUARIUS_WEBCAM_HARDWARE -ne '1') { Write-Host "略過實體攝影機範例：$entry"; continue }
            if ($RetryFailed -and -not $retryKeys.ContainsKey("$name/source/$entry")) { continue }
            $capture = if ($entry -eq 'opengl_cube/main.aqua') { Join-Path $build "$name-source-opengl.ppm" } elseif ($entry -match '^(processing_|color_mapping/|multilingual_input/)') { Join-Path $build "$name-source-$($entry.Replace('/','-')).png" } else { '' }
            try {
                $run = Invoke-Aqua $desktop @((Join-Path $inputDirectory $entry)) $capture (Split-Path (Join-Path $inputDirectory $entry))
                $expectedError = $entry.StartsWith('generate_errors/')
                $passed = if ($expectedError) { $run.code -eq 1 -and $run.output.Contains('Identifier not found: array') } else { $run.code -eq 0 -and -not $run.error }
                if ($entry -eq 'wgpu_window/main.aqua') { $passed = $passed -and $run.output.Contains('closed after 2 frames') }
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
            if ($entry -match '^webcam/(main|影格驗證)\.aqua$' -and $env:AQUARIUS_WEBCAM_HARDWARE -ne '1') { Write-Host "略過實體攝影機範例：$entry"; continue }
            if ($RetryFailed -and -not $retryKeys.ContainsKey("$name/wasm/$entry")) { continue }
            $capture = if ($entry -eq 'opengl_cube/main.aqua') { Join-Path $build "$name-wasm-opengl.ppm" } elseif ($entry -match '^(processing_|color_mapping/|multilingual_input/)') { Join-Path $build "$name-wasm-$($entry.Replace('/','-')).png" } else { '' }
            try {
                $run = Invoke-Aqua $cli @('run', $wasm, '--entry', $entry) $capture
                $expectedError = $entry.StartsWith('generate_errors/')
                $passed = if ($expectedError) { $run.code -eq 1 -and $run.output.Contains('Identifier not found: array') } else { $run.code -eq 0 -and -not $run.error }
                if ($entry -eq 'wgpu_window/main.aqua') { $passed = $passed -and $run.output.Contains('closed after 2 frames') }
                if ($entry -eq 'smoke.aqua') { $passed = $passed -and $run.output.Contains('整合驗證通過') }
                if ($capture) { $passed = $passed -and (Test-Path -LiteralPath $capture) }
                $results.Add(@{ project = $name; mode = 'wasm'; entry = $entry; passed = $passed; expectedFailure = $expectedError; output = $run.output; error = $run.error })
                Write-Host "$(if ($passed) {'PASS'} else {'FAIL'}) $name/wasm/$entry"
            } catch { $results.Add(@{ project = $name; mode = 'wasm'; entry = $entry; passed = $false; error = $_.Exception.Message }) }
        }
        if (-not $isProject) {
            $exported = Invoke-Aqua $cli @('build', $wasm, '--target', 'web', '-o', (Join-Path $build "$name-wasm"))
            if ($exported.code -ne 0) { throw "Source-free web export failed: $($exported.error)" }
        }
    }
} finally {
    $reportName = if ($ProjectNames.Count -and -not $RetryFailed) { 'desktop-selected-example-results.json' } else { 'desktop-example-results.json' }
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $build $reportName) -Encoding utf8
    Pop-Location
}
$failed = @($results | Where-Object { -not $_.passed })
Write-Host "Desktop example executions: $($results.Count); passed: $($results.Count - $failed.Count); failed: $($failed.Count)"
if ($failed.Count -ne 0) { throw "$($failed.Count) example executions failed; see .web-build/desktop-example-results.json." }
