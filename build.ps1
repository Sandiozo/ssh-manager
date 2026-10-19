# Builds build\SSHManager-Setup.exe (+ .sha256): the server as an exe (PyInstaller), the app (.NET 8),
# HiddenRun (self-contained), then the installer (Inno Setup 6).
# Needs: Python 3.11+ with requirements.txt + pyinstaller, the .NET 8 SDK, Inno Setup 6 (ISCC.exe).
param([string]$Python = "$PSScriptRoot\.venv\Scripts\python.exe")
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = "$root\build"
$version = ((Get-Content "$root\sshmgr\__init__.py" -Raw) -replace '(?s).*__version__\s*=\s*"([^"]+)".*', '$1').Trim()
"Building SSH Manager $version"
foreach ($d in 'app', 'server', 'tools') { if (Test-Path "$build\$d") { Remove-Item "$build\$d" -Recurse -Force } }

& $Python -m PyInstaller "$root\server.spec" --noconfirm --distpath "$build\dist" --workpath "$build\work" --log-level WARN
if ($LASTEXITCODE) { throw "PyInstaller failed" }
Move-Item "$build\dist\SSHManagerServer" "$build\server"

dotnet publish "$root\app\SSHManager\SSHManager.csproj" -c Release -o "$build\app" -p:Version=$version --nologo -v quiet
if ($LASTEXITCODE) { throw "app build failed" }
Remove-Item "$build\app\*.pdb", "$build\app\*.xml" -ErrorAction SilentlyContinue

dotnet publish "$root\app\HiddenRun\HiddenRun.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
    -p:PublishTrimmed=true -p:Version=$version -o "$build\tools" --nologo -v quiet
if ($LASTEXITCODE) { throw "HiddenRun build failed" }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command iscc -ErrorAction Stop).Source }
& $iscc "/DVersion=$version" /Q "$root\installer\sshmanager.iss"
if ($LASTEXITCODE) { throw "Inno Setup failed" }
$hash = (Get-FileHash "$build\SSHManager-Setup.exe" -Algorithm SHA256).Hash.ToLower()
Set-Content "$build\SSHManager-Setup.exe.sha256" "$hash  SSHManager-Setup.exe" -Encoding ascii
"Built $build\SSHManager-Setup.exe ($([math]::Round((Get-Item "$build\SSHManager-Setup.exe").Length / 1MB, 1)) MB)"
