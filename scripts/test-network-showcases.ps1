#requires -Version 7
param([string]$Dotnet='')
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$parent=Split-Path $repository
$output=Join-Path $repository '.web-build/compatibility/network';New-Item -ItemType Directory -Force $output | Out-Null
$cli=Join-Path $repository 'AquariusCli/bin/Release/net8.0/aqua.dll'
if(-not $Dotnet){$Dotnet=Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'}
$env:AQUA_BOTTLE_HOME=Join-Path $output 'bottles';$env:AQUARIUS_BUILD_TARGETS=Join-Path $repository 'build-targets'
$results=@();$script:sequence=0
function Start-Command([string]$Exe,[string[]]$Arguments,[int]$Frames=0){
    $start=[Diagnostics.ProcessStartInfo]::new($Exe);$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.WorkingDirectory=$output;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.Encoding]::UTF8;$start.StandardErrorEncoding=[Text.Encoding]::UTF8
    $start.Environment['AQUARIUS_GRAPHICS_FRAMES']=[string]$Frames
    foreach($a in $Arguments){$start.ArgumentList.Add($a)}
    $p=[Diagnostics.Process]::Start($start)
    return @{process=$p;stdout=$p.StandardOutput.ReadToEndAsync();stderr=$p.StandardError.ReadToEndAsync()}
}
function Run([string[]]$Arguments,[string]$Marker='', [int]$Frames=0){
    $script:sequence++;$command=Start-Command $Dotnet (@($cli)+$Arguments) $Frames
    try{
        if(-not $command.process.WaitForExit(120000)){$command.process.Kill($true);throw 'Network showcase command timed out.'}
        $text=$command.stdout.GetAwaiter().GetResult();$errors=$command.stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $output ('command-'+$script:sequence+'.txt')),$text+"`nSTDERR:`n"+$errors,[Text.UTF8Encoding]::new($false))
        if($command.process.ExitCode -ne 0 -or ($Marker -and $text -notmatch $Marker)){throw ($text+"`n"+$errors)}
        return $text
    }finally{$command.process.Dispose()}
}
function Wait-Port($Command,[int]$Port){
    $deadline=(Get-Date).AddSeconds(30)
    do{
        if($Command.process.HasExited){throw ($Command.stdout.GetAwaiter().GetResult()+$Command.stderr.GetAwaiter().GetResult())}
        $client=[Net.Sockets.TcpClient]::new()
        try{$task=$client.ConnectAsync('127.0.0.1',$Port);if($task.Wait(200) -and $client.Connected){return}}catch{}finally{$client.Dispose()}
        Start-Sleep -Milliseconds 100
    }while((Get-Date) -lt $deadline)
    throw ('Server did not open port '+$Port)
}
foreach($case in @(@{name='crdt-webrtc';folder='AquariusLangTW_CRDT_WebRTC_Showcase';server='server';client='client';ports=@(8090,8091,8080);entry='bot.aqua';marker='SHOWCASE_BOT_OK'},@{name='network-lab';folder='AquariusLangTW_NetworkLab';server='backend';client='frontend';ports=@(8787);entry='驗證.aqua';marker='網路驗證完成 真'})){
    $server=$null
    try{
        foreach($port in $case.ports){if(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue){throw ('Port '+$port+' is already in use; no existing service was stopped.')}}
        $root=Join-Path $parent $case.folder;$dir=Join-Path $output $case.name;New-Item -ItemType Directory -Force $dir | Out-Null
        $backend=Join-Path $dir 'server.wasm';$frontend=Join-Path $dir 'client.wasm'
        Run @('build',(Join-Path $root $case.server),'--platform','windows','-o',$backend) | Out-Null
        Run @('build',(Join-Path $root $case.client),'--platform','windows','-o',$frontend) | Out-Null
        Run @('build',(Join-Path $root $case.client),'--target','web','-o',(Join-Path $dir 'web')) | Out-Null
        foreach($item in @(@{wasm=$backend;exe='server.exe'},@{wasm=$frontend;exe='client.exe'})){Run @('build',$item.wasm,'--target','windows','-o',(Join-Path $dir $item.exe)) | Out-Null}
        $server=Start-Command (Join-Path $dir 'server.exe') @()
        Wait-Port $server $case.ports[0]
        $text=Run @('run',$frontend,'--entry',$case.entry) $case.marker
        $results+=@{project=$case.name;mode='standalone-server-integration';passed=$true;output=$text}
        $gui=Start-Command (Join-Path $dir 'client.exe') @() 3
        try{
            if(-not $gui.process.WaitForExit(60000)){$gui.process.Kill($true);throw 'Network UI did not exit after three frames.'}
            $text=$gui.stdout.GetAwaiter().GetResult();$errors=$gui.stderr.GetAwaiter().GetResult()
            if($gui.process.ExitCode -ne 0){throw ($text+"`n"+$errors)}
            $results+=@{project=$case.name;mode='standalone-client-ui';passed=$true;output=$text;diagnostics=$errors}
        }finally{$gui.process.Dispose()}
        Write-Host ('PASS '+$case.name+' standalone server, protocol verification, desktop UI and web export')
    }catch{$results+=@{project=$case.name;passed=$false;error=$_.Exception.Message};Write-Host ('FAIL '+$case.name+': '+$_.Exception.Message)}
    finally{
        if($server){if(-not $server.process.HasExited){$server.process.Kill($true);$server.process.WaitForExit()};[IO.File]::WriteAllText((Join-Path $output ($case.name+'-server.txt')),$server.stdout.GetAwaiter().GetResult()+"`nSTDERR:`n"+$server.stderr.GetAwaiter().GetResult(),[Text.UTF8Encoding]::new($false));$server.process.Dispose()}
        $results | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'results.json') -Encoding UTF8
    }
}
if(@($results | Where-Object {-not $_.passed}).Count){exit 1}
