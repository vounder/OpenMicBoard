#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0.0"
#endif
#ifndef Architecture
  #define Architecture "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\" + Architecture
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

#if Architecture == "arm64"
  #define AllowedArchitecture "arm64"
#else
  #define AllowedArchitecture "x64compatible"
#endif

[Setup]
AppId={{2752EAB4-222D-49B5-A43D-E92BFF71876C}
AppName=OpenMicBoard
AppVersion={#AppVersion}
AppPublisher=OpenMicBoard Contributors
AppPublisherURL=https://github.com/vounder/OpenMicBoard
AppSupportURL=https://github.com/vounder/OpenMicBoard/issues
AppUpdatesURL=https://github.com/vounder/OpenMicBoard/releases
DefaultDirName={localappdata}\Programs\OpenMicBoard
DefaultGroupName=OpenMicBoard
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=OpenMicBoard-{#AppVersion}-windows-{#Architecture}-setup
SetupIconFile=..\src\OpenMicBoard.App\Assets\AppIcon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed={#AllowedArchitecture}
ArchitecturesInstallIn64BitMode={#AllowedArchitecture}
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\OpenMicBoard.exe
VersionInfoVersion={#FileVersion}
VersionInfoCompany=OpenMicBoard
VersionInfoDescription=OpenMicBoard installer
VersionInfoProductName=OpenMicBoard

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\OpenMicBoard"; Filename: "{app}\OpenMicBoard.exe"
Name: "{autodesktop}\OpenMicBoard"; Filename: "{app}\OpenMicBoard.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OpenMicBoard.exe"; Description: "{cm:LaunchProgram,OpenMicBoard}"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "OpenMicBoard"; Flags: uninsdeletevalue
