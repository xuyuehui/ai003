@echo off
rem Same-frame luma comparison, optionally split by a vegetation mask.
rem   scripts\stills-luma.cmd gui.png batchmode.png --mask mask.png
setlocal
set "PY=%DSH_PYTHON%"
if not defined PY (
  python -c "import numpy, PIL" >nul 2>&1 && set "PY=python"
)
if not defined PY (
  set "PY=C:\Users\xuyuehui\AppData\Local\Programs\DS Bot\resources\primary-runtime\dependencies\python\python.exe"
)
"%PY%" "%~dp0stills-luma.py" %*
exit /b %ERRORLEVEL%
