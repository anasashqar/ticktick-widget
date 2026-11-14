; Inno Setup script: per-user install (no admin), Start Menu shortcut, optional autostart (Task Scheduler via install.ps1).
; Built by .github/workflows/build.yml:  ISCC /DAppVersion=1.0.N installer\TickWidget.iss
; User data lives in %APPDATA%\TickWidget and is NOT removed on uninstall.
; Test switch:  TickWidget-Setup.exe /SKIPKILL=1  (does not kill a running TickWidget.exe)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6F0C5C3E-8B1D-4D52-9C7A-2E4B1F6A9D31}
AppName=TickWidget
AppVersion={#AppVersion}
AppPublisher=Anas
DefaultDirName={localappdata}\Programs\TickWidget
DefaultGroupName=TickWidget
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\Output
OutputBaseFilename=TickWidget-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\TickWidget.exe
CloseApplications=no

[Files]
Source: "..\TickWidget.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\sounds\*"; DestDir: "{app}\sounds"; Flags: ignoreversion recursesubdirs
Source: "..\install.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\TickWidget"; Filename: "{app}\TickWidget.exe"; Parameters: "--auto"

[Tasks]
Name: "autostart"; Description: "Start TickWidget automatically when I sign in to Windows"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\install.ps1"""; Flags: runhidden; Tasks: autostart; Check: RealRun; StatusMsg: "Setting up autostart..."
Filename: "{app}\TickWidget.exe"; Parameters: "--auto"; Description: "Launch TickWidget"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\install.ps1"" -Remove"; Flags: runhidden; RunOnceId: "RemoveAutostartTask"; Check: RealRun

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
{ /SKIPKILL=1 = test run: do not kill the running app and do not touch the scheduled task (same switch for setup and uninstall) }
function RealRun(): Boolean;
begin
  Result := ExpandConstant('{param:SKIPKILL|0}') <> '1';
end;

procedure KillApp();
var
  rc: Integer;
begin
  if RealRun() then
    Exec('taskkill.exe', '/F /IM TickWidget.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillApp();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then KillApp();
end;
