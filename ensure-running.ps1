# Run over SSH by the SSH Manager app when it connects: makes sure the server is running on this PC,
# prints its version, port and access token for the app, then keeps the SSH session (and with it the
# port forward) open until the app disconnects.
# The token is only printed when the program listening on the server's port really is this server
# (its executable in this folder, or server.py from this folder when running from source), so another
# program can't take the port while the server is down and collect the token. Errors are printed as
# "SSHMGR-ERROR:<message>".
# The server is started through the logon task the installer creates, so it runs in the signed-in
# user's session (the screen preview and shared clipboard need that). When nobody is signed in it is
# started through WMI instead, so it is not part of the SSH session (Windows ends a session's
# processes when it closes).
# -Root: this script's folder, for callers that run it as a script block (no file path then).
param([string]$Root)
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$root = if ($Root) { $Root.TrimEnd('\') } elseif ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$installed = Test-Path -LiteralPath "$root\SSHManagerServer.exe"

function Fail([string]$message) {
    "SSHMGR-ERROR:$($message -replace '[\r\n]+', ' ')"
    exit 3
}

# Setup may be replacing the files right now (an update): wait for it, it starts the server itself.
function Test-SetupRunning {
    $m = $null
    if ([Threading.Mutex]::TryOpenExisting('Global\SSHManagerSetup', [ref]$m)) { $m.Dispose(); return $true }
    return [bool](Get-Process 'SSHManager-Setup*' -ErrorAction SilentlyContinue)
}
$until = (Get-Date).AddMinutes(5)
while ((Test-SetupRunning) -and (Get-Date) -lt $until) { Start-Sleep -Seconds 2 }

if ($installed) {
    $info = & "$root\SSHManagerServer.exe" --client-info 2>&1 | ForEach-Object { "$_" }
    $start = "`"$root\SSHManagerServer.exe`" --log-console"
} else {                                              # running from source
    $info = & "$root\.venv\Scripts\python.exe" "$root\server.py" --client-info 2>&1 | ForEach-Object { "$_" }
    $start = "`"$root\.venv\Scripts\pythonw.exe`" `"$root\server.py`""
}
$portText = (@($info) | Where-Object { $_ -like 'SSHMGR-PORT:*' } | Select-Object -First 1) -replace '^SSHMGR-PORT:', ''
$port = 0
if (-not [int]::TryParse("$portText".Trim(), [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
    $why = @($info | Where-Object { $_ -and $_ -notlike 'SSHMGR-*' }) | Select-Object -Last 1
    if ($portText) { Fail "SSH Manager's settings on this PC have an invalid port ('$portText'). Fix ""port"" in its config.json." }
    Fail "SSH Manager's server on this PC couldn't read its settings (config.json): $(if ($why) { $why } else { 'no port' })"
}

function Get-ListenerPids {
    @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
}

# Is this process the server from this folder?
function Test-OurServer($proc) {
    if (-not $proc) { return $false }
    if ($installed) { return $proc.ExecutablePath -and $proc.ExecutablePath -eq "$root\SSHManagerServer.exe" }
    # from source: python running this folder's server.py (the venv's python starts the real one as a child)
    return $proc.Name -like 'python*.exe' -and $proc.CommandLine -and $proc.CommandLine.IndexOf("$root\server.py", [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-OurServers {
    $name = if ($installed) { "Name='SSHManagerServer.exe'" } else { "Name LIKE 'python%'" }
    @(Get-CimInstance Win32_Process -Filter $name -ErrorAction SilentlyContinue | Where-Object { Test-OurServer $_ })
}

function Wait-Listening([int]$seconds) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        if ((Get-ListenerPids).Count) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return [bool](Get-ListenerPids).Count
}

if (-not (Get-ListenerPids).Count) {
    $task = if ($installed) { Get-ScheduledTask -TaskName 'SSH Manager' -ErrorAction SilentlyContinue } else { $null }
    if ($task -and $task.State -eq 'Running') {
        # the logon task is starting it (or it is restarting): wait instead of starting a second copy
        Wait-Listening 30 | Out-Null
    } elseif ($task) {
        Start-ScheduledTask -TaskName 'SSH Manager' -ErrorAction SilentlyContinue
        Wait-Listening 20 | Out-Null
    }
    if (-not (Get-ListenerPids).Count) {
        if ((Get-OurServers).Count) {
            Wait-Listening 30 | Out-Null                  # running but still starting up
        } else {
            Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{ CommandLine = $start; CurrentDirectory = $root } | Out-Null
            Wait-Listening 30 | Out-Null
        }
    }
}

$pids = Get-ListenerPids
if (-not $pids.Count) { Fail "SSH Manager's server on this PC didn't start (port $port). Its log is server.log in its data folder." }
foreach ($id in $pids) {
    $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction SilentlyContinue
    if (-not (Test-OurServer $proc)) {
        if ($proc -and -not $proc.ExecutablePath) {
            Fail "Can't check which program uses port $port on this PC. Connect with an administrator account."
        }
        $what = if ($proc) { "$($proc.Name), PID $id" } else { "PID $id" }
        Fail "Another program ($what) is using port $port on this PC, where SSH Manager's server should be. Close it or give SSH Manager another port."
    }
}
$info | Where-Object { $_ -like 'SSHMGR-*' }
while ($true) { Start-Sleep -Seconds 3600 }
