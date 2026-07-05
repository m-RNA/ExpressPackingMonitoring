@echo off
setlocal

chcp 65001 >nul
cd /d "%~dp0"

set "BASELINE_VERSION=0.0.18"
if not "%~2"=="" set "BASELINE_VERSION=%~2"
set "BASELINE_TAG=v%BASELINE_VERSION%"
set "BASELINE_PACKAGE_DIR=package\ExpressPackingMonitoring+%BASELINE_TAG%"
set "BASELINE_FULL_DIR=%BASELINE_PACKAGE_DIR%\ExpressPackingMonitoring+%BASELINE_TAG%"
set "BASELINE_APP_DIR=%BASELINE_FULL_DIR%\app"
set "BASELINE_LAUNCHER_MANIFEST=%BASELINE_PACKAGE_DIR%\launcher_manifest_%BASELINE_TAG%.json"

set "VERSION_ARG="
if not "%~1"=="" set "VERSION_ARG=-Version %~1"

if not exist "%BASELINE_APP_DIR%\ExpressPackingMonitoring.exe" (
    echo [ERROR] Baseline app not found:
    echo         %BASELINE_APP_DIR%
    echo.
    echo Put the v%BASELINE_VERSION% full package app directory here first, then run this script again.
    echo Expected:
    echo         %BASELINE_APP_DIR%\ExpressPackingMonitoring.exe
    echo.
    pause
    exit /b 1
)

set "LAUNCHER_MANIFEST_ARG="
if exist "%BASELINE_LAUNCHER_MANIFEST%" (
    set "LAUNCHER_MANIFEST_ARG=-BaselineLauncherManifestPath ""%BASELINE_LAUNCHER_MANIFEST%"""
) else (
    echo [WARN] Baseline launcher manifest not found:
    echo        %BASELINE_LAUNCHER_MANIFEST%
    echo.
    echo Patch can still be generated, but launcher source fingerprint will not be checked.
    echo To enable launcher fingerprint check, keep launcher_manifest_%BASELINE_TAG%.json under %BASELINE_PACKAGE_DIR%\.
    echo.
)

echo Baseline version: %BASELINE_VERSION%
echo Baseline app:     %BASELINE_APP_DIR%
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "Tools\Publish-CleanPackage.ps1" ^
  %VERSION_ARG% ^
  -PatchBaselineVersion %BASELINE_VERSION% ^
  -BaselineAppDir "%BASELINE_APP_DIR%" ^
  %LAUNCHER_MANIFEST_ARG%

set "EXIT_CODE=%ERRORLEVEL%"
echo.
if "%EXIT_CODE%"=="0" (
    echo Package completed.
) else (
    echo Package failed. Exit code: %EXIT_CODE%
)
pause
exit /b %EXIT_CODE%
