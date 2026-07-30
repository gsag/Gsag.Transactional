@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-graphify.ps1" %*
if errorlevel 1 (
    echo Graphify setup failed.
    pause
    exit /b %errorlevel%
)

echo.
echo Press any key to close this window.
pause >nul
endlocal
