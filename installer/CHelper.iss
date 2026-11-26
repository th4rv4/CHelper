; C-Helper installer (Inno Setup 6)
; Build:  ISCC.exe /DAppVersion=0.7.2 installer\CHelper.iss
; Expects the published exe in out\CHelper.exe (dotnet publish src/CHelper.csproj -c Release -o out).
; Installs the .NET 8 Desktop Runtime first if it is missing (downloaded from Microsoft).
; Vendor files (see vendor/README.md) are bundled when present:
;   vendor\InsydeDCHU.dll      copied next to CHelper.exe
;   vendor\driver\*            Clevo ACPI bridge driver, installed with pnputil if included
;   vendor\sound\*             Sound Blaster Studio drivers (Creative Realtek extension, Creative's engine and
;                              speaker tuning), set up by Setup-SoundBlaster.ps1 on the EVOL P15's audio chip
;   vendor\wifi-fallback\*     An older Intel Wi-Fi driver, tried by Wi-Fi Driver Refresh as a last resort
;   vendor\wifi\*              Intel Wi-Fi driver (AX210 / AX211), used by Wi-Fi Driver Refresh and installed with
;                              pnputil when the Wi-Fi card has no working driver

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "C-Helper"
#define AppExe "CHelper.exe"
#define AppPublisher "th4rv4"
#define AppUrl "https://github.com/th4rv4/CHelper"
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
Source: "{#Root}\vendor\sound\*"; DestDir: "{app}\sound"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "{#Root}\vendor\wifi\*"; DestDir: "{app}\wifi"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "{#Root}\vendor\wifi-fallback\*"; DestDir: "{app}\wifi-fallback"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "{#Root}\installer\Setup-SoundBlaster.ps1"; DestDir: "{app}\sound"; Flags: ignoreversion

[InstallDelete]
; The bundled Wi-Fi drivers are replaced as a whole, so files of an older bundle don't pile up next to the new ones.
Type: filesandordirs; Name: "{app}\wifi"
Type: filesandordirs; Name: "{app}\wifi-fallback"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Clevo ACPI bridge driver (only if it was bundled and the laptop is missing it)
Filename: "{sys}\pnputil.exe"; Parameters: "/add-driver ""{app}\driver\*.inf"" /install"; \
  StatusMsg: "Installing the Clevo ACPI bridge driver..."; Flags: runhidden waituntilterminated; Check: NeedsBridgeDriver
; Intel Wi-Fi driver (only if it was bundled and the Wi-Fi card has no working driver, e.g. a clean Windows install)
Filename: "{sys}\pnputil.exe"; Parameters: "/add-driver ""{app}\wifi\*.inf"" /install"; \
  StatusMsg: "Installing the Wi-Fi driver..."; Flags: runhidden waituntilterminated; Check: NeedsWifiDriver
; Same sign-in task C-Helper's own Startup option makes (runs elevated at sign-in, no UAC prompt). C-Helper
; writes it as XML so it also runs on battery: plain "schtasks /create" lets Windows stop it on unplug.
Filename: "{app}\{#AppExe}"; Parameters: "--install-task"; StatusMsg: "Setting up start at sign-in..."; \
  Flags: runhidden waituntilterminated runascurrentuser; Tasks: startup
; runascurrentuser: Setup is already elevated; C-Helper's manifest requires admin, so launching it as the
; original (non-admin) user fails with error 740
Filename: "{app}\{#AppExe}"; Description: "Launch C-Helper"; Flags: nowait postinstall skipifsilent runascurrentuser
; In-app update (Setup /SILENT /UPDATE): start the new version again once the files are in place
Filename: "{app}\{#AppExe}"; Flags: nowait runascurrentuser; Check: IsUpdate

[UninstallRun]
; Quit cleanly first so the fans are handed back to the BIOS
Filename: "{app}\{#AppExe}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitApp"
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /f /tn ""C-Helper"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteTask"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\CHelper\dchu"

[Code]
// ---------- .NET 8 Desktop Runtime (C-Helper is framework-dependent) ----------
const
  DotNetUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';

var
  DownloadPage: TDownloadWizardPage;

function DotNetDesktop8Installed(): Boolean;
var
  Find: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), Find);
  if Result then FindClose(Find);
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading the .NET 8 Desktop Runtime...', nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if (CurPageID <> wpReady) or DotNetDesktop8Installed() then Exit;

  DownloadPage.Clear;
  DownloadPage.Add(DotNetUrl, 'dotnet-desktop-8-x64.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      DownloadPage.SetText('Installing the .NET 8 Desktop Runtime...', '');
      if not Exec(ExpandConstant('{tmp}\dotnet-desktop-8-x64.exe'), '/install /quiet /norestart', '',
                  SW_SHOW, ewWaitUntilTerminated, ResultCode) or ((ResultCode <> 0) and (ResultCode <> 3010)) then
      begin
        MsgBox('The .NET 8 Desktop Runtime could not be installed (code ' + IntToStr(ResultCode) + ').' + #13#10 +
               'Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run setup again.', mbError, MB_OK);
        Result := False;
      end;
    except
      MsgBox('Downloading the .NET 8 Desktop Runtime failed:' + #13#10 + GetExceptionMessage + #13#10 +
             'Check the internet connection, or install it from https://dotnet.microsoft.com/download/dotnet/8.0', mbError, MB_OK);
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

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

// Started by C-Helper's own updater ("/UPDATE"): Inno Setup ignores switches it doesn't know.
function IsUpdate(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/UPDATE') = 0 then
      Result := True;
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

// Install the bundled Wi-Fi driver only when no Wi-Fi adapter is working and an Intel PCI device is waiting for a
// driver (the Wi-Fi card on a clean Windows install). Otherwise Windows' own driver stays as it is.
function NeedsWifiDriver(): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if not DirExists(ExpandConstant('{app}\wifi')) then
    Exit;
  // Exit code 1 = no working Wi-Fi adapter and an Intel PCI device without a working driver
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -Command "$ok = Get-PnpDevice -Class Net -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match ''Wi-?Fi|Wireless'' -and $_.Status -eq ''OK'' }; ' +
       '$bad = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like ''PCI\VEN_8086*'' -and $_.Status -ne ''OK'' -and (-not $_.Class -or $_.Class -eq ''Net'') }; ' +
       'if ($ok -or -not $bad) { exit 0 } else { exit 1 }"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode = 1;
end;

// ---------- Sound Blaster Studio ----------
var
  SoundRestart: Boolean;

// After the files are in place: set up Sound Blaster when it's missing (fresh Windows, or Windows Update replaced
// its driver). The script does nothing on other laptops or when everything is already set up. Skipped on in-app
// updates; the Sound page offers the same setup if it's ever needed.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep <> ssPostInstall) or IsUpdate() then Exit;
  if not FileExists(ExpandConstant('{app}\sound\Setup-SoundBlaster.ps1')) then Exit;
  WizardForm.StatusLabel.Caption := 'Setting up Sound Blaster...';
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       ExpandConstant('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{app}\sound\Setup-SoundBlaster.ps1" -Source "{app}\sound"'),
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    SoundRestart := ResultCode = 0;
end;

// Sound Blaster was just set up: Windows needs a restart to load it.
function NeedRestart(): Boolean;
begin
  Result := SoundRestart;
end;
