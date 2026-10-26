# Packages an application .wasm with an Android runtime build into a debug-signed APK and, with -Run, runs it on the
# connected device or emulator: the program's output (logcat tag Aquarius) is printed and the exit status mirrors the
# runtime's AQUARIUS_ANDROID_OK / AQUARIUS_ANDROID_ERROR markers. Test helper for runtime and library work; exported
# applications are packaged by the compiler's Android build target.
#   scripts/android-run.ps1 app.wasm -Runtime .native-build/install/android-x86_64 [-Libraries a.so,b.so] [-Frames 3] -Run
param(
    [Parameter(Mandatory)][string]$Program,
    [Parameter(Mandatory)][string]$Runtime,                     # a runtime build or install folder for one ABI
    [ValidateSet('x86_64', 'arm64-v8a')][string]$Abi = 'x86_64',
    [string[]]$Libraries = @(),                                 # extra native libraries for lib/<abi>/
    [string]$Assets = '',                                       # a folder copied to assets/aquarius/assets/
    [string]$Package = 'org.aquariuslang.test.run',
    [string]$Label = 'Aquarius test',
    [string[]]$Permissions = @('android.permission.INTERNET'),
    [string]$Output = '',
    [int]$Frames = 0,
    [string[]]$Grant = @(),                                     # runtime permissions granted after installing (tests)
    [int]$TimeoutSeconds = 120,
    [switch]$Run,
    [switch]$Keep                                               # leave the application installed
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'toolchain.ps1')
# Comma-separated values arrive as one string through powershell -File.
$Libraries = @($Libraries | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Permissions = @($Permissions | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Grant = @($Grant | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
$tools = Join-Path $sdk "build-tools/$($AquaPins.androidSdk.buildTools)"
$androidJar = Join-Path $sdk "platforms/android-$($AquaPins.androidSdk.platform)/android.jar"
$adb = Join-Path $sdk 'platform-tools/adb.exe'
$env:JAVA_HOME = $AquaToolchain.Jdk
foreach ($path in @($tools, $androidJar, (Join-Path $AquaToolchain.Jdk 'bin'))) {
    if (-not (Test-Path -LiteralPath $path)) { throw "$path is missing (install-toolchain.ps1 -Targets android-x86_64 and sdkmanager)." }
}
$Runtime = (Resolve-Path -LiteralPath $Runtime).Path
foreach ($name in @('libaquarius.so', 'classes.dex')) {
    if (-not (Test-Path -LiteralPath (Join-Path $Runtime $name))) { throw "$name is missing from $Runtime." }
}
$manifestTemplate = Join-Path $Runtime 'AndroidManifest.xml'
if (-not (Test-Path -LiteralPath $manifestTemplate)) { $manifestTemplate = Join-Path $AquaToolchain.Repository 'runtime/platform/android/AndroidManifest.xml' }

$work = Join-Path $AquaToolchain.Native "android-run/$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
try {
    # Manifest and the resource table (aapt2 link) form the base APK.
    $permissionLines = ($Permissions | ForEach-Object { "    <uses-permission android:name=`"$_`" />" }) -join "`n"
    $manifest = (Get-Content -LiteralPath $manifestTemplate -Raw).Replace('@PACKAGE@', $Package).Replace('@LABEL@', [Security.SecurityElement]::Escape($Label)).
        Replace('@VERSION_CODE@', '1').Replace('@VERSION_NAME@', '1.0').Replace('@PERMISSIONS@', $permissionLines)
    [IO.File]::WriteAllText((Join-Path $work 'AndroidManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))
    $unsigned = Join-Path $work 'unsigned.apk'
    & (Join-Path $tools 'aapt2.exe') link -o $unsigned --manifest (Join-Path $work 'AndroidManifest.xml') -I $androidJar
    if ($LASTEXITCODE -ne 0) { throw 'aapt2 link failed.' }

    # Code, native libraries and the program, stored uncompressed (the JDK's jar tool: zip method "stored"), so the
    # runtime maps libaquarius.so straight from the APK once zipalign page-aligns it.
    $stage = Join-Path $work 'stage'
    function Add-File([string]$File, [string]$Name) {
        $target = Join-Path $stage $Name
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $File -Destination $target
    }
    Add-File (Join-Path $Runtime 'classes.dex') 'classes.dex'
    # The runtime and the other shared libraries of its pack (ONNX Runtime), as the compiler's Android target ships them.
    foreach ($library in Get-ChildItem -LiteralPath $Runtime -Filter '*.so' -File) { Add-File $library.FullName "lib/$Abi/$($library.Name)" }
    foreach ($library in $Libraries) { Add-File $library "lib/$Abi/$([IO.Path]::GetFileName($library))" }
    Add-File (Resolve-Path -LiteralPath $Program).Path 'assets/aquarius/program.wasm'
    if ($Assets) {
        $root = (Resolve-Path -LiteralPath $Assets).Path
        foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) { Add-File $file.FullName ('assets/aquarius/assets/' + $file.FullName.Substring($root.Length + 1)) }
    }
    & (Join-Path $AquaToolchain.Jdk 'bin/jar.exe') --update --no-compress --no-manifest --file $unsigned -C $stage .
    if ($LASTEXITCODE -ne 0) { throw 'Adding files to the APK failed.' }

    $aligned = Join-Path $work 'aligned.apk'
    & (Join-Path $tools 'zipalign.exe') -P 16 -f 4 $unsigned $aligned
    if ($LASTEXITCODE -ne 0) { throw 'zipalign failed.' }
    $keystore = Join-Path $AquaToolchain.Native 'android/debug.keystore'
    if (-not (Test-Path -LiteralPath $keystore)) {
        New-Item -ItemType Directory -Path (Split-Path $keystore) -Force | Out-Null
        & (Join-Path $AquaToolchain.Jdk 'bin/keytool.exe') -genkeypair -keystore $keystore -alias androiddebugkey -keyalg RSA -keysize 2048 `
            -validity 10000 -dname 'CN=Android Debug,O=Android,C=US' -storepass android -keypass android | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'keytool failed.' }
    }
    $apk = if ($Output) { $Output } else { Join-Path $work 'app.apk' }
    & (Join-Path $tools 'apksigner.bat') sign --ks $keystore --ks-pass pass:android --key-pass pass:android --out $apk $aligned
    if ($LASTEXITCODE -ne 0) { throw 'apksigner failed.' }
    if (-not $Run) { Write-Output $apk; return }

    # Run: install, start with an intent extra for frame-limited graphics, follow the log, uninstall.
    & $adb install -r $apk | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Installing $apk failed." }
    # Install-time permissions (INTERNET) are granted by installing; pm refuses to grant them, which is not an error.
    foreach ($permission in $Grant) { & $adb shell "pm grant $Package $permission 2>/dev/null" | Out-Null }
    try {
        & $adb logcat -c
        & $adb shell am start -W -n "$Package/org.aquariuslang.runtime.AquariusActivity" --ei frames $Frames | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        do {
            Start-Sleep -Milliseconds 500
            $log = @(& $adb logcat -d -v raw -s 'Aquarius:*' 'AndroidRuntime:E' 'DEBUG:F' 'libc:F')
            $text = $log -join "`n"
        } while ($text -notmatch 'AQUARIUS_ANDROID_(OK|ERROR)|FATAL EXCEPTION|Fatal signal' -and [DateTime]::UtcNow -lt $deadline)
        # The program's output only: the markers become the exit status.
        $lines = @($log | Where-Object { $_ -notmatch '^-{9} beginning of' -and $_ -notmatch '^AQUARIUS_(ENGINE|ANDROID_OK|ANDROID_ERROR)' })
        $lines | ForEach-Object { Write-Output $_ }
        if ($text -notmatch 'AQUARIUS_ANDROID_OK') {
            if ($text -notmatch 'AQUARIUS_ANDROID_(OK|ERROR)|FATAL EXCEPTION|Fatal signal') { Write-Error "Timed out after $TimeoutSeconds s." }
            exit 1
        }
    } finally {
        & $adb shell am force-stop $Package | Out-Null
        if (-not $Keep) { & $adb uninstall $Package | Out-Null }
    }
} finally {
    # Packaging only keeps the work folder: it holds the printed APK unless -Output named another place.
    if ($Run) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
