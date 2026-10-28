# Stops the running SSH Manager server and starts it again (e.g. after updating). -StopOnly just stops it.
# Works for an installed copy (it is in the Server folder, next to SSHManagerServer.exe) and for a
# copy running from source (repository folder, .venv). Only the server from this folder is stopped.
# Run it over SSH from an admin account, or as admin, so the server keeps its admin rights (needed to
# read SSH command lines). An installed server is started through its logon task (so it runs in the
# signed-in user's session); otherwise through WMI, so it isn't part of the SSH session, which would
# end it as soon as the command that started it finishes.
param([switch]$StopOnly)
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$installed = Test-Path -LiteralPath "$root\SSHManagerServer.exe"
$task = if ($installed) { Get-ScheduledTask -TaskName 'SSH Manager' -ErrorAction SilentlyContinue } else { $null }

if ($task) { Stop-ScheduledTask -TaskName 'SSH Manager' -ErrorAction SilentlyContinue }
$filter = if ($installed) { "Name='SSHManagerServer.exe'" } else { "Name LIKE 'python%'" }
Get-CimInstance Win32_Process -Filter $filter |
    Where-Object { if ($installed) { $_.ExecutablePath -eq "$root\SSHManagerServer.exe" } else { $_.CommandLine -like "*$root\server.py*" } } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
if ($StopOnly) { "SSH Manager stopped."; return }
Start-Sleep -Seconds 1
if ($task) {
    Start-ScheduledTask -TaskName 'SSH Manager'
    foreach ($i in 1..10) {
        if (Get-CimInstance Win32_Process -Filter $filter | Where-Object { $_.ExecutablePath -eq "$root\SSHManagerServer.exe" }) {
            "SSH Manager restarted (logon task)."
            return
        }
        Start-Sleep -Seconds 1
    }
    # the task's user isn't signed in: start it directly
}
$command = if ($installed) { "`"$root\SSHManagerServer.exe`" --log-console" } else { "`"$root\.venv\Scripts\pythonw.exe`" `"$root\server.py`"" }
$r = Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{ CommandLine = $command; CurrentDirectory = $root }
if ($r.ReturnValue -eq 0) { "SSH Manager restarted (PID $($r.ProcessId))." } else { "Start failed: $($r.ReturnValue)" }
