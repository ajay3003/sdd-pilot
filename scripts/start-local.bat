@echo off
title BirkNext Launcher

echo ============================================================
echo Starting BirkNext...
echo ============================================================

powershell.exe -ExecutionPolicy Bypass -File "%~dp0start-local.ps1" %*

pause