param(
    [ValidateSet('desktop','web','android')][string]$Platform = 'web',
    [string]$Device = 'emulator-5554',
    [switch]$Verify
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Push-Location $PSScriptRoot
$backendProcess = $null
try {
    $required = switch ($Platform) { 'web' { 'public/index.html' } 'android' { 'dist/frontend.apk' } default { 'dist/frontend.wasm' } }
    if (-not (Test-Path -LiteralPath $required) -or -not (Test-Path -LiteralPath 'dist/backend.wasm')) {
        & "$PSScriptRoot/Build.ps1" -Platforms @($Platform)
    }
    $compiler = (Get-Command aqua -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $backendFile = Join-Path $PSScriptRoot 'dist/backend.wasm'
    $backendProcess = Start-Process -FilePath $compiler -ArgumentList @('run', ('"' + $backendFile + '"')) -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput "$PSScriptRoot/dist/server.log" -RedirectStandardError "$PSScriptRoot/dist/server-error.log"
    $ready = $false
    for ($attempt=0; $attempt -lt 60; $attempt++) {
        if ($backendProcess.HasExited) { throw "後端未能啟動，請查看 dist/server-error.log。" }
        try { $health=Invoke-RestMethod 'http://127.0.0.1:8787/api/健康' -TimeoutSec 1; if ($health.正常) { $ready=$true; break } } catch {}
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw '後端啟動逾時。' }
    if ($Verify) {
        $verification = & aqua run dist/frontend.wasm --entry 驗證.aqua
        $verification | Write-Output
        if ($LASTEXITCODE -ne 0 -or ($verification -join "`n") -notmatch '網路驗證完成 真') { throw '桌面網路驗證失敗。' }
    } elseif ($Platform -eq 'desktop') {
        & aqua run dist/frontend.wasm
        if ($LASTEXITCODE -ne 0) { throw '桌面前端執行失敗。' }
    } elseif ($Platform -eq 'web') {
        Start-Process 'http://127.0.0.1:8787/'
        Read-Host '按 Enter 停止後端服務' | Out-Null
    } else {
        & adb -s $Device reverse tcp:8787 tcp:8787
        if ($LASTEXITCODE -ne 0) { throw "無法建立裝置 $Device 的連線。" }
        & adb -s $Device install -r dist/frontend.apk
        if ($LASTEXITCODE -ne 0) { throw 'Android 安裝失敗。' }
        & adb -s $Device shell am start -n org.aquariuslang.networklab/org.aquariuslang.android.AquariusActivity
        if ($LASTEXITCODE -ne 0) { throw 'Android 啟動失敗。' }
        Read-Host '按 Enter 停止後端服務' | Out-Null
    }
} finally {
    if ($backendProcess -and -not $backendProcess.HasExited) { Stop-Process -Id $backendProcess.Id }
    Pop-Location
}
