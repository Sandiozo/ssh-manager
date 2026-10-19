#Requires -RunAsAdministrator
# Registers the "SSH Manager" scheduled task: starts the dashboard at logon with admin rights (needed
# to read the command lines of SSH sessions, which run elevated) and restarts it if it crashes.
# Run by setup.ps1; undo with Uninstall.cmd.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$user = "$env:USERDOMAIN\$env:USERNAME"

$action = New-ScheduledTaskAction -Execute "$root\.venv\Scripts\pythonw.exe" -Argument "`"$root\server.py`"" -WorkingDirectory $root
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'SSH Manager' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

# Replace a copy that is already running without admin rights, then start the task now.
Get-CimInstance Win32_Process -Filter "Name like 'python%'" |
    Where-Object { $_.CommandLine -like "*$root\server.py*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Seconds 1
Start-ScheduledTask -TaskName 'SSH Manager'
"SSH Manager will now start with admin rights at every logon. It is running at http://localhost:8765"
