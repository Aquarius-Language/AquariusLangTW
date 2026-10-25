# Runs the default libraries' tests on the runtime packs: every libraries/<name>/tests/<case>.aqua is compiled with the
# repository's compiler and run on each target; its standard output must equal <case>.expected exactly (LF endings).
#   scripts/test-libraries.ps1                                   # every library, windows-x64 and web
#   scripts/test-libraries.ps1 -Libraries audio,graphics -Targets windows-x64,web,linux-x64
#   scripts/test-libraries.ps1 -Devices                          # also cases that need a window, device or service
# Runtime packs come from .native-build/install/<target> (scripts/build-runtime.ps1). linux-x64 runs in WSL, Android
# targets run on the connected device or emulator through scripts/android-run.ps1 (only with -Devices).
#
# A case says on its first line what it needs and where it runs (a # comment):
#   needs <a window | a ... device | a GPU | a camera | the clipboard | a driver | a peer server | models ...>
#                                       run only with -Devices
#   web only | windows only | desktop only | desktop and web | (Windows, Linux, Android) | not Android
#                                       the targets it runs on (default: every target)
# Sources: the case, plus every .aqua file in the subfolders of tests/ (support modules); assets: those subfolders.
# The program is compiled beside the case, so files it reads relative to its folder are found on native targets.
param(
    [string[]]$Libraries = @(),
    [string[]]$Targets = @('windows-x64', 'web'),
    [string[]]$Cases = @(),
    [switch]$Devices,
    [string]$Compiler = '',
    [string]$Dotnet = 'dotnet',
    [string]$Distribution = 'Ubuntu-22.04',
    [int]$TimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'toolchain.ps1')
$repository = $AquaToolchain.Repository
$Libraries = @($Libraries | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Targets = @($Targets | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Cases = @($Cases | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
foreach ($target in $Targets) { if ($AquaTargets -notcontains $target) { throw "Unknown target $target; use $($AquaTargets -join ', ')." } }

if (-not $Compiler) {
    $Compiler = @('Release', 'Debug') | ForEach-Object { Join-Path $repository "compiler/AquariusCli/bin/$_/net8.0/aqua.dll" } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $Compiler) { throw 'Build the compiler first (dotnet build compiler/AquariusCli -c Release) or pass -Compiler aqua.dll.' }
}

# Windows PowerShell 5.1 has no ProcessStartInfo.ArgumentList: quote each argument by the Windows argv rules.
function ConvertTo-WindowsArgument([string]$Argument) {
    if ($Argument -and $Argument -notmatch '[\s"]') { return $Argument }  # wsl.exe does not unquote its own options
    '"' + [regex]::Replace([regex]::Replace($Argument, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
function Invoke-Captured([string]$Program, [string[]]$Arguments, [hashtable]$Environment = @{}) {
    $start = [Diagnostics.ProcessStartInfo]::new($Program)
    $start.Arguments = ($Arguments | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join ' '
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false); $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $process = [Diagnostics.Process]::Start($start)
    $errors = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill(); return [pscustomobject]@{ Code = -1; Output = $output.Result; Error = "timed out after $TimeoutSeconds s" } }
    $process.WaitForExit()
    return [pscustomobject]@{ Code = $process.ExitCode; Output = $output.Result.Replace("`r`n", "`n"); Error = $errors.Result }
}
function ConvertTo-Wsl([string]$Path) { '/mnt/' + $Path.Substring(0, 1).ToLowerInvariant() + $Path.Substring(2).Replace('\', '/') }

# The targets a case runs on and whether it needs something this machine may not have, from its first line.
function Get-CaseFacts([string]$File) {
    $line = ((Get-Content -LiteralPath $File -TotalCount 1 -Encoding UTF8) + '').ToLowerInvariant()
    $targets = $AquaTargets
    if ($line -match '\bweb only\b') { $targets = @('web') }
    elseif ($line -match '\bwindows only\b') { $targets = @('windows-x64') }
    elseif ($line -match '\bdesktop only\b') { $targets = @('windows-x64', 'linux-x64') }
    elseif ($line -match '\bdesktop and web\b') { $targets = @('windows-x64', 'linux-x64', 'web') }
    elseif ($line -match '\(windows, linux,? (or |and )?android\)') { $targets = @($AquaTargets | Where-Object { $_ -ne 'web' }) }
    elseif ($line -match '\bnot android\b') { $targets = @($AquaTargets | Where-Object { $_ -notlike 'android-*' }) }
    $device = $line -match '\bneeds?\b[^.;]*\b(window|device|gpu|camera|clipboard|driver|display|peer|server|models?)\b'
    return [pscustomobject]@{ Targets = $targets; NeedsDevice = $device }
}

$results = [Collections.Generic.List[object]]::new()
$folders = Get-ChildItem -LiteralPath (Join-Path $repository 'libraries') -Directory | Sort-Object Name
if ($Libraries.Count) { $folders = $folders | Where-Object { $Libraries -contains $_.Name } }
foreach ($folder in $folders) {
    $tests = Join-Path $folder.FullName 'tests'
    if (-not (Test-Path -LiteralPath $tests)) { continue }
    # The platforms the library's manifest lists bound every case (platforms = ["windows", "linux", ...]).
    $manifest = [IO.File]::ReadAllText((Join-Path $folder.FullName 'aqua.toml'))
    $platforms = if ($manifest -match '(?m)^\s*platforms\s*=\s*\[([^\]]*)\]') { @([regex]::Matches($Matches[1], '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }) } else { @() }
    $support = @(Get-ChildItem -LiteralPath $tests -Directory)
    $modules = @($support | ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter '*.aqua' -File -Recurse } | ForEach-Object FullName)
    foreach ($case in Get-ChildItem -LiteralPath $tests -Filter '*.aqua' -File | Sort-Object Name) {
        $name = [IO.Path]::GetFileNameWithoutExtension($case.Name)
        if ($Cases.Count -and $Cases -notcontains $name) { continue }
        $expectedFile = Join-Path $tests "$name.expected"
        $facts = Get-CaseFacts $case.FullName
        $wasm = Join-Path $tests ".aqua-test-$name.wasm"
        $compiled = $false
        foreach ($target in $Targets) {
            $row = [pscustomobject]@{ Library = $folder.Name; Case = $name; Target = $target; Result = ''; Detail = '' }
            $results.Add($row)
            if ($facts.Targets -notcontains $target -or ($platforms.Count -and $platforms -notcontains ($target -split '-')[0])) {
                $row.Result = 'skip'; $row.Detail = 'not for this target'; continue
            }
            if ($facts.NeedsDevice -and -not $Devices) { $row.Result = 'skip'; $row.Detail = 'needs a device or window (-Devices)'; continue }
            if ($target -like 'android-*' -and -not $Devices) { $row.Result = 'skip'; $row.Detail = 'Android runs need a device (-Devices)'; continue }
            if (-not (Test-Path -LiteralPath $expectedFile)) { $row.Result = 'FAIL'; $row.Detail = "missing $name.expected"; continue }
            $pack = Join-Path $AquaToolchain.Native "install/$target"
            if (-not (Test-Path -LiteralPath $pack)) { $row.Result = 'skip'; $row.Detail = "no $target runtime pack"; continue }
            if (-not $compiled) {
                $arguments = @($Compiler, 'build', $case.FullName) + $modules + @('--root', $tests, '--entry', $case.Name, '-o', $wasm)
                foreach ($directory in $support) { $arguments += @('--assets', $directory.FullName) }
                $build = Invoke-Captured $Dotnet $arguments
                if ($build.Code -ne 0) { $row.Result = 'FAIL'; $row.Detail = 'compile: ' + ($build.Output + $build.Error).Trim(); break }
                $compiled = $true
            }
            switch -Wildcard ($target) {
                'windows-x64' { $run = Invoke-Captured (Join-Path $pack 'aquarius.exe') @($wasm) }
                'linux-x64' {
                    $env:MSYS_NO_PATHCONV = '1'
                    $run = Invoke-Captured 'wsl.exe' @('-d', $Distribution, '--cd', (ConvertTo-Wsl $tests), (ConvertTo-Wsl (Join-Path $pack 'aquarius')), (ConvertTo-Wsl $wasm))
                }
                'web' { $run = Invoke-Captured 'node' @((Join-Path $PSScriptRoot 'web-run.mjs'), $pack, $wasm, '--timeout', "$TimeoutSeconds") }
                'android-*' {
                    $abi = $target.Substring('android-'.Length)
                    $run = Invoke-Captured 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'android-run.ps1'), $wasm, '-Runtime', $pack, '-Abi', $abi, '-Run')
                }
            }
            $expected = [IO.File]::ReadAllText($expectedFile).Replace("`r`n", "`n")
            if ($run.Output -eq $expected) { $row.Result = 'pass' }
            else {
                $row.Result = 'FAIL'
                $actualLines = $run.Output -split "`n"; $expectedLines = $expected -split "`n"
                for ($i = 0; $i -lt [Math]::Max($actualLines.Count, $expectedLines.Count); $i++) {
                    if ($i -ge $actualLines.Count -or $i -ge $expectedLines.Count -or $actualLines[$i] -ne $expectedLines[$i]) {
                        $row.Detail = "exit $($run.Code), line $($i + 1): expected '$(if ($i -lt $expectedLines.Count) { $expectedLines[$i] })', got '$(if ($i -lt $actualLines.Count) { $actualLines[$i] })' $($run.Error.Trim())"
                        break
                    }
                }
            }
        }
        if (Test-Path -LiteralPath $wasm) { Remove-Item -LiteralPath $wasm -Force }
    }
}
$results | Format-Table -AutoSize -Wrap Library, Case, Target, Result, Detail | Out-String -Width 220 | Write-Host
$failed = @($results | Where-Object Result -eq 'FAIL').Count
$passed = @($results | Where-Object Result -eq 'pass').Count
$skipped = @($results | Where-Object Result -eq 'skip').Count
Write-Host "$passed passed, $failed failed, $skipped skipped"
if ($failed) { exit 1 }
