@echo off
rem Removes the entry added by register-cws.cmd. Nothing else is changed.
setlocal
cd /d "%~dp0..\"
if not exist logs mkdir logs
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\cws.exe" /f > logs\unregister-cws.log 2>&1
if errorlevel 1 (
  echo Nothing to remove, or it failed. See logs\unregister-cws.log
) else (
  echo Done. The name cws is removed.
)
pause
