@echo off
rem One-command demo: scenario + visual compare + room fight, then a report.
rem Usage: demo-hub.cmd [-SkipScenario] [-SkipCompare] [-SkipRoom] [-UpdateBaseline]
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0demo-hub.ps1" %*
exit /b %ERRORLEVEL%
