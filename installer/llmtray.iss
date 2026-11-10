; LLMTray for Windows: the per-user installer (adr/0007).
; Built by scripts/build-portable.ps1 from the same folder as the portable ZIP:
;   ISCC /DAppVersion=0.1.0-beta.5 /DArch=x64 /DSource=<LLMTray folder> /DOutput=<dir> installer\llmtray.iss
; Installs into %LOCALAPPDATA%\Programs\LLMTray without administrator rights,
; adds a Start menu shortcut and an uninstaller, and updates in place.

#ifndef AppVersion
  #define AppVersion "0.0.0"
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
PrivilegesRequiredOverridesAllowed=dialog
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
; The running app (and its llama-server) is closed for an update and started again.
AppMutex=Local\LLMTray.Windows.Instance
CloseApplications=yes
RestartApplications=no
VersionInfoVersion=0.0.0.0
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

[InstallDelete]
; An update replaces the app folder whole: no files of an older version stay.
Type: filesandordirs; Name: "{app}\app"

[Icons]
Name: "{autoprograms}\LLMTray"; Filename: "{app}\LLMTray.exe"
Name: "{autodesktop}\LLMTray"; Filename: "{app}\LLMTray.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LLMTray.exe"; Description: "{cm:LaunchProgram,LLMTray}"; Flags: nowait postinstall skipifsilent
