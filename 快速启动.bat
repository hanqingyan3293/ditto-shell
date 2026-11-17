@echo off
title Ditto Shell（快速启动）
cd /d "%~dp0"
if not exist "src\DittoShell.App\bin\Debug\net8.0-windows\DittoShell.exe" (
    echo 还没编译过，请先运行「启动.bat」
    pause
    exit /b 1
)
start "" "%~dp0src\DittoShell.App\bin\Debug\net8.0-windows\DittoShell.exe"
