; Inno Setup script for Practical Music (compiled by build-windows.ps1)
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\win-x64\PracticalMusic"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif

[Setup]
AppId={{7C2B9E4A-1F6D-4A6E-9B2B-5A1C2D3E4F60}
AppName=Practical Music
AppVersion={#AppVersion}
AppPublisher=Russ Judge
DefaultDirName={autopf}\Practical Music
DefaultGroupName=Practical Music
UninstallDisplayIcon={app}\PracticalMusic.exe
OutputDir={#OutputDir}
OutputBaseFilename=PracticalMusic-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
ChangesAssociations=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Practical Music"; Filename: "{app}\PracticalMusic.exe"
Name: "{autodesktop}\Practical Music"; Filename: "{app}\PracticalMusic.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Registry]
Root: HKA; Subkey: "Software\Classes\.pmbook"; ValueType: string; ValueName: ""; ValueData: "PracticalMusic.Book"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\PracticalMusic.Book"; ValueType: string; ValueName: ""; ValueData: "Practical Music Book"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\PracticalMusic.Book\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\PracticalMusic.exe,0"
Root: HKA; Subkey: "Software\Classes\PracticalMusic.Book\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PracticalMusic.exe"" ""%1"""

[Run]
Filename: "{app}\PracticalMusic.exe"; Description: "Launch Practical Music"; Flags: nowait postinstall skipifsilent
