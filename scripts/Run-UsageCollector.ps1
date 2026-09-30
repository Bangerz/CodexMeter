param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'collector-config.json'),
    [switch]$MergeOnly
)
$ErrorActionPreference = 'Stop'
$configuration = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $configuration.dataDirectory | Out-Null
$arguments = @('--no-warnings', (Join-Path $PSScriptRoot 'usage-collector.mjs'), '--config', $ConfigPath)
if ($MergeOnly) { $arguments += '--merge-only' }
$result = & $configuration.nodePath @arguments 2>&1
$exitCode = $LASTEXITCODE
$logPath = Join-Path $configuration.dataDirectory 'collector-last-run.log'
$result | Set-Content -LiteralPath $logPath -Encoding UTF8
Write-Output $result
exit $exitCode
