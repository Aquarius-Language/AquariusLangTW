param(
    [string]$Dotnet = 'dotnet', [string]$Configuration = 'Debug',
    [switch]$BuildApks, [switch]$Emulator, [switch]$DeviceServices, [string]$Adb = 'adb',
    [string[]]$ProjectRoots = @(), [string[]]$CaseNames = @(), [switch]$SkipBuild,
    [string]$ApplicationIdPrefix = 'org.aquariuslang.smoke.p',
    [ValidateRange(1, 600)][int]$ExecutionTimeoutSeconds = 180,
    [switch]$UninstallAfterRun
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repository '.android-build/smoke'
$cli = Join-Path $repository "AquariusCli/bin/$Configuration/net8.0/aqua.dll"
function Invoke-Aqua([string[]]$Arguments) {
    & $Dotnet $cli @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Android compiler smoke failed: $($Arguments -join ' ')" }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (-not $SkipBuild) {
    & $Dotnet build (Join-Path $repository 'AquariusCli') -c $Configuration -m:1 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Compiler build failed.' }
}
& node --test (Join-Path $repository 'AquariusAndroid/tests/*.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Android bridge unit tests failed.' }
$cases = @(
    @{ name='runtime'; root=(Join-Path $repository 'examples/android_smoke'); entry='runtime.aqua' },
    @{ name='language'; root=(Join-Path $repository 'examples/starship_expedition'); entry='main.aqua' },
    @{ name='threading'; root=(Join-Path $repository 'examples/threading'); entry='verification.aqua' },
    @{ name='capabilities'; root=(Join-Path $repository 'examples/android_smoke'); entry='main.aqua' },
    @{ name='processing'; root=(Join-Path $repository 'examples/processing_showcase'); entry='main.aqua' },
    @{ name='processing-wgpu'; root=(Join-Path $repository 'examples/processing_wgpu'); entry='main.aqua' },
    @{ name='graphics'; root=(Join-Path $repository 'examples/android_smoke'); entry='graphics.aqua' },
    @{ name='colors'; root=(Join-Path $repository 'examples/color_mapping'); entry='main.aqua' },
    @{ name='text-input'; root=(Join-Path $repository 'examples/multilingual_input'); entry='main.aqua' },
    @{ name='application'; root=(Join-Path $repository 'examples/application_libraries'); entry='main.aqua' },
    @{ name='physics'; root=(Join-Path $repository 'examples/jolt_physics'); entry='main.aqua' },
    @{ name='triangle'; root=(Join-Path $repository 'examples/wgpu_triangle'); entry='main.aqua' },
    @{ name='compute'; root=(Join-Path $repository 'examples/wgpu_compute'); entry='main.aqua' },
    @{ name='modules'; root=(Join-Path $repository 'examples/using_modules'); entry='main.aqua' },
    @{ name='paths'; root=(Join-Path $repository 'examples/dir_paths'); entry='main.aqua' }
)
if($DeviceServices) {
    $cases += @{ name='camera'; root=(Join-Path $repository 'examples/android_smoke'); entry='camera.aqua' }
    $cases += @{ name='input'; root=(Join-Path $repository 'examples/android_smoke'); entry='input.aqua' }
}
foreach ($root in $ProjectRoots) {
    $resolved = [IO.Path]::GetFullPath($root)
    $cases += @{ name=[IO.Path]::GetFileName($resolved); root=$resolved; entry='' }
}
if($CaseNames.Count) { $cases=@($cases | Where-Object { $_.name -in $CaseNames }) }
if(-not $cases.Count) { throw 'No Android smoke cases matched the requested case names.' }
$javaTested=$false
foreach ($case in $cases) {
    $sources = @(Get-ChildItem -LiteralPath $case.root -Filter '*.aqua' -Recurse | Where-Object { $_.FullName -notmatch '[\\/](web|網頁|dist|bin|obj|\.git)[\\/]' } | Sort-Object FullName)
    if (-not $sources.Count) { throw "No sources: $($case.root)" }
    $entry = $case.entry
    if (-not $entry) {
        $entry = @('main.aqua','主程式.aqua','貪吃蛇.aqua','星泉塗鴉室.aqua','啟動看板.aqua','星泉視覺實驗室.aqua','星泉聲音工坊.aqua') | Where-Object { Test-Path -LiteralPath (Join-Path $case.root $_) } | Select-Object -First 1
    }
    if (-not $entry) { throw "Specify a known application entry in $($case.root)." }
    $wasm = Join-Path $output ($case.name + '.wasm')
    if (Test-Path -LiteralPath (Join-Path $case.root 'aqua.toml')) {
        $arguments = @('build',$case.root,'--platform','android','-o',$wasm)
    } else {
        $arguments = @('build') + @($sources.FullName) + @('--root',$case.root,'--entry',$entry,'-o',$wasm)
        foreach ($name in @('assets','模型','音色')) { $assets=Join-Path $case.root $name; if(Test-Path -LiteralPath $assets) { $arguments+=@('--assets',$assets) } }
    }
    Invoke-Aqua $arguments
    $project = Join-Path $output ($case.name + '-' + [Guid]::NewGuid().ToString('N'))
    Invoke-Aqua @('build',$wasm,'--target','android','-o',$project)
    if ($BuildApks -or $Emulator) {
        if(-not $javaTested) {
            $gradle=if($env:AQUARIUS_GRADLE){$env:AQUARIUS_GRADLE}else{Join-Path $project 'gradlew.bat'}
            & $gradle -p $project --no-daemon --console=plain :app:testDebugUnitTest
            if($LASTEXITCODE -ne 0){throw 'Native Android unit tests failed.'}
            $javaTested=$true
        }
        $apk = Join-Path $output ($case.name + '.apk')
        $id = $ApplicationIdPrefix + ($case.name -replace '[^A-Za-z0-9_]','_').ToLowerInvariant()
        Invoke-Aqua @('build',$wasm,'--target','android','--application-id',$id,'-o',$apk)
        if ($Emulator) {
            & $Adb install -r $apk
            if ($LASTEXITCODE -ne 0) { throw "APK installation failed: $apk" }
            if($case.name -eq 'camera') { & $Adb shell pm grant $id android.permission.CAMERA }
            & $Adb logcat -c
            & $Adb shell am force-stop $id
            $frames=if($case.name -in @('processing','graphics')){3}elseif($case.name -eq 'input'){0}else{1}
            & $Adb shell am start -n "$id/org.aquariuslang.android.AquariusActivity" --ei frames $frames
            $deadline = [DateTime]::UtcNow.AddSeconds($ExecutionTimeoutSeconds)
            $injected=$false
            do {
                Start-Sleep -Milliseconds 500
                $logs = (& $Adb logcat -d -s 'Aquarius:I' 'AndroidRuntime:E' 'DEBUG:F' 'libc:F') -join "`n"
                if ($logs -match 'AQUARIUS_ANDROID_ERROR|FATAL EXCEPTION|Fatal signal|ANDROID_[A-Z_]+FAILED') { throw $logs }
                if($case.name -eq 'input' -and -not $injected -and $logs -match 'ANDROID_INPUT_READY') {
                    & $Adb shell input tap 300 500
                    & $Adb shell input text Aquarius
                    & $Adb shell input keyevent 67
                    & $Adb shell input keyevent 4
                    Start-Sleep -Milliseconds 250
                    & $Adb shell input keyevent 4
                    Start-Sleep -Milliseconds 250
                    & $Adb shell input keyevent 4
                    $injected=$true
                }
            } while ($logs -notmatch 'AQUARIUS_ANDROID_OK' -and [DateTime]::UtcNow -lt $deadline)
            $logs | Set-Content -LiteralPath (Join-Path $output ($case.name + '.log')) -Encoding utf8
            if ($logs -notmatch 'AQUARIUS_ANDROID_OK') { throw "Android execution timed out: $($case.name)" }
            if ($logs -notmatch 'AQUARIUS_ENGINE Wasmtime') { throw 'The Android application did not run with Wasmtime.' }
            if ($case.name -eq 'runtime' -and $logs -notmatch 'ANDROID_WASMTIME_CONTRACT_OK') { throw 'Wasmtime adapter contract was not exercised.' }
            if ($case.name -eq 'threading' -and $logs -notmatch 'THREADING_OK') { throw 'Threading contract was not exercised.' }
            if($case.name -eq 'camera' -and $logs -notmatch 'ANDROID_CAMERA_OK') { throw 'Camera contract was not exercised.' }
            if($case.name -eq 'input') {
                foreach($marker in @('ANDROID_TOUCH_OK','ANDROID_POINTER_OK','ANDROID_WINDOW_POINTER_OK','ANDROID_IME_OK','ANDROID_KEY_OK','ANDROID_BACK_CANCELLED','ANDROID_INPUT_OK')) {
                    if($logs -notmatch $marker){throw "Missing input smoke assertion: $marker"}
                }
            }
            if ($UninstallAfterRun) {
                & $Adb uninstall $id
                if ($LASTEXITCODE -ne 0) { throw "Test APK cleanup failed: $id" }
            }
        }
    }
    Write-Host "PASS Android/$($case.name)"
}
