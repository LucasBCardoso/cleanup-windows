; Cleanup for Windows — the Setup.exe that installs Cleanup on other PCs (Inno Setup 6).
; Built by scripts/publish.ps1 from artifacts\Cleanup. Per-user install, no administrator needed (like install.ps1):
; %LOCALAPPDATA%\Programs\Cleanup, Start Menu (+ optional Desktop) shortcut, an entry in Configurações › Aplicativos,
; optional `cleanup` on the PATH. Uninstalling keeps the history and settings in %LOCALAPPDATA%\Cleanup.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\Cleanup"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{6F3C2B1E-8C4A-4F0D-9B2E-C1E4A7D9F051}
AppName=Cleanup
AppVersion={#AppVersion}
AppVerName=Cleanup {#AppVersion}
AppPublisher=Lucas Cardoso
AppPublisherURL=https://github.com/LucasBCardoso/cleanup-windows
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Cleanup
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Cleanup-Setup-{#AppVersion}
SetupIconFile=..\src\Cleanup.App\Assets\Cleanup.ico
UninstallDisplayIcon={app}\Cleanup.exe
UninstallDisplayName=Cleanup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
ChangesEnvironment=yes

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na Área de Trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked
Name: "addtopath"; Description: "Usar ""cleanup"" no terminal (adiciona a pasta ao PATH do usuário)"; GroupDescription: "Linha de comando:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,install.ps1"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Cleanup"; Filename: "{app}\Cleanup.exe"; Comment: "Limpe o PC com segurança"
Name: "{userdesktop}\Cleanup"; Filename: "{app}\Cleanup.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Run]
Filename: "{app}\Cleanup.exe"; Description: "Abrir o Cleanup"; Flags: nowait postinstall skipifsilent
; The app updating itself runs this Setup with /VERYSILENT /RELAUNCH=1 (window) or 2 (tray only); 0 or none: stay closed.
Filename: "{app}\Cleanup.exe"; Flags: nowait; Check: RelaunchAfterUpdate('1')
Filename: "{app}\Cleanup.exe"; Parameters: "--background"; Flags: nowait; Check: RelaunchAfterUpdate('2')

[UninstallDelete]
Type: files; Name: "{app}\*.InstallLog"

[Code]
function RelaunchAfterUpdate(Mode: string): Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = Mode);
end;

function NeedsAddPath(Dir: string): Boolean;
var
  Path: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') = 0;
end;

procedure RemoveFromPath(Dir: string);
var
  Path, Upper, Needle: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then exit;
  Path := ';' + Path + ';';
  Upper := Uppercase(Path);
  Needle := ';' + Uppercase(Dir) + ';';
  P := Pos(Needle, Upper);
  if P = 0 then exit;
  Delete(Path, P, Length(Needle) - 1);
  Path := Copy(Path, 2, Length(Path) - 2);
  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Path);
end;

// What the app registers by itself (launch at login, the cleanup: link) goes away with it; the history and settings in
// %LOCALAPPDATA%\Cleanup stay, like install.ps1 -Uninstall.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Cleanup');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\cleanup');
    RemoveFromPath(ExpandConstant('{app}'));
  end;
end;
