$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskSource = Join-Path $PSScriptRoot 'source'
$taskTestOutput = Join-Path $env:TEMP 'DongleDeck.ProtocolTests.exe'
& (Join-Path $taskFramework 'csc.exe') /nologo /target:exe /platform:x64 /langversion:5 ('/out:'+$taskTestOutput) ('/reference:'+(Join-Path $taskFramework 'System.ServiceProcess.dll')) (Join-Path $taskSource 'NativeHid.cs') (Join-Path $taskSource 'ReceiverCore.cs') (Join-Path $taskSource 'ProtocolTests.cs')
if($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
& $taskTestOutput
if($LASTEXITCODE -ne 0) { throw 'Protocol tests failed.' }
