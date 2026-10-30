@echo off
rem Builds a ready-to-run copy of the app into the "program" folder (run this only when the code changed).
rem After that, double-click open-app.cmd (or program\CmdWorkspace.exe) to start the app. No build wait.
setlocal
cd /d "%~dp0"
if not exist logs mkdir logs
set "CMDWS_LOGDIR=%~dp0logs"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo The .NET SDK is not installed. Open CMD and run:  winget install Microsoft.DotNet.SDK.10
  pause
  exit /b 1
)
if not exist "app\Web\vendor\xterm.js" (
  echo First run: downloading the terminal files...
  powershell -NoProfile -ExecutionPolicy Bypass -File "app\setup-vendor.ps1" > logs\setup.log 2>&1
  if errorlevel 1 (
    echo The download failed. Log: %~dp0logs\setup.log
    echo Run make-log-bundle.cmd and send me logs-for-claude.zip.
    pause
    exit /b 1
  )
)

echo Building the app into the program folder. This can take a few minutes...
dotnet publish "app\CmdWorkspace.csproj" -c Release -r win-x64 --self-contained true -o program -nologo -v minimal > logs\publish.log 2>&1
if errorlevel 1 (
  echo.
  echo THE BUILD FAILED. These are the error lines:
  echo ------------------------------------------------------------
  findstr /i /c:" error " logs\publish.log
  echo ------------------------------------------------------------
  echo Full log: %~dp0logs\publish.log
  echo Run make-log-bundle.cmd and send me logs-for-claude.zip.
  pause
  exit /b 1
)
rem Phase 7 fix (2026-10-28): dotnet publish leaves out the XAML resource files (.pri and .xbf), and the exe then crashes
rem with a XamlParseException (found in logs: Microsoft.UI.Xaml.dll, code 0xc000027b). The normal build folder has them, so every file that
rem is missing in "program" is copied from it (nothing that already exists is changed, debug .pdb files are skipped).
set "BIN=app\bin\Release\net10.0-windows10.0.19041.0\win-x64"
if exist "%BIN%" (
  robocopy "%BIN%" "program" /E /XC /XN /XO /XF *.pdb /NJH /NJS /NFL /NDL >> logs\publish.log 2>&1
  if errorlevel 8 echo WARNING: copying the resource files failed. See logs\publish.log
) else (
  echo WARNING: the build folder %BIN% was not found, so the resource files were not copied.
)
if not exist "program\CmdWorkspace.pri" if not exist "program\resources.pri" echo WARNING: the .pri resource file is still missing in the program folder. The app may not open. Run diagnose-exe.cmd.
echo.
rem Shortcut in the project folder, so the many files stay inside "program".
powershell -NoProfile -Command "$w=New-Object -ComObject WScript.Shell; $l=$w.CreateShortcut('%~dp0CMD Workspace.lnk'); $l.TargetPath='%~dp0program\CmdWorkspace.exe'; $l.Arguments='--no-cwd'; $l.WorkingDirectory='%~dp0program'; $l.Save()" >> logs\publish.log 2>&1
echo Done. Start the app with "CMD Workspace" (shortcut) or open-app.cmd. All program files are inside the program folder.
pause
