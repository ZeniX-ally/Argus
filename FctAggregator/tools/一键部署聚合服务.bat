@echo off
chcp 65001 >nul
rem ============================================================
rem  Argus 聚合服务一键部署（产线维护者用）
rem  功能: 生成 config.json / 防火墙放行 / 开机自启 / 启动聚合服务
rem  用法: 解压发布包后双击本文件, UAC 弹窗点"是"
rem ============================================================
cd /d "%~dp0"

if not exist "%~dp0Argus.exe" (
    echo [错误] 未找到 Argus.exe, 请把本 bat 放到发布包根目录（与 Argus.exe 同目录）。
    pause
    exit /b 1
)

net session >nul 2>&1
if %errorlevel% neq 0 goto :elevate
goto :deploy

:elevate
echo 需要管理员权限, 正在请求提权（UAC 弹窗请点"是"）...
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
if %errorlevel% neq 0 (
    echo [错误] 提权失败, 请右键本 bat - 以管理员身份运行。
    pause
    exit /b 1
)
echo 提权成功, 原窗口即将关闭, 部署结果请看新窗口。
exit /b 0

:deploy
echo ============================================================
echo  Argus 聚合服务一键部署
echo ============================================================
"%~dp0Argus.exe" agg --install
echo.
pause
