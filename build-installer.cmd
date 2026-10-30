@echo off
rem Phase 8d: makes the installer  installer\Output\CMDWorkspace-Setup-0.9.2.exe  from the built "program" folder.
rem Needs Inno Setup 6.3 or newer (free). Run build-exe.cmd first (and again after every code change).
setlocal
cd /d "%~dp0"
if not exist logs mkdir logs
echo Build installer > logs\installer.log
if not exist "program\CmdWorkspace.exe" (
  echo The app is not built yet. Double-click build-exe.cmd first.
  pause
  exit /b 1
)
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC goto :noinno

if not exist "app\Web\vendor\licenses\licenses-summary.txt" (
  echo Downloading the licence texts of the terminal files and fonts. Needs internet...
  powershell -NoProfile -ExecutionPolicy Bypass -File "tools\fetch-licences.ps1" > logs\licences.log 2>&1
  if errorlevel 1 (
    echo The licence download failed. See logs\licences.log
    echo Run make-log-bundle.cmd and send me logs-for-claude.zip.
    pause
    exit /b 1
  )
)
findstr /i /c:"WARNING" logs\licences.log 2>nul
echo Licence summary:
type "app\Web\vendor\licenses\licenses-summary.txt"
echo.
echo Building the installer with:  %ISCC%
"%ISCC%" "installer\CmdWorkspace.iss" >> logs\installer.log 2>&1
if errorlevel 1 (
  echo.
  echo THE INSTALLER BUILD FAILED. Last lines of logs\installer.log:
  powershell -NoProfile -Command "Get-Content 'logs\installer.log' -Tail 15"
  echo Run make-log-bundle.cmd and send me logs-for-claude.zip.
  pause
  exit /b 1
)
echo.
powershell -NoProfile -Command "Get-ChildItem 'installer\Output\*.exe' | ForEach-Object { '{0}: {1:N1} MB' -f $_.FullName, ($_.Length/1MB) } | Tee-Object -FilePath 'logs\installer-size.txt'"
echo.
echo Done. The installer is unsigned: Windows SmartScreen will warn (More info, then Run anyway).
pause
exit /b 0

:noinno
echo Inno Setup is not installed. Open CMD and run:
echo   winget install JRSoftware.InnoSetup
echo Then double-click build-installer.cmd again.
pause
exit /b 1
