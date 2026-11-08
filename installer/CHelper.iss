; C-Helper installer (Inno Setup 6)
; Build:  ISCC.exe /DAppVersion=0.7.2 installer\CHelper.iss
; Expects the published exe in out\CHelper.exe (dotnet publish src/CHelper.csproj -c Release -o out).
; Optional vendor files (see vendor/README.md) are bundled when present:
;   vendor\InsydeDCHU.dll      copied next to CHelper.exe
;   vendor\driver\*            Clevo ACPI bridge driver, installed with pnputil if included

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "C-Helper"
#define AppExe "CHelper.exe"
#define AppPublisher "th4rv4"
#define AppUrl "https://github.com/th4rv4/C-Helper"
#define Root ".."

[Setup]
AppId={{6C2E8A6F-3B1D-4E7A-9C55-0F4B2D9E7A13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile={#Root}\LICENSE
OutputDir={#Root}\out
OutputBaseFilename=CHelper-Setup-{#AppVersion}
SetupIconFile={#Root}\src\Resources\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=no
VersionInfoVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start C-Helper when I sign in (recommended)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#Root}\out\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\vendor\InsydeDCHU.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#Root}\vendor\driver\*"; DestDir: "{app}\driver"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Clevo ACPI bridge driver (only if it was bundled and the laptop is missing it)
Filename: "{sys}\pnputil.exe"; Parameters: "/add-driver ""{app}\driver\*.inf"" /install"; \
  StatusMsg: "Installing the Clevo ACPI bridge driver..."; Flags: runhidden waituntilterminated; Check: NeedsBridgeDriver
; Same scheduled task C-Helper's own Startup option uses (runs elevated at sign-in, no UAC prompt)
Filename: "{sys}\schtasks.exe"; \
  Parameters: "/create /f /tn ""C-Helper"" /tr ""\""{app}\{#AppExe}\"" --tray"" /sc onlogon /rl highest /delay 0000:10"; \
  Flags: runhidden waituntilterminated; Tasks: startup
Filename: "{app}\{#AppExe}"; Description: "Launch C-Helper"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Quit cleanly first so the fans are handed back to the BIOS
Filename: "{app}\{#AppExe}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitApp"
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /f /tn ""C-Helper"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteTask"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\CHelper\dchu"

[Code]
// Close a running copy (installed or portable) before files are replaced, using C-Helper's own clean quit.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Install the bundled bridge driver only when it is included and the ACPI\CLV0001 device has no working driver.
function NeedsBridgeDriver(): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if not DirExists(ExpandConstant('{app}\driver')) then
    Exit;
  // Exit code 0 = the bridge device is present and working, so nothing to install
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -Command "if ((Get-PnpDevice -InstanceId ''ACPI\CLV0001*'' -ErrorAction SilentlyContinue | Where-Object Status -eq ''OK'')) { exit 0 } else { exit 1 }"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode <> 0;
end;
