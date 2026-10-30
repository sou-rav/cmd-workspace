@echo off
rem Starts the built app from the "program" folder. No build. Tells you if it did not start.
cd /d "%~dp0"
if not exist "program\CmdWorkspace.exe" (
  echo The app is not built yet. Double-click build-exe.cmd first.
  pause
  exit /b 1
)
if not exist logs mkdir logs
set "CMDWS_LOGDIR=%~dp0logs"
start "" "program\CmdWorkspace.exe" --no-cwd
timeout /t 6 /nobreak >nul
tasklist /fi "imagename eq CmdWorkspace.exe" | find /i "CmdWorkspace.exe" >nul
if errorlevel 1 (
  echo.
  echo The app did not stay open. Double-click make-log-bundle.cmd and send me logs-for-claude.zip.
  pause
)
