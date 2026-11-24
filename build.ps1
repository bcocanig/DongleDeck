param([switch]$Test,[switch]$Package,[string]$OutputPath)
$ErrorActionPreference='Stop'
$taskSource=Join-Path $PSScriptRoot 'src'
$taskProject=Join-Path $taskSource 'DongleDeck.csproj'
$taskTestProject=Join-Path $taskSource 'tests\DongleDeck.Tests.csproj'

if($Test) {
    & dotnet run --project $taskTestProject -c Release
    if($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    return
}

if(!$OutputPath) { $OutputPath=Join-Path $PSScriptRoot 'dist\DongleDeck.exe' }
$publishDir=Split-Path ([IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

& dotnet publish $taskProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishDir
if($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$builtExe=Join-Path $publishDir 'DongleDeck.exe'
if(([IO.Path]::GetFullPath($builtExe)) -ne ([IO.Path]::GetFullPath($OutputPath))) {
    Move-Item -LiteralPath $builtExe -Destination $OutputPath -Force
}
"Built $OutputPath"

if($Package) {
    $taskVersion=[regex]::Match([IO.File]::ReadAllText((Join-Path $taskSource 'AssemblyInfo.cs')),'AssemblyInformationalVersion\("([^"]+)"\)').Groups[1].Value
    $taskZip=Join-Path $publishDir ("DongleDeckPrime-v$taskVersion-win-x64.zip")
    $packageItems=@($OutputPath,(Join-Path $PSScriptRoot 'LICENSE'))
    $jabraDir=Join-Path $publishDir 'Jabra.Utilities'
    if(Test-Path -LiteralPath $jabraDir) { $packageItems+=$jabraDir }
    Compress-Archive -LiteralPath $packageItems -DestinationPath $taskZip -Force
    ((Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($taskZip)) | Set-Content -LiteralPath ($taskZip+'.sha256') -Encoding ascii
    $taskZip
}
