@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1" %*
set "build_result=%ERRORLEVEL%"
echo.
pause
exit /b %build_result%

