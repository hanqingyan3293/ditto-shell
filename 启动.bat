@echo off
title Ditto Shell
cd /d "%~dp0"

echo ============================================
echo   Ditto Shell 启动器
echo ============================================
echo.

REM ═══ 1) 关掉旧进程（不关的话构建会因文件锁失败，然后启动旧 exe）═══
echo [1/4] 关闭旧版本...
taskkill /IM DittoShell.exe /F >nul 2>&1
ping -n 2 127.0.0.1 >nul
echo       完成

REM ═══ 2) 编译 ═══
echo [2/4] 编译中（约 10 秒）...
set "LOG=%TEMP%\dittoshell_build.log"
dotnet build "src\DittoShell.App\DittoShell.App.csproj" -c Debug -v q --nologo > "%LOG%" 2>&1

if errorlevel 1 (
    echo.
    echo   [失败] 编译出错，详情如下：
    echo   ----------------------------------------
    type "%LOG%"
    echo   ----------------------------------------
    echo.
    pause
    exit /b 1
)
echo       完成

REM ═══ 3) 检查产物是否真的比源码新 ═══
echo [3/4] 检查产物...
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\check-build.ps1" ^
    -ExePath "src\DittoShell.App\bin\Debug\net8.0-windows\DittoShell.exe" ^
    -SrcDir "src"

if errorlevel 1 (
    echo.
    pause
    exit /b 1
)

REM ═══ 4) 启动 ═══
echo [4/4] 启动...
start "" "src\DittoShell.App\bin\Debug\net8.0-windows\DittoShell.exe"
echo.
echo ============================================
echo   已启动（这是最新版本）
echo ============================================
ping -n 3 127.0.0.1 >nul
