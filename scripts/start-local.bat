@echo off
title BirkNext Launcher

echo ============================================================
echo Starting BirkNext...
echo ============================================================

REM Detect whether dev-build.ps1 exists (source checkout vs tester package)
set "DEV_BUILD=%~dp0..\AIAssisted\frontend\dev-build.ps1"
set "FRONTEND_CSS=%~dp0..\AIAssisted\frontend\BirkNext.Web\wwwroot\BirkNext.Web.styles.css"

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

    REM Remove the development CSS before start-local.ps1 builds.
    REM start-local.ps1 will build the frontend again, and the CSS file
    REM in wwwroot causes static assets manifest conflicts.
    REM dev-build.ps1 sets up the CSS for immediate dev-server use;
    REM start-local.ps1's build will regenerate everything needed.
    if exist "%FRONTEND_CSS%" (
        del /q "%FRONTEND_CSS%"
    )
) else (
    echo Published package detected - skipping development build.
    echo.
)

powershell.exe -ExecutionPolicy Bypass -File "%~dp0start-local.ps1" %*

pause