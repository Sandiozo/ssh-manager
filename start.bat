@echo off
rem Opens SSH Manager in the browser on this PC (starts the server first if needed).
cd /d "%~dp0"
powershell -NoProfile -Command "if (-not (Get-NetTCPConnection -LocalPort 8765 -State Listen -ErrorAction SilentlyContinue)) { Start-Process '.venv\Scripts\pythonw.exe' 'server.py' -WorkingDirectory '%~dp0'; Start-Sleep 4 }"
for /f "delims=" %%u in ('".venv\Scripts\python.exe" server.py --login-url') do start "" "%%u"
