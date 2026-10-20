#requires -Version 7
param([string]$Dotnet='')
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$root=Join-Path (Split-Path $repository) 'AquariusLangTW_MineClone'
$output=Join-Path $repository '.web-build/compatibility/mineclone';$project=Join-Path $output 'project'
if(-not $Dotnet){$Dotnet=Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'}
$cli=Join-Path $repository 'AquariusCli/bin/Release/net8.0/aqua.dll'
New-Item -ItemType Directory -Path $project -Force | Out-Null
foreach($directory in @('src','assets','shaders')){Copy-Item -LiteralPath (Join-Path $root $directory) -Destination $project -Recurse -Force}
# Use the project's own full survival test in a disposable source copy. Its new-world
# flag suppresses saves; bypass preference reads too, leaving the user's game untouched.
$main=Get-Content (Join-Path $root 'main.aqua') -Raw
$main=$main.Replace('變數 讀設定 = 存放.GetPreference(設定鍵);','變數 讀設定 = 空;')
[IO.File]::WriteAllText((Join-Path $project 'main.aqua'),$main,[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $root 'tools/debug/debug.aqua') -Destination (Join-Path $project 'src/debug.aqua') -Force
$sources=@((Join-Path $project 'main.aqua'))+@(Get-ChildItem (Join-Path $project 'src') -Filter '*.aqua' -File | Select-Object -ExpandProperty FullName)
$wasm=Join-Path $output 'mineclone.wasm'
$env:AQUARIUS_BUILD_TARGETS=Join-Path $repository 'build-targets'
& $Dotnet $cli build @sources --root $project --entry main.aqua --assets (Join-Path $project 'assets') --assets (Join-Path $project 'shaders') -o $wasm
if($LASTEXITCODE -ne 0){throw 'MineClone test build failed.'}
& $Dotnet $cli build $wasm --target web -o (Join-Path $output 'web')
if($LASTEXITCODE -ne 0){throw 'MineClone web export failed.'}
$exe=Join-Path $output 'MineClone.exe';& $Dotnet $cli build $wasm --target windows -o $exe
if($LASTEXITCODE -ne 0){throw 'MineClone Windows export failed.'}
$start=[Diagnostics.ProcessStartInfo]::new($exe);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$project
$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true;$start.StandardOutputEncoding=[Text.Encoding]::UTF8;$start.StandardErrorEncoding=[Text.Encoding]::UTF8
$started=[DateTime]::UtcNow
$p=[Diagnostics.Process]::Start($start)
try{
    $stdout=$p.StandardOutput.ReadToEndAsync();$stderr=$p.StandardError.ReadToEndAsync()
    if(-not $p.WaitForExit(300000)){$p.Kill($true);throw 'MineClone survival test timed out.'}
    $text=$stdout.GetAwaiter().GetResult();$errors=$stderr.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $output 'survival.txt'),$text+"`nSTDERR:`n"+$errors,[Text.UTF8Encoding]::new($false))
    $screenshots=@(Get-ChildItem -LiteralPath $output -Filter 'cap-*.png' -File | Where-Object LastWriteTimeUtc -ge $started)
    $passed=$p.ExitCode -eq 0 -and $text -match 'savecheck inventory match 真' -and $text -match 'debug step quit' -and $screenshots.Count -ge 12
    @{project='mineclone';passed=$passed;screenshots=$screenshots.Count;output=$text;diagnostics=$errors;isolatedNewWorld=$true} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'results.json') -Encoding UTF8
    if(-not $passed){throw ($text+"`n"+$errors)}
    Write-Host ('PASS MineClone survival, crafting, save round-trip and '+$screenshots.Count+' screenshots')
}finally{$p.Dispose()}
