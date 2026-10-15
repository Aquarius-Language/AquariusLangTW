param(
    [ValidateSet('desktop','web','android')][string[]]$Platforms = @('desktop','web','android')
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Path dist -Force | Out-Null
    function Invoke-Aqua([string[]]$Arguments) {
        & aqua @Arguments
        if ($LASTEXITCODE -ne 0) { throw "星泉建置失敗：$($Arguments -join ' ')" }
    }
    Invoke-Aqua @('build','backend/aqua.toml','-o','dist/backend.wasm')
    Invoke-Aqua @('build','frontend/aqua.toml','-o','dist/frontend.wasm')
    if ('web' -in $Platforms) { Invoke-Aqua @('build','dist/frontend.wasm','--target','web','-o','public') }
    if ('desktop' -in $Platforms) {
        Invoke-Aqua @('build','dist/backend.wasm','--target','windows','-o','dist/backend.exe')
        Invoke-Aqua @('build','dist/frontend.wasm','--target','windows','-o','dist/frontend.exe')
    }
    if ('android' -in $Platforms) {
        Invoke-Aqua @('build','dist/frontend.wasm','--target','android','--application-id','org.aquariuslang.networklab','--app-name','星泉網路實驗室','-o','dist/frontend.apk')
    }
    Write-Host "建置完成：$PSScriptRoot\dist"
} finally { Pop-Location }
