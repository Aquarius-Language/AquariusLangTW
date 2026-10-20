#requires -Version 7
param([string]$Dotnet='')
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=Join-Path $repository '.web-build/cuda-packaging-proof'
$project=Join-Path $output 'project'
$cli=Join-Path $repository 'AquariusCli/bin/Release/net8.0/aqua.dll'
if(-not $Dotnet){$Dotnet=Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'}
$env:AQUARIUS_BUILD_TARGETS=Join-Path $repository 'build-targets'
New-Item -ItemType Directory -Path (Join-Path $project 'assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'AquariusTests/onnx/fixtures/add.onnx') -Destination (Join-Path $project 'assets/add.onnx') -Force
$manifest=@'
[project]
name='FutureCudaApplication'
type='executable'
entry='main.aqua'
sources=['main.aqua','off.aqua','fallback.aqua','test.aqua']
[onnx]
cuda=true
[[assets]]
source='assets'
'@
$test=@'
變數 ort=匯入("ONNX");
變數 Run=函式(provider){
    變數 s=ort.CreateSession(目前工作目錄+"/assets/add.onnx",provider);
    變數 y=s.Run(["X","B"],[ort.CreateTensor("float32",[2],[1,2]),ort.CreateTensor("float32",[2],[3,4])],["Y"])[0].GetData();
    如果((provider!="Auto" && s.Backend!=provider) || y[0]!=4 || y[1]!=6){CUDA_PACKAGING_FAILED;}
    印出("AUTO_PACKAGE_PASS",provider,s.Backend,y);s.Dispose();
};
'@
[IO.File]::WriteAllText((Join-Path $project 'aqua.toml'),$manifest,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $project 'test.aqua'),$test,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $project 'main.aqua'),'變數 t=匯入("test.aqua");t.Run("CUDA");',[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $project 'off.aqua'),'變數 t=匯入("test.aqua");變數 o=匯入("ONNX");如果(o.IsSupported("CUDA")){CUDA_OFF_FAILED;}t.Run("CPU");',[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $project 'fallback.aqua'),'變數 t=匯入("test.aqua");變數 o=匯入("ONNX");如果(o.IsSupported("CUDA")){CUDA_DEVICE_NOT_HIDDEN;}t.Run("CPU");t.Run("Auto");',[Text.UTF8Encoding]::new($false))
$temporary=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('星泉 CUDA standalone '+[Guid]::NewGuid().ToString('N'))))
$results=@()
try{
    New-Item -ItemType Directory -Path $temporary | Out-Null
    foreach($mode in @('CUDA','CPU','NoCUDA')){
        $name=$mode.ToLowerInvariant()+'.exe';$compiled=Join-Path $output $name
        $arguments=@($cli,'build',$project,'--target','windows','-o',$compiled)
        if($mode -eq 'CPU'){
            [IO.File]::WriteAllText((Join-Path $project 'aqua.toml'),$manifest.Replace("entry='main.aqua'","entry='off.aqua'"),[Text.UTF8Encoding]::new($false))
            $arguments+=@('--onnx-cuda','false')
        }
        if($mode -eq 'NoCUDA'){
            [IO.File]::WriteAllText((Join-Path $project 'aqua.toml'),$manifest.Replace("entry='main.aqua'","entry='fallback.aqua'"),[Text.UTF8Encoding]::new($false))
        }
        & $Dotnet @arguments
        if($LASTEXITCODE -ne 0){throw 'Generic project executable build failed.'}
        $exe=Join-Path $temporary $name
        Copy-Item -LiteralPath $compiled -Destination $exe
        $start=[Diagnostics.ProcessStartInfo]::new($exe)
        $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$env:SystemRoot
        $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        $start.Environment['PATH']=$env:SystemRoot+'\System32;'+$env:SystemRoot
        $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=Join-Path $temporary '私有執行階段'
        if($mode -eq 'NoCUDA'){$start.Environment['CUDA_VISIBLE_DEVICES']='-1'}else{$null=$start.Environment.Remove('CUDA_VISIBLE_DEVICES')}
        foreach($key in @('CUDA_PATH','AQUARIUS_ONNX_CUDA_PATH','AQUARIUS_ONNX_PROVIDER_PATH','AQUARIUS_BUILD_TARGETS','DOTNET_ROOT')){$null=$start.Environment.Remove($key)}
        $filesBeforeLaunch=@(Get-ChildItem -LiteralPath $temporary -File | Select-Object -ExpandProperty Name)
        $process=[Diagnostics.Process]::Start($start)
        try{
            $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
            if(-not $process.WaitForExit(180000)){$process.Kill($true);throw 'Self-contained CUDA test timed out.'}
            $text=$stdout.GetAwaiter().GetResult();$errors=$stderr.GetAwaiter().GetResult()
            $marker=if($mode -eq 'NoCUDA'){'AUTO_PACKAGE_PASS Auto'}else{'AUTO_PACKAGE_PASS '+$mode}
            if($process.ExitCode -ne 0 -or $text -notmatch $marker){throw ($text+"`n"+$errors)}
            $results+=@{mode=$mode;passed=$true;filesBeforeLaunch=$filesBeforeLaunch;pathEnvironment=$start.Environment['PATH'];workingDirectory=$start.WorkingDirectory;exeDirectory=$temporary;extractionDirectory=$start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'];cudaDevicesHidden=($mode -eq 'NoCUDA');output=$text;diagnostics=$errors;bytes=(Get-Item $compiled).Length}
            Write-Host ('PASS generic project '+$mode+' with EXE-only deployment')
        }finally{$process.Dispose()}
    }
    $results | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'results.json') -Encoding UTF8
}finally{
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if([IO.Path]::GetDirectoryName($temporary) -ne $tempRoot -or [IO.Path]::GetFileName($temporary) -notmatch '^星泉 CUDA standalone [0-9a-f]{32}$'){throw 'Unsafe test cleanup path.'}
    if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Recurse -Force}
    foreach($generated in @('cuda.exe','cpu.exe','nocuda.exe','extraction')){
        $path=[IO.Path]::GetFullPath((Join-Path $output $generated))
        if(-not $path.StartsWith([IO.Path]::GetFullPath($output)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test artifact cleanup path.'}
        if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Recurse -Force}
    }
}
