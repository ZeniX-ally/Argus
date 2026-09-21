@echo off
setlocal
title Argus �Զ�������
cd /d "%~dp0"

where powershell >nul 2>nul
if errorlevel 1 (
    echo [����] δ�ҵ� PowerShell���޷�������
    pause
    exit /b 1
)

if not exist "%~dp0deploy_update.ps1" (
    echo [����] ȱ�� deploy_update.ps1�����뱾�ļ�ͬĿ¼����
    pause
    exit /b 1
)

if not exist "%~dp0Argus-v*-update.zip" (
    echo [����] δ�ҵ� Argus-v*-update.zip ���°������뱾�ļ�ͬĿ¼����
    pause
    exit /b 1
)

:menu
cls
echo ============================================================
echo                   Argus �Զ�������
echo ------------------------------------------------------------
echo   ���°�: Argus-v{ver}-update.zip��ͬĿ¼�Զ�ʶ��
echo   ����:   �Զ����� / �ϲ�����(������̨��) / ��������
echo            / ������ exe / ����У�� / �ɻع�
echo ------------------------------------------------------------
echo    [1] ����ģʽ   ��ֻ�鿴����ƻ������Ķ��κ��ļ���
echo    [2] ִ�в���   ���Զ���λ��װλ�ã����ݺ�������
echo    [3] �ֶ�ָ��Ŀ¼����
echo    [4] �ع�       ���ָ�������ǰ��
echo    [0] �˳�
echo ============================================================
set /p act=��ѡ��󰴻س�:

if "%act%"=="1" goto preview
if "%act%"=="2" goto deploy
if "%act%"=="3" goto deploy_manual
if "%act%"=="4" goto rollback
if "%act%"=="0" exit /b 0
goto menu

:preview
echo.
echo ---- ����ģʽ�����鿴�ƻ�������Ķ��κ��ļ� ----
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_update.ps1"
echo.
pause
goto menu

:deploy
echo.
echo ---- ִ�в����ȱ��ݣ��ٸ������� ----
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_update.ps1" -Execute
echo.
echo ���Ϸ����� [WARN] ����ϸ�Ķ����ع������������ĩβ��
pause
goto menu

:deploy_manual
echo.
echo ---- �ֶ�ָ��Ŀ¼���� ----
echo ���Զ���λʧ�ܣ��������̨����Ŀ¼�����磺
echo   D:\Argus
set /p tdir=��װĿ¼:
if "%tdir%"=="" goto menu
if not exist "%tdir%" (
    echo [����] Ŀ¼������: %tdir%
    pause
    goto menu
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_update.ps1" -Target "%tdir%" -Execute
echo.
pause
goto menu

:rollback
echo.
echo ---- �ع� ----
echo �����벿��ʱ���ɵı���Ŀ¼·�������磺
echo   D:\Argus\_backup_20260805_164127
set /p bk=����Ŀ¼·��:
if "%bk%"=="" goto menu
if not exist "%bk%" (
    echo [����] Ŀ¼������: %bk%
    pause
    goto menu
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_update.ps1" -Rollback "%bk%"
echo.
pause
goto menu
