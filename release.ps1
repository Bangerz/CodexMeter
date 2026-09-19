param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a semantic version such as 0.1.0.' }
[xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'CodexMeter.csproj') -Raw
if ($project.Project.PropertyGroup.Version -ne $Version) { throw 'Release version must match CodexMeter.csproj.' }
& (Join-Path $PSScriptRoot 'build.ps1')
dotnet run --project (Join-Path $PSScriptRoot 'tests\CodexMeter.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Release checks failed.' }
$releaseRoot = Join-Path $PSScriptRoot 'artifacts\releases'
$stage = Join-Path $releaseRoot ('stage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'docs\images') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'artifacts\publish\CodexMeter.exe') -Destination $stage
foreach ($file in @('README.md','LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $stage
}
foreach ($file in @('weekly.png','icon-sizes.png')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('docs\images\' + $file)) -Destination (Join-Path $stage 'docs\images')
}
$exeHash = (Get-FileHash -LiteralPath (Join-Path $stage 'CodexMeter.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $stage 'SHA256SUMS.txt'), $exeHash + '  CodexMeter.exe' + [Environment]::NewLine)
$zip = Join-Path $releaseRoot "CodexMeter-v$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $releaseRoot 'SHA256SUMS.txt'), $zipHash + '  ' + [IO.Path]::GetFileName($zip) + [Environment]::NewLine)
Get-Item -LiteralPath $zip,(Join-Path $releaseRoot 'SHA256SUMS.txt') | Select-Object Name,Length
