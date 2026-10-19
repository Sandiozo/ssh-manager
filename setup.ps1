#Requires -RunAsAdministrator
# SSH Manager setup for the PC you want to manage (run Setup.cmd, which asks for admin rights).
# 1. OpenSSH Server: installs it if needed, starts it automatically, allows it through the firewall
# 2. Python: finds Python 3.11+ (offers to install it with winget)
# 3. Installs SSH Manager's Python packages into .venv
# 4. Starts SSH Manager at every logon (scheduled task, with admin rights) and right now
# Safe to run again; it only fixes what's missing.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "   OK  $text" -ForegroundColor Green }
function Warn($text) { Write-Host "   !   $text" -ForegroundColor Yellow }
function Ask($text) { (Read-Host "   ?   $text [Y/n]") -notmatch '^(n|no)$' }

Write-Host "SSH Manager setup" -ForegroundColor White
Write-Host "Installs what this PC needs so you can manage it from another computer with the SSH Manager app."

# ---- 1. OpenSSH Server ----------------------------------------------------------------------
Step "OpenSSH Server"
$cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
if ($cap.State -ne 'Installed') {
    if (-not (Ask "OpenSSH Server (part of Windows) is not installed. Install it now? This can take a few minutes.")) { throw "OpenSSH Server is required." }
    Add-WindowsCapability -Online -Name $cap.Name | Out-Null
    Ok "installed"
} else { Ok "installed" }
$svc = Get-Service sshd -ErrorAction SilentlyContinue
if (-not $svc) {
    # seen in the wild: the capability is present but its service entry is gone
    if (Test-Path "$env:WINDIR\System32\OpenSSH\sshd.exe") {
        New-Service -Name sshd -BinaryPathName "$env:WINDIR\System32\OpenSSH\sshd.exe" -StartupType Automatic -DisplayName 'OpenSSH SSH Server' | Out-Null
        sc.exe privs sshd SeAssignPrimaryTokenPrivilege/SeTcbPrivilege/SeBackupPrivilege/SeRestorePrivilege/SeImpersonatePrivilege | Out-Null
        Ok "re-created the missing sshd service"
    } else { throw "The sshd service is missing. Remove and re-add 'OpenSSH Server' in Settings > System > Optional features." }
}
Set-Service sshd -StartupType Automatic
if ((Get-Service sshd).Status -ne 'Running') { Start-Service sshd }
Ok "running, starts automatically"
$rule = Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' } |
    Get-NetFirewallPortFilter -ErrorAction SilentlyContinue | Where-Object { $_.Protocol -eq 'TCP' -and $_.LocalPort -eq '22' }
if (-not $rule) {
    New-NetFirewallRule -Name 'SSHManager-sshd' -DisplayName 'OpenSSH Server (sshd) for SSH Manager' -Enabled True -Direction Inbound `
        -Protocol TCP -Action Allow -LocalPort 22 -Profile Private, Domain | Out-Null
    Ok "firewall: allowed SSH (port 22) on private networks"
} else { Ok "firewall allows SSH (port 22)" }
$public = Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object NetworkCategory -eq 'Public'
if ($public) {
    Warn "Your network '$($public[0].Name)' is set to Public, so other computers may not reach this PC."
    Warn "If this is your home network, set it to Private: Settings > Network & internet > (your connection) > Private network."
}

# ---- 2. Python --------------------------------------------------------------------------------
Step "Python"
function Find-Python {
    $candidates = @()
    if (Get-Command py -ErrorAction SilentlyContinue) { $candidates += (& py -3 -c "import sys; print(sys.executable)" 2>$null) }
    $candidates += (Get-Command python -All -ErrorAction SilentlyContinue | Where-Object { $_.Source -notlike '*WindowsApps*' }).Source
    $candidates += Get-ChildItem "$env:LOCALAPPDATA\Programs\Python\Python3*\python.exe", "$env:ProgramFiles\Python3*\python.exe" -ErrorAction SilentlyContinue | ForEach-Object FullName
    foreach ($p in $candidates | Where-Object { $_ } | Select-Object -Unique) {
        $v = & $p -c "import sys; print(sys.version_info >= (3, 11))" 2>$null
        if ($v -eq 'True') { return $p }
    }
}
$python = Find-Python
if (-not $python) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw "Python 3.11 or newer is needed. Install it from https://www.python.org/downloads/ and run Setup.cmd again." }
    if (-not (Ask "Python 3.11+ is needed and wasn't found. Install Python 3.12 with winget?")) { throw "Python is required." }
    winget install -e --id Python.Python.3.12 --accept-package-agreements --accept-source-agreements --silent
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    $python = Find-Python
    if (-not $python) { throw "Python was installed but can't be found yet. Close this window and run Setup.cmd again." }
}
Ok $python

# ---- 3. SSH Manager packages ------------------------------------------------------------------
Step "SSH Manager"
if (-not (Test-Path "$root\.venv\Scripts\python.exe")) { & $python -m venv "$root\.venv" }
& "$root\.venv\Scripts\python.exe" -m pip install --quiet --disable-pip-version-check -r "$root\requirements.txt"
if ($LASTEXITCODE -ne 0) { throw "Installing the Python packages failed (see above)." }
Ok "packages installed"
& "$root\.venv\Scripts\python.exe" "$root\server.py" --print-token | Out-Null   # creates the home folder, config and token
New-Item -ItemType Directory -Force "$env:ProgramData\SSHManager" | Out-Null
Set-Content -Path "$env:ProgramData\SSHManager\install-path.txt" -Value $root -Encoding utf8
Ok "installed in $root"

# ---- 4. Start at logon --------------------------------------------------------------------------
Step "Start automatically"
& "$root\install-autostart.ps1" | Out-Null
$up = $false
for ($i = 0; $i -lt 30 -and -not $up; $i++) {
    Start-Sleep -Milliseconds 500
    try { $up = (Invoke-RestMethod http://127.0.0.1:8765/api/ping -TimeoutSec 2).app -eq 'ssh-manager' } catch { }
}
if ($up) { Ok "SSH Manager is running and starts at every logon" } else { Warn "SSH Manager didn't answer yet; it will start at the next logon (or run start.bat)." }

# ---- done -------------------------------------------------------------------------------------
$ips = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notmatch '^(127|169\.254)\.' -and $_.PrefixOrigin -ne 'WellKnown' }).IPAddress -join ', '
Write-Host "`nThis PC is ready." -ForegroundColor Green
Write-Host "On your other computer, open the SSH Manager app and enter:"
Write-Host ("   PC name or IP address : {0}   ({1})" -f $env:COMPUTERNAME, $ips) -ForegroundColor White
Write-Host ("   Windows user name     : {0}" -f $env:USERNAME) -ForegroundColor White
Write-Host "The first time, the app asks for this account's Windows password once (if you sign in to Windows with a"
Write-Host "Microsoft account, that's the Microsoft account password). After that it uses a key."
Write-Host "To use SSH Manager on this PC itself, run start.bat."
