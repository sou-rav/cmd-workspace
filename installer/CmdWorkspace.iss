; CMD Workspace installer (Phase 8d). Inno Setup 6. Written, NOT compiled by the assistant (no Windows here).
; Unsigned: Windows SmartScreen will warn.
; Install, upgrade and uninstall never touch %LOCALAPPDATA%\CMDWorkspace (settings, projects, logs, WebView2 data).
; Only the uninstaller can remove it, and only if the user answers Yes (default No, silent uninstall keeps it).

#define AppName "CMD Workspace"
#define AppVersion "0.9.2"
#define AppExe "CmdWorkspace.exe"

[Setup]
AppId={{B6D286BA-D974-4D5A-996A-F632A4FBEB5E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=CMD Workspace
DefaultDirName={autopf}\CMD Workspace
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=Output
OutputBaseFilename=CMDWorkspace-Setup-{#AppVersion}
SetupIconFile=..\app\Assets\icon-dark.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription={#AppName} setup

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; Flags: unchecked
Name: "contextmenu"; Description: "Add 'Open CMD Workspace here' to the right click menu of folders (on Windows 11 under Show more options)"
Name: "addtopath"; Description: "Add the cws command to PATH (so cws also works in Command Prompt and PowerShell)"; Flags: unchecked

[Files]
; The built full app (build-exe.cmd, folder "program"). Debug files are left out.
Source: "..\program\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; Licence texts of the terminal files and fonts (made by tools\fetch-licences.ps1) and the notices list.
Source: "..\app\Web\vendor\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
; cws.cmd for Command Prompt and PowerShell (only reachable when the PATH task is chosen).
Source: "cws.cmd"; DestDir: "{app}\bin"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--no-cwd"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--no-cwd"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; Typing cws in the Explorer address bar or Win+R.
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\cws.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe}"; Flags: uninsdeletekey
; Right click item for folders, the empty space inside a folder, and drive roots (optional task). "%V" is the folder. The ending backslash dot keeps a drive root like D:\ from swallowing the closing quote.
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\shell\CMDWorkspace"; ValueType: string; ValueName: ""; ValueData: "Open CMD Workspace here"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\shell\CMDWorkspace"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: contextmenu
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\shell\CMDWorkspace\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" --folder ""%V\."""; Tasks: contextmenu
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\Background\shell\CMDWorkspace"; ValueType: string; ValueName: ""; ValueData: "Open CMD Workspace here"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\Background\shell\CMDWorkspace"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: contextmenu
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\Background\shell\CMDWorkspace\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" --folder ""%V\."""; Tasks: contextmenu
Root: HKLM; Subkey: "SOFTWARE\Classes\Drive\shell\CMDWorkspace"; ValueType: string; ValueName: ""; ValueData: "Open CMD Workspace here"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKLM; Subkey: "SOFTWARE\Classes\Drive\shell\CMDWorkspace"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: contextmenu
Root: HKLM; Subkey: "SOFTWARE\Classes\Drive\shell\CMDWorkspace\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" --folder ""%V\."""; Tasks: contextmenu
; PATH entry (optional task).
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\bin"; Check: NeedsAddPath('{app}\bin'); Tasks: addtopath

[UninstallRun]
; Phase 12b: puts the Windows console values back if the default terminal switch was on.
Filename: "{app}\{#AppExe}"; Parameters: "--restore-default-terminal"; RunOnceId: "RestoreDefaultTerminal"; Flags: runhidden waituntilterminated skipifdoesntexist

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--no-cwd"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  EnvKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function NeedsAddPath(Param: string): Boolean;
var
  Old, P: string;
begin
  P := ExpandConstant(Param);
  if not RegQueryStringValue(HKLM, EnvKey, 'Path', Old) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(P) + ';', ';' + Uppercase(Old) + ';') = 0;
end;

procedure RemoveFromPath(P: string);
var
  Paths: string;
  Idx: Integer;
begin
  if not RegQueryStringValue(HKLM, EnvKey, 'Path', Paths) then exit;
  Idx := Pos(';' + Uppercase(P) + ';', ';' + Uppercase(Paths) + ';');
  if Idx = 0 then exit;
  if Idx = 1 then
    Delete(Paths, 1, Length(P) + 1)
  else
    Delete(Paths, Idx - 1, Length(P) + 1);
  RegWriteExpandStringValue(HKLM, EnvKey, 'Path', Paths);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, Term: string;
begin
  if CurUninstallStep = usUninstall then
    RemoveFromPath(ExpandConstant('{app}\bin'));

  if CurUninstallStep = usPostUninstall then
  begin
    // Safety net for the default terminal: if the two Windows values still point to this app, remove them (Windows then uses its own default).
    if RegQueryStringValue(HKCU, 'Console\%%Startup', 'DelegationTerminal', Term) and
       (CompareText(Term, '{3B7D619E-8A2F-4954-A488-FDD3E58776D1}') = 0) then
    begin
      RegDeleteValue(HKCU, 'Console\%%Startup', 'DelegationTerminal');
      RegDeleteValue(HKCU, 'Console\%%Startup', 'DelegationConsole');
    end;
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\CLSID\{3B7D619E-8A2F-4954-A488-FDD3E58776D1}');
    DataDir := ExpandConstant('{localappdata}\CMDWorkspace');
    if DirExists(DataDir) and (not UninstallSilent) then
    begin
      // Default button is No. Nothing is deleted unless the user answers Yes.
      if MsgBox('Do you also want to remove your settings, saved projects, logs and web data?' + #13#10 + #13#10 +
                DataDir + #13#10 + #13#10 +
                'Choose No to keep them (for example for a later reinstall).',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        if Pos('\CMDWORKSPACE', Uppercase(DataDir)) > 0 then
          DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
