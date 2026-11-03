@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0smoke-ue5.ps1" %*
exit /b %ERRORLEVEL%
