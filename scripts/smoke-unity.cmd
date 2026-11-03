@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0smoke-unity.ps1" %*
exit /b %ERRORLEVEL%
