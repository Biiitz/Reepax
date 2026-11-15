#define MyAppName "Reepax"
#ifndef MyAppVersion
#define MyAppVersion "26.11.3"
#endif
#define MyAppPublisher "Biiitz"
#define MyAppURL "https://github.com/Biiitz/Reepax"
#define MyAppExeName "Reepax.exe"

#ifdef UpdateSmokeTest
#define MyAppId "{B48F27AE-C302-45B9-9865-D9DA33D59410}}"
#else
#define MyAppId "{B729352A-3D28-444F-A1BC-535A0F507F62}}"
#endif

[Setup]
AppId={#"{" + MyAppId}
#ifdef UpdateSmokeTest
Uninstallable=no
CreateUninstallRegKey=no
#endif
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases

DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableWelcomePage=yes
DisableDirPage=no
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=no

OutputDir=..\dist
OutputBaseFilename=setup
SetupIconFile=..\Reepax\Assets\app.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

CloseApplications=yes
RestartApplications=no

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\InstallerPayload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,Data,.portable"; Check: InstallManagedLayout
Source: "..\dist\Reepax\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,Data"; Check: not InstallManagedLayout
#ifndef UpdateSmokeTest
Source: "..\dist\Reepax\runtimes\win-x64\native\par2.exe"; DestDir: "{localappdata}\{#MyAppName}"; DestName: "par2.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{code:InstalledExecutable}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{code:InstalledExecutable}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.repx"; ValueType: string; ValueData: "Reepax.Package"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Reepax.Package"; ValueType: string; ValueData: "Reepax Download Package"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Reepax.Package\DefaultIcon"; ValueType: string; ValueData: "{code:InstalledExecutable},0"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Reepax.Package\shell\open\command"; ValueType: string; ValueData: """{code:InstalledExecutable}"" ""%1"""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueData: "{code:InstalledExecutable}"; Flags: uninsdeletekey
#endif

[Run]
Filename: "{code:InstalledExecutable}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
Filename: "{code:InstalledExecutable}"; Parameters: "--restart"; Flags: nowait; Check: ShouldRestartAfterSetup

#ifndef UpdateSmokeTest
[UninstallRun]
Filename: "{app}\current\{#MyAppExeName}"; Parameters: "--installer-cleanup"; Flags: runhidden skipifdoesntexist; Check: InstallManagedLayout
#endif

[CustomMessages]
german.DeleteAppDataPrompt=Möchten Sie auch alle Einstellungen, Logs und Benutzerdaten in AppData löschen?
english.DeleteAppDataPrompt=Do you also want to delete all settings, logs, and user data in AppData?
german.UpdateStartFailed=Die aktualisierte Anwendung konnte nicht gestartet werden.
english.UpdateStartFailed=The updated application could not be started.
german.UpdateStartTimeout=Die aktualisierte Anwendung hat den Start nicht bestätigt.
english.UpdateStartTimeout=The updated application did not confirm startup.
german.MachineInstallationFound=Reepax ist für alle Benutzer installiert. Deinstalliere diese Version zuerst und behalte dabei deine Benutzerdaten. Starte danach dieses Setup erneut. Die neue Installation gilt nur für dein Benutzerkonto.
english.MachineInstallationFound=Reepax is installed for all users. Uninstall that version first and choose to keep your user data. Then run this setup again to install Reepax for your account.

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppId}_is1';

var
  ShouldDeleteAppData: Boolean;

function InitializeSetup: Boolean;
begin
  Result := not (RegKeyExists(HKLM64, UninstallKey) or RegKeyExists(HKLM32, UninstallKey));
  if not Result then
  begin
    Log('An existing all-users installation must be uninstalled first.');
    SuppressibleMsgBox(CustomMessage('MachineInstallationFound'), mbError, MB_OK, IDOK);
  end;
end;

function InstallManagedLayout: Boolean;
var
  Installation, DataPath: String;
begin
  // Released helpers wait for all installer descendants and require a flat update.
  if (ExpandConstant('{param:UPDATEHELPER|0}') = '1') and
    (ExpandConstant('{param:MANAGEDUPDATE|0}') <> '1') then
  begin
    Result := False;
    Exit;
  end;
  Installation := UpperCase(AddBackslash(ExpandConstant('{app}')));
  DataPath := UpperCase(AddBackslash(ExpandConstant('{localappdata}\{#MyAppName}')));
  Result := not IsAdminInstallMode and (Pos(Installation, DataPath) <> 1) and (Pos(DataPath, Installation) <> 1);
end;

function InstalledExecutable(Param: String): String;
begin
  if InstallManagedLayout then
    Result := ExpandConstant('{app}\current\{#MyAppExeName}')
  else
    Result := ExpandConstant('{app}\{#MyAppExeName}');
end;

function ShouldRestartAfterSetup: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:UPDATEHELPER|0}') <> '1');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  RequestPath, ReadyPath: String;
  ResultCode, Attempts: Integer;
begin
  if (CurStep <> ssPostInstall) or (ExpandConstant('{param:UPDATEHELPER|0}') <> '1') or not InstallManagedLayout then Exit;
  RequestPath := ExpandConstant('{localappdata}\{#MyAppName}\update-startup.json');
  if not FileExists(RequestPath) then Exit;
  ReadyPath := ExpandConstant('{localappdata}\{#MyAppName}\update-startup.ready');
  if not Exec(ExpandConstant('{app}\current\{#MyAppExeName}'), '--restart --legacy-update-bootstrap',
    ExpandConstant('{app}\current'), SW_SHOWNORMAL, ewNoWait, ResultCode) then
    RaiseException(CustomMessage('UpdateStartFailed'));
  Attempts := 0;
  while not FileExists(ReadyPath) and (Attempts < 1200) do
  begin
    Sleep(50);
    Attempts := Attempts + 1;
  end;
  if not FileExists(ReadyPath) then RaiseException(CustomMessage('UpdateStartTimeout'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  LocalDataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if not UninstallSilent then
    begin
      ShouldDeleteAppData := (MsgBox(CustomMessage('DeleteAppDataPrompt'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES);
    end
    else
    begin
      // NEVER delete user data or settings during automated or silent updates!
      ShouldDeleteAppData := False;
    end;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    if ShouldDeleteAppData then
    begin
      LocalDataDir := ExpandConstant('{localappdata}\{#MyAppName}');
      if DirExists(LocalDataDir) then
      begin
        DelTree(LocalDataDir, True, True, True);
      end;
    end;
  end;
end;
