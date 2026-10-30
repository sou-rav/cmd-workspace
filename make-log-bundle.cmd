@echo off
rem Packs everything useful for troubleshooting into ONE file: logs-for-claude.zip (in this folder).
rem It contains the logs folder and a copy of settings.json. No terminal text and no clipboard text are ever logged.
rem The logs DO contain folder paths (they can show your Windows user name) and error messages. Read them first if you want.
setlocal
cd /d "%~dp0"
if not exist logs mkdir logs
if exist "%LOCALAPPDATA%\CMDWorkspace\settings.json" copy /y "%LOCALAPPDATA%\CMDWorkspace\settings.json" logs\settings-copy.json >nul
if exist "app\Web\vendor\versions.txt" copy /y "app\Web\vendor\versions.txt" logs\vendor-versions.txt >nul
ver > logs\windows-version.txt
rem Phase 7: the logs of a window started by Open as Administrator can land here, so they are copied in too.
if exist "%LOCALAPPDATA%\CMDWorkspace\logs" robocopy "%LOCALAPPDATA%\CMDWorkspace\logs" "logs\localappdata-logs" /E /NJH /NJS /NFL /NDL >nul
powershell -NoProfile -Command "Compress-Archive -Path 'logs\*' -DestinationPath 'logs-for-claude.zip' -Force"
echo.
echo Done. Send this file to Claude:  %~dp0logs-for-claude.zip
pause
