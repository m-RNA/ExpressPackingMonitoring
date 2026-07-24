@echo off
setlocal

chcp 65001 >nul
title 快递打包监控 - 增量更新
cd /d "%~dp0"

echo 快递打包监控增量更新
echo.
echo 脚本会从 config.json 读取原安装目录，并校验补丁后完成更新。
echo 如果软件正在运行，脚本会请求软件正常退出并等待录像保存。
echo 请勿单独移动此 CMD、apply_app_patch.ps1、patch_manifest.json 或 files 文件夹。
echo.

where powershell.exe >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 Windows PowerShell，无法安装增量更新。
    echo.
    pause
    exit /b 1
)

set "EPM_APP_PATCH_ROOT=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$root=$env:EPM_APP_PATCH_ROOT; $scriptPath=Join-Path $root 'apply_app_patch.ps1'; $scriptText=[System.IO.File]::ReadAllText($scriptPath,[System.Text.Encoding]::UTF8); & ([ScriptBlock]::Create($scriptText)) -PatchRoot $root"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if "%EXIT_CODE%"=="0" (
    echo 更新流程已完成。
) else (
    echo 更新未完成，请根据上方提示处理后重试。
)
echo.
pause
exit /b %EXIT_CODE%
