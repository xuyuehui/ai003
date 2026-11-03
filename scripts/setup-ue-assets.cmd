@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-ue-assets.ps1" %*
exit /b %ERRORLEVEL%
