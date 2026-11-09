#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif

[Setup]
AppId={{628E2102-C9EA-43EF-BC38-FD069314785E}
AppName=Janela
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Janela
DefaultGroupName=Janela
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\dist
OutputBaseFilename=Janela-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Janela.exe
CloseApplications=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\extension\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\Register-Extension.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "update-enabled"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Janela"; Filename: "{app}\Janela.exe"
Name: "{autodesktop}\Janela"; Filename: "{app}\Janela.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Janela.exe"; Description: "{cm:LaunchProgram,Janela}"; Flags: nowait postinstall skipifsilent

[Code]
var
  RestartAfterUpdate: Boolean;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Attempt: Integer;
begin
  Result := '';
  if ExpandConstant('{param:JanelaUpdate|0}') <> '1' then
  begin
    if CheckForMutexes('Local\Janela.Tracker') then
      Result := 'Feche o Janela pelo menu da bandeja e tente novamente.';
    exit;
  end;
  for Attempt := 1 to 100 do
  begin
    if not CheckForMutexes('Local\Janela.Tracker') then
    begin
      RestartAfterUpdate := True;
      exit;
    end;
    Sleep(100);
  end;
  Result := 'O Janela ainda está aberto. A atualização será tentada novamente.';
end;

procedure DeinitializeSetup();
var
  ResultCode: Integer;
begin
  if RestartAfterUpdate and FileExists(ExpandConstant('{app}\Janela.exe')) then
    Exec(ExpandConstant('{app}\Janela.exe'), '--background', ExpandConstant('{app}'),
      SW_HIDE, ewNoWait, ResultCode);
end;
