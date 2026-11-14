@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-fbx-assets.ps1" %*
exit /b %ERRORLEVEL%
