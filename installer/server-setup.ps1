# Run by the installer (as admin) when "Let other PCs manage this PC" is selected:
#  - OpenSSH Server: installed (Windows optional feature), automatic start, allowed through the firewall
#  - %ProgramData%\SSHManager\install-path.txt: where the app's remote command finds ensure-running.ps1
#  - "SSH Manager" scheduled task: starts the server at logon of $User, with admin rights, hidden
# Progress goes to -ProgressFile as "percent|what is happening|detail" lines (the installer shows them),
# ending with "DONE" or "ERROR|message".
param([Parameter(Mandatory)][string]$InstallDir, [Parameter(Mandatory)][string]$User, [string]$ProgressFile)
$ErrorActionPreference = 'Stop'
$server = Join-Path $InstallDir 'Server'
Start-Transcript -Path (Join-Path $server 'setup.log') -Force | Out-Null

function Report([int]$percent, [string]$text, [string]$detail = '') {
    "$percent% $text $detail"
    if ($ProgressFile) { Add-Content -Path $ProgressFile -Value "$percent|$text|$detail" -Encoding ascii }
}

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

try {
    $User = Resolve-Account $User

    # ---- OpenSSH Server ----
    Report 2 "Checking Windows' OpenSSH Server"
    $cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
    if ($cap.State -ne 'Installed') {
        # DISM prints a percentage while Windows downloads and installs the feature; show it
        $log = Join-Path $env:TEMP 'sshmanager-dism.txt'
        Remove-Item $log -ErrorAction SilentlyContinue
        $dism = Start-Process dism.exe -ArgumentList '/Online', '/Add-Capability', "/CapabilityName:$($cap.Name)", '/NoRestart' `
            -RedirectStandardOutput $log -WindowStyle Hidden -PassThru
        $started = Get-Date
        $last = -1
        while (-not $dism.HasExited) {
            Start-Sleep -Milliseconds 700
            $pct = 0
            $text = if (Test-Path $log) { Get-Content $log -Raw -ErrorAction SilentlyContinue } else { '' }
            $found = [regex]::Matches([string]$text, '(\d{1,3}(?:[.,]\d)?)%')
            if ($found.Count) { $pct = [int][double]($found[$found.Count - 1].Groups[1].Value -replace ',', '.') }
            $elapsed = (Get-Date) - $started
            $what = if ($pct -gt 0) { "$pct% done" } else { 'starting' }
            $detail = "Downloading from Windows Update and installing: $what, $([int]$elapsed.TotalMinutes) min $($elapsed.Seconds) s so far (usually 5-15 minutes)"
            if ($pct -ne $last -or $elapsed.Seconds % 5 -eq 0) { Report (5 + [int]($pct * 0.75)) "Installing Windows' OpenSSH Server" $detail; $last = $pct }
        }
        $state = (Get-WindowsCapability -Online -Name $cap.Name).State
        if ($state -ne 'Installed') {
            throw "Installing OpenSSH Server failed (DISM exit code $($dism.ExitCode), state $state). Details: $log"
        }
        "installed OpenSSH Server"
    }
    Report 82 "Starting the SSH server"
    if (-not (Get-Service sshd -ErrorAction SilentlyContinue) -and (Test-Path "$env:WINDIR\System32\OpenSSH\sshd.exe")) {
        # seen in the wild: the feature is present but its service entry is gone
        New-Service -Name sshd -BinaryPathName "$env:WINDIR\System32\OpenSSH\sshd.exe" -StartupType Automatic -DisplayName 'OpenSSH SSH Server' | Out-Null
        sc.exe privs sshd SeAssignPrimaryTokenPrivilege/SeTcbPrivilege/SeBackupPrivilege/SeRestorePrivilege/SeImpersonatePrivilege | Out-Null
        "re-created the sshd service"
    }
    Set-Service sshd -StartupType Automatic
    if ((Get-Service sshd).Status -ne 'Running') { Start-Service sshd }

    Report 86 "Allowing SSH through the firewall" "port 22, private networks"
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
    Report 90 "Setting SSH Manager to start when you sign in" $User
    $action = New-ScheduledTaskAction -Execute (Join-Path $InstallDir 'Tools\HiddenRun.exe') `
        -Argument "`"$(Join-Path $server 'SSHManagerServer.exe')`" --log-console" -WorkingDirectory $server
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $User
    $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName 'SSH Manager' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    "server task registered for $User"

    Report 95 "Starting SSH Manager"
    if ((Get-ScheduledTask -TaskName 'SSH Manager').State -ne 'Running') { Start-ScheduledTask -TaskName 'SSH Manager' }
    Report 100 "Done"
    if ($ProgressFile) { Add-Content -Path $ProgressFile -Value 'DONE' -Encoding ascii }
} catch {
    "FAILED: $_"
    if ($ProgressFile) { Add-Content -Path $ProgressFile -Value "ERROR|$($_.Exception.Message -replace '[\r\n|]+', ' ')" -Encoding ascii }
    exit 1
} finally {
    Stop-Transcript | Out-Null
}
