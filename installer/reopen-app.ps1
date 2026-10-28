# Run by the installer after an update (or a failed or cancelled one): starts the app again (in the
# tray) for every Windows user who had it open. -StateFile is the list server-stop.ps1 wrote (one
# DOMAIN\user per line); without it, the user signed in at the console. A one-time scheduled task per
# user does that in the user's own desktop session, also when the installer runs in another session
# (e.g. started over SSH or by the server), where starting the app directly would leave it invisible.
# Each task is deleted once it has run (or after a minute, if the user isn't signed in any more).
param([Parameter(Mandatory)][string]$Exe, [string]$StateFile, [string[]]$Users = @())
$ErrorActionPreference = 'Continue'
if (-not (Test-Path -LiteralPath $Exe)) { "not installed: $Exe"; return }

if ($StateFile -and (Test-Path -LiteralPath $StateFile)) {
    $Users += @([IO.File]::ReadAllLines($StateFile) | Where-Object { $_ -and $_ -ne 'server' })
}
if (-not $Users.Count) {
    $u = (Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).UserName
    if ($u) { $Users = @($u) }
}

function Resolve-Account([string]$name) {
    $short = $name.Split('\')[-1]
    foreach ($candidate in @($name, "$env:COMPUTERNAME\$short", $short)) {
        try { (New-Object Security.Principal.NTAccount($candidate)).Translate([Security.Principal.SecurityIdentifier]) | Out-Null; return $candidate } catch { }
    }
    return $null
}

$started = @()
$i = 0
foreach ($name in ($Users | Select-Object -Unique)) {
    $user = Resolve-Account $name
    if (-not $user) { "unknown account: $name"; continue }
    $i++
    $task = "SSH Manager app (restart after update) $i"
    # refresh the icon cache in that session first (shortcuts keep the old icon otherwise), then start the app
    $action = @((New-ScheduledTaskAction -Execute "$env:WINDIR\System32\ie4uinit.exe" -Argument '-show'),
                (New-ScheduledTaskAction -Execute $Exe -Argument '--tray'))
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    # no time limit: the task's last action is the app itself, which keeps running
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    try {
        Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force -ErrorAction Stop | Out-Null
        Start-ScheduledTask -TaskName $task -ErrorAction Stop
        $started += [pscustomobject]@{ Task = $task; User = $user }
    } catch {
        "couldn't reopen the app for ${user}: $_"
        Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    }
}

# delete each task only after it has started the app (deleting it earlier can stop it from running)
function Test-AppRunning([string]$user) {
    $sid = (New-Object Security.Principal.NTAccount($user)).Translate([Security.Principal.SecurityIdentifier]).Value
    foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='SSHManager.exe'" -ErrorAction SilentlyContinue)) {
        if ($p.ExecutablePath -eq $Exe -and (Invoke-CimMethod -InputObject $p -MethodName GetOwnerSid -ErrorAction SilentlyContinue).Sid -eq $sid) { return $true }
    }
    return $false
}
$until = (Get-Date).AddSeconds(60)
foreach ($s in $started) {
    $ok = $false
    while (-not $ok -and (Get-Date) -lt $until) {
        $ok = Test-AppRunning $s.User
        if ($ok) { break }
        # an error result (e.g. 0x800710E0: the user isn't signed in) means it won't start
        $result = [int64](Get-ScheduledTaskInfo -TaskName $s.Task -ErrorAction SilentlyContinue).LastTaskResult
        if ($result -ge 2147483648 -or $result -lt 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Unregister-ScheduledTask -TaskName $s.Task -Confirm:$false -ErrorAction SilentlyContinue
    if ($ok) { "reopened the app for $($s.User)" } else { "the app didn't start for $($s.User) (not signed in?)" }
}
