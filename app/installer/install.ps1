# Installs the SSH Manager app for the current user: copies it to %LOCALAPPDATA%\Programs\SSH Manager,
# adds Start menu and desktop shortcuts, and checks that the .NET 8 Desktop Runtime is present.
$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = "$env:LOCALAPPDATA\Programs\SSH Manager"
Write-Host "Installing SSH Manager..." -ForegroundColor Cyan

$runtimes = & { try { dotnet --list-runtimes 2>$null } catch { } }
if (-not ($runtimes | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App (\d+)\.' -and [int]$Matches[1] -ge 8 })) {
    Write-Host "SSH Manager needs the .NET Desktop Runtime 8 (or newer)." -ForegroundColor Yellow
    if ((Get-Command winget -ErrorAction SilentlyContinue) -and (Read-Host "Install it now with winget? [Y/n]") -notmatch '^(n|no)$') {
        winget install -e --id Microsoft.DotNet.DesktopRuntime.8 --accept-package-agreements --accept-source-agreements
    } else {
        Write-Host "Download it from https://dotnet.microsoft.com/download/dotnet/8.0 (.NET Desktop Runtime, x64), then run Install.cmd again."
        return
    }
}

Get-Process SSHManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest*" } | Stop-Process -Force
if ((Resolve-Path $src).Path -ne $dest) {
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item "$src\*" $dest -Recurse -Force
}

$shell = New-Object -ComObject WScript.Shell
foreach ($lnk in "$([Environment]::GetFolderPath('Programs'))\SSH Manager.lnk", "$([Environment]::GetFolderPath('Desktop'))\SSH Manager.lnk") {
    $s = $shell.CreateShortcut($lnk); $s.TargetPath = "$dest\SSHManager.exe"; $s.WorkingDirectory = $dest
    $s.Description = 'SSH Manager'; $s.Save()
}
Write-Host "Installed. Starting SSH Manager..." -ForegroundColor Green
Start-Process "$dest\SSHManager.exe"
