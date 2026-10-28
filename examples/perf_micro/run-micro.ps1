# Builds and runs the micro-benchmarks with one aqua compiler and prints the median ELAPSED_MS of each program.
# Usage: reports/perf-micro/run-micro.ps1 -Aqua path\to\aqua.exe [-Runs 5] [-Names hash_int,sort_aqua]
param([Parameter(Mandatory)][string]$Aqua, [int]$Runs = 5, [string[]]$Names = @())
$ErrorActionPreference = 'Stop'
$Aqua = (Resolve-Path $Aqua).Path
$Names = @($Names | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if (-not $Names.Count) { $Names = @(Get-ChildItem $PSScriptRoot -Filter *.aqua | ForEach-Object BaseName | Sort-Object) }
$work = Join-Path ([IO.Path]::GetTempPath()) "aqua-micro-$PID"; New-Item -ItemType Directory -Force $work | Out-Null
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    foreach ($name in $Names) {
        $wasm = Join-Path $work "$name.wasm"
        & $Aqua build (Join-Path $PSScriptRoot "$name.aqua") -o $wasm | Out-Null
        if ($LASTEXITCODE) { throw "build failed: $name" }
        $times = @(); $result = ''
        for ($i = 0; $i -lt $Runs; $i++) {
            $lines = @(& $Aqua run $wasm)
            if ($LASTEXITCODE) { throw "run failed: $name`n$($lines -join "`n")" }
            $result = ($lines | Where-Object { $_ -like 'RESULT*' } | Select-Object -First 1)
            $times += [double](($lines | Where-Object { $_ -like 'ELAPSED_MS*' } | Select-Object -First 1) -split ' ')[1]
        }
        $sorted = $times | Sort-Object; $median = $sorted[[int][Math]::Floor(($sorted.Count - 1) / 2)]
        '{0,-16} {1,10:F3} ms  {2}' -f $name, $median, $result
    }
} finally { Remove-Item -Recurse -Force $work }
