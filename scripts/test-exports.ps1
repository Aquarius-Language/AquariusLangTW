# Tests the native library outputs (docs/native-libraries.md) on this host's desktop target: builds the test libraries
# of tests/exports (geometry, counter) with the repository's compiler, builds the consumers of tests/exports with the
# pinned Clang through the SDK toolchain file, and checks
#   - the C and C++ consumers print geometry.expected exactly, and aqua run of geometry/parity.aqua prints its lines
#     that are not marked "native" (parity with the Wasm engine);
#   - a binary audit: the library exports exactly the functions of its header, needs only system libraries, has its
#     SONAME (Linux) or import libraries listing every export (Windows);
#   - two libraries in one process, 1000 starts and stops, 8 calling threads, and (POSIX) a reentrant call refused;
#   - on Linux, the consumers again under AddressSanitizer, LeakSanitizer and UndefinedBehaviorSanitizer.
#   scripts/test-exports.ps1                       # this host's target (windows-x64 here; linux-x64 in WSL or on Linux)
# Runs under Windows PowerShell 5.1 or PowerShell 7 (pwsh). Needs the target's runtime pack (scripts/build-runtime.ps1)
# and a built compiler (dotnet build compiler/AquariusCli). build-release.ps1 runs it.
param(
    [string]$Compiler = '',
    [string]$Dotnet = 'dotnet',
    [string]$Work = ''
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
$platform = $target.platform
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
$cargoEnvironment[$linker] = $AquaToolchain.Clang
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
    else { $runEnvironment['LD_LIBRARY_PATH'] = Join-Path $libraries.geometry 'lib' }
    Compare-Output 'Rust consumer' (Invoke-Program $program @() $runEnvironment) $expected
}

# ---- Parity with aqua run ----
Compare-Output 'parity with aqua run' (Invoke-Program $Dotnet @($Compiler, 'run', (Join-Path $tests 'geometry/parity.aqua'))) $portable

# ---- Binary audit ----
$tools = Join-Path $AquaToolchain.Llvm 'bin'
function Tool([string]$Name) { Join-Path $tools ($Name + $AquaToolchain.Exe) }
foreach ($name in $libraries.Keys) {
    $folder = $libraries[$name]
    $header = Get-Content -LiteralPath (Join-Path $folder "include/$name.h") -Raw -Encoding UTF8
    $declared = @([regex]::Matches($header, '(?m)^\w+_API [^;(]*?\b(\w+)\(') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
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
    # Static and object outputs: the only global names defined are the exports; every internal is local (ELF) or carries
    # the library's prefix (Windows).
    foreach ($file in @("lib/lib$name.a", "lib/$name.o") | Where-Object { Test-Path -LiteralPath (Join-Path $folder $_) }) {
        $prefix = [regex]::Match($header, '(?m)^\w+_API bool (\w+)initialize\(').Groups[1].Value
        $globals = @(((Invoke-Checked (Tool 'llvm-nm') @('--defined-only', '--extern-only', '--format=just-symbols', (Join-Path $folder $file))).Output -split "`n") |
            Where-Object { $_ -and $_ -notmatch ':$' -and -not $_.StartsWith($prefix + 'aqua_') } | Sort-Object -Unique)
        if (-not (Compare-Object $globals $declared)) { Pass "$name $file defines only its exports globally" }
        else { Fail "$name $file globals" "global: $($globals -join ', ')" }
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
