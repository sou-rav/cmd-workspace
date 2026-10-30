# CMD Workspace 0.9.2

Needs: Windows 11 x64, .NET SDK 10, internet on the first build. For the installer also Inno Setup 6.3 or newer (winget install JRSoftware.InnoSetup).

## Run it
1. Unzip to a folder. Close any running CMD Workspace.
2. Open Command Prompt in the folder (type cmd in the Explorer address bar, press Enter).
3. Type: build-exe.cmd  and press Enter. Wait for the build to finish. It makes the folder "program".
4. Type: open-app.cmd  and press Enter. The app opens. (start.cmd builds and runs from source instead, slower.)

## Make the installer
1. Do the steps above first (build-exe.cmd).
2. Type: build-installer.cmd  and press Enter.
3. The installer is installer\Output\CMDWorkspace-Setup-0.9.2.exe. It is unsigned, so SmartScreen warns: click More info, then Run anyway.

## Default terminal (optional, off by default)
Settings > Shell > Default terminal. When on, Command Prompt and PowerShell windows that Windows would open (Win+R, Explorer) open inside CMD Workspace. Turn it off in the same place. If something is wrong, run extras\default-terminal-off.cmd. The uninstaller also puts the Windows values back.

## Where things are saved
Settings and projects: %LOCALAPPDATA%\CMDWorkspace. Logs: the logs folder next to the scripts. Terminal text and clipboard text are never saved or logged.

## If something fails
Double-click make-log-bundle.cmd and send logs-for-claude.zip.
