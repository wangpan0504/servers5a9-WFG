@echo off
setlocal DisableDelayedExpansion
set "SERVER_EXE="
if exist "%~dp0DfoServer.exe" set "SERVER_EXE=%~dp0DfoServer.exe"
if not defined SERVER_EXE if exist "%~dp0dist\win-x64\DfoServer.exe" set "SERVER_EXE=%~dp0dist\win-x64\DfoServer.exe"
if not defined SERVER_EXE if exist "%~dp0Server\DfoServer\bin\Debug\net10.0\DfoServer.exe" set "SERVER_EXE=%~dp0Server\DfoServer\bin\Debug\net10.0\DfoServer.exe"
if not defined SERVER_EXE (
  echo DfoServer was not found. Build first:
  echo   dotnet build ServerS5A9.sln -c Debug
  exit /b 1
)
for %%I in ("%SERVER_EXE%") do cd /d "%%~dpI"
if "%~1"=="" if exist "DfoLauncher.exe" (
  start "" "DfoLauncher.exe"
  exit /b 0
)
if "%~1"=="" (
  echo GM web ^(embedded, local only^): http://127.0.0.1:23103/
  "%SERVER_EXE%" --gm-port 23102 --gm-web-port 23103
  exit /b %ERRORLEVEL%
)
"%SERVER_EXE%" %*
exit /b %ERRORLEVEL%
