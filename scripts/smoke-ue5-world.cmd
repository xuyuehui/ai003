@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0smoke-ue5-world.ps1" %*
exit /b %ERRORLEVEL%
