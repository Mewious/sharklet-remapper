#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif

[Setup]
AppId={{8B4F07FC-89BC-48E1-929D-D3738771B996}
AppName=Sharklet
AppVersion={#AppVersion}
AppPublisher=Tailwind
DefaultDirName={localappdata}\Programs\TailwindRemapper
DefaultGroupName=Sharklet
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
WizardStyle=modern
SetupIconFile=sharklet.ico
InfoBeforeFile=installer-notes.txt
OutputDir=dist\installer
OutputBaseFilename=Sharklet-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\TailwindRemapper.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
#ifdef SignedBuild
SignTool=TailwindSigning
SignedUninstaller=yes
#endif

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
; Explicit files keep private controller profiles and test output out of the installer.
#ifdef SignedBuild
Source: "obj\installer\publish\TailwindRemapper.exe"; DestDir: "{app}"; Flags: ignoreversion sign
#else
Source: "obj\installer\publish\TailwindRemapper.exe"; DestDir: "{app}"; Flags: ignoreversion
#endif
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "installer-notes.txt"; DestDir: "{app}"; DestName: "Getting Started.txt"; Flags: ignoreversion
Source: "obj\installer\ViGEmBus_1.22.0_x64_x86_arm64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\Sharklet"; Filename: "{app}\TailwindRemapper.exe"
Name: "{group}\Getting started"; Filename: "{app}\Getting Started.txt"
Name: "{autodesktop}\Sharklet"; Filename: "{app}\TailwindRemapper.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\ViGEmBus_1.22.0_x64_x86_arm64.exe"; Parameters: "/norestart"; Verb: "runas"; Description: "Open ViGEmBus driver setup (required for Xbox output; needs administrator approval)"; Flags: postinstall shellexec waituntilterminated skipifsilent; Check: NeedsDriver
Filename: "{app}\TailwindRemapper.exe"; Description: "Launch Sharklet"; Flags: postinstall nowait skipifsilent

[Code]
function NeedsDriver: Boolean;
begin
  Result := not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\ViGEmBus');
end;
