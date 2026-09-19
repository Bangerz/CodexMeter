param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\publish'))
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\nuget'
$env:TEMP = Join-Path $PSScriptRoot 'artifacts\tmp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:TEMP | Out-Null
dotnet publish (Join-Path $PSScriptRoot 'CodexMeter.csproj') -c Release -r win-x64 -p:SelfContained=false -p:PublishSelfContained=false -p:PublishSingleFile=true -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host (Join-Path $OutputDirectory 'CodexMeter.exe')
