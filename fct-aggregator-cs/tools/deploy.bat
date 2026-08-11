@echo off
setlocal
title Argus Update Tool
cd /d "%~dp0"

where powershell >nul 2>nul
if errorlevel 1 (
    echo [ERROR] PowerShell not found.
    pause
    exit /b 1
)

if not exist "%~dp0deploy_update.ps1" (
    echo [ERROR] deploy_update.ps1 not found.
    pause
    exit /b 1
)

if not exist "%~dp0Argus-v*-update.zip" (
    echo [ERROR] Argus-v*-update.zip not found.
    pause
    exit /b 1
)

:menu
cls
echo ============================================================
echo                   Argus Update Tool
echo ------------------------------------------------------------
echo  Update package: Argus-v*-update.zip (auto-detect)
echo  Features:  backup / merge config / upgrade / cleanup old exe
echo ------------------------------------------------------------
echo   [1]  Preview  (dry run, no files changed)
echo   [2]  Deploy   (backup then upgrade)
echo   [3]  Deploy with manual target dir
echo   [4]  Rollback (restore previous version)
echo   [0]  Exit
echo ============================================================
set /p act=Select: 

if "%act%"=="1" goto preview
if "%act%"=="2" goto deploy
if "%act%"=="3" goto deploy_manual
if "%act%"=="4" goto rollback
if "%act%"=="0" exit /b 0
goto menu

:preview
echo.
echo ---- Preview mode: will NOT change any files ----
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_update.ps1"
echo.
pause
goto menu

:deploy
echo.
echo ---- Deploy: backup first, then upgrade ----
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_update.ps1" -Execute
echo.
echo If you see [WARN] above, read carefully. Rollback command shown at end.
pause
goto menu

:deploy_manual
echo.
echo ---- Deploy with manual target dir ----
echo If auto-detect fails, specify the install dir, e.g. D:\Argus
set /p tdir=Install dir: 
if "%tdir%"=="" goto menu
if not exist "%tdir%" (
    echo [ERROR] Directory not found: %tdir%
    pause
    goto menu
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_update.ps1" -Target "%tdir%" -Execute
echo.
pause
goto menu

:rollback
echo.
echo ---- Rollback ----
echo Enter the backup directory path, e.g.
echo   D:\Argus\_backup_20260805_164127
set /p bk=Backup path: 
if "%bk%"=="" goto menu
if not exist "%bk%" (
    echo [ERROR] Directory not found: %bk%
    pause
    goto menu
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_update.ps1" -Rollback "%bk%"
echo.
pause
goto menu
