@echo off
setlocal DisableDelayedExpansion
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Build-Server.ps1" %*
set "BUILD_EXIT=%ERRORLEVEL%"
if "%~1"=="" pause
exit /b %BUILD_EXIT%
