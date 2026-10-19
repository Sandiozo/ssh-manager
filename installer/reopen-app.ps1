# Run by the installer after a silent update: starts the app again (in the tray) in $User's desktop
# session. A one-time scheduled task does that even when the installer itself runs in another session
# (e.g. started over SSH), where launching the app directly would leave it invisible.
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$User)
$short = $User.Split('\')[-1]
foreach ($candidate in @($User, "$env:COMPUTERNAME\$short", $short)) {
    try { (New-Object Security.Principal.NTAccount($candidate)).Translate([Security.Principal.SecurityIdentifier]) | Out-Null; $User = $candidate; break } catch { }
}
$name = 'SSH Manager app (restart after update)'
# refresh the icon cache in that session first (shortcuts keep the old icon otherwise), then start the app
$action = @((New-ScheduledTaskAction -Execute "$env:WINDIR\System32\ie4uinit.exe" -Argument '-show'),
            (New-ScheduledTaskAction -Execute $Exe -Argument '--tray'))
$principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $name
Start-Sleep -Seconds 6
Unregister-ScheduledTask -TaskName $name -Confirm:$false
