@echo off
setlocal
title Argus 自动更新器
cd /d "%~dp0"

where powershell >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 PowerShell，无法继续。
    pause
    exit /b 1
)

if not exist "%~dp0deploy_update.ps1" (
    echo [错误] 缺少 deploy_update.ps1（请与本文件同目录）。
    pause
    exit /b 1
)

if not exist "%~dp0Argus-v*-update.zip" (
    echo [错误] 未找到 Argus-v*-update.zip 更新包（请与本文件同目录）。
    pause
    exit /b 1
)

:menu
cls
echo ============================================================
echo                   Argus 自动更新器
echo ------------------------------------------------------------
echo   更新包: Argus-v3.2.2-update.zip（同目录自动识别）
echo   功能:   自动备份 / 合并配置(保留机台号) / 覆盖升级
echo            / 清理旧 exe / 启动校验 / 可回滚
echo ------------------------------------------------------------
echo    [1] 演练模式   （只查看部署计划，不改动任何文件）
echo    [2] 执行部署   （自动定位安装位置，备份后升级）
echo    [3] 手动指定目录部署
echo    [4] 回滚       （恢复到升级前）
echo    [0] 退出
echo ============================================================
set /p act=请选择后按回车:

if "%act%"=="1" goto preview
if "%act%"=="2" goto deploy
if "%act%"=="3" goto deploy_manual
if "%act%"=="4" goto rollback
if "%act%"=="0" exit /b 0
goto menu

:preview
echo.
echo ---- 演练模式：仅查看计划，不会改动任何文件 ----
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_update.ps1"
echo.
pause
goto menu

:deploy
echo.
echo ---- 执行部署：先备份，再覆盖升级 ----
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_update.ps1" -Execute
echo.
echo 若上方出现 [WARN] 请仔细阅读；回滚命令见部署结果末尾。
pause
goto menu

:deploy_manual
echo.
echo ---- 手动指定目录部署 ----
echo 若自动定位失败，请输入机台程序目录，例如：
echo   D:\Argus
set /p tdir=安装目录:
if "%tdir%"=="" goto menu
if not exist "%tdir%" (
    echo [错误] 目录不存在: %tdir%
    pause
    goto menu
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_update.ps1" -Target "%tdir%" -Execute
echo.
pause
goto menu

:rollback
echo.
echo ---- 回滚 ----
echo 请输入部署时生成的备份目录路径，例如：
echo   D:\Argus\_backup_20260805_164127
set /p bk=备份目录路径:
if "%bk%"=="" goto menu
if not exist "%bk%" (
    echo [错误] 目录不存在: %bk%
    pause
    goto menu
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_update.ps1" -Rollback "%bk%"
echo.
pause
goto menu
