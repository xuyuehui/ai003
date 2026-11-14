@echo off
rem Six-frame diff against a reference/baseline directory.
rem   scripts\stills-diff.cmd artifacts\stills\accepted-YYYYMMDD-HHMM artifacts\ashkar
rem Uses %DSH_PYTHON% if set, else the python on PATH; the bundled DSH runtime is
rem tried last because it is the only one guaranteed to carry numpy + Pillow.
setlocal
set "PY=%DSH_PYTHON%"
if not defined PY (
  python -c "import numpy, PIL" >nul 2>&1 && set "PY=python"
)
if not defined PY (
  set "PY=C:\Users\xuyuehui\AppData\Local\Programs\DS Bot\resources\primary-runtime\dependencies\python\python.exe"
)
"%PY%" "%~dp0stills-diff.py" %*
exit /b %ERRORLEVEL%
