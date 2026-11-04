@echo off
rem Two headless clients + /demo counters: one-command technical demo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0demo-scenario.ps1" %*
