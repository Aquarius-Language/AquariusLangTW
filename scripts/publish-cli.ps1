# -OnnxCuda additionally publishes the build-targets/win-x64-cuda runtime pack and builds aqua itself with
# ONNX Runtime's CUDA flavour, so 'aqua run' and [onnx] cuda = true projects can use NVIDIA GPUs.
param([string]$Dotnet = 'dotnet', [string]$Runtime = 'win-x64', [string]$Output = 'dist/aqua', [switch]$OnnxCuda)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repository
try {
    & npm.cmd ci --prefix AquariusWebCompiler
    if ($LASTEXITCODE -ne 0) { throw 'Browser dependencies could not be installed.' }
    & npm.cmd run --prefix AquariusWebCompiler prepare:browser
    if ($LASTEXITCODE -ne 0) { throw 'Browser resources could not be prepared.' }
    if ($OnnxCuda) { & (Join-Path $PSScriptRoot 'publish-apphost.ps1') -Dotnet $Dotnet -OnnxCuda }
    & (Join-Path $PSScriptRoot 'publish-apphost.ps1') -Dotnet $Dotnet
    $onnxCudaProperty = if ($OnnxCuda) { 'true' } else { 'false' }
    & $Dotnet publish AquariusCli -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:AquariusOnnxCuda=$onnxCudaProperty" -o $Output
    if ($LASTEXITCODE -ne 0) { throw 'CLI publishing failed.' }
    $executable = Join-Path $Output $(if ($Runtime.StartsWith('win-')) { 'aqua.exe' } else { 'aqua' })
    & $executable --help
    if ($LASTEXITCODE -ne 0) { throw 'Published CLI smoke check failed.' }
} finally { Pop-Location }
