param([string]$Dotnet = 'dotnet', [string]$Configuration = 'Debug', [switch]$Hardware)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build = Join-Path $repository '.web-build/webcam'
if ($Dotnet -eq 'dotnet') {
    # 系統搜尋路徑可能只有執行階段；同時檢查使用者目錄中的 SDK。
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    $localDotnet = Join-Path ([System.Environment]::GetFolderPath('UserProfile')) '.dotnet/dotnet.exe'
    $candidates = @($command.Source, $localDotnet) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
    $foundSdk = $false
    foreach ($candidate in $candidates) {
        $sdkList = & $candidate --list-sdks
        if (($sdkList -join "`n") -match '(?m)^(8|9|[1-9][0-9]+)\.') {
            $Dotnet = $candidate; $foundSdk = $true; break
        }
    }
    if (-not $foundSdk) { throw '找不到 .NET 8 或更新版本的 SDK。請安裝 SDK，或使用 -Dotnet 指定執行檔。' }
}
$previousSmoke = $env:AQUARIUS_WEBCAM_SMOKE
$previousWeb = $env:AQUARIUS_WEBCAM_WEB
Push-Location $repository
try {
    $env:AQUARIUS_WEBCAM_SMOKE = '1'
    & $Dotnet test AquariusTests/AquariusTests.csproj -c $Configuration -m:1 -p:UseSharedCompilation=false --nologo --filter 'FullyQualifiedName~AquariusTests.Webcam'
    if ($LASTEXITCODE -ne 0) { throw '攝影機單元測試或桌面虛擬裝置測試失敗。' }
    & node --test AquariusWebCompiler/tests/webcam.test.mjs
    if ($LASTEXITCODE -ne 0) { throw '攝影機網頁單元測試失敗。' }
    & $Dotnet build AquariusCli/AquariusCli.csproj -c $Configuration -m:1 -p:UseSharedCompilation=false --nologo
    if ($LASTEXITCODE -ne 0) { throw '攝影機測試所需的命令列工具建置失敗。' }
    New-Item -ItemType Directory -Path $build -Force | Out-Null
    $cli = Join-Path $repository "AquariusCli/bin/$Configuration/net8.0/aqua.dll"
    $example = Join-Path $repository 'examples/webcam'
    $sources = @(Get-ChildItem -LiteralPath $example -Filter '*.aqua' | ForEach-Object FullName)
    $wasm = Join-Path $build '攝影機.wasm'
    & $Dotnet $cli build @sources --root $example --entry main.aqua -o $wasm
    if ($LASTEXITCODE -ne 0) { throw '攝影機範例封裝失敗。' }
    & $Dotnet $cli run $wasm --entry '冒煙測試.aqua'
    if ($LASTEXITCODE -ne 0) { throw '桌面封裝冒煙測試失敗。' }
    if ($Hardware) {
        $output = & $Dotnet $cli run $wasm --entry '影格驗證.aqua'
        if ($LASTEXITCODE -ne 0 -or -not (($output -join "`n").Contains('攝影機影格驗證全部通過'))) { throw '實體攝影機驗證失敗。' }
        $output | Write-Output
    }
    $web = Join-Path $build 'web'
    & $Dotnet $cli build $wasm --target web -o $web
    if ($LASTEXITCODE -ne 0) { throw '攝影機網頁匯出失敗。' }
    $env:AQUARIUS_WEBCAM_WEB = $web
    & node AquariusWebCompiler/tests/webcam-browser.mjs
    if ($LASTEXITCODE -ne 0) { throw '攝影機瀏覽器冒煙測試失敗。' }
    Write-Host '攝影機單元測試與冒煙測試全部通過。'
} finally {
    $env:AQUARIUS_WEBCAM_SMOKE = $previousSmoke
    $env:AQUARIUS_WEBCAM_WEB = $previousWeb
    Pop-Location
}
