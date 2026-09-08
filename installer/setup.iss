[Setup]
AppName=TrimKit
AppVersion=0.1.0
AppPublisher=Gorstak
AppPublisherURL=https://github.com/Gorstak/TrimKit
DefaultDirName={autopf}\TrimKit
DefaultGroupName=TrimKit
OutputDir=..\releases\0.1.0
OutputBaseFilename=TrimKit-Setup-0.1.0
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
SetupIconFile=assets\TrimKit.ico
UninstallDisplayIcon={app}\TrimKit.ico
WizardStyle=modern

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "assets\TrimKit.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\TrimKit"; Filename: "{app}\TrimKit.exe"; IconFilename: "{app}\TrimKit.ico"
Name: "{commondesktop}\TrimKit"; Filename: "{app}\TrimKit.exe"; IconFilename: "{app}\TrimKit.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\TrimKit.exe"; Description: "Launch TrimKit"; Flags: nowait postinstall skipifsilent runascurrentuser
