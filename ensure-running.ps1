# Run over SSH by the SSH Manager app when it connects: makes sure the server is running on this PC,
# prints its version, port and access token for the app, then keeps the SSH session (and with it the
# port forward) open until the app disconnects.
# The server is started through the logon task the installer creates, so it runs in the signed-in
# user's session (the screen preview and shared clipboard need that). When nobody is signed in it is
# started through WMI instead, so it is not part of the SSH session (Windows ends a session's
# processes when it closes).
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Setup may be replacing the files right now (an update): wait for it, it starts the server itself.
$until = (Get-Date).AddMinutes(5)
while ((Get-Process 'SSHManager-Setup*' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $until) { Start-Sleep -Seconds 2 }

if (Test-Path "$root\SSHManagerServer.exe") {        # installed
    $info = & "$root\SSHManagerServer.exe" --client-info
    $start = "`"$root\SSHManagerServer.exe`" --log-console"
} else {                                              # running from source
    $info = & "$root\.venv\Scripts\python.exe" "$root\server.py" --client-info
    $start = "`"$root\.venv\Scripts\pythonw.exe`" `"$root\server.py`""
}
$port = (($info | Where-Object { $_ -like 'SSHMGR-PORT:*' }) -replace 'SSHMGR-PORT:', '')
function Listening { [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) }

if (-not (Listening)) {
    if ((Test-Path "$root\SSHManagerServer.exe") -and (Get-ScheduledTask -TaskName 'SSH Manager' -ErrorAction SilentlyContinue)) {
        Start-ScheduledTask -TaskName 'SSH Manager' -ErrorAction SilentlyContinue
        foreach ($i in 1..15) { if (Listening) { break }; Start-Sleep -Seconds 1 }
    }
    if (-not (Listening)) {
        Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{ CommandLine = $start; CurrentDirectory = $root } | Out-Null
    }
}
$info
while ($true) { Start-Sleep -Seconds 3600 }
