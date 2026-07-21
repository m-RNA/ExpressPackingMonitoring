@echo off
setlocal
where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo PowerShell 7 ^(pwsh.exe^) was not found.
  echo Please send the videos.db and log folder for offline diagnosis.
  pause
  exit /b 1
)

pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Diagnose-MissingVideos.ps1" -AppDir "%~dp0app" -OutputDir "%~dp0diagnostic-report"
echo.
echo Diagnostic completed. See: %~dp0diagnostic-report
pause
