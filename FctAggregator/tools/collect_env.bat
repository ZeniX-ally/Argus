@echo off
chcp 65001 >nul
setlocal
title Argus Environment Collector
cd /d "%~dp0"

if not exist "%~dp0collect_env.ps1" (
    echo [ERROR] collect_env.ps1 not found next to this file.
    pause
    exit /b 1
)

set "PSEXE=powershell"
where pwsh >nul 2>nul && set "PSEXE=pwsh"

echo ============================================================
echo   Argus Environment Collector (read-only)
echo ------------------------------------------------------------
echo   Collects every path / config / environment fact Argus uses,
echo   plus a bundle of config + sample XML + db snapshot.
echo   Takes 1-3 minutes. Do not close this window.
echo ============================================================
echo.

"%PSEXE%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect_env.ps1" %*

echo.
echo Done. The report folder (and .zip) is on your Desktop: Argus-env-*
echo Send the .zip file back.
pause
