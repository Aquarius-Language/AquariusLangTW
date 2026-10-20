#requires -Version 7
param([string]$Dotnet='', [string[]]$ProjectNames=@())
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$parent=Split-Path $repository
$output=Join-Path $repository '.web-build/compatibility'
$cli=Join-Path $repository 'AquariusCli/bin/Release/net8.0/aqua.dll'
if(-not $Dotnet){$Dotnet=Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$env:AQUARIUS_BUILD_TARGETS=Join-Path $repository 'build-targets'
$env:AQUA_BOTTLE_HOME=Join-Path $output 'bottles'
$results=[Collections.Generic.List[object]]::new()
if($ProjectNames.Count -and (Test-Path (Join-Path $output 'extended-results.json'))){
    foreach($row in Get-Content (Join-Path $output 'extended-results.json') -Raw | ConvertFrom-Json){if($row.project -notin $ProjectNames){$results.Add($row)}}
}
$script:caseName='';$script:sequence=0
function Invoke-Program([string]$Exe,[string[]]$Arguments,[string]$Marker='',[int]$Frames=0,[string]$WorkingDirectory=$output,[int]$Timeout=180){
    $script:sequence++
    $start=[Diagnostics.ProcessStartInfo]::new($Exe)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$WorkingDirectory
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.Encoding]::UTF8;$start.StandardErrorEncoding=[Text.Encoding]::UTF8
    foreach($arg in $Arguments){$start.ArgumentList.Add($arg)}
    $start.Environment['AQUARIUS_GRAPHICS_FRAMES']=[string]$Frames
    $null=$start.Environment.Remove('AQUARIUS_GRAPHICS_CAPTURE')
    $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=Join-Path $output 'extracted'
    $process=[Diagnostics.Process]::Start($start)
    try{
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit($Timeout*1000)){$process.Kill($true);throw ('Timed out: '+($Arguments -join ' '))}
        $text=$stdout.GetAwaiter().GetResult();$errorText=$stderr.GetAwaiter().GetResult()
        $log=Join-Path $output ($script:caseName+'-'+$script:sequence+'.txt')
        [IO.File]::WriteAllText($log,$text+"`nSTDERR:`n"+$errorText,[Text.UTF8Encoding]::new($false))
        if($process.ExitCode -ne 0 -or ($Marker -and $text -notmatch $Marker) -or $text -match '(?m)^ERROR:|SHOWCASE_BOT_FAIL|INSPECTOR_GUI_FAILED|SHOWCASE_FAILED'){
            throw ($text+"`n"+$errorText)
        }
        return @{output=$text;diagnostics=$errorText;log=$log;exitCode=$process.ExitCode}
    }finally{$process.Dispose()}
}
function Aqua([string[]]$Arguments,[string]$Marker='',[int]$Frames=0,[string]$WorkingDirectory=$output){
    Invoke-Program $Dotnet (@($cli)+$Arguments) $Marker $Frames $WorkingDirectory
}
function Project([string]$Name,[string]$ProjectRoot,[string[]]$Entries,[string[]]$Markers,[switch]$NoWeb,[switch]$Gui){
    $directory=Join-Path $output $Name;New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $wasm=Join-Path $directory 'app.wasm'
    Aqua @('build',$ProjectRoot,'--platform','windows','-o',$wasm) | Out-Null
    if(-not $NoWeb){Aqua @('build',$ProjectRoot,'--target','web','-o',(Join-Path $directory 'web')) | Out-Null}
    for($i=0;$i -lt $Entries.Count;$i++){
        $run=Aqua @('run',$wasm,'--entry',$Entries[$i]) $Markers[$i]
        $results.Add(@{project=$Name;mode='wasm';entry=$Entries[$i];passed=$true;output=$run.output;log=$run.log})
        $exe=Join-Path $directory ('verify-'+$i+'.exe')
        Aqua @('build',$wasm,'--target','windows','--entry',$Entries[$i],'-o',$exe) | Out-Null
        $run=Invoke-Program $exe @() $Markers[$i] 0 $ProjectRoot
        $results.Add(@{project=$Name;mode='exe';entry=$Entries[$i];passed=$true;output=$run.output;log=$run.log})
    }
    if($Gui){
        $run=Aqua @('run',$wasm) '' 2 $ProjectRoot
        $results.Add(@{project=$Name;mode='gui';passed=$true;output=$run.output;log=$run.log})
    }
}
$cases=@(
    @{name='bottle';run={Project 'bottle' (Join-Path $parent 'AquariusLangTW_BottleShowcase') @('main.aqua') @('BOTTLE_SHOWCASE_OK')}},
    @{name='opencv';run={Project 'opencv' (Join-Path $parent 'Aqua_OpenCV_Showcase') @('功能驗證.aqua','雙語驗證.aqua') @('OpenCV功能驗證通過 45','OpenCV雙語驗證通過') -Gui}},
    @{name='jbridging';run={
        $env:JAVA_HOME=Join-Path $parent 'Aqua_JBridging_Showcase/jdk'
        Project 'jbridging' (Join-Path $parent 'Aqua_JBridging_Showcase') @('main.aqua') @('JBRIDGING_SHOWCASE_OK') -NoWeb
    }},
    @{name='inspector';run={
        # The inspector deliberately resolves its JDK/JARs from the original project folder.
        $root=Join-Path $parent 'AquariusLangTW_IJ_ColorInspector3D'
        # Preserve the documented app/JDK/JAR layout for the relative Java runtime.
        $wasm=Join-Path $root '.compatibility-inspector.wasm'
        try{
          Aqua @('build',$root,'--platform','windows','-o',$wasm) | Out-Null
          foreach($entry in @('validate.aqua','gui-smoke.aqua')){
            $marker=if($entry -eq 'validate.aqua'){'AQUARIUS_INSPECTOR_VALIDATED'}else{'INSPECTOR_GUI_VALIDATED'}
            $run=Aqua @('run',$wasm,'--entry',$entry) $marker 0 $root
            $results.Add(@{project='inspector';mode='wasm';entry=$entry;passed=$true;output=$run.output;log=$run.log})
          }
        }finally{if(Test-Path -LiteralPath $wasm){Remove-Item -LiteralPath $wasm -Force}}
    }},
    @{name='filament';run={Project 'filament' (Join-Path $parent 'AquariusLangTW_Filament_Showcase') @('tests/capture.aqua') @('SHOWCASE_CAPTURE_OK')}},
    @{name='dynamic-linking';run={
        $root=Join-Path $parent 'AquariusLang_DynamicLinkingShowcase';$directory=Join-Path $output 'dynamic-linking'
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $wasm=Join-Path $directory 'app.wasm'
        Aqua @('build',(Join-Path $root 'main.aqua'),'--root',$root,'--assets',(Join-Path $root 'libraries'),'--assets',(Join-Path $root 'native'),'-o',$wasm) | Out-Null
        $run=Aqua @('run',$wasm) 'DYNAMIC_LINKING_OK'
        $results.Add(@{project='dynamic-linking';mode='wasm';passed=$true;output=$run.output;log=$run.log})
        $exe=Join-Path $directory 'verify.exe';Aqua @('build',$wasm,'--target','windows','-o',$exe) | Out-Null
        $run=Invoke-Program $exe @() 'DYNAMIC_LINKING_OK'
        $results.Add(@{project='dynamic-linking';mode='exe';passed=$true;output=$run.output;log=$run.log})
        Aqua @('build',$wasm,'--target','web','-o',(Join-Path $directory 'web')) | Out-Null
    }},
    @{name='dreamer';run={
        $root=Join-Path $parent 'AquariusDreamer';$directory=Join-Path $output 'dreamer'
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $wasm=Join-Path $directory 'app.wasm';Aqua @('build',$root,'--platform','windows','-o',$wasm) | Out-Null
        # Reuse the project's offline Markdown regression without contacting any AI service.
        Copy-Item -LiteralPath (Join-Path $root 'tests/markdown.aqua') -Destination (Join-Path $directory 'markdown.aqua') -Force
        Copy-Item -LiteralPath (Join-Path $root 'tests/headless-processing.aqua') -Destination (Join-Path $directory 'headless-processing.aqua') -Force
        Copy-Item -LiteralPath (Join-Path $root '原始碼/介面/排版.aqua') -Destination (Join-Path $directory 'layout.aqua') -Force
        $manifest="[project]`nname='DreamerMarkdownCompatibility'`ntype='executable'`nentry='markdown.aqua'`nsources=['markdown.aqua','headless-processing.aqua','layout.aqua']`n"
        [IO.File]::WriteAllText((Join-Path $directory 'aqua.toml'),$manifest,[Text.UTF8Encoding]::new($false))
        $tests=Join-Path $directory 'tests.wasm';Aqua @('build',$directory,'-o',$tests) | Out-Null
        $run=Aqua @('run',$tests) 'MARKDOWN_TESTS_PASSED'
        $results.Add(@{project='dreamer';mode='offline-tests';passed=$true;output=$run.output;log=$run.log})
    }}
)
foreach($case in $cases){
    if($ProjectNames.Count -and $case.name -notin $ProjectNames){continue}
    $script:caseName=$case.name
    try{& $case.run;Write-Host ('PASS '+$case.name)}
    catch{$results.Add(@{project=$case.name;passed=$false;error=$_.Exception.Message});Write-Host ('FAIL '+$case.name+': '+$_.Exception.Message)}
    $report=Join-Path $output 'extended-results.json'
    $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report -Encoding UTF8
}
if(@($results | Where-Object {-not $_.passed}).Count){exit 1}

