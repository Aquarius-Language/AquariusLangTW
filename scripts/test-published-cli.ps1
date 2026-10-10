param([string]$Executable = 'dist/aqua/aqua.exe')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executablePath = [IO.Path]::GetFullPath((Join-Path $repository $Executable))
$build = Join-Path $repository '.web-build'
$fixture = Join-Path $build ('published-smoke-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture 'source'
$results = [Collections.Generic.List[object]]::new()
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'AquariusWebCompiler/tests/fixtures/portable') -Destination $source -Recurse
function Invoke-Published([string]$name, [string[]]$arguments, [string]$expected = '', [string]$program = '') {
    if (-not $program) { $program = $executablePath }
    $start = [Diagnostics.ProcessStartInfo]::new($program)
    $start.WorkingDirectory = $fixture
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $start.Environment['DOTNET_ROOT'] = Join-Path $fixture 'no-installed-dotnet'
    $start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.Environment['AQUARIUS_GRAPHICS_FRAMES'] = '2'
    $start.Environment.Remove('AQUARIUS_GRAPHICS_CAPTURE') | Out-Null
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw "Published check timed out: $name" }
        $output = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        $passed = $process.ExitCode -eq 0 -and -not $errorText -and (-not $expected -or $output.Contains($expected))
        $results.Add(@{ name = $name; passed = $passed; output = $output; error = $errorText })
        Write-Host "$(if ($passed) {'PASS'} else {'FAIL'}) published/$name"
        if (-not $passed) { throw "Published check failed: $name`n$output`n$errorText" }
    } finally { $process.Dispose() }
}
try {
    if (-not (Test-Path (Join-Path ([IO.Path]::GetDirectoryName($executablePath)) 'coreclr.dll'))) { throw 'Expected a self-contained Windows release.' }
    Invoke-Published 'help' @('--help') 'aqua build'
    $modules = @(Get-ChildItem -LiteralPath $source -Filter '*.aqua' -Recurse | Sort-Object FullName)
    Invoke-Published 'compile' (@('build') + @($modules.FullName) + @('--root', $source, '--assets', $source, '--entry', 'main.aqua', '-o', 'app.wasm')) 'Compiled'
    $resolvedSource = [IO.Path]::GetFullPath($source)
    if (-not $resolvedSource.StartsWith([IO.Path]::GetFullPath($fixture) + [IO.Path]::DirectorySeparatorChar)) { throw 'Unsafe disposable source path.' }
    Remove-Item -LiteralPath $resolvedSource -Recurse -Force
    Invoke-Published 'source-free run' @('run', 'app.wasm') '[40, 42, 封裝成功'
    Invoke-Published 'source-free web export' @('build', 'app.wasm', '--target', 'web', '-o', 'web') 'Website built'
    if (-not (Test-Path (Join-Path $fixture 'web/vendor-jolt.wasm'))) { throw 'Published web exporter omitted WASM.' }
    Invoke-Published 'standalone windows export' @('build', 'app.wasm', '--target', 'windows', '-o', 'standalone/app.exe') 'Executable built'
    Remove-Item -LiteralPath (Join-Path $fixture 'app.wasm')
    Invoke-Published 'standalone windows execution' @() '[40, 42, 封裝成功' (Join-Path $fixture 'standalone/app.exe')
    $exampleWasm = Join-Path $build 'examples.wasm'
    Invoke-Published 'native compute' @('run', $exampleWasm, '--entry', 'wgpu_compute/main.aqua') '真'
    Invoke-Published 'native image output' @('run', $exampleWasm, '--entry', 'wgpu_triangle/main.aqua') '真'
    Invoke-Published 'native Jolt' @('run', $exampleWasm, '--entry', 'jolt_physics/main.aqua') '真'
    Invoke-Published 'native OpenGL assets' @('run', $exampleWasm, '--entry', 'opengl_cube/main.aqua') 'OpenGL error: 0'
} finally {
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $build 'published-cli-results.json') -Encoding utf8
}
