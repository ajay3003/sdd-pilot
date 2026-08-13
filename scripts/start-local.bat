@echo off
title BirkNext Launcher

echo ============================================================
echo Starting BirkNext...
echo ============================================================

REM Detect whether dev-build.ps1 exists (source checkout vs tester package)
set "DEV_BUILD=%~dp0..\AIAssisted\frontend\dev-build.ps1"

if exist "%DEV_BUILD%" (
    echo.
    echo Development source detected - preparing frontend...
    echo.

    powershell.exe -ExecutionPolicy Bypass -File "%DEV_BUILD%"

    if errorlevel 1 (
        echo.
        echo ============================================================
        echo Development build failed. BirkNext was not started.
        echo ============================================================
        pause
        exit /b 1
    )
) else (
    echo Published package detected - skipping development build.
    echo.
)

powershell.exe -ExecutionPolicy Bypass -File "%~dp0start-local.ps1" %*

pause