# Run by the installer (as admin) for the "Client and server" role.
# Full setup (first install, role change, or the installer run by hand):
#  - OpenSSH Server: installed (Windows optional feature) unless an sshd service is already there (also
#    the one from the Win32-OpenSSH MSI), automatic start, allowed through the firewall (its own port)
#  - %ProgramData%\SSHManager\install-path.txt: where the app's remote command finds ensure-running.ps1;
#    the folder gets fixed permissions (Administrators and SYSTEM write, Users read)
#  - "SSH Manager" scheduled task: starts the server at logon of an administrator account (the existing
#    task's user, else the user signed in at the console, else the installing user), with admin rights, hidden
# -Upgrade (silent update, role unchanged): only the %ProgramData% folder and the logon task; OpenSSH
# Server and the firewall are left exactly as they are.
# Progress goes to -ProgressFile (UTF-8) as "percent|what is happening|detail" lines (the installer
# shows them) and "WARN|message" lines, ending with "DONE" or "ERROR|message".
param([Parameter(Mandatory)][string]$InstallDir, [string]$User = '', [string]$ServerUser = '',
      [switch]$Upgrade, [string]$ProgressFile)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$InstallDir = $InstallDir.TrimEnd('\')
$server = Join-Path $InstallDir 'Server'
try { Start-Transcript -Path (Join-Path $server 'setup.log') -Force | Out-Null } catch { }

$utf8 = New-Object Text.UTF8Encoding($true)
function Write-Progress-Line([string]$line) {
    if ($ProgressFile) { [IO.File]::AppendAllText($ProgressFile, "$line`r`n", $utf8) }
}
function Report([int]$percent, [string]$text, [string]$detail = '') {
    "$percent% $text $detail"
    Write-Progress-Line "$percent|$text|$($detail -replace '[\r\n|]+', ' ')"
}
function Warn([string]$text) {
    "WARNING: $text"
    Write-Progress-Line "WARN|$($text -replace '[\r\n|]+', ' ')"
}

$AdminsSid = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'

function Get-Sid([string]$name) {
    try { return (New-Object Security.Principal.NTAccount($name)).Translate([Security.Principal.SecurityIdentifier]) } catch { return $null }
}

# The installer passes DOMAIN\user; for local accounts the "domain" can come through as WORKGROUP
# (e.g. when installing over SSH). Use the first form Windows can actually resolve.
function Resolve-Account([string]$name) {
    if (-not $name) { return $null }
    $short = $name.Split('\')[-1]
    foreach ($candidate in @($name, "$env:COMPUTERNAME\$short", $short)) {
        if (Get-Sid $candidate) { return $candidate }
    }
    return $null
}

# $true / $false, or $null when Windows can't tell (e.g. admin rights through a domain or Azure AD group)
function Test-Admin([string]$account) {
    $sid = Get-Sid $account
    if (-not $sid) { return $false }
    $me = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($me.User -eq $sid -and (New-Object Security.Principal.WindowsPrincipal($me)).IsInRole($AdminsSid)) { return $true }
    try {
        $group = $AdminsSid.Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]   # localized name
        $unsure = $false
        foreach ($m in @(([ADSI]"WinNT://./$group,group").psbase.Invoke('Members'))) {
            $t = $m.GetType()
            $msid = New-Object Security.Principal.SecurityIdentifier($t.InvokeMember('objectSid', 'GetProperty', $null, $m, $null), 0)
            if ($msid -eq $sid) { return $true }
            if ($t.InvokeMember('Class', 'GetProperty', $null, $m, $null) -ne 'User') { $unsure = $true }
        }
        if ($unsure) { return $null }
        return $false
    } catch { return $null }
}

# The user signed in at the console (also when the installer runs elevated as another admin, or over SSH)
function Get-ConsoleUser {
    try { $u = (Get-CimInstance Win32_ComputerSystem).UserName; if ($u) { return $u } } catch { }
    $owners = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" -ErrorAction SilentlyContinue | Sort-Object SessionId |
        ForEach-Object { $o = Invoke-CimMethod -InputObject $_ -MethodName GetOwner -ErrorAction SilentlyContinue; if ($o.User) { "$($o.Domain)\$($o.User)" } }
    return @($owners)[0]
}

# Who the logon task runs as. The server needs admin rights, so it must be an administrator account.
function Select-TaskUser {
    $existing = Get-ScheduledTask -TaskName 'SSH Manager' -ErrorAction SilentlyContinue
    if ($ServerUser) {
        $u = Resolve-Account $ServerUser
        if (-not $u) { throw "Can't find the Windows account '$ServerUser'." }
        if ((Test-Admin $u) -eq $false) { throw "'$u' isn't an administrator account; the server needs one." }
        return $u
    }
    if ($existing -and $existing.Principal.UserId) {
        $u = Resolve-Account $existing.Principal.UserId
        if ($u) { return $u }
    }
    $console = Resolve-Account (Get-ConsoleUser)
    if ($console -and (Test-Admin $console) -ne $false) { return $console }
    $installer = Resolve-Account $User
    if ($installer -and (Test-Admin $installer) -ne $false) {
        if ($console -and $console -ne $installer) {
            Warn "$console is a standard account, and the server needs administrator rights, so SSH Manager's server starts when $installer signs in. Screen previews and the shared clipboard work in that account's session."
        }
        return $installer
    }
    throw "The server role needs a Windows administrator account, and $(if ($console) { $console } else { $User }) is a standard account. Run this installer from an administrator account (or install with /SERVERUSER=<admin account>)."
}

# %ProgramData%\SSHManager: Administrators and SYSTEM may change it, Users may only read. A folder
# someone else created (or a link put there) is replaced, so install-path.txt can be trusted.
function Reset-SharedFolder {
    $dir = Join-Path $env:ProgramData 'SSHManager'
    $item = Get-Item -LiteralPath $dir -Force -ErrorAction SilentlyContinue
    if ($item -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or -not $item.PSIsContainer)) {
        if ($item.PSIsContainer) { [IO.Directory]::Delete($dir, $false) } else { [IO.File]::Delete($dir) }   # the link, not its target
        "removed a link or file at $dir"
        $item = $null
    }
    if (-not $item) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetOwner($AdminsSid)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-32-544', 'S-1-5-18') {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', $inherit, 'None', 'Allow')))
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'ReadAndExecute', $inherit, 'None', 'Allow')))
    try { [IO.Directory]::SetAccessControl($dir, $acl) }
    catch {
        # owned by someone else who locked admins out: take ownership first (needs the privilege takeown enables)
        takeown.exe /F $dir /A | Out-Null
        [IO.Directory]::SetAccessControl($dir, $acl)
    }
    # anything else in there is replaced; the updater's download folder keeps its files but gets
    # its permissions reset (the server's updater locks it down again before using it)
    foreach ($child in @(Get-ChildItem -LiteralPath $dir -Force)) {
        if ($child.Name -eq 'updates' -and $child.PSIsContainer -and -not ($child.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            $a = New-Object Security.AccessControl.DirectorySecurity
            $a.SetOwner($AdminsSid)
            $a.SetAccessRuleProtection($false, $false)
            try { [IO.Directory]::SetAccessControl($child.FullName, $a) } catch { "couldn't reset $($child.FullName): $_" }
        } elseif ($child.PSIsContainer -and -not ($child.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            cmd.exe /c rmdir /s /q "$($child.FullName)" | Out-Null   # doesn't follow links inside, unlike Remove-Item
        } elseif ($child.PSIsContainer) {
            [IO.Directory]::Delete($child.FullName, $false)
        } else {
            [IO.File]::Delete($child.FullName)
        }
    }
    # with a BOM, so Windows PowerShell reads a non-English path correctly
    [IO.File]::WriteAllText((Join-Path $dir 'install-path.txt'), $server, $utf8)
}

# The port sshd listens on (sshd_config), 22 unless changed
function Get-SshdPort {
    $cfg = Join-Path $env:ProgramData 'ssh\sshd_config'
    if (Test-Path -LiteralPath $cfg) {
        foreach ($line in [IO.File]::ReadAllLines($cfg)) {
            if ($line -match '^\s*Match\s') { break }
            if ($line -match '^\s*Port\s+(\d+)') { return [int]$Matches[1] }
        }
    }
    return 22
}

# Allows sshd's port on private and domain networks (unless a rule already does), then says if SSH
# is still blocked on a network this PC is connected to (e.g. one set to Public).
function Set-Firewall([int]$port, [switch]$Again) {
    # all rules in effect (also Group Policy ones); the filters are read in bulk (one by one takes minutes)
    $ports = @{}; Get-NetFirewallPortFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue | ForEach-Object { $ports[$_.InstanceID] = $_ }
    $apps = @{}; Get-NetFirewallApplicationFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue | ForEach-Object { $apps[$_.InstanceID] = $_ }
    # a rule allows SSH if it opens the port (for any program, not an app package) or allows sshd.exe itself
    $allowing = @(Get-NetFirewallRule -PolicyStore ActiveStore -Direction Inbound -Action Allow -Enabled True -ErrorAction SilentlyContinue | Where-Object {
        $pf = $ports[$_.InstanceID]; $af = $apps[$_.InstanceID]
        $program = if ($af) { "$($af.Program)" } else { 'Any' }
        $pf -and ($pf.Protocol -eq 'TCP' -or $pf.Protocol -eq 'Any') -and -not $_.Owner -and
            ((@($pf.LocalPort) -contains "$port" -and ($program -eq 'Any' -or $program -match 'sshd\.exe$')) -or
             ($program -match 'sshd\.exe$' -and @($pf.LocalPort) -contains 'Any'))
    })
    function Covers([string]$profileName) {
        foreach ($r in $allowing) { if ("$($r.Profile)" -eq 'Any' -or "$($r.Profile)" -match $profileName) { return $true } }
        return $false
    }
    $ours = Get-NetFirewallRule -Name 'SSHManager-sshd' -ErrorAction SilentlyContinue
    $onDomain = [bool](Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object { "$($_.NetworkCategory)" -eq 'DomainAuthenticated' })
    if (-not $Again -and (-not (Covers 'Private') -or ($onDomain -and -not (Covers 'Domain')))) {
        if ($ours) { Remove-NetFirewallRule -Name 'SSHManager-sshd' }
        New-NetFirewallRule -Name 'SSHManager-sshd' -DisplayName 'OpenSSH Server (sshd) for SSH Manager' -Enabled True -Direction Inbound `
            -Protocol TCP -Action Allow -LocalPort $port -Profile Private, Domain | Out-Null
        "firewall rule added for port $port"
        return Set-Firewall $port -Again   # now only to say where it is still blocked
    }
    foreach ($net in @(Get-NetConnectionProfile -ErrorAction SilentlyContinue)) {
        $profileName = switch ("$($net.NetworkCategory)") { 'DomainAuthenticated' { 'Domain' } 'Private' { 'Private' } default { 'Public' } }
        $fw = Get-NetFirewallProfile -Name $profileName -ErrorAction SilentlyContinue
        if (-not $fw -or "$($fw.Enabled)" -eq 'False' -or "$($fw.DefaultInboundAction)" -eq 'Allow') { continue }
        if (-not (Covers $profileName)) {
            Warn "This PC's network ""$($net.Name)"" is set to $profileName, and Windows Firewall blocks SSH (port $port) there, so other PCs can't connect over it. Set that network to Private (Settings > Network & internet > the network > Private network) if you trust it."
        }
    }
}

function Explain-Dism([int]$code, [string]$log) {
    $hex = '0x{0:X8}' -f $code
    $why = switch ($hex) {
        '0x800F0954' { 'this PC gets its updates from WSUS (a company update server), which has no OpenSSH Server' }
        '0x800F0950' { 'Windows couldn''t get the feature (Windows Update is blocked or limited by policy)' }
        '0x800F081F' { 'Windows couldn''t find the feature''s files' }
        '0x8024402C' { 'this PC can''t reach Windows Update (no internet or a proxy)' }
        '0x80072EE7' { 'this PC can''t reach Windows Update (no internet or a proxy)' }
        '0x80240438' { 'this PC can''t reach Windows Update' }
        default { "DISM error $hex" }
    }
    "Windows couldn't install OpenSSH Server: $why. To install it without Windows Update, download the OpenSSH-Win64 MSI from https://github.com/PowerShell/Win32-OpenSSH/releases, install it on this PC, then run this installer again (or allow optional features from Windows Update in Group Policy: Computer Configuration > Administrative Templates > System > 'Specify settings for optional component installation and component repair'). Details: $log"
}

function Install-OpenSshServer {
    $svc = Get-CimInstance Win32_Service -Filter "Name='sshd'" -ErrorAction SilentlyContinue
    if ($svc) { "sshd service present: $($svc.PathName)"; return }   # Windows' feature or the Win32-OpenSSH MSI
    $sys = "$env:WINDIR\System32\OpenSSH\sshd.exe"
    if (-not (Test-Path $sys)) {
        Report 6 "Checking Windows' OpenSSH Server"
        $cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
        if (-not $cap) { throw "This edition of Windows has no OpenSSH Server feature. Install the OpenSSH-Win64 MSI from https://github.com/PowerShell/Win32-OpenSSH/releases, then run this installer again." }
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
                $detail = "Downloading from Windows Update and installing: $what, $([int][math]::Floor($elapsed.TotalMinutes)) min $($elapsed.Seconds) s so far (usually 5-15 minutes)"
                if ($pct -ne $last -or $elapsed.Seconds % 5 -eq 0) { Report (8 + [int]($pct * 0.7)) "Installing Windows' OpenSSH Server" $detail; $last = $pct }
            }
            $dism.WaitForExit()
            $state = (Get-WindowsCapability -Online -Name $cap.Name).State
            if ($state -ne 'Installed') { throw (Explain-Dism $dism.ExitCode $log) }
            "installed OpenSSH Server"
        }
    }
    if (-not (Get-Service sshd -ErrorAction SilentlyContinue) -and (Test-Path $sys)) {
        # seen in the wild: the feature is present but its service entry is gone
        New-Service -Name sshd -BinaryPathName $sys -StartupType Automatic -DisplayName 'OpenSSH SSH Server' | Out-Null
        sc.exe privs sshd SeAssignPrimaryTokenPrivilege/SeTcbPrivilege/SeBackupPrivilege/SeRestorePrivilege/SeImpersonatePrivilege | Out-Null
        "re-created the sshd service"
    }
}

function Register-ServerTask([string]$taskUser) {
    $action = New-ScheduledTaskAction -Execute (Join-Path $InstallDir 'Tools\HiddenRun.exe') `
        -Argument "`"$(Join-Path $server 'SSHManagerServer.exe')`" --log-console" -WorkingDirectory $server
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $taskUser
    $principal = New-ScheduledTaskPrincipal -UserId $taskUser -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName 'SSH Manager' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    "server task registered for $taskUser"
}

$sshError = $null
try {
    Report 2 "Checking the account SSH Manager runs as"
    $taskUser = Select-TaskUser
    "task user: $taskUser"
    Write-Progress-Line "USER|$taskUser"

    Report 4 "Setting permissions on SSH Manager's settings folder" "$env:ProgramData\SSHManager"
    Reset-SharedFolder

    if (-not $Upgrade) {
        # OpenSSH problems don't stop the rest: the server still runs (and this PC's own app can use
        # it); the problem is reported at the end
        try {
            Install-OpenSshServer
            Report 82 "Starting the SSH server"
            try {
                Set-Service sshd -StartupType Automatic
                if ((Get-Service sshd).Status -ne 'Running') { Start-Service sshd }
            } catch {
                Warn "Windows' OpenSSH Server didn't start ($($_.Exception.Message)). Other PCs can't connect to this one until it runs: check the 'OpenSSH SSH Server' service in Services."
            }
            $port = Get-SshdPort
            Report 86 "Allowing SSH through the firewall" "port $port, private networks"
            try { Set-Firewall $port } catch { Warn "Couldn't check Windows Firewall for SSH (port $port): $($_.Exception.Message)" }
        } catch {
            $sshError = $_.Exception.Message
            "OpenSSH: $sshError"
        }
    }

    Report 90 "Setting SSH Manager to start when you sign in" $taskUser
    Register-ServerTask $taskUser

    Report 95 "Starting SSH Manager"
    if ((Get-ScheduledTask -TaskName 'SSH Manager').State -ne 'Running') { Start-ScheduledTask -TaskName 'SSH Manager' }
    Report 100 "Done"
    if ($sshError) { Write-Progress-Line "ERROR|$($sshError -replace '[\r\n|]+', ' ')"; exit 1 }
    Write-Progress-Line 'DONE'
} catch {
    "FAILED: $_"
    Write-Progress-Line "ERROR|$($_.Exception.Message -replace '[\r\n|]+', ' ')"
    exit 1
} finally {
    try { Stop-Transcript | Out-Null } catch { }
}
