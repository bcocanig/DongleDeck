param([switch]$Test,[switch]$Package,[string]$OutputPath)
$ErrorActionPreference='Stop'
$taskFramework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskSource=Join-Path $PSScriptRoot 'src'
$taskAssets=Join-Path $taskSource 'assets\windows'
if(!$OutputPath) { $OutputPath=Join-Path $PSScriptRoot 'dist\DongleDeck.exe' }
if($Test) { $OutputPath=Join-Path $env:TEMP 'DongleDeck.Tests.exe' }
New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($OutputPath))) -Force | Out-Null
$taskReferences=@('System.dll','System.Core.dll','System.ServiceProcess.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:'+(Join-Path $taskFramework $_) }
$taskSources=@(Get-ChildItem -LiteralPath $taskSource -Filter '*.cs' | ForEach-Object {$_.FullName})
$taskArgs=@('/nologo','/platform:x64','/optimize+','/langversion:5','/codepage:65001',('/out:'+$OutputPath),('/resource:'+(Join-Path $taskSource 'App.xaml')+',DongleDeck.App.xaml'),('/resource:'+(Join-Path $taskAssets 'DongleDeck.ico')+',DongleDeck.ico'),('/resource:'+(Join-Path $taskAssets 'icon-dark.png')+',DongleDeck.LogoDark.png'),('/resource:'+(Join-Path $taskAssets 'icon-light.png')+',DongleDeck.LogoLight.png'))+$taskReferences
if($Test) {
    $taskArgs+=@('/target:exe','/main:DongleDeck.ProtocolTests')+@(Get-ChildItem -LiteralPath (Join-Path $taskSource 'tests') -Filter '*.cs' | ForEach-Object {$_.FullName})
} else {
    $taskArgs+=@('/target:winexe',('/win32manifest:'+(Join-Path $taskSource 'app.manifest')),('/win32icon:'+(Join-Path $taskAssets 'DongleDeck.ico')))
}
$taskArgs+=$taskSources
& (Join-Path $taskFramework 'csc.exe') @taskArgs
if($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if($Test) {
    & $OutputPath
    if($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    return
}
"Built $OutputPath"
if($Package) {
    $taskVersion=[regex]::Match([IO.File]::ReadAllText((Join-Path $taskSource 'AssemblyInfo.cs')),'AssemblyInformationalVersion\("([^"]+)"\)').Groups[1].Value
    $taskZip=Join-Path (Split-Path $OutputPath) ("DongleDeck-v$taskVersion-win-x64.zip")
    Compress-Archive -LiteralPath @($OutputPath,(Join-Path $PSScriptRoot 'LICENSE')) -DestinationPath $taskZip -Force
    ((Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($taskZip)) | Set-Content -LiteralPath ($taskZip+'.sha256') -Encoding ascii
    $taskZip
}
