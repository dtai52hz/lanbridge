; Build with scripts/build-installer.ps1 after publishing the Windows payload.
; Keep AppId unchanged so later installers upgrade the same per-user installation.
#if Ver < EncodeVer(6, 7, 4)
  #error Inno Setup 6.7.4 or newer is required
#endif
#ifndef AppVersion
  #error AppVersion must be provided by build-installer.ps1
#endif
#ifndef PublishDir
  #error PublishDir must be provided by build-installer.ps1
#endif
#ifndef ProjectRoot
  #error ProjectRoot must be provided by build-installer.ps1
#endif

[Setup]
AppId={{82183248-7B34-4DC8-95F3-D86B1DD26D70}
AppName=LanBridge
AppVersion={#AppVersion}
AppPublisher=LanBridge contributors
AppPublisherURL=https://github.com/dtai52hz/lanbridge
AppSupportURL=https://github.com/dtai52hz/lanbridge/issues
AppUpdatesURL=https://github.com/dtai52hz/lanbridge/releases
DefaultDirName={localappdata}\Programs\LanBridge
DefaultGroupName=LanBridge
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern dynamic
DisableProgramGroupPage=yes
AllowNoIcons=yes
UsePreviousAppDir=not IsPackageTest
UsePreviousGroup=not IsPackageTest
UsePreviousTasks=not IsPackageTest
UsePreviousLanguage=no
AppMutex={code:GetApplicationMutex}
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
Uninstallable=yes
CreateUninstallRegKey=not IsPackageTest
UninstallDisplayIcon={app}\LanBridge.exe
UninstallDisplayName=LanBridge
LicenseFile={#PublishDir}\LICENSE
SetupIconFile={#ProjectRoot}\src\assets\lanbridge.ico
OutputDir={#ProjectRoot}\release
OutputBaseFilename=LanBridge-{#AppVersion}-Setup-x64
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=LanBridge Windows installer
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked; Check: not IsPackageTest

[Files]
; The self-contained publish directory also contains the README, MIT license,
; runtime licenses, third-party notices, and screenshots. AppData settings and
; clipboard caches are outside this directory and are never removed by Setup.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\LanBridge"; Filename: "{app}\LanBridge.exe"; WorkingDir: "{app}"; Check: not IsPackageTest
Name: "{group}\Uninstall LanBridge"; Filename: "{uninstallexe}"; Check: not IsPackageTest
Name: "{autodesktop}\LanBridge"; Filename: "{app}\LanBridge.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: not IsPackageTest

[Code]
function IsPackageTest: Boolean;
begin
  { A smoke test installs to an isolated directory, without registering or
    changing shortcuts. Its uninstall log contains no application mutex. }
  Result := ExpandConstant('{param:PACKAGETEST|0}') = '1';
end;

function GetApplicationMutex(Param: String): String;
begin
  if IsPackageTest then
    Result := ''
  else
    Result := 'Local\LanBridgeDesktop';
end;
