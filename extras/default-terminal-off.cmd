@echo off
rem Phase 12b: turns the default terminal OFF without opening the app window (same code as the switch in Settings > Shell).
rem It puts your two Windows console values back from the backup that was saved when the switch was turned on.
set "EXE=%~dp0..\program\CmdWorkspace.exe"
if not exist "%EXE%" set "EXE=C:\Program Files\CMD Workspace\CmdWorkspace.exe"
if not exist "%EXE%" (
  echo CmdWorkspace.exe was not found. Build it first with build-exe.cmd, or install the app.
  pause
  exit /b 1
)
echo Putting the Windows console values back...
start "" /wait "%EXE%" --restore-default-terminal
echo Done. Press Windows key + R, type cmd and press Enter: a normal window opens again.
echo Details are in the newest file in %LOCALAPPDATA%\CMDWorkspace\logs
pause
