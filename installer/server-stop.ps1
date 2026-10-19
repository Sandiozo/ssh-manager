# Run by the installer before files are replaced (-Remove when uninstalling): stops the server, and on
# uninstall removes the logon task and %ProgramData%\SSHManager. OpenSSH Server and settings stay.
param([switch]$Remove)
Stop-ScheduledTask -TaskName 'SSH Manager' -ErrorAction SilentlyContinue
Get-Process SSHManagerServer -ErrorAction SilentlyContinue | Stop-Process -Force
if ($Remove) {
    Unregister-ScheduledTask -TaskName 'SSH Manager' -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item "$env:ProgramData\SSHManager" -Recurse -Force -ErrorAction SilentlyContinue
}
