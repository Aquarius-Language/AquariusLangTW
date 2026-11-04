# Runs the default libraries' tests on the runtime packs: every libraries/<name>/tests/<case>.aqua is compiled with the
# repository's compiler and run on each target; its standard output must equal <case>.expected exactly (LF endings).
#   scripts/test-libraries.ps1                                   # every library, windows-x64 and web
#   scripts/test-libraries.ps1 -Libraries audio,graphics -Targets windows-x64,web,linux-x64
#   scripts/test-libraries.ps1 -Devices                          # also cases that need a window, device or service
# Runtime packs come from .native-build/install/<target> (scripts/build-runtime.ps1). linux-x64 runs in WSL (directly
# on Linux), Android targets run on the connected device or emulator through scripts/android-run.ps1 (only with -Devices).
#
# A case says on its first line what it needs and where it runs (a # comment):
#   needs <a window | a ... device | a GPU | a camera | the clipboard | a driver | a peer server | models ...>
#                                       run only with -Devices
#   web only | windows only | desktop only | desktop and web | (Windows, Linux, Android) | not Android
#                                       the targets it runs on (default: every target)
# A case naming "(environment NAME=value, ...)" gets those variables in its native runs (Windows, Linux).
# A case naming "servers/<file>.aqua serving http://host:port" gets that peer server compiled and run on this machine
# while it runs (Android reaches it through adb reverse), stopped afterwards. tests/downloads.json pins files the tests
# read that are not committed (models): they are downloaded and SHA-256 checked before the library's first case.
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
    # Linux builds write to bin/linux/ (compiler/Directory.Build.props), so WSL and Windows can share a checkout.
    $Compiler = @('Release', 'Debug') | ForEach-Object { $c = $_; @('', 'linux/') | ForEach-Object { Join-Path $repository "compiler/AquariusCli/bin/$_$c/net8.0/aqua.dll" } } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $Compiler) { throw 'Build the compiler first (dotnet build compiler/AquariusCli -c Release) or pass -Compiler aqua.dll.' }
}

# Windows PowerShell 5.1 has no ProcessStartInfo.ArgumentList: quote each argument by the Windows argv rules.
function ConvertTo-WindowsArgument([string]$Argument) {
    if ($Argument -and $Argument -notmatch '[\s"]') { return $Argument }  # wsl.exe does not unquote its own options
    '"' + [regex]::Replace([regex]::Replace($Argument, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
function Invoke-Captured([string]$Program, [string[]]$Arguments, [hashtable]$Environment = @{}, [string]$Directory = '') {
    $start = [Diagnostics.ProcessStartInfo]::new($Program)
    if ($Directory) { $start.WorkingDirectory = $Directory }
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
    # A peer server the case names as "servers/<file>.aqua serving http://host:port" runs natively during its runs.
    $server = if ($line -match '\b(servers/[^\s)]+\.aqua) serving https?://[^\s:/)]+:(\d+)') { [pscustomobject]@{ Source = $Matches[1]; Port = [int]$Matches[2] } } else { $null }
    $environment = @{}
    $first = (Get-Content -LiteralPath $File -TotalCount 1 -Encoding UTF8) + ''
    if ($first -match '\(environment ([^)]+)\)') { foreach ($pair in $Matches[1] -split ',\s*') { $name, $value = $pair -split '=', 2; $environment[$name.Trim()] = $value } }
    return [pscustomobject]@{ Targets = $targets; NeedsDevice = $device; Server = $server; Environment = $environment }
}

# tests/downloads.json pins files a library's tests read that are not committed (models): each is downloaded once into
# .native-build/test-downloads, checked against its SHA-256, and copied to tests/<file> (which the library git-ignores).
function Install-TestDownloads([string]$Tests) {
    $pins = Join-Path $Tests 'downloads.json'
    if (-not (Test-Path -LiteralPath $pins)) { return }
    $cache = Join-Path $AquaToolchain.Native 'test-downloads'
    [void](New-Item -ItemType Directory -Path $cache -Force)
    foreach ($pin in (Get-Content -LiteralPath $pins -Raw -Encoding UTF8 | ConvertFrom-Json).files) {
        $destination = Join-Path $Tests $pin.file
        if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $pin.sha256) { continue }
        $cached = Join-Path $cache $pin.sha256
        if (-not (Test-Path -LiteralPath $cached) -or (Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash -ne $pin.sha256) {
            Write-Host "Downloading $($pin.file) ($($pin.license))..."
            $partial = "$cached.part"
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            (New-Object Net.WebClient).DownloadFile($pin.url, $partial)
            if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $pin.sha256) { Remove-Item -LiteralPath $partial -Force; throw "Checksum mismatch for $($pin.url)" }
            Move-Item -LiteralPath $partial -Destination $cached -Force
        }
        [void](New-Item -ItemType Directory -Path (Split-Path $destination) -Force)
        Copy-Item -LiteralPath $cached -Destination $destination -Force
    }
}

# The peer server of a case: compiled from its tests folder and run on this machine's runtime until Stop-PeerServer.
function Start-PeerServer([string]$Tests, $Server) {
    $wasm = Join-Path $Tests ".aqua-test-server-$([IO.Path]::GetFileNameWithoutExtension($Server.Source)).wasm"
    $build = Invoke-Captured $Dotnet @($Compiler, 'build', (Join-Path $Tests $Server.Source), '--root', $Tests, '-o', $wasm)
    if ($build.Code -ne 0) { throw "Cannot compile the peer server $($Server.Source): $($build.Output)$($build.Error)" }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $AquaToolchain.Native 'install/windows-x64/aquarius.exe'), (ConvertTo-WindowsArgument $wasm))
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    [void]$process.StandardOutput.ReadToEndAsync(); [void]$process.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        $client = [Net.Sockets.TcpClient]::new()
        try { $client.Connect('127.0.0.1', $Server.Port); return [pscustomobject]@{ Process = $process; Wasm = $wasm; Port = $Server.Port } } catch { Start-Sleep -Milliseconds 200 } finally { $client.Dispose() }
    }
    if (-not $process.HasExited) { $process.Kill() }
    throw "The peer server $($Server.Source) did not listen on port $($Server.Port)."
}
function Stop-PeerServer($Peer) {
    if (-not $Peer) { return }
    if (-not $Peer.Process.HasExited) { $Peer.Process.Kill(); [void]$Peer.Process.WaitForExit(5000) }
    Remove-Item -LiteralPath $Peer.Wasm -Force -ErrorAction SilentlyContinue
}
$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
$adb = Join-Path $sdk 'platform-tools/adb.exe'

$results = [Collections.Generic.List[object]]::new()
$folders = Get-ChildItem -LiteralPath (Join-Path $repository 'libraries') -Directory | Sort-Object Name
if ($Libraries.Count) { $folders = $folders | Where-Object { $Libraries -contains $_.Name } }
foreach ($folder in $folders) {
    $tests = Join-Path $folder.FullName 'tests'
    if (-not (Test-Path -LiteralPath $tests)) { continue }
    # The platforms the library's manifest lists bound every case (platforms = ["windows", "linux", ...]).
    $manifest = [IO.File]::ReadAllText((Join-Path $folder.FullName 'aqua.toml'))
    $platforms = if ($manifest -match '(?m)^\s*platforms\s*=\s*\[([^\]]*)\]') { @([regex]::Matches($Matches[1], '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }) } else { @() }
    $androidPermissions = if ($manifest -match '(?ms)^\[android\][^\[]*?^\s*permissions\s*=\s*\[([^\]]*)\]') { @([regex]::Matches($Matches[1], '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }) } else { @() }
    $support = @(Get-ChildItem -LiteralPath $tests -Directory)
    $modules = @($support | ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter '*.aqua' -File -Recurse } | ForEach-Object FullName)
    foreach ($case in Get-ChildItem -LiteralPath $tests -Filter '*.aqua' -File | Sort-Object Name) {
        $name = [IO.Path]::GetFileNameWithoutExtension($case.Name)
        if ($Cases.Count -and $Cases -notcontains $name) { continue }
        $expectedFile = Join-Path $tests "$name.expected"
        $facts = Get-CaseFacts $case.FullName
        $wasm = Join-Path $tests ".aqua-test-$name.wasm"
        $compiled = $false
        $peer = $null
        try {
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
                Install-TestDownloads $tests
                $arguments = @($Compiler, 'build', $case.FullName) + $modules + @('--root', $tests, '--entry', $case.Name, '-o', $wasm)
                foreach ($directory in $support) { $arguments += @('--assets', $directory.FullName) }
                $build = Invoke-Captured $Dotnet $arguments
                if ($build.Code -ne 0) { $row.Result = 'FAIL'; $row.Detail = 'compile: ' + ($build.Output + $build.Error).Trim(); break }
                $compiled = $true
            }
            if ($facts.Server -and -not $peer) { $peer = Start-PeerServer $tests $facts.Server }
            # Every native run gets an empty storage root, so cases never see what an earlier run (or the user) stored.
            $retried = $false
            $storage = Join-Path ([IO.Path]::GetTempPath()) ("aquarius-test-storage-" + [Guid]::NewGuid().ToString('N'))
            [void](New-Item -ItemType Directory -Path $storage)
            switch -Wildcard ($target) {
                'windows-x64' { $run = Invoke-Captured (Join-Path $pack 'aquarius.exe') @($wasm) (@{ AQUARIUS_STORAGE_ROOT = $storage } + $facts.Environment) }
                'linux-x64' {
                    # On Linux itself (CI) the pack runs directly; on Windows it runs in WSL.
                    if ($IsLinux) {
                        $run = Invoke-Captured (Join-Path $pack 'aquarius') @($wasm) (@{ AQUARIUS_STORAGE_ROOT = $storage } + $facts.Environment) $tests
                    } else {
                        $env:MSYS_NO_PATHCONV = '1'
                        $run = Invoke-Captured 'wsl.exe' @('-d', $Distribution, '--cd', (ConvertTo-Wsl $tests), (ConvertTo-Wsl (Join-Path $pack 'aquarius')), (ConvertTo-Wsl $wasm)) `
                            (@{ AQUARIUS_STORAGE_ROOT = $storage; WSLENV = (@('AQUARIUS_STORAGE_ROOT/p') + @($facts.Environment.Keys)) -join ':' } + $facts.Environment)
                    }
                }
                'web' { $run = Invoke-Captured 'node' @((Join-Path $PSScriptRoot 'web-run.mjs'), $pack, $wasm, '--timeout', "$TimeoutSeconds", '--fake-camera') }
                'android-*' {
                    $abi = $target.Substring('android-'.Length)
                    # The library's [android] permissions, declared and granted as an application that needs it would ask.
                    $androidArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'android-run.ps1'), $wasm, '-Runtime', $pack, '-Abi', $abi, '-Run',
                        '-TimeoutSeconds', "$TimeoutSeconds", '-Permissions', ((@('android.permission.INTERNET') + $androidPermissions | Select-Object -Unique) -join ','))
                    if ($androidPermissions.Count) { $androidArguments += @('-Grant', ($androidPermissions -join ',')) }
                    # The device reaches the peer server at its own 127.0.0.1:port through adb reverse.
                    if ($peer) { & $adb reverse "tcp:$($peer.Port)" "tcp:$($peer.Port)" | Out-Null }
                    try {
                        $run = Invoke-Captured 'powershell' $androidArguments
                        # adb's own server sometimes stops mid-run on this kind of machine (it restarts on the next
                        # command): a run that failed because adb was down, not because of its output, runs once more.
                        if ($run.Code -ne 0 -and $run.Error -match 'daemon (still )?not running|no devices/emulators found|device offline') {
                            & $adb start-server 2>$null | Out-Null; & $adb wait-for-device | Out-Null
                            if ($peer) { & $adb reverse "tcp:$($peer.Port)" "tcp:$($peer.Port)" | Out-Null }
                            $run = Invoke-Captured 'powershell' $androidArguments
                            $retried = $true
                        }
                    }
                    finally { if ($peer) { & $adb reverse --remove "tcp:$($peer.Port)" 2>$null | Out-Null } }
                }
            }
            Remove-Item -LiteralPath $storage -Recurse -Force -ErrorAction SilentlyContinue
            # <case>.<platform>.expected (windows, linux, android, web) replaces <case>.expected where a target differs.
            $platformExpected = Join-Path $tests "$name.$(($target -split '-')[0]).expected"
            $expected = [IO.File]::ReadAllText($(if (Test-Path -LiteralPath $platformExpected) { $platformExpected } else { $expectedFile })).Replace("`r`n", "`n")
            if ($run.Output -eq $expected) { $row.Result = 'pass'; if ($retried) { $row.Detail = 'passed on a second run after adb restarted' } }
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
        } finally { Stop-PeerServer $peer }
        if (Test-Path -LiteralPath $wasm) { Remove-Item -LiteralPath $wasm -Force }
    }
}
$results | Format-Table -AutoSize -Wrap Library, Case, Target, Result, Detail | Out-String -Width 220 | Write-Host
$failed = @($results | Where-Object Result -eq 'FAIL').Count
$passed = @($results | Where-Object Result -eq 'pass').Count
$skipped = @($results | Where-Object Result -eq 'skip').Count
Write-Host "$passed passed, $failed failed, $skipped skipped"
if ($failed) { exit 1 }
