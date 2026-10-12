param([string]$Dotnet='', [string]$Configuration='Debug', [switch]$SkipBuild, [switch]$WindowsExecutable)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $Dotnet){$localDotnet=Join-Path $env:USERPROFILE '.dotnet/dotnet.exe';$Dotnet=if(Test-Path -LiteralPath $localDotnet){$localDotnet}else{'dotnet'}}
$output=Join-Path $repository '.web-build/pbr'
$project=Join-Path $repository 'examples/pbr_models'
$cli=Join-Path $repository "AquariusCli/bin/$Configuration/net8.0/aqua.dll"
function Invoke-Aqua([string[]]$arguments){
    $text=@(& $Dotnet $cli @arguments)
    if($LASTEXITCODE -ne 0){throw "PBR validation failed: $($text -join [Environment]::NewLine)"}
    return $text -join [Environment]::NewLine
}
$previousFrames=$env:AQUARIUS_GRAPHICS_FRAMES
$previousCapture=$env:AQUARIUS_GRAPHICS_CAPTURE
$previousWeb=$env:AQUARIUS_PBR_WEB
Push-Location $repository
try{
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    if(-not $SkipBuild){& $Dotnet build AquariusCli -c $Configuration -m:1 --nologo;if($LASTEXITCODE -ne 0){throw 'Compiler build failed'}}
    $sources=@(Get-ChildItem -LiteralPath $project -Filter '*.aqua' | Sort-Object Name | ForEach-Object FullName)
    $wasm=Join-Path $output 'showcase/pbr_models.wasm'
    Invoke-Aqua (@('build')+$sources+@('--root',$project,'--assets',(Join-Path $project 'assets'),'--entry','main.aqua','-o',$wasm)) | Write-Output
    $env:AQUARIUS_PBR_WEB=Join-Path $output 'showcase/web'
    Invoke-Aqua @('build',$wasm,'--target','web','-o',$env:AQUARIUS_PBR_WEB) | Write-Output
    foreach($entry in @('smoke.aqua','gpu-smoke.aqua')){
        $env:AQUARIUS_GRAPHICS_FRAMES='2';Remove-Item Env:AQUARIUS_GRAPHICS_CAPTURE -ErrorAction SilentlyContinue
        $result=Invoke-Aqua @('run',$wasm,'--entry',$entry)
        if($result -notmatch '驗證通過'){throw "Failed $entry assertions: $result"};Write-Output $result
    }
    $env:AQUARIUS_GRAPHICS_FRAMES='3';$env:AQUARIUS_GRAPHICS_CAPTURE=Join-Path $output 'desktop.png'
    Invoke-Aqua @('run',$wasm) | Write-Output
    if(-not(Test-Path -LiteralPath $env:AQUARIUS_GRAPHICS_CAPTURE)){throw 'Desktop capture missing'}
    if($WindowsExecutable){
        $exe=Join-Path $output 'pbr_models.exe';Invoke-Aqua @('build',$wasm,'--target','windows','-o',$exe) | Write-Output
        $env:AQUARIUS_GRAPHICS_CAPTURE=Join-Path $output 'windows-exe.png'; & $exe
        if($LASTEXITCODE -ne 0 -or -not(Test-Path -LiteralPath $env:AQUARIUS_GRAPHICS_CAPTURE)){throw 'Standalone EXE validation failed'}
    }
    & node AquariusWebCompiler/tests/pbr-browser.mjs
    if($LASTEXITCODE -ne 0){throw 'Browser PBR validation failed'}
}finally{
    $env:AQUARIUS_GRAPHICS_FRAMES=$previousFrames;$env:AQUARIUS_GRAPHICS_CAPTURE=$previousCapture;$env:AQUARIUS_PBR_WEB=$previousWeb;Pop-Location
}
