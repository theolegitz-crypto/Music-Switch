@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build-Installer.ps1"
if errorlevel 1 (
  echo.
  echo Build failed. See the message above.
  pause
)
