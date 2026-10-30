param([string]$Version='0.4.0')
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a numeric version such as 0.4.0.' }
$taskDist=Join-Path $PSScriptRoot 'dist'
$taskBin=Join-Path $taskDist 'package'
New-Item -ItemType Directory -Path $taskBin -Force | Out-Null
& (Join-Path $PSScriptRoot 'build.ps1') -OutputPath (Join-Path $taskBin 'DongleDeck.exe')
if($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$taskArchive=Join-Path $taskDist ("DongleDeck-v$Version-win-x64.zip")
$taskFiles=@(Join-Path $taskBin 'DongleDeck.exe'; Join-Path $taskBin 'DongleDeck.exe.config') + @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','assets','docs' | ForEach-Object { Join-Path $PSScriptRoot $_ })
Compress-Archive -LiteralPath $taskFiles -DestinationPath $taskArchive -Force
$taskHash=Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256
($taskHash.Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($taskArchive)) | Set-Content -LiteralPath ($taskArchive+'.sha256') -Encoding ascii
$taskArchive
