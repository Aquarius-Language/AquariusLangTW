param()
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not [OperatingSystem]::IsWindows() -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw 'This bootstrap supports Windows x64. On other platforms install Zig 0.15.1 and pass its executable to build-wasm-runtime.ps1 -Compiler.' }
$toolchains = Join-Path $repository '.native-build/toolchains'
$directory = Join-Path $toolchains 'zig-x86_64-windows-0.15.1'
$compiler = Join-Path $directory 'zig.exe'
if (Test-Path -LiteralPath $compiler) { if ((& $compiler version) -ne '0.15.1') { throw 'Unexpected installed Zig version.' }; Write-Output $compiler; return }
New-Item -ItemType Directory -Path $toolchains -Force | Out-Null
$archive = Join-Path $toolchains 'zig-x86_64-windows-0.15.1.zip'
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri 'https://ziglang.org/download/0.15.1/zig-x86_64-windows-0.15.1.zip' -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne '91e69e887ca8c943ce9a515df3af013d95a66a190a3df3f89221277ebad29e34') { throw 'Zig archive checksum mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath $toolchains
if ((& $compiler version) -ne '0.15.1') { throw 'Zig toolchain verification failed.' }
Write-Output $compiler
