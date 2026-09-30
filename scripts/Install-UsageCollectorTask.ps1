param(
    [string]$CollectorDirectory = $PSScriptRoot,
    [string]$TaskName = 'Coding Agent Usage Collector',
    [int]$LogonDelaySeconds = 120
)
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $CollectorDirectory 'Run-UsageCollector.ps1'
$configPath = Join-Path $CollectorDirectory 'collector-config.json'
if (-not (Test-Path -LiteralPath $scriptPath) -or -not (Test-Path -LiteralPath $configPath)) { throw 'Install the collector scripts and config before registering the task.' }
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$userId = $identity.User.Value
$powershellPath = (Get-Command powershell.exe).Source
$action = New-ScheduledTaskAction -Execute $powershellPath -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -ConfigPath "{1}"' -f $scriptPath, $configPath) -WorkingDirectory $CollectorDirectory
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$trigger.Delay = 'PT{0}S' -f $LogonDelaySeconds
$principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$task = New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Collect local coding-agent usage two minutes after this user logs in; a persisted seven-day attempt guard prevents more than one collection per week, including failures.'
Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Select-Object TaskName,TaskPath,State
