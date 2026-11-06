@echo off
setlocal
cd /d "%~dp0"
set "SERVER_ROOT=%~dp0"
for %%I in ("%~dp0.") do if /i "%%~nxI"==".95jp-runtime" set "SERVER_ROOT=%~dp0..\"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Install .NET 10 ASP.NET Core Runtime x64 first.
  exit /b 1
)
dotnet "%~dp0DfoServer.dll" --gm-port 23102 --gm-web-port 23103 --data-dir "%SERVER_ROOT%Data" --log-dir "%SERVER_ROOT%Logs" --resources-dir "%SERVER_ROOT%Resources" --pvf-path "%SERVER_ROOT%Data\Pvf\Script.pvf" %*
exit /b %errorlevel%
