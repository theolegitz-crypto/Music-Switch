@echo off
setlocal
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Install-GameBarOverlay.ps1"
set EXIT_CODE=%ERRORLEVEL%

echo.
if not "%EXIT_CODE%"=="0" (
    echo Game Bar overlay installation failed. See the message above.
) else (
    echo Game Bar overlay installation completed.
)

echo.
pause
exit /b %EXIT_CODE%
