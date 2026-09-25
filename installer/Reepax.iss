#define MyAppName "Reepax"
#define MyAppVersion "26.9.1"
#define MyAppPublisher "Biiitz"
#define MyAppURL "https://github.com/Biiitz/Reepax"
#define MyAppExeName "Reepax.exe"

[Setup]
AppId={{B729352A-3D28-444F-A1BC-535A0F507F62}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases

; Per-User Installation like JDownloader2, Chrome, VS Code (No Admin rights needed!)
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
UninstallDisplayIcon={app}\{#MyAppExeName}

Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
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
Source: "..\dist\Reepax\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
Source: "..\dist\Reepax\runtimes\win-x64\native\par2.exe"; DestDir: "{localappdata}\{#MyAppName}"; DestName: "par2.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.repx"; ValueType: string; ValueData: "Reepax.Package"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Reepax.Package"; ValueType: string; ValueData: "Reepax Download Package"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Reepax.Package\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExeName},0"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Reepax.Package\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall

[CustomMessages]
german.DeleteAppDataPrompt=Möchten Sie auch alle Einstellungen, Logs und Benutzerdaten in AppData löschen?
english.DeleteAppDataPrompt=Do you also want to delete all settings, logs, and user data in AppData?

[Code]
var
  ShouldDeleteAppData: Boolean;

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