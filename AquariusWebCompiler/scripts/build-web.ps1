param(
    [string]$MarbleSource = (Join-Path $PSScriptRoot '../tests/fixtures/marble-run'),
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repository
try {
    & npm.cmd ci --prefix AquariusWebCompiler
    if ($LASTEXITCODE -ne 0) { throw 'Browser dependency installation failed.' }
    & npm.cmd run --prefix AquariusWebCompiler prepare:browser
    if ($LASTEXITCODE -ne 0) { throw 'Browser dependency preparation failed.' }
    & $Dotnet build AquariusLang.sln -m:1 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    & $Dotnet AquariusWebCompiler/bin/Debug/net8.0/AquariusWebCompiler.dll examples .web-build/examples
    if ($LASTEXITCODE -ne 0) { throw 'Example website compilation failed.' }
    & $Dotnet AquariusWebCompiler/bin/Debug/net8.0/AquariusWebCompiler.dll $MarbleSource .web-build/marble main.aqua
    if ($LASTEXITCODE -ne 0) { throw 'Marble website compilation failed.' }
    foreach ($project in @(
        @{ name='examples'; source='examples'; entry='increment.aqua' },
        @{ name='marble'; source=$MarbleSource; entry='main.aqua' },
        @{ name='portable'; source='AquariusWebCompiler/tests/fixtures/portable'; entry='main.aqua' },
        @{ name='resize'; source='AquariusWebCompiler/tests/fixtures/resize'; entry='main.aqua' }
    )) {
        $sourceRoot = [IO.Path]::GetFullPath($project.source)
        $sources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.aqua' -Recurse | Sort-Object FullName)
        $bottle = ".web-build/$($project.name).bottle"
        & $Dotnet AquariusCli/bin/Debug/net8.0/aqua.dll build @($sources.FullName) --root $sourceRoot --assets $sourceRoot --entry $project.entry -o $bottle
        if ($LASTEXITCODE -ne 0) { throw 'Portable bottle compilation failed.' }
        & $Dotnet AquariusCli/bin/Debug/net8.0/aqua.dll build $bottle --target web -o ".web-build/$($project.name)-bottle"
        if ($LASTEXITCODE -ne 0) { throw 'Bottle website compilation failed.' }
    }
} finally { Pop-Location }
