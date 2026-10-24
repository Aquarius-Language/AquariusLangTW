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
        # Nested TOML projects (Bottle showcases) build on their own; the portable bundle takes the plain sources.
        $nested = @(Get-ChildItem -LiteralPath $sourceRoot -Filter 'aqua.toml' -Recurse | ForEach-Object { $_.DirectoryName + [IO.Path]::DirectorySeparatorChar })
        $sources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.aqua' -Recurse | Where-Object { $file = $_.FullName; -not ($nested | Where-Object { $file.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }) } | Sort-Object FullName)
        $wasm = ".web-build/$($project.name).wasm"
        & $Dotnet AquariusCli/bin/Debug/net8.0/aqua.dll build @($sources.FullName) --root $sourceRoot --assets $sourceRoot --entry $project.entry -o $wasm
        if ($LASTEXITCODE -ne 0) { throw 'Portable wasm compilation failed.' }
        & $Dotnet AquariusCli/bin/Debug/net8.0/aqua.dll build $wasm --target web -o ".web-build/$($project.name)-wasm"
        if ($LASTEXITCODE -ne 0) { throw 'Wasm website compilation failed.' }
    }
} finally { Pop-Location }
