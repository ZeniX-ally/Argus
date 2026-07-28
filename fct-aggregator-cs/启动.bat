@echo off
rem FCT-Aggregator 启动脚本(用 dotnet 运行 DLL, 不用 exe, 规避杀毒对无签名exe的拦截)
rem 需要机台已安装 .NET 8 Desktop Runtime
cd /d "%~dp0"
start "" /b dotnet "FCT-Aggregator.dll"
