# Run by the installer (as admin). Only touches SSH Manager's own processes: the ones started from
# -InstallDir (the app, the server, HiddenRun), never other programs.
#  - before files are replaced: -StateFile records which Windows users had the app open (one
#    DOMAIN\user per line, so it can be reopened for each of them) and whether the server was running
#    ("server" line), then the app and the server are stopped
#  - -Remove (uninstall, or the "Client and server" role being switched off): also removes the logon
#    task, %ProgramData%\SSHManager and SSH Manager's firewall rule
#  - -DisableSshd: also stops Windows' OpenSSH Server and sets it to Disabled
#  - -RemoveKeys: also removes the SSH keys SSH Manager added (comment "ssh-manager@...") from
#    administrators_authorized_keys (a backup is kept next to it)
#  - -StopCli: also ends sshmgr.exe (command line / MCP) processes started from -InstallDir (uninstall)
# Settings and data stay.
param([string]$InstallDir, [string]$StateFile, [switch]$Remove, [switch]$DisableSshd, [switch]$RemoveKeys, [switch]$StopCli)
$ErrorActionPreference = 'Continue'
if (-not $InstallDir -and $PSScriptRoot) { $InstallDir = Split-Path -Parent $PSScriptRoot }   # run as a file from Tools
if (-not $InstallDir) { throw 'Pass -InstallDir (the SSH Manager folder).' }
$InstallDir = $InstallDir.TrimEnd('\')

# our processes: the executable lies in the install folder
function Get-Ours([string[]]$names) {
    foreach ($n in $names) {
        Get-CimInstance Win32_Process -Filter "Name='$n'" -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith("$InstallDir\", [StringComparison]::OrdinalIgnoreCase) }
    }
}

function Stop-Ours([string[]]$names) {
    foreach ($p in @(Get-Ours $names)) {
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        "stopped $($p.Name) (PID $($p.ProcessId))"
    }
}

if ($StateFile) {
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($p in @(Get-Ours 'SSHManager.exe')) {
        $o = Invoke-CimMethod -InputObject $p -MethodName GetOwner -ErrorAction SilentlyContinue
        if ($o -and $o.User) {
            $u = if ($o.Domain) { "$($o.Domain)\$($o.User)" } else { $o.User }
            if (-not $lines.Contains($u)) { $lines.Add($u) }
        }
    }
    if (@(Get-Ours 'SSHManagerServer.exe').Count) { $lines.Add('server') }
    [IO.File]::WriteAllLines($StateFile, $lines.ToArray(), (New-Object Text.UTF8Encoding($true)))
}

Stop-ScheduledTask -TaskName 'SSH Manager' -ErrorAction SilentlyContinue
Stop-Ours 'SSHManager.exe', 'SSHManagerServer.exe', 'HiddenRun.exe'
if ($StopCli) { Stop-Ours 'sshmgr.exe' }

if ($Remove) {
    Unregister-ScheduledTask -TaskName 'SSH Manager' -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item "$env:ProgramData\SSHManager" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-NetFirewallRule -Name 'SSHManager-sshd' -ErrorAction SilentlyContinue
    "removed the logon task, $env:ProgramData\SSHManager and the firewall rule"
}

if ($RemoveKeys) {
    $f = "$env:ProgramData\ssh\administrators_authorized_keys"
    if (Test-Path -LiteralPath $f) {
        $lines = [IO.File]::ReadAllLines($f)
        # [options] type base64 comment: the comment the app gives its key starts with ssh-manager@
        $ours = '(^|\s)(ssh-|ecdsa-|sk-)\S+\s+[A-Za-z0-9+/=]+\s+ssh-manager@'
        $keep = @($lines | Where-Object { $_ -notmatch $ours })
        if ($keep.Count -ne $lines.Count) {
            Copy-Item -LiteralPath $f -Destination "$f.before-sshmanager-uninstall" -Force
            # rewritten in place, so the file keeps its admin-only permissions (sshd insists on them)
            $text = if ($keep.Count) { ($keep -join "`n") + "`n" } else { '' }
            [IO.File]::WriteAllText($f, $text, (New-Object Text.UTF8Encoding($false)))
            "removed $($lines.Count - $keep.Count) SSH Manager key(s) from $f"
        }
    }
}

if ($DisableSshd) {
    Stop-Service sshd -Force -ErrorAction SilentlyContinue
    Set-Service sshd -StartupType Disabled -ErrorAction SilentlyContinue
    "turned off OpenSSH Server"
}
