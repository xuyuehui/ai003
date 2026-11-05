@echo off
rem Verifies UE camera-relative WASD -> protocol direction mapping.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-ue-input.ps1" %*
