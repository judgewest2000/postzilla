; Build with: .\build-installer.ps1 -Platform x64 -Version 1.0.0
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{E96FEE26-3BE1-44F2-8D93-B9506F1B3584}
AppName=Postzilla
AppVersion={#AppVersion}
AppPublisher=judgewest2000
DefaultDirName={localappdata}\Programs\Postzilla
DefaultGroupName=Postzilla
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\Postzilla.exe
OutputDir=..\publish
OutputBaseFilename=Postzilla-Setup-{#Arch}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
SetupIconFile=..\src\Postzilla\Assets\AppIcon.ico
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
Name: "{autoprograms}\Postzilla"; Filename: "{app}\Postzilla.exe"
Name: "{autodesktop}\Postzilla"; Filename: "{app}\Postzilla.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\Postzilla.exe"; Description: "Launch Postzilla"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Postzilla.exe"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[Registry]
; Remove the Apps & Features entry written by install.ps1 so only this installer's entry remains.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\Postzilla"; Flags: deletekey

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
