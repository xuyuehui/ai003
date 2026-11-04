@echo off
rem Read-only demo status: health + /demo counters.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0demo-status.ps1" %*
