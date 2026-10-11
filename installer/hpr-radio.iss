; Inno Setup script for HPR Radio. Built by the GitHub workflow:
;   iscc /DMyVersion=1.0.0 /DSourceDir=..\publish installer\hpr-radio.iss

#ifndef MyVersion
  #define MyVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6C1F2A57-3E2B-4C67-9E0B-5B8A6A1D4E21}
AppName=HPR Radio
AppVersion={#MyVersion}
AppVerName=HPR Radio {#MyVersion}
AppPublisher=KG4BHR (unofficial)
AppPublisherURL=https://hpr.org
DefaultDirName={localappdata}\Programs\HPR Radio
DefaultGroupName=HPR Radio
DisableProgramGroupPage=yes
; Installs just for you: no administrator password needed.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\out
OutputBaseFilename=HPR-Radio-Setup-{#MyVersion}
SetupIconFile=..\src\HprRadio\Assets\hpr.ico
UninstallDisplayIcon={app}\HprRadio.exe
UninstallDisplayName=HPR Radio
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Put an icon on the desktop"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\HPR Radio"; Filename: "{app}\HprRadio.exe"
Name: "{autodesktop}\HPR Radio"; Filename: "{app}\HprRadio.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\HprRadio.exe"; Description: "Start HPR Radio"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Old app files from earlier versions (your settings in %APPDATA% are kept).
Type: filesandordirs; Name: "{app}\libvlc"
