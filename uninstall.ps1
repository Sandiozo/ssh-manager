#Requires -RunAsAdministrator
# Removes SSH Manager's logon task, stops the server and forgets the install folder. Your settings
# (the home folder) and OpenSSH Server are left alone; remove OpenSSH Server in Settings > System >
# Optional features if you don't need it any more.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Unregister-ScheduledTask -TaskName 'SSH Manager' -Confirm:$false -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name like 'python%'" |
    Where-Object { $_.CommandLine -like "*$root\server.py*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Remove-Item "$env:ProgramData\SSHManager" -Recurse -Force -ErrorAction SilentlyContinue
"SSH Manager is stopped and won't start at logon any more. You can delete this folder now."
