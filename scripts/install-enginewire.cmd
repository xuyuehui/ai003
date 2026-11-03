@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-enginewire.ps1" %*
exit /b %ERRORLEVEL%
