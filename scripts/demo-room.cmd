@echo off
rem One-command room demo: gateway + Unity (room) + UE bot, captures both and composes.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0demo-room.ps1" %*
