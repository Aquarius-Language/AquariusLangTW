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
$libraries = @{}
foreach ($name in 'geometry', 'counter') {
    Invoke-Checked $Dotnet @($Compiler, 'build', (Join-Path $tests $name), '--target', $platform, '-o', (Join-Path $Work $name)) | Out-Null
    $libraries[$name] = Join-Path $Work "$name/$($target.name)"
}
Pass "build geometry and counter for $($target.name)"

# ---- The consumers, as any program would build them ----
function Build-Consumers([string]$Folder, [bool]$Sanitize) {
    $toolchain = Join-Path $repository "runtime/sdk/cmake/toolchains/$($target.name).cmake"
    Invoke-Checked $AquaToolchain.CMake @('-S', $tests, '-B', $Folder, '-G', 'Ninja', "-DCMAKE_MAKE_PROGRAM=$($AquaToolchain.Ninja)",
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain", '-DCMAKE_BUILD_TYPE=Debug', "-DAQUA_EXPORTS_GEOMETRY=$($libraries.geometry)",
        "-DAQUA_EXPORTS_COUNTER=$($libraries.counter)", "-DAQUA_EXPORTS_SANITIZE=$(if ($Sanitize) { 'ON' } else { 'OFF' })") | Out-Null
    Invoke-Checked $AquaToolchain.CMake @('--build', $Folder) | Out-Null
    # Windows finds a program's DLLs beside it.
    if ($platform -eq 'windows') { foreach ($name in $libraries.Keys) { Copy-Item (Join-Path $libraries[$name] "bin/$name.dll") $Folder -Force } }
}
function Consumer([string]$Folder, [string]$Name) { Join-Path $Folder ($Name + $AquaToolchain.Exe) }

$consumers = Join-Path $Work 'consumers'
Build-Consumers $consumers $false
Pass 'build the C and C++ consumers'
$expected = (Get-Content -LiteralPath (Join-Path $tests 'geometry.expected') -Raw -Encoding UTF8).Replace("`r`n", "`n")
$portable = (($expected -split "`n") | Where-Object { $_ -and $_ -notlike 'native *' } | ForEach-Object { "$_`n" }) -join ''

function Test-Consumers([string]$Folder, [string]$Variant, [hashtable]$Environment = @{}) {
    Compare-Output "C consumer$Variant" (Invoke-Program (Consumer $Folder 'consumer_c') @() $Environment) $expected
    Compare-Output "C++ consumer$Variant" (Invoke-Program (Consumer $Folder 'consumer_cpp') @() $Environment) $expected
    Compare-Output "two libraries in one process$Variant" (Invoke-Program (Consumer $Folder 'two_libraries') @() $Environment) "two libraries 1 101 2 102`nafter one shutdown 3 101`n"
    Compare-Output "lifecycle x1000$Variant" (Invoke-Program (Consumer $Folder 'lifecycle') @() $Environment) "lifecycle 1000 ok`n"
    Compare-Output "8 calling threads$Variant" (Invoke-Program (Consumer $Folder 'threads') @() $Environment) "threads 2000 calls, 0 failures, next 2001`n"
    if ($platform -ne 'windows') {
        $run = Invoke-Program (Consumer $Folder 'reentrancy') @() $Environment
        if ($run.Code -eq 0 -and $run.Output -eq "reentrancy refused, next call ok`n") { Pass "reentrant call refused$Variant" }
        else { Fail "reentrant call refused$Variant" "exit $($run.Code)`n$($run.Output)$($run.Error)" }
    }
}
Test-Consumers $consumers ''

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
        $system = '^(?i)(api-ms-win-[\w-]+|kernel32|ntdll|advapi32|shell32|ole32|user32|ws2_32|userenv|bcrypt|synchronization|ucrtbase|msvcrt)\.dll$'
        foreach ($import in 'mingw', 'msvc') {
            $file = Join-Path $folder $(if ($import -eq 'mingw') { "lib/lib$name.dll.a" } else { "lib/$name.lib" })
            $symbols = if (Test-Path -LiteralPath $file) { (Invoke-Checked (Tool 'llvm-nm') @($file)).Output } else { '' }
            $missing = @($declared | Where-Object { $symbols -notmatch "\b__imp_$_\b" })
            if ($symbols -and -not $missing.Count) { Pass "$name $import import library" } else { Fail "$name $import import library" "missing: $($missing -join ', ')" }
        }
    } else {
        $versioned = Get-ChildItem -LiteralPath (Join-Path $folder 'lib') -Filter "lib$name.so.*" | Select-Object -First 1
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
}

# ---- Sanitizers (Linux) ----
if ($platform -eq 'linux') {
    $sanitized = Join-Path $Work 'sanitized'
    Build-Consumers $sanitized $true
    Test-Consumers $sanitized ' (ASan, LSan, UBSan)' @{ ASAN_OPTIONS = 'detect_leaks=1:abort_on_error=0'; UBSAN_OPTIONS = 'halt_on_error=1:print_stacktrace=1' }
}

Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
if ($failures.Count) { Write-Host "`n$($failures.Count) export check(s) failed: $($failures -join '; ')"; exit 1 }
Write-Host "`nEvery native library check passed on $($target.name)."
exit 0
