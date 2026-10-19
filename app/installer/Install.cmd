@echo off
rem Installs the SSH Manager app for this Windows user (no admin rights needed).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo.
pause
