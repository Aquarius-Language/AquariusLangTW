# Tests the native library outputs (docs/native-libraries.md) on this host's desktop target, the web and, with -Android,
# Android: builds the test libraries of tests/exports (geometry, counter, services, the [library] shapes) with the
# repository's compiler and checks
#   - the consumers print geometry.expected: C and C++ (built with the pinned Clang through the SDK toolchain file, as
#     shared, static and, on ELF, object libraries), Rust (the pinned Rust), Java on the desktop JVM (JNI) and
#     JavaScript in Chromium (loadLibrary()); aqua run of geometry/parity.aqua prints its portable lines;
#   - a binary audit: each library exports exactly its header's functions, needs only system libraries, has its SONAME
#     (Linux) or import libraries (Windows); static and object outputs define only the exports globally;
#   - two libraries in one process (shared, static, object), 1000 starts and stops, 8 calling threads, (POSIX) a
#     reentrant call refused, default libraries inside a library, and --kind executable of an exported project;
#   - on Linux, the C and C++ consumers again under AddressSanitizer, LeakSanitizer and UndefinedBehaviorSanitizer;
#   - with -Android, on the connected device or emulator: the Java and Kotlin consumers (dexed, run with app_process),
#     and an audit of the .so (exports, dependencies, 16 KB pages) and the .aar;
#   - on a Mac (not verified yet: written before a Mac could run it): the Swift consumer, the install names and ad hoc
#     signatures of the dylib and of a framework package, and iOS: the xcframeworks of the device and simulator slices
#     (Info.plist, install names, platforms, exports) and the Swift, C, C++ and Rust consumers on an iOS simulator.
#   scripts/test-exports.ps1 [-Android]           # this host's target (windows-x64 here; linux-x64 in WSL or on Linux;
#                                                 # macos-arm64 and iOS on a Mac)
# Runs under Windows PowerShell 5.1 or PowerShell 7 (pwsh). Needs the runtime packs of this target, the web and (with
# -Android) Android (scripts/build-runtime.ps1), a built compiler (dotnet build compiler/AquariusCli), Node with
# Playwright (npm install in scripts/), and installs the test-only Rust, Kotlin and JDK pins when missing.
# build-release.ps1 runs it.
param(
    [string]$Compiler = '',
    [string]$Dotnet = 'dotnet',
    [string]$Work = '',
    [switch]$Android
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'toolchain.ps1')
Assert-AquaToolchain
$repository = $AquaToolchain.Repository
$target = Get-AquaTarget $AquaToolchain.Platform
$tests = Join-Path $repository 'tests/exports'
if (-not $Work) { $Work = Join-Path ([IO.Path]::GetTempPath()) ("aquarius-test-exports-" + [Guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
if (-not $Compiler) {
    $Compiler = @('Release', 'Debug') | ForEach-Object { $c = $_; @('', 'linux/') | ForEach-Object { Join-Path $repository "compiler/AquariusCli/bin/$_$c/net8.0/aqua.dll" } } |
        Where-Object { Test-Path -LiteralPath $_ } | Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } -Descending | Select-Object -First 1
    if (-not $Compiler) { throw 'Build the compiler first (dotnet build compiler/AquariusCli -c Release) or pass -Compiler aqua.dll.' }
}

$failures = [Collections.Generic.List[string]]::new()
function Pass([string]$Check) { Write-Host "PASS $Check" }
function Fail([string]$Check, [string]$Detail) { Write-Host "FAIL $Check`n$Detail"; $failures.Add($Check) }
# Runs a program; its output with LF line endings and its exit code.
function Invoke-Program([string]$Program, [string[]]$Arguments = @(), [hashtable]$Environment = @{}) {
    $start = [Diagnostics.ProcessStartInfo]::new($Program)
    $start.Arguments = ($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ } }) -join ' '
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false); $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $process = [Diagnostics.Process]::Start($start)
    $errors = $process.StandardError.ReadToEndAsync(); $output = $process.StandardOutput.ReadToEndAsync()
    if (-not $process.WaitForExit(600000)) { $process.Kill(); return [pscustomobject]@{ Code = -1; Output = $output.Result; Error = 'timed out' } }
    $process.WaitForExit()
    [pscustomobject]@{ Code = $process.ExitCode; Output = $output.Result.Replace("`r`n", "`n"); Error = $errors.Result }
}
function Invoke-Checked([string]$Program, [string[]]$Arguments, [hashtable]$Environment = @{}) {
    $run = Invoke-Program $Program $Arguments $Environment
    if ($run.Code -ne 0) { throw "$Program $($Arguments -join ' ') failed ($($run.Code)):`n$($run.Output)`n$($run.Error)" }
    $run
}
function Compare-Output([string]$Check, $Run, [string]$Expected) {
    if ($Run.Code -eq 0 -and $Run.Output -ceq $Expected) { Pass $Check }
    else { Fail $Check "exit $($Run.Code)`n--- expected`n$Expected--- actual`n$($Run.Output)`n$($Run.Error)" }
}

# ---- The libraries ----
# geometry has the Java binding, whose jar aqua build compiles with the pinned JDK.
if (-not (Test-Path -LiteralPath (Join-Path $AquaToolchain.Jdk 'bin'))) {
    & $AquaToolchain.PowerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'install-toolchain.ps1') -Tools jdk | Out-Null
}
$platform = $target.platform
$apple = $platform -eq 'macos'
# Every kind the platform builds (Windows has no object kind: lld cannot prelink COFF objects).
$kinds = if ($platform -eq 'windows') { @('shared', 'static') } else { @('shared', 'static', 'object') }
$libraries = [ordered]@{}
foreach ($name in 'geometry', 'counter', 'shapes', 'services') {
    Invoke-Checked $Dotnet @($Compiler, 'build', (Join-Path $tests $name), '--target', $platform, '--kind', ($kinds -join ','), '-o', (Join-Path $Work $name)) | Out-Null
    $libraries[$name] = Join-Path $Work "$name/$($target.name)"
}
Pass "build geometry, counter, services (default libraries) and the [library] shapes as $($kinds -join ', ') for $($target.name)"
# A project with [export] still builds its executable when asked to.
$executable = Join-Path $Work ('geometry-app' + $AquaToolchain.Exe)
Invoke-Checked $Dotnet @($Compiler, 'build', (Join-Path $tests 'geometry'), '--target', $platform, '--kind', 'executable', '-o', $executable) | Out-Null
Compare-Output 'executable of a project with [export] (--kind executable)' (Invoke-Program $executable) ''

# ---- The consumers, as any program would build them ----
function Build-Consumers([string]$Folder, [bool]$Sanitize) {
    $toolchain = Join-Path $repository "runtime/sdk/cmake/toolchains/$($target.name).cmake"
    Invoke-Checked $AquaToolchain.CMake @('-S', $tests, '-B', $Folder, '-G', 'Ninja', "-DCMAKE_MAKE_PROGRAM=$($AquaToolchain.Ninja)",
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain", '-DCMAKE_BUILD_TYPE=Debug', "-DAQUA_EXPORTS_GEOMETRY=$($libraries.geometry)",
        "-DAQUA_EXPORTS_COUNTER=$($libraries.counter)", "-DAQUA_EXPORTS_SHAPES=$($libraries.shapes)", "-DAQUA_EXPORTS_SERVICES=$($libraries.services)", "-DAQUA_EXPORTS_KINDS=$($kinds -join ';')",
        "-DAQUA_EXPORTS_SANITIZE=$(if ($Sanitize) { 'ON' } else { 'OFF' })") | Out-Null
    Invoke-Checked $AquaToolchain.CMake @('--build', $Folder) | Out-Null
    # Windows finds a program's DLLs (and their runtime files) beside it.
    if ($platform -eq 'windows') { foreach ($name in $libraries.Keys) { Copy-Item (Join-Path $libraries[$name] 'bin/*.dll') $Folder -Force } }
}
function Consumer([string]$Folder, [string]$Name) { Join-Path $Folder ($Name + $AquaToolchain.Exe) }

$consumers = Join-Path $Work 'consumers'
Build-Consumers $consumers $false
Pass 'build the C and C++ consumers'
$expected = (Get-Content -LiteralPath (Join-Path $tests 'geometry.expected') -Raw -Encoding UTF8).Replace("`r`n", "`n")
# parity.aqua prints the lines of every consumer but those about the native interface ("native", "c-abi"); consumers in
# JavaScript and Java print every line but the "c-abi" ones, which only the C ABI can cause (invalid UTF-8, NULL arrays).
$portable = (($expected -split "`n") | Where-Object { $_ -and $_ -notlike 'native *' -and $_ -notlike 'c-abi *' } | ForEach-Object { "$_`n" }) -join ''
$managed = (($expected -split "`n") | Where-Object { $_ -and $_ -notlike 'c-abi *' } | ForEach-Object { "$_`n" }) -join ''

function Test-Consumers([string]$Folder, [string]$Variant, [hashtable]$Environment = @{}) {
    $two = "two libraries 1 101 2 102`nafter one shutdown 3 101`n"
    Compare-Output "C consumer$Variant" (Invoke-Program (Consumer $Folder 'consumer_c') @() $Environment) $expected
    Compare-Output "C++ consumer$Variant" (Invoke-Program (Consumer $Folder 'consumer_cpp') @() $Environment) $expected
    Compare-Output "two libraries in one process$Variant" (Invoke-Program (Consumer $Folder 'two_libraries') @() $Environment) $two
    foreach ($kind in $kinds | Where-Object { $_ -ne 'shared' }) {
        Compare-Output "C consumer, $kind library$Variant" (Invoke-Program (Consumer $Folder "consumer_c_$kind") @() $Environment) $expected
        Compare-Output "two $kind libraries in one program$Variant" (Invoke-Program (Consumer $Folder "two_libraries_$kind") @() $Environment) $two
    }
    Compare-Output "[library] through [export] module$Variant" (Invoke-Program (Consumer $Folder 'shapes') @() $Environment) "shapes 225`n"
    Compare-Output "default libraries inside a library (network, application, terminal, onnx)$Variant" (Invoke-Program (Consumer $Folder 'default_libraries') @() $Environment) "status Not Found`njson {`"name`":`"Ada`",`"count`":3}`nterminal 1`nonnx 1`n"
    Compare-Output "lifecycle x1000$Variant" (Invoke-Program (Consumer $Folder 'lifecycle') @() $Environment) "lifecycle 1000 ok`n"
    Compare-Output "8 calling threads$Variant" (Invoke-Program (Consumer $Folder 'threads') @() $Environment) "threads 2000 calls, 0 failures, next 2001`n"
    if ($platform -ne 'windows') {
        $run = Invoke-Program (Consumer $Folder 'reentrancy') @() $Environment
        if ($run.Code -eq 0 -and $run.Output -eq "reentrancy refused, next call ok`n") { Pass "reentrant call refused$Variant" }
        else { Fail "reentrant call refused$Variant" "exit $($run.Code)`n$($run.Output)$($run.Error)" }
    }
}
Test-Consumers $consumers ''

# ---- The Rust binding: a crate depending on the geometry output's crate, built with the pinned Rust ----
if (-not (Test-Path -LiteralPath (Join-Path $AquaToolchain.Rust 'bin'))) {
    & $AquaToolchain.PowerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'install-toolchain.ps1') -Tools rust | Out-Null
}
$rust = $AquaPins.rust.($AquaToolchain.Platform)
$crate = Join-Path $Work 'rust-consumer'
New-Item -ItemType Directory -Force -Path (Join-Path $crate 'src') | Out-Null
Copy-Item (Join-Path $tests 'consumer.rs') (Join-Path $crate 'src/main.rs')
$geometryCrate = (Join-Path $libraries.geometry 'rust/geometry').Replace('\', '/')
Set-Content -LiteralPath (Join-Path $crate 'Cargo.toml') -Encoding ascii -Value "[package]`nname = `"consumer`"`nversion = `"0.0.0`"`nedition = `"2021`"`n`n[dependencies]`ngeometry = { path = `"$geometryCrate`" }`n"
$cargoEnvironment = @{ CARGO_HOME = (Join-Path $Work 'cargo-home'); RUSTC = (Join-Path $AquaToolchain.Rust "bin/rustc$($AquaToolchain.Exe)");
    PATH = (Join-Path $AquaToolchain.Rust 'bin') + [IO.Path]::PathSeparator + $env:PATH }
$linker = 'CARGO_TARGET_' + $rust.target.ToUpperInvariant().Replace('-', '_') + '_LINKER'
# On a Mac the system cc links (it finds the SDK for the macOS and iOS simulator targets); elsewhere the pinned clang.
if (-not $apple) { $cargoEnvironment[$linker] = $AquaToolchain.Clang }
if ($platform -eq 'windows') {
    # The gnullvm target links like llvm-mingw's x86_64-w64-mingw32-clang: the pinned clang with the MinGW sysroot and
    # compiler-rt, statically (a program then needs no libunwind.dll).
    $resource = (Get-ChildItem -LiteralPath (Join-Path $AquaToolchain.MinGW 'lib/clang') -Directory | Select-Object -First 1).FullName
    $cargoEnvironment['RUSTFLAGS'] = "-C target-feature=+crt-static -C link-arg=-rtlib=compiler-rt -C link-arg=--target=x86_64-w64-mingw32 -C link-arg=--sysroot=$($AquaToolchain.MinGW.Replace('\', '/')) -C link-arg=-resource-dir=$($resource.Replace('\', '/'))"
}
$built = Invoke-Program (Join-Path $AquaToolchain.Rust "bin/cargo$($AquaToolchain.Exe)") @('build', '--offline', '--release', '--target', $rust.target,
    '--manifest-path', (Join-Path $crate 'Cargo.toml'), '--target-dir', (Join-Path $crate 'target')) $cargoEnvironment
if ($built.Code -ne 0) { Fail 'build the Rust consumer' "$($built.Output)$($built.Error)" }
else {
    Pass "build the Rust consumer (Rust $($AquaPins.rust.version), $($rust.target))"
    $program = Join-Path $crate "target/$($rust.target)/release/consumer$($AquaToolchain.Exe)"
    $runEnvironment = @{}
    if ($platform -eq 'windows') { Copy-Item (Join-Path $libraries.geometry 'bin/geometry.dll') (Split-Path $program) -Force }
    elseif ($apple) { $runEnvironment['DYLD_LIBRARY_PATH'] = Join-Path $libraries.geometry 'lib' }
    else { $runEnvironment['LD_LIBRARY_PATH'] = Join-Path $libraries.geometry 'lib' }
    Compare-Output 'Rust consumer' (Invoke-Program $program @() $runEnvironment) $expected
}

# ---- The Java binding on the desktop JVM (JNI): Consumer.java against the geometry output's jar ----
$javaClasses = Join-Path $Work 'java-consumer'
$geometryJar = Join-Path $libraries.geometry 'lib/geometry.jar'
Invoke-Checked (Join-Path $AquaToolchain.Jdk "bin/javac$($AquaToolchain.Exe)") @('-encoding', 'UTF-8', '-cp', $geometryJar, '-d', $javaClasses, (Join-Path $tests 'Consumer.java')) | Out-Null
$libraryFolder = Join-Path $libraries.geometry $(if ($platform -eq 'windows') { 'bin' } else { 'lib' })
Compare-Output 'Java consumer (desktop JVM, JNI)' (Invoke-Program (Join-Path $AquaToolchain.Jdk "bin/java$($AquaToolchain.Exe)") @('-Dstdout.encoding=UTF-8',
    "-Djava.library.path=$libraryFolder", '-cp', ($geometryJar + [IO.Path]::PathSeparator + $javaClasses), 'Consumer')) $managed

# ---- The Swift binding on macOS: consumer.swift (as main.swift) with the output's Geometry.swift and C module ----
function Build-Swift([string]$Folder, [string[]]$Arguments) {
    New-Item -ItemType Directory -Force -Path $Folder | Out-Null
    Copy-Item (Join-Path $tests 'consumer.swift') (Join-Path $Folder 'main.swift')
    Invoke-Checked 'xcrun' (@('swiftc') + $Arguments + @((Join-Path $Folder 'main.swift'), '-o', (Join-Path $Folder 'consumer'))) | Out-Null
    Join-Path $Folder 'consumer'
}
if ($apple) {
    $geometryLib = Join-Path $libraries.geometry 'lib'
    $program = Build-Swift (Join-Path $Work 'swift-consumer') @('-I', (Join-Path $libraries.geometry 'include'), '-L', $geometryLib,
        '-Xlinker', '-rpath', '-Xlinker', $geometryLib, (Join-Path $libraries.geometry 'swift/Geometry.swift'))
    Compare-Output 'Swift consumer (macOS)' (Invoke-Program $program) $expected
}

# ---- The web: loadLibrary() of the web glue, a JavaScript consumer in Chromium ----
$webOutput = Join-Path $Work 'geometry-web'
Invoke-Checked $Dotnet @($Compiler, 'build', (Join-Path $tests 'geometry'), '--target', 'web', '-o', $webOutput) | Out-Null
Compare-Output 'JavaScript consumer (web, Chromium)' (Invoke-Program 'node' @((Join-Path $PSScriptRoot 'web-library-run.mjs'), (Join-Path $webOutput 'web'), (Join-Path $tests 'consumer.mjs'))) $managed

# ---- Parity with aqua run ----
Compare-Output 'parity with aqua run' (Invoke-Program $Dotnet @($Compiler, 'run', (Join-Path $tests 'geometry/parity.aqua'))) $portable

# ---- Binary audit ----
$tools = Join-Path $AquaToolchain.Llvm 'bin'
function Tool([string]$Name) { Join-Path $tools ($Name + $AquaToolchain.Exe) }
foreach ($name in $libraries.Keys) {
    $folder = $libraries[$name]
    $header = Get-Content -LiteralPath (Join-Path $folder "include/$name.h") -Raw -Encoding UTF8
    $declared = @([regex]::Matches($header, '(?m)^\w+_API [^;(]*?\b(\w+)\(') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    # A library with the Java binding also exports JNI_OnLoad (runtime/embed/jni.c).
    if (Test-Path -LiteralPath (Join-Path $folder "lib/$name.jar")) { $declared = @($declared + 'JNI_OnLoad' | Sort-Object -Unique) }
    if ($platform -eq 'windows') {
        $library = Join-Path $folder "bin/$name.dll"
        $exports = @(((Invoke-Checked (Tool 'llvm-readobj') @('--coff-exports', $library)).Output -split "`n") |
            Where-Object { $_ -match '^\s*Name: (\S+)' } | ForEach-Object { $Matches[1] } | Sort-Object -Unique)
        $needed = @(((Invoke-Checked (Tool 'llvm-readobj') @('--coff-imports', $library)).Output -split "`n") |
            Where-Object { $_ -match '^\s*Name: (\S+\.dll)\s*$' } | ForEach-Object { $Matches[1] } | Sort-Object -Unique)
        $system = '^(?i)(api-ms-win-[\w-]+|kernel32|ntdll|advapi32|shell32|ole32|oleaut32|oleacc|user32|gdi32|comctl32|comdlg32|ws2_32|userenv|bcrypt|crypt32|synchronization|ucrtbase|msvcrt)\.dll$'
        foreach ($import in 'mingw', 'msvc') {
            $file = Join-Path $folder $(if ($import -eq 'mingw') { "lib/lib$name.dll.a" } else { "lib/$name.lib" })
            $symbols = if (Test-Path -LiteralPath $file) { (Invoke-Checked (Tool 'llvm-nm') @($file)).Output } else { '' }
            $missing = @($declared | Where-Object { $symbols -notmatch "\b__imp_$_\b" })
            if ($symbols -and -not $missing.Count) { Pass "$name $import import library" } else { Fail "$name $import import library" "missing: $($missing -join ', ')" }
        }
    } elseif ($apple) {
        $library = Join-Path $folder "lib/lib$name.dylib"
        $exports = @(((Invoke-Checked 'nm' @('-gU', '-j', $library)).Output -split "`n") | Where-Object { $_ } | ForEach-Object { $_ -replace '^_', '' } | Sort-Object -Unique)
        $installName = ((Invoke-Checked 'otool' @('-D', $library)).Output -split "`n")[1]
        $needed = @(((Invoke-Checked 'otool' @('-L', $library)).Output -split "`n") | Select-Object -Skip 1 |
            Where-Object { $_ -match '^\s+(\S+) \(' } | ForEach-Object { $Matches[1] } | Where-Object { $_ -ne $installName } | Sort-Object -Unique)
        $system = '^(/usr/lib/|/System/Library/)'
        $signed = (Invoke-Program 'codesign' @('--verify', '--strict', $library)).Code -eq 0
        if ($installName -eq "@rpath/lib$name.dylib" -and $signed) { Pass "$name install name $installName, signed" }
        else { Fail "$name install name and signature" "install name '$installName', signature valid: $signed" }
    } else {
        $versioned = Get-ChildItem -LiteralPath (Join-Path $folder 'lib') | Where-Object Name -match "^lib$name\.so\.\d+$" | Select-Object -First 1
        $library = $versioned.FullName
        $exports = @(((Invoke-Checked (Tool 'llvm-nm') @('-D', '--defined-only', $library)).Output -split "`n") |
            Where-Object { $_ -match '^\S+ \w (\S+)$' } | ForEach-Object { $Matches[1] } | Sort-Object -Unique)
        $dynamic = (Invoke-Checked (Tool 'llvm-readelf') @('-d', $library)).Output
        $needed = @([regex]::Matches($dynamic, 'NEEDED.*\[(.+?)\]') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        $system = '^(libc|libm|libpthread|libdl|librt|ld-linux-x86-64)\.so\.\d+$'
        $soname = [regex]::Match($dynamic, 'SONAME.*\[(.+?)\]').Groups[1].Value
        if ($soname -eq $versioned.Name -and (Test-Path -LiteralPath (Join-Path $folder "lib/lib$name.so"))) { Pass "$name SONAME $soname" }
        else { Fail "$name SONAME" "SONAME '$soname', file $($versioned.Name)" }
    }
    if (-not (Compare-Object $exports $declared)) { Pass "$name exports exactly its header's $($declared.Count) functions" }
    else { Fail "$name exports" "exported: $($exports -join ', ')`ndeclared: $($declared -join ', ')" }
    $foreign = @($needed | Where-Object { $_ -notmatch $system })
    if (-not $foreign.Count) { Pass "$name needs only system libraries ($($needed -join ', '))" } else { Fail "$name dependencies" "not system libraries: $($foreign -join ', ')" }
    # Static and object outputs: the only global names defined are the exports; every internal is local (ELF, Mach-O) or
    # carries the library's prefix (Windows). Mach-O names have a leading underscore.
    foreach ($file in @("lib/lib$name.a", "lib/$name.o") | Where-Object { Test-Path -LiteralPath (Join-Path $folder $_) }) {
        $prefix = [regex]::Match($header, '(?m)^\w+_API bool (\w+)initialize\(').Groups[1].Value
        $globals = @(((Invoke-Checked (Tool 'llvm-nm') @('--defined-only', '--extern-only', '--format=just-symbols', (Join-Path $folder $file))).Output -split "`n") |
            Where-Object { $_ -and $_ -notmatch ':$' } | ForEach-Object { if ($apple) { $_ -replace '^_', '' } else { $_ } } |
            Where-Object { -not $_.StartsWith($prefix + 'aqua_') } | Sort-Object -Unique)
        if (-not (Compare-Object $globals $declared)) { Pass "$name $file defines only its exports globally" }
        else { Fail "$name $file globals" "global: $($globals -join ', ')" }
    }
}

# ---- Android (-Android): the library of each ABI, audited, and the Java and Kotlin consumers on the device ----
if ($Android) {
    $androidOutput = Join-Path $Work 'geometry-android'
    Invoke-Checked $Dotnet @($Compiler, 'build', (Join-Path $tests 'geometry'), '--target', 'android', '-o', $androidOutput) | Out-Null
    $androidFolder = Join-Path $androidOutput 'android'
    $sdk = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, $AquaToolchain.AndroidSdk) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    $adb = Join-Path $sdk "platform-tools/adb$($AquaToolchain.Exe)"
    $d8 = Join-Path $sdk "build-tools/$($AquaPins.androidSdk.buildTools)/d8$($AquaToolchain.Script)"
    $javaHome = @{ JAVA_HOME = $AquaToolchain.Jdk }
    $header = Get-Content -LiteralPath (Join-Path $androidFolder 'include/geometry.h') -Raw -Encoding UTF8
    $declared = @(@([regex]::Matches($header, '(?m)^\w+_API [^;(]*?\b(\w+)\(') | ForEach-Object { $_.Groups[1].Value }) + 'JNI_OnLoad' | Sort-Object -Unique)
    foreach ($abiFolder in Get-ChildItem -LiteralPath (Join-Path $androidFolder 'lib') -Directory) {
        $library = Join-Path $abiFolder.FullName 'libgeometry.so'
        $exports = @(((Invoke-Checked (Tool 'llvm-nm') @('-D', '--defined-only', $library)).Output -split "`n") |
            Where-Object { $_ -match '^\S+ \w (\S+)$' } | ForEach-Object { $Matches[1] } | Sort-Object -Unique)
        if (-not (Compare-Object $exports $declared)) { Pass "android $($abiFolder.Name) exports its header's functions and JNI_OnLoad" }
        else { Fail "android $($abiFolder.Name) exports" "exported: $($exports -join ', ')" }
        $dynamic = (Invoke-Checked (Tool 'llvm-readelf') @('-d', $library)).Output
        $needed = @([regex]::Matches($dynamic, 'NEEDED.*\[(.+?)\]') | ForEach-Object { $_.Groups[1].Value })
        $foreign = @($needed | Where-Object { $_ -notmatch '^lib(c|m|dl|log)\.so$' })
        if (-not $foreign.Count) { Pass "android $($abiFolder.Name) needs only system libraries ($($needed -join ', '))" } else { Fail "android $($abiFolder.Name) dependencies" ($foreign -join ', ') }
        $segments = @(((Invoke-Checked (Tool 'llvm-readelf') @('-l', $library)).Output -split "`n") | Where-Object { $_ -match '^\s*LOAD\s' })
        if ($segments.Count -and -not ($segments | Where-Object { $_ -notmatch '0x4000\s*$' })) { Pass "android $($abiFolder.Name) segments aligned to 16 KB pages" }
        else { Fail "android $($abiFolder.Name) page alignment" ($segments -join "`n") }
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $aar = [IO.Compression.ZipFile]::OpenRead((Join-Path $androidFolder 'geometry.aar'))
    $entries = @($aar.Entries | ForEach-Object FullName); $aar.Dispose()
    $missing = @('AndroidManifest.xml', 'classes.jar', 'R.txt', 'prefab/prefab.json', 'prefab/modules/geometry/include/geometry.h') + @(Get-ChildItem -LiteralPath (Join-Path $androidFolder 'lib') -Directory |
        ForEach-Object { "jni/$($_.Name)/libgeometry.so" }) | Where-Object { $entries -notcontains $_ }
    if (-not $missing.Count) { Pass 'android .aar (manifest, classes.jar, jni libraries, Prefab)' } else { Fail 'android .aar' "missing: $($missing -join ', ')" }

    $devices = @(((Invoke-Program $adb @('devices')).Output -split "`n") | Where-Object { $_ -match '\tdevice$' })
    if (-not $devices.Count) { Fail 'Android device' 'No device or emulator is connected (adb devices): the Java and Kotlin consumers did not run.' }
    else {
        $abi = (Invoke-Checked $adb @('shell', 'getprop', 'ro.product.cpu.abi')).Output.Trim()
        $deviceLibrary = Join-Path $androidFolder "lib/$abi/libgeometry.so"
        $remote = '/data/local/tmp/aquarius-exports'
        Invoke-Checked $adb @('shell', "rm -rf $remote && mkdir -p $remote") | Out-Null
        Invoke-Checked $adb @('push', $deviceLibrary, "$remote/") | Out-Null
        $geometryJar = Join-Path $androidFolder 'lib/geometry.jar'
        # Dexes classes (jars and class folders) into one jar the device runs with app_process.
        function Dex([string]$Name, [string[]]$Inputs) {
            $folder = Join-Path $Work "dex-$Name"
            New-Item -ItemType Directory -Force -Path $folder | Out-Null
            Invoke-Checked $d8 (@('--release', '--min-api', '29', '--output', $folder) + $Inputs) $javaHome | Out-Null
            $jar = Join-Path $Work "$Name.jar"
            $zip = [IO.Compression.ZipFile]::Open($jar, 'Create')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $folder 'classes.dex'), 'classes.dex')
            $zip.Dispose()
            Invoke-Checked $adb @('push', $jar, "$remote/") | Out-Null
            return "$remote/$Name.jar"
        }
        function Run-OnDevice([string]$Jar, [string]$Class) {
            Invoke-Program $adb @('shell', "cd $remote && LD_LIBRARY_PATH=$remote CLASSPATH=$Jar app_process $remote $Class")
        }
        # Java
        $javaClasses = Join-Path $Work 'android-java-classes'
        Invoke-Checked (Join-Path $AquaToolchain.Jdk "bin/javac$($AquaToolchain.Exe)") @('--release', '11', '-encoding', 'UTF-8', '-cp', $geometryJar, '-d', $javaClasses, (Join-Path $tests 'Consumer.java')) | Out-Null
        $javaJar = Join-Path $Work 'android-java.jar'
        Invoke-Checked (Join-Path $AquaToolchain.Jdk "bin/jar$($AquaToolchain.Exe)") @('cf', $javaJar, '-C', $javaClasses, '.') | Out-Null
        Compare-Output "Java consumer (Android $abi)" (Run-OnDevice (Dex 'java-consumer' @($geometryJar, $javaJar)) 'Consumer') $managed
        # Kotlin
        if (-not (Test-Path -LiteralPath (Join-Path $AquaToolchain.Kotlin 'bin'))) {
            & $AquaToolchain.PowerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'install-toolchain.ps1') -Tools kotlin | Out-Null
        }
        $kotlinJar = Join-Path $Work 'android-kotlin.jar'
        Invoke-Checked (Join-Path $AquaToolchain.Kotlin "bin/kotlinc$($AquaToolchain.Script)") @((Join-Path $tests 'Consumer.kt'), '-cp', $geometryJar, '-d', $kotlinJar) $javaHome | Out-Null
        Compare-Output "Kotlin consumer (Android $abi, Kotlin $($AquaPins.kotlin.version))" (Run-OnDevice (Dex 'kotlin-consumer' @($geometryJar, $kotlinJar,
            (Join-Path $AquaToolchain.Kotlin 'lib/kotlin-stdlib.jar'))) 'ConsumerKt') $managed
    }
}

# ---- macOS (on a Mac): a framework package of geometry, signed, and the Swift consumer against it ----
if ($apple) {
    $frameworkProject = Join-Path $Work 'geometry-framework'
    Copy-Item -Recurse (Join-Path $tests 'geometry') $frameworkProject
    $manifest = Join-Path $frameworkProject 'aqua.toml'
    [IO.File]::WriteAllText($manifest, ([IO.File]::ReadAllText($manifest) -replace '\[export\.macos\]', "[export.macos]`npackage = `"framework`""))
    Invoke-Checked $Dotnet @($Compiler, 'build', $frameworkProject, '--target', 'macos', '-o', (Join-Path $Work 'geometry-framework-out')) | Out-Null
    $frameworks = Join-Path $Work "geometry-framework-out/$($target.name)"
    $framework = Join-Path $frameworks 'Geometry.framework'
    $installName = ((Invoke-Checked 'otool' @('-D', (Join-Path $framework 'Geometry'))).Output -split "`n")[1]
    $signed = (Invoke-Program 'codesign' @('--verify', '--strict', '--deep', $framework)).Code -eq 0
    if ($installName -eq '@rpath/Geometry.framework/Versions/A/Geometry' -and $signed -and (Test-Path -LiteralPath (Join-Path $framework 'Versions/Current/Modules/module.modulemap'))) {
        Pass 'macOS framework package (versioned, module map, signed)'
    } else { Fail 'macOS framework package' "install name '$installName', signature valid: $signed" }
    $program = Build-Swift (Join-Path $Work 'swift-framework-consumer') @('-F', $frameworks, '-framework', 'Geometry',
        '-Xlinker', '-rpath', '-Xlinker', $frameworks, (Join-Path $frameworks 'swift/Geometry.swift'))
    Compare-Output 'Swift consumer (macOS framework)' (Invoke-Program $program) $expected
}

# ---- iOS (on a Mac): the xcframeworks, audited, and the Swift, C, C++ and Rust consumers on an iOS simulator ----
if ($apple) {
    $iosOutput = Join-Path $Work 'geometry-ios'
    Invoke-Checked $Dotnet @($Compiler, 'build', (Join-Path $tests 'geometry'), '--target', 'ios', '-o', $iosOutput) | Out-Null
    $ios = Join-Path $iosOutput 'ios'
    $header = Get-Content -LiteralPath (Join-Path $ios 'include/geometry.h') -Raw -Encoding UTF8
    $declared = @([regex]::Matches($header, '(?m)^\w+_API [^;(]*?\b(\w+)\(') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    foreach ($xcframework in 'Geometry.xcframework', 'Geometry-static.xcframework') {
        $lint = Invoke-Program 'plutil' @('-lint', (Join-Path $ios "$xcframework/Info.plist"))
        if ($lint.Code -eq 0) { Pass "ios $xcframework Info.plist" } else { Fail "ios $xcframework Info.plist" "$($lint.Output)$($lint.Error)" }
    }
    foreach ($slice in @(@{ Id = 'ios-arm64'; Platform = 'IOS' }, @{ Id = 'ios-arm64-simulator'; Platform = 'IOSSIMULATOR' })) {
        $binary = Join-Path $ios "Geometry.xcframework/$($slice.Id)/Geometry.framework/Geometry"
        $installName = ((Invoke-Checked 'otool' @('-D', $binary)).Output -split "`n")[1]
        $build = (Invoke-Checked 'xcrun' @('vtool', '-show-build', $binary)).Output
        $exports = @(((Invoke-Checked 'nm' @('-gU', '-j', $binary)).Output -split "`n") | Where-Object { $_ } | ForEach-Object { $_ -replace '^_', '' } | Sort-Object -Unique)
        if ($installName -eq '@rpath/Geometry.framework/Geometry' -and $build -match "platform $($slice.Platform)\b" -and -not (Compare-Object $exports $declared)) {
            Pass "ios $($slice.Id) framework (install name, platform $($slice.Platform), exports)"
        } else { Fail "ios $($slice.Id) framework" "install name '$installName'`n$build`nexported: $($exports -join ', ')" }
        $archive = Join-Path $ios "Geometry-static.xcframework/$($slice.Id)/libgeometry.a"
        $globals = @(((Invoke-Checked (Tool 'llvm-nm') @('--defined-only', '--extern-only', '--format=just-symbols', $archive)).Output -split "`n") |
            Where-Object { $_ -and $_ -notmatch ':$' } | ForEach-Object { $_ -replace '^_', '' } | Sort-Object -Unique)
        if (-not (Compare-Object $globals $declared)) { Pass "ios $($slice.Id) static library defines only its exports globally" }
        else { Fail "ios $($slice.Id) static globals" "global: $($globals -join ', ')" }
    }
    $signed = Invoke-Program 'codesign' @('--verify', (Join-Path $ios 'Geometry.xcframework'))
    if ($signed.Code -eq 0) { Pass 'ios xcframework signed' } else { Fail 'ios xcframework signature' "$($signed.Output)$($signed.Error)" }

    # A simulator: a booted one, else the first available iPhone, booted.
    $devices = (Invoke-Checked 'xcrun' @('simctl', 'list', 'devices', 'available', '-j')).Output | ConvertFrom-Json
    $all = @($devices.devices.PSObject.Properties | ForEach-Object { $_.Value } | ForEach-Object { $_ })
    $simulator = @($all | Where-Object state -eq 'Booted') + @($all | Where-Object name -like 'iPhone*') | Select-Object -First 1
    if (-not $simulator) { Fail 'iOS simulator' 'no available simulator: install one in Xcode (Settings > Platforms)' }
    else {
        if ($simulator.state -ne 'Booted') { Invoke-Checked 'xcrun' @('simctl', 'boot', $simulator.udid) | Out-Null }
        Invoke-Checked 'xcrun' @('simctl', 'bootstatus', $simulator.udid, '-b') | Out-Null
        $triple = 'arm64-apple-ios16.0-simulator'
        $frameworks = Join-Path $ios 'Geometry.xcframework/ios-arm64-simulator'
        $linkFramework = @('-F', $frameworks, '-framework', 'Geometry', '-Xlinker', '-rpath', '-Xlinker', $frameworks)
        function Invoke-Simulator([string]$Program) { Invoke-Program 'xcrun' @('simctl', 'spawn', $simulator.udid, $Program) }
        $program = Build-Swift (Join-Path $Work 'swift-ios') (@('-sdk', (Invoke-Checked 'xcrun' @('--sdk', 'iphonesimulator', '--show-sdk-path')).Output.Trim(),
            '-target', $triple) + $linkFramework + @((Join-Path $ios 'swift/Geometry.swift')))
        Compare-Output "Swift consumer (iOS simulator $($simulator.name))" (Invoke-Simulator $program) $expected
        $nativeFolder = Join-Path $Work 'ios-native'
        New-Item -ItemType Directory -Force -Path $nativeFolder | Out-Null
        foreach ($consumer in @(@{ Name = 'C'; Driver = 'clang'; Source = 'consumer.c'; Flags = @('-std=c17') },
                                @{ Name = 'C++'; Driver = 'clang++'; Source = 'consumer.cpp'; Flags = @('-std=c++20') })) {
            $program = Join-Path $nativeFolder ($consumer.Source + '.out')
            Invoke-Checked 'xcrun' (@('--sdk', 'iphonesimulator', $consumer.Driver, '-target', $triple) + $consumer.Flags + @('-I', (Join-Path $ios 'include'),
                '-F', $frameworks, '-framework', 'Geometry', "-Wl,-rpath,$frameworks", (Join-Path $tests $consumer.Source), '-o', $program)) | Out-Null
            Compare-Output "$($consumer.Name) consumer (iOS simulator)" (Invoke-Simulator $program) $expected
        }
        # Rust links the static slice (the crate's build.rs takes GEOMETRY_LIB_DIR) for the simulator's target.
        $iosCrate = Join-Path $Work 'rust-ios'
        New-Item -ItemType Directory -Force -Path (Join-Path $iosCrate 'src') | Out-Null
        Copy-Item (Join-Path $tests 'consumer.rs') (Join-Path $iosCrate 'src/main.rs')
        Set-Content -LiteralPath (Join-Path $iosCrate 'Cargo.toml') -Encoding ascii -Value ("[package]`nname = `"consumer`"`nversion = `"0.0.0`"`nedition = `"2021`"`n`n[dependencies]`n" +
            "geometry = { path = `"$((Join-Path $ios 'rust/geometry').Replace('\', '/'))`" }`n")
        $iosRust = $cargoEnvironment.Clone()
        $iosRust['GEOMETRY_LIB_DIR'] = Join-Path $ios 'Geometry-static.xcframework/ios-arm64-simulator'
        $simulatorTarget = $rust.simulatorTarget
        $built = Invoke-Program (Join-Path $AquaToolchain.Rust 'bin/cargo') @('build', '--offline', '--release', '--target', $simulatorTarget,
            '--manifest-path', (Join-Path $iosCrate 'Cargo.toml'), '--target-dir', (Join-Path $iosCrate 'target')) $iosRust
        if ($built.Code -ne 0) { Fail 'build the Rust consumer (iOS simulator)' "$($built.Output)$($built.Error)" }
        else { Compare-Output 'Rust consumer (iOS simulator)' (Invoke-Simulator (Join-Path $iosCrate "target/$simulatorTarget/release/consumer")) $expected }
    }
}

# ---- Sanitizers (Linux) ----
if ($platform -eq 'linux') {
    $sanitized = Join-Path $Work 'sanitized'
    Build-Consumers $sanitized $true
    Test-Consumers $sanitized ' (ASan, LSan, UBSan)' @{ ASAN_OPTIONS = 'detect_leaks=1:abort_on_error=0'; UBSAN_OPTIONS = 'halt_on_error=1:print_stacktrace=1'
        LSAN_OPTIONS = "suppressions=$(Join-Path $tests 'lsan.supp')" }
}

Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
if ($failures.Count) { Write-Host "`n$($failures.Count) export check(s) failed: $($failures -join '; ')"; exit 1 }
Write-Host "`nEvery native library check passed on $($target.name)."
exit 0
