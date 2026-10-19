# Stops the running SSH Manager server and starts it again (e.g. after updating). -StopOnly just stops it.
# Run it over SSH from an admin account, or as admin, so the server keeps its admin rights (needed to
# read SSH command lines). The server is started through WMI so it isn't part of the SSH session,
# which would otherwise end it as soon as the command that started it finishes.
param([switch]$StopOnly)
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Get-CimInstance Win32_Process -Filter "Name like 'python%'" |
    Where-Object { $_.CommandLine -like "*$root\server.py*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
if ($StopOnly) { "SSH Manager stopped."; return }
Start-Sleep -Seconds 1
$r = Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{
    CommandLine = "`"$root\.venv\Scripts\pythonw.exe`" `"$root\server.py`""; CurrentDirectory = $root }
if ($r.ReturnValue -eq 0) { "SSH Manager restarted (PID $($r.ProcessId))." } else { "Start failed: $($r.ReturnValue)" }
