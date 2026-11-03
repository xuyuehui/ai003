@echo off
rem Runs smoke-all.ps1 with a bypassed execution policy (this machine defaults to Restricted).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0smoke-all.ps1" %*
exit /b %ERRORLEVEL%
