# Builds an application .wasm into an iOS simulator application with this checkout's compiler (aqua build --target ios
# --simulator true) and runs it on a simulator: the program's standard output and errors are printed, and the exit status
# is 0 once it finished (the output says how it went). Test helper for runtime and library work on a
# Mac with Xcode and an iOS simulator runtime; exported applications are built by the compiler's iOS build target.
#   scripts/ios-run.ps1 app.wasm [-Device 'iPhone 18 Pro'] [-Environment @{ AQUARIUS_GRAPHICS_FRAMES = '3' }]
param(
    [Parameter(Mandatory)][string]$Program,
    [string]$Device = '',                                       # a simulator's name or UDID; default: the first booted, else the first iPhone
    [string]$Compiler = '',                                     # aqua.dll (default: this checkout's Debug build)
    [string]$BundleId = 'org.aquariuslang.test.run',
    [hashtable]$Environment = @{},
    [int]$TimeoutSeconds = 120,
    [switch]$Keep                                               # leave the application installed
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'toolchain.ps1')
if (-not $IsMacOS) { throw 'iOS simulator runs need a Mac with Xcode.' }
if (-not $Compiler) { $Compiler = Join-Path $AquaToolchain.Repository 'compiler/AquariusCli/bin/Debug/net8.0/aqua.dll' }

# The simulator: the named one, else a booted one, else the first available iPhone; booted if needed.
$devices = (& xcrun simctl list devices available --json | ConvertFrom-Json).devices.PSObject.Properties |
    ForEach-Object { $_.Value } | Where-Object { $_ }
$simulator = if ($Device) { $devices | Where-Object { $_.name -eq $Device -or $_.udid -eq $Device } | Select-Object -First 1 }
    else { ($devices | Where-Object state -eq 'Booted' | Select-Object -First 1) ?? ($devices | Where-Object name -like 'iPhone*' | Select-Object -First 1) }
if (-not $simulator) { throw 'No iOS simulator is available: install one with xcodebuild -downloadPlatform iOS.' }
if ($simulator.state -ne 'Booted') { & xcrun simctl boot $simulator.udid | Out-Null; & xcrun simctl bootstatus $simulator.udid -b | Out-Null }

$work = Join-Path ([IO.Path]::GetTempPath()) ('aquarius-ios-run-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $app = Join-Path $work 'AquariusTest.app'
    & dotnet $Compiler build $Program --target ios --simulator true --bundle-id $BundleId -o $app | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Building the iOS application failed (exit $LASTEXITCODE)." }
    & xcrun simctl install $simulator.udid $app
    if ($LASTEXITCODE -ne 0) { throw 'Installing the application on the simulator failed.' }
    # --console-pty stays attached until the application exits; SIMCTL_CHILD_* variables reach the application.
    $start = [Diagnostics.ProcessStartInfo]::new('xcrun')
    foreach ($argument in @('simctl', 'launch', '--console-pty', '--terminate-running-process', $simulator.udid, $BundleId)) { $start.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $start.Environment["SIMCTL_CHILD_$key"] = [string]$Environment[$key] }
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync(); $errors = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        & xcrun simctl terminate $simulator.udid $BundleId 2>$null | Out-Null
        $process.WaitForExit()
        throw "The application did not finish within $TimeoutSeconds s."
    }
    # simctl adds a "<bundle id>: <pid>" line; the rest is the application's output (a pty: CR LF line ends).
    $lines = @($output.Result.Replace("`r`n", "`n") -split "`n" | Where-Object { $_ -notmatch "^$([regex]::Escape($BundleId)): \d+$" })
    [Console]::Out.Write($lines -join "`n")
    exit 0
} finally {
    if (-not $Keep) { & xcrun simctl uninstall $simulator.udid $BundleId 2>$null | Out-Null }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
