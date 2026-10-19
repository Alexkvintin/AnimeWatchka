#define AppVersion "0.1.17"
[Setup]
AppId={{7C63C651-80B9-4404-80EF-07D4959C8409}
AppName=AnimeWatchka
AppVersion={#AppVersion}
AppVerName=AnimeWatchka {#AppVersion} (тестовая версия)
DefaultDirName={localappdata}\Programs\AnimeWatchka
DefaultGroupName=AnimeWatchka
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename=AnimeWatchka-Setup-{#AppVersion}-win-x64
SetupIconFile=..\AnimeApp\Assets\app.ico
UninstallDisplayIcon={app}\AnimeApp.exe
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
SetupLogging=yes
RestartApplications=no
VersionInfoVersion=0.1.17.0

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"

[Files]
Source: "..\artifacts\installer-payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Downloads\*,*.pdb,*.db,*.db-shm,*.db-wal,*.log,credentials.dat,tokens.dat,libvlc\win-x86\*,libvlc\win-arm64\*"
Source: "..\.local\tools\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy

[Icons]
Name: "{group}\AnimeWatchka"; Filename: "{app}\AnimeApp.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\AnimeWatchka"; Filename: "{app}\AnimeApp.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\AnimeApp.exe"; Description: "Запустить AnimeWatchka"; Flags: nowait postinstall skipifsilent

[Code]
function HasWebView: Boolean;
var Version: String;
begin
  Result := (RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
  if not Result then
    Result := (RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Result := '';
  if not HasWebView then begin
    ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
    if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, Code) then begin
      Result := 'Не удалось запустить установку Microsoft WebView2.';
      exit;
    end;
    if not HasWebView then
      Result := 'Для веб-плеера нужен Microsoft WebView2. Проверьте подключение к интернету и повторите установку.';
  end;
end;



