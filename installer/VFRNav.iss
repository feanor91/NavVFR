; Installeur de MSFS Rhumb (Inno Setup 6). Compilé par le workflow release.yml :
;   ISCC.exe /DAppVersion=0.1.0 /DSourceDir=<dossier publié> installer\VFRNav.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif

[Setup]
AppId={{6F0B8D52-3C1E-4B7A-9A44-5E2D7C1F9B31}
AppName=MSFS Rhumb
AppVersion={#AppVersion}
AppVerName=MSFS Rhumb {#AppVersion}
AppPublisher=feanor91
AppPublisherURL=https://github.com/feanor91/NavVFR
AppSupportURL=https://github.com/feanor91/NavVFR/issues
DefaultDirName={autopf}\MSFS Rhumb
DefaultGroupName=MSFS Rhumb
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=MSFS-Rhumb-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\VFRNav.App\Assets\VFRNav.ico
UninstallDisplayIcon={app}\MSFSRhumb.exe
VersionInfoVersion={#AppVersion}

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\MSFS Rhumb"; Filename: "{app}\MSFSRhumb.exe"
Name: "{autodesktop}\MSFS Rhumb"; Filename: "{app}\MSFSRhumb.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MSFSRhumb.exe"; Description: "{cm:LaunchProgram,MSFS Rhumb}"; Flags: nowait postinstall skipifsilent
