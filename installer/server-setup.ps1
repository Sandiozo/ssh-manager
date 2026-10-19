# Run by the installer (as admin) when "Let other PCs manage this PC" is selected:
#  - OpenSSH Server: installed (Windows optional feature), automatic start, allowed through the firewall
#  - %ProgramData%\SSHManager\install-path.txt: where the app's remote command finds ensure-running.ps1
#  - "SSH Manager" scheduled task: starts the server at logon of $User, with admin rights, hidden
param([Parameter(Mandatory)][string]$InstallDir, [Parameter(Mandatory)][string]$User)
$ErrorActionPreference = 'Stop'
$server = Join-Path $InstallDir 'Server'

# The installer passes DOMAIN\user; for local accounts the "domain" can come through as WORKGROUP
# (e.g. when installing over SSH). Use the first form Windows can actually resolve.
function Resolve-Account([string]$name) {
    $short = $name.Split('\')[-1]
    foreach ($candidate in @($name, "$env:COMPUTERNAME\$short", $short)) {
        try {
            (New-Object Security.Principal.NTAccount($candidate)).Translate([Security.Principal.SecurityIdentifier]) | Out-Null
            return $candidate
        } catch { }
    }
    throw "Can't find the Windows account '$name'."
}
$User = Resolve-Account $User
Start-Transcript -Path (Join-Path $server 'setup.log') -Force | Out-Null
try {
    # ---- OpenSSH Server ----
    $cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
    if ($cap.State -ne 'Installed') { Add-WindowsCapability -Online -Name $cap.Name | Out-Null; "installed OpenSSH Server" }
    if (-not (Get-Service sshd -ErrorAction SilentlyContinue) -and (Test-Path "$env:WINDIR\System32\OpenSSH\sshd.exe")) {
        # seen in the wild: the feature is present but its service entry is gone
        New-Service -Name sshd -BinaryPathName "$env:WINDIR\System32\OpenSSH\sshd.exe" -StartupType Automatic -DisplayName 'OpenSSH SSH Server' | Out-Null
        sc.exe privs sshd SeAssignPrimaryTokenPrivilege/SeTcbPrivilege/SeBackupPrivilege/SeRestorePrivilege/SeImpersonatePrivilege | Out-Null
        "re-created the sshd service"
    }
    Set-Service sshd -StartupType Automatic
    if ((Get-Service sshd).Status -ne 'Running') { Start-Service sshd }
    $open = Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' } |
        Get-NetFirewallPortFilter -ErrorAction SilentlyContinue | Where-Object { $_.Protocol -eq 'TCP' -and $_.LocalPort -eq '22' }
    if (-not $open) {
        New-NetFirewallRule -Name 'SSHManager-sshd' -DisplayName 'OpenSSH Server (sshd) for SSH Manager' -Enabled True -Direction Inbound `
            -Protocol TCP -Action Allow -LocalPort 22 -Profile Private, Domain | Out-Null
        "firewall rule added"
    }

    # ---- where the server is ----
    New-Item -ItemType Directory -Force "$env:ProgramData\SSHManager" | Out-Null
    [IO.File]::WriteAllText("$env:ProgramData\SSHManager\install-path.txt", $server)

    # ---- start at logon (and now) ----
    $action = New-ScheduledTaskAction -Execute (Join-Path $InstallDir 'Tools\HiddenRun.exe') `
        -Argument "`"$(Join-Path $server 'SSHManagerServer.exe')`" --log-console" -WorkingDirectory $server
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $User
    $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName 'SSH Manager' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    if ((Get-ScheduledTask -TaskName 'SSH Manager').State -ne 'Running') { Start-ScheduledTask -TaskName 'SSH Manager' }
    "server task registered for $User"
} catch {
    "FAILED: $_"
    exit 1
} finally {
    Stop-Transcript | Out-Null
}
