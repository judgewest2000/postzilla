; Build with: .\build-installer.ps1 -Platform x64 -Version 1.0.0
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{E68B0FE1-C1C7-4EE4-8AB9-0982661005DA}
AppName=Brunozilla
AppVersion={#AppVersion}
AppPublisher=judgewest2000
DefaultDirName={localappdata}\Programs\Brunozilla
DefaultGroupName=Brunozilla
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\Brunozilla.exe
OutputDir=..\publish
OutputBaseFilename=Brunozilla-Setup-{#Arch}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
SetupIconFile=..\src\Brunozilla\Assets\AppIcon.ico
#if Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#elif Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x86compatible
#endif

[Files]
Source: "..\publish\win-{#Arch}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Brunozilla"; Filename: "{app}\Brunozilla.exe"
Name: "{autodesktop}\Brunozilla"; Filename: "{app}\Brunozilla.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\Brunozilla.exe"; Description: "Launch Brunozilla"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Brunozilla.exe"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[Registry]
; Remove the Apps & Features entry written by install.ps1 so only this installer's entry remains.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\Brunozilla"; Flags: deletekey

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
