@echo off
setlocal DisableDelayedExpansion
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Build-Server.ps1" -SingleFile %*
set "BUILD_EXIT=%ERRORLEVEL%"
if "%~1"=="" pause
exit /b %BUILD_EXIT%
