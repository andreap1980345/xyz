@echo off
powershell.exe -NoProfile -File "%~dp0Build.ps1"
exit /b %errorlevel%
