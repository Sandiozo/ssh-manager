# Run over SSH by the SSH Manager app when it connects: makes sure the server is running on this PC,
# prints its version, port and access token for the app, then keeps the SSH session (and with it the
# port forward) open until the app disconnects.
# The server is started through WMI so it is not part of the SSH session (Windows ends a session's
# processes when it closes); the logon task the installer creates starts it at every logon anyway.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (Test-Path "$root\SSHManagerServer.exe") {        # installed
    $info = & "$root\SSHManagerServer.exe" --client-info
    $start = "`"$root\SSHManagerServer.exe`" --log-console"
} else {                                              # running from source
    $info = & "$root\.venv\Scripts\python.exe" "$root\server.py" --client-info
    $start = "`"$root\.venv\Scripts\pythonw.exe`" `"$root\server.py`""
}
$port = (($info | Where-Object { $_ -like 'SSHMGR-PORT:*' }) -replace 'SSHMGR-PORT:', '')
if (-not (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)) {
    Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{ CommandLine = $start; CurrentDirectory = $root } | Out-Null
}
$info
while ($true) { Start-Sleep -Seconds 3600 }
