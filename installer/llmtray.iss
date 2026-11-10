; LLMTray for Windows: the per-user installer (adr/0007).
; Built by scripts/build-portable.ps1 from the same folder as the portable ZIP:
;   ISCC /DAppVersion=0.1.0-beta.5 /DArch=x64 /DSource=<LLMTray folder> /DOutput=<dir> installer\llmtray.iss
; Installs into %LOCALAPPDATA%\Programs\LLMTray without administrator rights,
; adds a Start menu shortcut and an uninstaller, and updates in place.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef NumVersion
  #define NumVersion "0.0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
; Never change: Windows knows the installed app by it.
AppId={{6F1E8C2A-4B7D-4E2F-9C3A-8D5B1F0E7A64}
AppName=LLMTray
AppVersion={#AppVersion}
AppVerName=LLMTray {#AppVersion}
AppPublisher=IPSupport LLC
AppPublisherURL=https://ipsupport.us
AppSupportURL=https://github.com/ipsupport-llc/llmtray-windows/issues
AppUpdatesURL=https://github.com/ipsupport-llc/llmtray-windows/releases
DefaultDirName={localappdata}\Programs\LLMTray
DefaultGroupName=LLMTray
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
#if Arch == "ARM64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.22621
WizardStyle=modern
WizardSizePercent=110
SetupIconFile={#Source}\app\Assets\AppIcon.ico
UninstallDisplayIcon={app}\LLMTray.exe
UninstallDisplayName=LLMTray
OutputDir={#Output}
OutputBaseFilename=LLMTray-{#AppVersion}-{#Arch}-setup
Compression=lzma2/ultra64
SolidCompression=yes
; A running LLMTray (and its llama-server) is closed through Restart Manager;
; the app's own updater runs setup only after it has exited.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
VersionInfoVersion={#NumVersion}
VersionInfoCompany=IPSupport LLC
VersionInfoProductName=LLMTray

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "ptBR"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "he"; MessagesFile: "compiler:Languages\Hebrew.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs


[Icons]
Name: "{autoprograms}\LLMTray"; Filename: "{app}\LLMTray.exe"
Name: "{autodesktop}\LLMTray"; Filename: "{app}\LLMTray.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LLMTray.exe"; Description: "{cm:LaunchProgram,LLMTray}"; Flags: nowait postinstall skipifsilent

[Code]
{ After a successful install, app\ keeps only the files this version lists in
  app\files.txt: an update leaves nothing of an older version behind, and a
  failed one never deleted anything. }
var
  Keep: TStringList;

procedure Prune(const Dir, Rel: String);
var
  R: TFindRec;
  Path, Sub: String;
begin
  if FindFirst(Dir + '\*', R) then
  try
    repeat
      if (R.Name <> '.') and (R.Name <> '..') then
      begin
        Path := Dir + '\' + R.Name;
        if Rel = '' then Sub := R.Name else Sub := Rel + '\' + R.Name;
        if (R.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Prune(Path, Sub);
          RemoveDir(Path);
        end
        else if Keep.IndexOf(Sub) < 0 then
          DeleteFile(Path);
      end;
    until not FindNext(R);
  finally
    FindClose(R);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Root: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Root := ExpandConstant('{app}\app');
  if not FileExists(Root + '\files.txt') then Exit;
  Keep := TStringList.Create;
  try
    Keep.LoadFromFile(Root + '\files.txt');
    Prune(Root, '');
  finally
    Keep.Free;
  end;
end;
