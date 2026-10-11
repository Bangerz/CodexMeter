param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [switch]$TestNotification
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

# A child of a packaged editor can inherit filesystem/registry redirection.
# Use the existing desktop's shell, not Shell.Application.ShellExecute on a
# newly created shell object (which can retain the caller's environment).
$shell = New-Object -ComObject Shell.Application
$windows = $shell.Windows()
$location = 0
$root = 0
$handle = 0
$desktop = $windows.FindWindowSW([ref]$location, [ref]$root, 8, [ref]$handle, 1)
if ($null -eq $desktop) { throw 'Interactive Explorer desktop is unavailable.' }
$arguments = if ($TestNotification) { '--test-notification' } else { '' }
$desktop.Document.Application.ShellExecute($Executable, $arguments, [IO.Path]::GetDirectoryName($Executable), 'open', 0)
Write-Output 'Launch requested through the interactive Explorer desktop. This does not confirm notification visibility.'
