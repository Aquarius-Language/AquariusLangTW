#requires -Version 5.1
# -CUDA：桌面執行檔加入選用的 NVIDIA CUDA／TensorRT 推論提供者（需以 scripts/build-runtime.ps1 -OnnxCuda 建置的 windows-x64-cuda 執行環境套件）。
param([switch]$略過桌面, [switch]$驗證圖形加速, [switch]$Android, [switch]$APK, [switch]$CUDA)
$ErrorActionPreference = 'Stop'
$命令 = Get-Command aqua -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $命令) { throw '找不到 aqua；請將支援姿態推論的星泉編譯器加入 PATH。' }
$編譯器 = $命令.Source
function 執行編譯器([string[]]$參數) {
    # Windows PowerShell 會將原生標準錯誤轉為錯誤紀錄；先收集，再依結束碼判斷。
    $原錯誤處理 = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $輸出 = & $編譯器 @參數 2>&1
        $結束碼 = $LASTEXITCODE
    } finally { $ErrorActionPreference = $原錯誤處理 }
    if ($結束碼 -ne 0 -or ($輸出 -join "`n") -match '(?m)^ERROR:') { $輸出 | ForEach-Object { Write-Host $_ }; throw '星泉建置或推論驗證失敗。' }
    return ($輸出 -join "`n")
}
$原編碼 = [Console]::OutputEncoding
Push-Location $PSScriptRoot
try {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $模型 = Join-Path $PSScriptRoot '模型/姿態.onnx'
    $雜湊 = '9d89c599319a18fb7d2e28451a883476164543182bafca5f09eb2cf767ed2f3f'
    if (-not (Test-Path -LiteralPath $模型)) {
        New-Item -ItemType Directory -Force -Path (Split-Path $模型) | Out-Null
        $版本 = '1f19f821d68288feff2ef5c53993b33da74b1509'
        $暫存 = $模型 + '.下載中'
        $原通訊協定 = [Net.ServicePointManager]::SecurityProtocol
        try {
            [Net.ServicePointManager]::SecurityProtocol = $原通訊協定 -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest "https://media.githubusercontent.com/media/opencv/opencv_zoo/$版本/models/pose_estimation_mediapipe/pose_estimation_mediapipe_2023mar.onnx" -UseBasicParsing -OutFile $暫存
            if ((Get-FileHash -LiteralPath $暫存 -Algorithm SHA256).Hash.ToLowerInvariant() -ne $雜湊) { throw '姿態模型校驗失敗。' }
            Move-Item -LiteralPath $暫存 -Destination $模型
        } finally {
            [Net.ServicePointManager]::SecurityProtocol = $原通訊協定
            if (Test-Path -LiteralPath $暫存) { Remove-Item -LiteralPath $暫存 -Force }
        }
    }
    if ((Get-FileHash -LiteralPath $模型 -Algorithm SHA256).Hash.ToLowerInvariant() -ne $雜湊) { throw '姿態模型校驗失敗。' }
    $人物模型 = Join-Path $PSScriptRoot '模型/人物偵測.onnx'
    $人物雜湊 = '47fd5599d6fa17608f03e0eb0ae230baa6e597d7e8a2c8199fe00abea55a701f'
    if (-not (Test-Path -LiteralPath $人物模型)) {
        $人物版本 = '8e2b658d1007864a24cc2cf0e5b4288c74b32584'
        $人物暫存 = $人物模型 + '.下載中'
        $原通訊協定 = [Net.ServicePointManager]::SecurityProtocol
        try {
            [Net.ServicePointManager]::SecurityProtocol = $原通訊協定 -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest "https://media.githubusercontent.com/media/opencv/opencv_zoo/$人物版本/models/person_detection_mediapipe/person_detection_mediapipe_2023mar.onnx" -UseBasicParsing -OutFile $人物暫存
            if ((Get-FileHash -LiteralPath $人物暫存 -Algorithm SHA256).Hash.ToLowerInvariant() -ne $人物雜湊) { throw '人物偵測模型校驗失敗。' }
            Move-Item -LiteralPath $人物暫存 -Destination $人物模型
        } finally {
            [Net.ServicePointManager]::SecurityProtocol = $原通訊協定
            if (Test-Path -LiteralPath $人物暫存) { Remove-Item -LiteralPath $人物暫存 -Force }
        }
    }
    if ((Get-FileHash -LiteralPath $人物模型 -Algorithm SHA256).Hash.ToLowerInvariant() -ne $人物雜湊) { throw '人物偵測模型校驗失敗。' }
    New-Item -ItemType Directory -Force -Path dist | Out-Null
    $原始碼 = @('星泉姿態觀測站.aqua','中文介面.aqua','姿態模型.aqua','姿態驗證.aqua','圖形加速驗證.aqua')
    執行編譯器 (@('build') + $原始碼 + @('--root','.','--assets','模型','--entry','星泉姿態觀測站.aqua','-o','dist/星泉姿態觀測站.wasm')) | Out-Null
    $驗證 = 執行編譯器 @('run','dist/星泉姿態觀測站.wasm','--entry','姿態驗證.aqua')
    if ($驗證 -notmatch '姿態模型推論驗證通過') { throw '姿態模型推論驗證失敗。' }
    if ($驗證圖形加速) {
        $驗證 = 執行編譯器 @('run','dist/星泉姿態觀測站.wasm','--entry','圖形加速驗證.aqua')
        if ($驗證 -notmatch '圖形加速姿態推論驗證通過') { throw '圖形加速姿態推論驗證失敗。' }
    }
    執行編譯器 @('build','dist/星泉姿態觀測站.wasm','--target','web','-o','web') | Out-Null
    $首頁 = Join-Path $PSScriptRoot 'web/index.html'
    $內容 = [IO.File]::ReadAllText($首頁).Replace('lang="en"','lang="zh-Hant"').Replace('<title>Aquarius WebAssembly</title>','<title>星泉姿態觀測站</title>')
    [IO.File]::WriteAllText($首頁, $內容, [Text.UTF8Encoding]::new($false))
    if (-not $略過桌面) { 執行編譯器 (@('build','dist/星泉姿態觀測站.wasm','--target','windows','-o','dist/星泉姿態觀測站.exe') + $(if ($CUDA) { @('--onnx-cuda','true') } else { @() })) | Out-Null }
    Write-Host '完成：星泉姿態觀測站與網頁版本，姿態模型推論驗證通過。'
    if ($Android -or $APK) {
        $AndroidOutput = if ($APK) { 'dist/android.apk' } else { 'dist/android-project-' + [Guid]::NewGuid().ToString('N').Substring(0,8) }
        執行編譯器 @('build','dist/星泉姿態觀測站.wasm','--target','android','-o',$AndroidOutput) | Out-Null
    }
} finally { [Console]::OutputEncoding = $原編碼; Pop-Location }
