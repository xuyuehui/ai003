@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0generate-world.ps1" %*
exit /b %ERRORLEVEL%
