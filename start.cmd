@echo off
rem CMD Workspace: one file does everything. Double-click it.
rem  1. checks the .NET SDK  2. downloads the terminal files (first time only)  3. builds  4. starts the app
rem Every step writes a log file into the "logs" folder next to this file.
setlocal
cd /d "%~dp0"
title CMD Workspace
if not exist logs mkdir logs
set "CMDWS_LOGDIR=%~dp0logs"

echo CMD Workspace start > logs\start.log
echo %DATE% %TIME% >> logs\start.log

where dotnet >nul 2>&1
if errorlevel 1 (
  echo.
  echo The .NET SDK is not installed.
  echo Open a new CMD window and run:  winget install Microsoft.DotNet.SDK.10
  echo Then double-click start.cmd again.
  pause
  exit /b 1
)
dotnet --version >> logs\start.log 2>&1

if not exist "app\Web\vendor\xterm.js" (
  echo First run: downloading the terminal files. This needs internet and takes a minute...
  powershell -NoProfile -ExecutionPolicy Bypass -File "app\setup-vendor.ps1" > logs\setup.log 2>&1
  if errorlevel 1 (
    echo.
    echo The download failed. The log is saved here: %~dp0logs\setup.log
    echo Run make-log-bundle.cmd and send me logs-for-claude.zip.
    pause
    exit /b 1
  )
)

echo Building the app. The first build can take a few minutes...
dotnet build "app\CmdWorkspace.csproj" -c Debug -nologo -v minimal > logs\build.log 2>&1
if errorlevel 1 (
  echo.
  echo THE BUILD FAILED. These are the error lines:
  echo ------------------------------------------------------------
  findstr /i /c:" error " logs\build.log
  echo ------------------------------------------------------------
  echo The full log is saved here: %~dp0logs\build.log
  echo Run make-log-bundle.cmd and send me logs-for-claude.zip.
  pause
  exit /b 1
)

echo Starting CMD Workspace. Close the app window to finish.
dotnet run --project "app\CmdWorkspace.csproj" -c Debug --no-build -- --no-cwd
echo App ended. Exit code: %ERRORLEVEL% >> logs\start.log
if not "%ERRORLEVEL%"=="0" (
  echo.
  echo The app ended with an error. Logs are in %~dp0logs
  echo Run make-log-bundle.cmd and send me logs-for-claude.zip.
  pause
)
