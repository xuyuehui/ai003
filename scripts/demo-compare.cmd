@echo off
rem Fixed-camera Unity vs UE5 demo frame + side-by-side comparison.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0demo-compare.ps1" %*
