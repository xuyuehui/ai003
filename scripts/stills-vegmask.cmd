@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0stills-vegmask.ps1" %*
exit /b %ERRORLEVEL%
