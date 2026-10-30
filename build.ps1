param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
if(!$OutputPath) { $OutputPath=Join-Path $PSScriptRoot 'DongleDeck.exe' }
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskSource = Join-Path $PSScriptRoot 'source'
$taskReferences = @('System.dll','System.Core.dll','System.ServiceProcess.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $taskFramework $_) }
$taskArguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/langversion:5','/codepage:65001',('/out:' + $OutputPath),('/win32manifest:' + (Join-Path $taskSource 'app.manifest')),('/win32icon:' + (Join-Path $PSScriptRoot 'assets\windows\DongleDeck.ico')),('/resource:' + (Join-Path $PSScriptRoot 'assets\windows\DongleDeck.ico') + ',DongleDeck.ico'),('/resource:' + (Join-Path $taskSource 'App.xaml') + ',DongleDeck.App.xaml'),('/resource:' + (Join-Path $PSScriptRoot 'assets\windows\icon-dark.png') + ',DongleDeck.LogoDark.png'),('/resource:' + (Join-Path $PSScriptRoot 'assets\windows\icon-light.png') + ',DongleDeck.LogoLight.png')) + $taskReferences + @(Join-Path $taskSource 'NativeHid.cs'; Join-Path $taskSource 'ReceiverCore.cs'; Join-Path $taskSource 'ActivityMonitor.cs'; Join-Path $taskSource 'Theme.cs'; Join-Path $taskSource 'AssemblyInfo.cs'; Join-Path $taskSource 'App.cs')
& (Join-Path $taskFramework 'csc.exe') @taskArguments
if($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$taskConfigSource=Join-Path $PSScriptRoot 'DongleDeck.exe.config'
if([IO.Path]::GetFullPath($taskConfigSource) -ne [IO.Path]::GetFullPath($OutputPath+'.config')) { Copy-Item $taskConfigSource ($OutputPath+'.config') -Force }
"Built $OutputPath"
