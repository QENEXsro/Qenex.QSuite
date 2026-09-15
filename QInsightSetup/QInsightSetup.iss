; Inno Setup script pro QInsight (QENEX s.r.o.)
; Kompilace: ISCC.exe QInsightSetup.iss
; Vyzaduje predchozi Release build solution:
;   dotnet build ..\Qenex.QSuite.sln -c Release

#define MyAppName "QInsight"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "QENEX s.r.o."
#define MyAppURL "https://www.qenex.cz/"
#define MyAppExeName "Qenex.QInsight.exe"
#define MyAppAssocName MyAppName + " Project"
#define MyAppAssocExt ".qproj"
#define MyAppAssocKey StringChange(MyAppAssocName, " ", "") + MyAppAssocExt

; Vystup Release buildu QInsight (framework-dependent, net10.0-windows)
#define BuildDir "..\QInsight\bin\Release\net10.0-windows"
#define ExamplesDir "..\Examples"

[Setup]
; AppId unikatne identifikuje tuto aplikaci - nemenit mezi verzemi.
AppId={{AED154D0-60FF-4C63-B63A-83B259A5DEDA}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\Qenex\{#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesAssociations=yes
; Uzivatel si muze zvolit instalacni cestu
DisableDirPage=no
DisableProgramGroupPage=yes
; Uvodni stranka s logem
DisableWelcomePage=no
; Potvrzeni licencnich podminek
LicenseFile=QInsightLicense-en.rtf
OutputDir=D:\Projects\Qenex\Release\QInsight
OutputBaseFilename=QInsight-Setup-{#MyAppVersion}
SetupIconFile=..\QInsight\Icons\QInsight.ico
WizardImageFile=QInsightWizardImage.bmp
WizardSmallImageFile=QInsightWizardSmallImage.bmp
WizardImageStretch=no
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "czech"; MessagesFile: "compiler:Languages\Czech.isl"

[CustomMessages]
english.DotNetMissing=QInsight requires the .NET Desktop Runtime 10 (x64), which does not appear to be installed.%nDo you want to open the download page now?%n%nYou can continue with the installation, but QInsight will not start until the runtime is installed.
czech.DotNetMissing=QInsight vyžaduje .NET Desktop Runtime 10 (x64), který zřejmě není nainstalován.%nChcete nyní otevřít stránku pro jeho stažení?%n%nV instalaci lze pokračovat, ale QInsight se bez nainstalovaného runtime nespustí.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Kompletni Release vystup vcetne pluginu (Controls, Drivers, Protocols),
; runtimes a SyntaxHighlighting. PDB a pripadne datove logy se neinstaluji.
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.qilog,DataLogs\*"

; Ukazkove projekty
Source: "Examples\*.qproj"; DestDir: "{app}\Examples"; Flags: ignoreversion
Source: "Examples\*.py"; DestDir: "{app}\Examples"; Flags: ignoreversion

; Ukazkove drivery a protokoly (pluginy z Examples projektu)
Source: "{#ExamplesDir}\IssDriver\bin\Release\net10.0\Qenex.QSuite.Examples.IssDriver.dll"; DestDir: "{app}\Drivers"; Flags: ignoreversion
Source: "{#ExamplesDir}\TempSensorDriver\bin\Release\net10.0\Qenex.QSuite.Examples.TempSensorDriver.dll"; DestDir: "{app}\Drivers"; Flags: ignoreversion
Source: "{#ExamplesDir}\IssJsonProtocol\bin\Release\net10.0\Qenex.QSuite.Examples.IssJsonProtocol.dll"; DestDir: "{app}\Protocols"; Flags: ignoreversion
Source: "{#ExamplesDir}\TempSensorProtocol\bin\Release\net10.0\Qenex.QSuite.Examples.TempSensorProtocol.dll"; DestDir: "{app}\Protocols"; Flags: ignoreversion

; Vychozi konfigurace do %LOCALAPPDATA%\Qenex\QInsight.
; onlyifdoesntexist - neprepsat existujici nastaveni uzivatele,
; uninsneveruninstall - odinstalace nesmaze uzivatelska data.
Source: "QInsightAppSettings.xml"; DestDir: "{localappdata}\Qenex\{#MyAppName}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "QInsightEditSettings.xml"; DestDir: "{localappdata}\Qenex\{#MyAppName}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "QInsightRuntimeSettings.xml"; DestDir: "{localappdata}\Qenex\{#MyAppName}"; Flags: onlyifdoesntexist uninsneveruninstall

[Dirs]
; Slozka pro datove logy aplikace
Name: "{localappdata}\Qenex\{#MyAppName}\DataLogs"; Flags: uninsneveruninstall

[Registry]
; Asociace .qproj s QInsight
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocExt}\OpenWithProgids"; ValueType: string; ValueName: "{#MyAppAssocKey}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}"; ValueType: string; ValueName: ""; ValueData: "{#MyAppAssocName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Kontrola pritomnosti .NET Desktop Runtime 10 (x64) - aplikace je
// framework-dependent, bez runtime se nespusti.
function IsDotNet10DesktopInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

function InitializeSetup: Boolean;
var
  ErrCode: Integer;
begin
  Result := True;
  if not IsDotNet10DesktopInstalled then
  begin
    if MsgBox(ExpandConstant('{cm:DotNetMissing}'), mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOW, ewNoWait, ErrCode);
  end;
end;
