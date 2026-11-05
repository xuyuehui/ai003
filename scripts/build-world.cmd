@echo off
rem Regenerates the shared world and copies it into both clients.
rem Usage: build-world.cmd -Size 4096   (default 2048)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-world.ps1" %*
