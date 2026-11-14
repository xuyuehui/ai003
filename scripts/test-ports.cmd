@echo off
rem Safety gate: Stop-PortListener may only stop the PID its caller started.
rem Usage: test-ports.cmd [-Port 8091]
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0test-ports.ps1" %*
exit /b %ERRORLEVEL%
