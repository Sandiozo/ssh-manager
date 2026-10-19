# Run by the installer: stops the server before its files are replaced. With -Remove (uninstall, or
# the "Let other PCs manage this PC" role being switched off) it also removes the logon task and
# %ProgramData%\SSHManager. With -DisableSshd it also turns off Windows' OpenSSH Server and removes the
# firewall rule SSH Manager added. Settings stay.
param([switch]$Remove, [switch]$DisableSshd)
Stop-ScheduledTask -TaskName 'SSH Manager' -ErrorAction SilentlyContinue
Get-Process SSHManagerServer -ErrorAction SilentlyContinue | Stop-Process -Force
if ($Remove) {
    Unregister-ScheduledTask -TaskName 'SSH Manager' -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item "$env:ProgramData\SSHManager" -Recurse -Force -ErrorAction SilentlyContinue
}
if ($DisableSshd) {
    Stop-Service sshd -Force -ErrorAction SilentlyContinue
    Set-Service sshd -StartupType Disabled -ErrorAction SilentlyContinue
    Remove-NetFirewallRule -Name 'SSHManager-sshd' -ErrorAction SilentlyContinue
}
