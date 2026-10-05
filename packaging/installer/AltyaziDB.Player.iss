#define MyAppName "ADB Player"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.1.0"
#endif
#define MyAppPublisher "ADB Player"
#define MyAppExeName "ADB.Player.exe"
#define MyAppProgId "ADB.Player.Media"
#define SourceDir "..\..\artifacts\portable"
#define OutputDir "..\..\artifacts\installer"

[Setup]
AppId={{ADBB5670-7D5A-4D12-BF2C-1DB2C499A6A0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\ADB Player
DefaultGroupName=ADB Player
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=ADB-Player-Setup-v{#MyAppVersion}-x64
SetupIconFile=..\..\src\AltyaziDB.Player.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=yes
ChangesAssociations=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Windows Kurulumu
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstü kısayolu oluştur"; GroupDescription: "Ek görevler:"; Flags: unchecked
Name: "associate_magnet"; Description: "Magnet bağlantılarını ADB Player ile aç"; GroupDescription: "Dosya ve bağlantı ilişkilendirmeleri:"; Flags: unchecked
Name: "associate_torrent"; Description: ".torrent dosyalarını ADB Player ile aç"; GroupDescription: "Dosya ve bağlantı ilişkilendirmeleri:"; Flags: unchecked

[InstallDelete]
Type: files; Name: "{app}\AltyaziDB.Player*"
Type: files; Name: "{autoprograms}\AltyazıDB Player.lnk"
Type: files; Name: "{autodesktop}\AltyazıDB Player.lnk"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "portable.flag,data\*"

[Icons]
Name: "{autoprograms}\ADB Player"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\ADB Player"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Remove the legacy class registration while keeping the same installer AppId for upgrade continuity.
Root: HKCU; Subkey: "Software\Classes\AltyaziDB.Player.Media"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\{#MyAppProgId}"; ValueType: string; ValueName: ""; ValueData: "ADB Player medya dosyası"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#MyAppProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\{#MyAppProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

Root: HKCU; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: none; ValueName: "{#MyAppProgId}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: none; ValueName: "{#MyAppProgId}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: none; ValueName: "{#MyAppProgId}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: none; ValueName: "{#MyAppProgId}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: none; ValueName: "{#MyAppProgId}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: none; ValueName: "{#MyAppProgId}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: none; ValueName: "{#MyAppProgId}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.m2ts\OpenWithProgids"; ValueType: none; ValueName: "{#MyAppProgId}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.torrent\OpenWithProgids"; ValueType: none; ValueName: "{#MyAppProgId}"; Tasks: associate_torrent; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.torrent"; ValueType: string; ValueName: ""; ValueData: "{#MyAppProgId}"; Tasks: associate_torrent; Flags: uninsdeletevalue

Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: ""; ValueData: "URL:Magnet Protocol"; Tasks: associate_magnet; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Tasks: associate_magnet
Root: HKCU; Subkey: "Software\Classes\magnet\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: associate_magnet
Root: HKCU; Subkey: "Software\Classes\magnet\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: associate_magnet

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "ADB Player'ı başlat"; Flags: nowait postinstall skipifsilent
