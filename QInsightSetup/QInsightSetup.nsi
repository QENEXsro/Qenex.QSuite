; NSIS script pro QInsight (QENEX s.r.o.)
; Kompilace: makensis.exe QInsightSetup.nsi
; Vyzaduje predchozi Release build solution:
;   dotnet build ..\Qenex.QSuite.sln -c Release
; a rozbaleny balicek QFW XCP SDK (Prepare-Sdk.ps1 rozbali a overi zip z Release):
;   pwsh .\Prepare-Sdk.ps1

Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma

;--------------------------------
; Definice

!define APP_NAME        "QInsight"
!define APP_VERSION     "1.0.0"
!define APP_PUBLISHER   "QENEX s.r.o."
!define APP_URL         "https://www.qenex.cz/"
!define APP_EXE         "Qenex.QInsight.exe"
!define ASSOC_EXT       ".qproj"
!define ASSOC_PROGID    "QInsightProject.qproj"
!define ASSOC_DESC      "QInsight Project"

!define SRC_ROOT        "D:\Projects\Qenex\Source\Qenex.QSuite"
!define BUILD_DIR       "${SRC_ROOT}\QInsight\bin\Release\net10.0-windows"
!define SETUP_DIR       "${SRC_ROOT}\QInsightSetup"

!define UNINST_KEY      "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"

; QFW XCP SDK bundled with QInsight (source package, unpacked into $INSTDIR\SDK).
; SDK_VERSION is the only place to change when a new SDK is released; the zip
; D:\Projects\Qenex\Release\qfw-xcp-sdk\qfw-xcp-sdk-<ver>.zip is unpacked and
; verified (sha256 + MANIFEST.json) by Prepare-Sdk.ps1 into SDK_DIR.
!define SDK_VERSION     "1.0.0"
!define SDK_NAME        "qfw-xcp-sdk-${SDK_VERSION}"
!define SDK_RELEASE_DIR "D:\Projects\Qenex\Release\qfw-xcp-sdk"
!define SDK_DIR         "${SDK_RELEASE_DIR}\unpacked\${SDK_NAME}"
!define SDK_GUIDE_URL_EN "https://qinsight.qenex.net/device-sdk/integration-tcp/"
!define SDK_GUIDE_URL_CS "https://qinsight.qenex.net/cs/device-sdk/integration-tcp/"

!if ! /FileExists "${SDK_DIR}\MANIFEST.json"
	!error "QFW XCP SDK ${SDK_VERSION} is not unpacked at ${SDK_DIR} - run Prepare-Sdk.ps1 first"
!endif

; Completeness check of the Release output. The plugins reach BUILD_DIR through post-build xcopy
; steps, which can silently miss in a parallel solution build (2026-09-21: TempSensorProtocol was
; not copied). "File /r" would then build an incomplete installer without any warning, so every
; expected plugin is required here. A new plugin = a new line.
!macro RequireFile PATH
	!if ! /FileExists "${PATH}"
		!error "Installer input is missing: ${PATH} - rebuild the solution in Release (and sign) first"
	!endif
!macroend

!insertmacro RequireFile "${BUILD_DIR}\${APP_EXE}"
!insertmacro RequireFile "${BUILD_DIR}\Qenex.QInsight.dll"

!insertmacro RequireFile "${BUILD_DIR}\Controls\Qenex.QSuite.Controls.GaugeControl.dll"
!insertmacro RequireFile "${BUILD_DIR}\Controls\Qenex.QSuite.Controls.GraphControl.dll"
!insertmacro RequireFile "${BUILD_DIR}\Controls\Qenex.QSuite.Controls.MatrixControl.dll"
!insertmacro RequireFile "${BUILD_DIR}\Controls\Qenex.QSuite.Controls.SignalControl.dll"
!insertmacro RequireFile "${BUILD_DIR}\Controls\Qenex.QSuite.Controls.WatchTableControl.dll"
!insertmacro RequireFile "${BUILD_DIR}\Controls\Qenex.QSuite.Controls.XYGraphControl.dll"

!insertmacro RequireFile "${BUILD_DIR}\Drivers\Qenex.QSuite.Drivers.FileDataLoggerDriver.dll"
!insertmacro RequireFile "${BUILD_DIR}\Drivers\Qenex.QSuite.Drivers.FileDataReplayDriver.dll"
!insertmacro RequireFile "${BUILD_DIR}\Drivers\Qenex.QSuite.Drivers.PeakCanDriver.dll"
!insertmacro RequireFile "${BUILD_DIR}\Drivers\Qenex.QSuite.Drivers.SerialPortDriver.dll"
!insertmacro RequireFile "${BUILD_DIR}\Drivers\Qenex.QSuite.Drivers.SimDataDriver.dll"
!insertmacro RequireFile "${BUILD_DIR}\Drivers\Qenex.QSuite.Drivers.TcpClientDriver.dll"
!insertmacro RequireFile "${BUILD_DIR}\Drivers\Qenex.QSuite.Drivers.TcpServerDriver.dll"
!insertmacro RequireFile "${BUILD_DIR}\Drivers\Qenex.QSuite.Drivers.VirtualDataDriver.dll"
!insertmacro RequireFile "${BUILD_DIR}\Drivers\Qenex.QSuite.Examples.IssDriver.dll"
!insertmacro RequireFile "${BUILD_DIR}\Drivers\Qenex.QSuite.Examples.TempSensorDriver.dll"

!insertmacro RequireFile "${BUILD_DIR}\Protocols\Qenex.QSuite.Protocols.DataLogReplayProtocol.dll"
!insertmacro RequireFile "${BUILD_DIR}\Protocols\Qenex.QSuite.Protocols.JsonSignalProtocol.dll"
!insertmacro RequireFile "${BUILD_DIR}\Protocols\Qenex.QSuite.Protocols.ModbusMasterProtocol.dll"
!insertmacro RequireFile "${BUILD_DIR}\Protocols\Qenex.QSuite.Protocols.ModbusSlaveProtocol.dll"
!insertmacro RequireFile "${BUILD_DIR}\Protocols\Qenex.QSuite.Protocols.PassThroughProtocol.dll"
!insertmacro RequireFile "${BUILD_DIR}\Protocols\Qenex.QSuite.Protocols.RawCanProtocol.dll"
!insertmacro RequireFile "${BUILD_DIR}\Protocols\Qenex.QSuite.Protocols.SimulDataProtocol.dll"
!insertmacro RequireFile "${BUILD_DIR}\Protocols\Qenex.QSuite.Protocols.VirtualDataProtocol.dll"
!insertmacro RequireFile "${BUILD_DIR}\Protocols\Qenex.QSuite.Protocols.XcpProtocol.dll"
!insertmacro RequireFile "${BUILD_DIR}\Protocols\Qenex.QSuite.Protocols.XcpTcpProtocol.dll"
!insertmacro RequireFile "${BUILD_DIR}\Protocols\Qenex.QSuite.Examples.IssJsonProtocol.dll"
!insertmacro RequireFile "${BUILD_DIR}\Protocols\Qenex.QSuite.Examples.TempSensorProtocol.dll"

!insertmacro RequireFile "${SETUP_DIR}\ThirdParty\THIRD-PARTY-NOTICES.txt"

Name "${APP_NAME}"
OutFile "D:\Projects\Qenex\Release\QInsight\QInsight-Setup-${APP_VERSION}.exe"
BrandingText "${APP_PUBLISHER}"

; 64bit instalace
InstallDir "$PROGRAMFILES64\Qenex\${APP_NAME}"
InstallDirRegKey HKLM "${UNINST_KEY}" "InstallLocation"
RequestExecutionLevel admin

;--------------------------------
; Verze v properties exe

VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=0 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=0 "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey /LANG=0 "FileDescription" "${APP_NAME} Setup"
VIAddVersionKey /LANG=0 "FileVersion" "${APP_VERSION}.0"
VIAddVersionKey /LANG=0 "ProductVersion" "${APP_VERSION}.0"
VIAddVersionKey /LANG=0 "LegalCopyright" "(c) ${APP_PUBLISHER}"
VIAddVersionKey /LANG=0 "Comments" "Includes QFW XCP SDK ${SDK_VERSION}"

;--------------------------------
; Modern UI 2

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!define MUI_ICON   "${SRC_ROOT}\QInsight\Icons\QInsight.ico"
!define MUI_UNICON "${SRC_ROOT}\QInsight\Icons\QInsight.ico"

; Logo QInsight v pruvodci
!define MUI_WELCOMEFINISHPAGE_BITMAP "${SETUP_DIR}\QInsightWizardImage.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "${SETUP_DIR}\QInsightWizardImage.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_BITMAP "${SETUP_DIR}\QInsightHeaderImage.bmp"
!define MUI_HEADERIMAGE_RIGHT

!define MUI_ABORTWARNING

; Stranky instalace: uvitani -> licence -> volba cesty -> instalace -> dokonceni
; Licence = QENEX Software License Agreement v1.2 (EN/CZ dle zvoleneho jazyka instalatoru);
; zdroj textu: QenexAi\Standa\KnowledgeBase\Legal\*.txt, RTF generovano z nej skriptem eula_to_rtf.py (nemenit rucne).
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "$(LicenseFile)"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES

; Dokonceni: moznost spustit aplikaci + volitelny zastupce na plose
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_SHOWREADME ""
!define MUI_FINISHPAGE_SHOWREADME_TEXT "$(CreateDesktopSC)"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION CreateDesktopShortcut
!insertmacro MUI_PAGE_FINISH

; Stranky odinstalace
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; Jazyky (poradi urcuje vychozi)
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Czech"
!insertmacro MUI_RESERVEFILE_LANGDLL

; Licencni text podle jazyka (LicenseLangString musi byt az za MUI_LANGUAGE)
LicenseLangString LicenseFile ${LANG_ENGLISH} "${SETUP_DIR}\QInsightLicense-en.rtf"
LicenseLangString LicenseFile ${LANG_CZECH}   "${SETUP_DIR}\QInsightLicense-cs.rtf"

;--------------------------------
; Texty

LangString CreateDesktopSC ${LANG_ENGLISH} "Create Desktop Shortcut"
LangString CreateDesktopSC ${LANG_CZECH}   "Vytvořit zástupce na ploše"

LangString DotNetMissing ${LANG_ENGLISH} "QInsight requires the .NET Desktop Runtime 10 (x64), which does not appear to be installed.$\r$\nDo you want to open the download page now?$\r$\n$\r$\nYou can continue with the installation, but QInsight will not start until the runtime is installed."
LangString DotNetMissing ${LANG_CZECH}   "QInsight vyžaduje .NET Desktop Runtime 10 (x64), který zřejmě není nainstalován.$\r$\nChcete nyní otevřít stránku pro jeho stažení?$\r$\n$\r$\nV instalaci lze pokračovat, ale QInsight se bez nainstalovaného runtime nespustí."

; Start menu entries for the bundled SDK (folder + online integration guide by language)
LangString SdkFolderSC ${LANG_ENGLISH} "QFW XCP SDK folder"
LangString SdkFolderSC ${LANG_CZECH}   "Složka QFW XCP SDK"
LangString SdkGuideSC  ${LANG_ENGLISH} "QFW XCP SDK - Integration guide"
LangString SdkGuideSC  ${LANG_CZECH}   "QFW XCP SDK - Průvodce integrací"
LangString SdkGuideUrl ${LANG_ENGLISH} "${SDK_GUIDE_URL_EN}"
LangString SdkGuideUrl ${LANG_CZECH}   "${SDK_GUIDE_URL_CS}"

LangString RemoveUserData ${LANG_ENGLISH} "Do you also want to remove the QInsight license, user settings, layouts and data logs?$\r$\n$\r$\n$LOCALAPPDATA\Qenex\QInsight$\r$\n$\r$\nChoose No to keep them for a future installation."
LangString RemoveUserData ${LANG_CZECH}   "Chcete odstranit také licenci, uživatelská nastavení, rozložení oken a datové logy QInsight?$\r$\n$\r$\n$LOCALAPPDATA\Qenex\QInsight$\r$\n$\r$\nVolbou Ne je ponecháte pro případnou budoucí instalaci."

;--------------------------------
; Instalace

Section "-Install"
	SetRegView 64
	SetShellVarContext all

	; Kompletni Release vystup vcetne pluginu (Controls, Drivers, Protocols),
	; runtimes a SyntaxHighlighting. PDB a datove logy se neinstaluji;
	; Examples z bin take ne (kanonicky zdroj ukazek je QInsightSetup\Examples).
	SetOutPath "$INSTDIR"
	File /r /x "*.pdb" /x "*.qilog" /x "DataLogs" /x "Examples" "${BUILD_DIR}\*.*"

	; Ukazkove projekty + Python generatory k nim (JSON Signal, Raw CAN)
	SetOutPath "$INSTDIR\Examples"
	File "${SETUP_DIR}\Examples\*.qproj"
	File "${SETUP_DIR}\Examples\*.py"

	; Third-party notices (EULA section 6): license texts of the redistributed components
	; (ScottPlot, SkiaSharp, OpenTK, AvalonEdit, Python.NET, ...) and the Telerik copyright notice.
	; Regenerate with Tomas/KnowledgeBase/ThirdPartyLicenses.md when a redistributed package changes.
	SetOutPath "$INSTDIR"
	File "${SETUP_DIR}\ThirdParty\THIRD-PARTY-NOTICES.txt"
	SetOutPath "$INSTDIR\LICENSES"
	File "${SETUP_DIR}\ThirdParty\LICENSES\*.txt"

	; QFW XCP SDK ${SDK_VERSION} - source package unpacked with the same layout as
	; the released zip (include/ engine/ glue/ port/ examples/ cmake/ tools/ docs/
	; LICENSES/), so CMake projects can include() it straight from here.
	; MANIFEST.json inside carries the SHA-256 of every file.
	SetOutPath "$INSTDIR\SDK\${SDK_NAME}"
	File /r "${SDK_DIR}\*.*"

	; Example drivers/protocols (Examples\*) reach $INSTDIR\Drivers|Protocols through the BUILD_DIR copy
	; above - their csproj post-build xcopy puts them into the QInsight output like every other plugin.

	; Vychozi konfigurace do %LOCALAPPDATA%\Qenex\QInsight aktualniho uzivatele
	; (app settings s prazdnou cestou k Pythonu + vychozi edit/runtime layouty).
	; Existujici nastaveni se NEprepisuje a odinstalace resi dotazem.
	SetShellVarContext current
	CreateDirectory "$LOCALAPPDATA\Qenex\${APP_NAME}\DataLogs"
	IfFileExists "$LOCALAPPDATA\Qenex\${APP_NAME}\QInsightAppSettings.xml" +2
		File "/oname=$LOCALAPPDATA\Qenex\${APP_NAME}\QInsightAppSettings.xml" "${SETUP_DIR}\QInsightAppSettings.xml"
	IfFileExists "$LOCALAPPDATA\Qenex\${APP_NAME}\QInsightEditSettings.xml" +2
		File "/oname=$LOCALAPPDATA\Qenex\${APP_NAME}\QInsightEditSettings.xml" "${SETUP_DIR}\QInsightEditSettings.xml"
	IfFileExists "$LOCALAPPDATA\Qenex\${APP_NAME}\QInsightRuntimeSettings.xml" +2
		File "/oname=$LOCALAPPDATA\Qenex\${APP_NAME}\QInsightRuntimeSettings.xml" "${SETUP_DIR}\QInsightRuntimeSettings.xml"
	SetShellVarContext all

	; Pracovni adresar aplikace musi byt $INSTDIR - zastupce i spusteni
	; z posledni stranky pruvodce prebiraji aktualni $OUTDIR.
	SetOutPath "$INSTDIR"

	; Nabidka Start: slozka QInsight = aplikace + slozka SDK + online pruvodce
	; integraci SDK (URL podle jazyka instalace). Plochy zastupce z instalaci
	; pred 1.0.0 se odstrani.
	Delete "$SMPROGRAMS\${APP_NAME}.lnk"
	CreateDirectory "$SMPROGRAMS\${APP_NAME}"
	CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
	CreateShortCut "$SMPROGRAMS\${APP_NAME}\$(SdkFolderSC).lnk" "$INSTDIR\SDK"
	WriteIniStr "$SMPROGRAMS\${APP_NAME}\$(SdkGuideSC).url" "InternetShortcut" "URL" "$(SdkGuideUrl)"

	; Asociace .qproj s QInsight
	WriteRegStr HKLM "Software\Classes\${ASSOC_EXT}\OpenWithProgids" "${ASSOC_PROGID}" ""
	WriteRegStr HKLM "Software\Classes\${ASSOC_PROGID}" "" "${ASSOC_DESC}"
	WriteRegStr HKLM "Software\Classes\${ASSOC_PROGID}\DefaultIcon" "" "$INSTDIR\${APP_EXE},0"
	WriteRegStr HKLM "Software\Classes\${ASSOC_PROGID}\shell\open\command" "" '"$INSTDIR\${APP_EXE}" "%1"'
	System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'

	; Odinstalace + zaznam v Ovladacich panelech
	WriteUninstaller "$INSTDIR\Uninstall.exe"
	WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
	WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"
	WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
	WriteRegStr HKLM "${UNINST_KEY}" "URLInfoAbout" "${APP_URL}"
	WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
	WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
	WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
	WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
	WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
	${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
	IntFmt $0 "0x%08X" $0
	WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" "$0"
SectionEnd

;--------------------------------
; Funkce

Function .onInit
	SetRegView 64
	!insertmacro MUI_LANGDLL_DISPLAY

	; Kontrola .NET Desktop Runtime 10 (x64) - aplikace je framework-dependent
	ClearErrors
	FindFirst $0 $1 "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App\10.*"
	FindClose $0
	${If} ${Errors}
		MessageBox MB_YESNO|MB_ICONQUESTION "$(DotNetMissing)" IDNO noDownload
		ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/10.0"
		noDownload:
	${EndIf}
FunctionEnd

Function CreateDesktopShortcut
	SetShellVarContext all
	SetOutPath "$INSTDIR"
	CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
FunctionEnd

;--------------------------------
; Odinstalace (uzivatelska data v %LOCALAPPDATA% - uzivatel si zvoli,
; zda je smazat, nebo ponechat; vychozi je ponechat)

Function un.onInit
	SetRegView 64
	!insertmacro MUI_UNGETLANGUAGE
FunctionEnd

Section "Uninstall"
	SetRegView 64
	SetShellVarContext all

	Delete "$SMPROGRAMS\${APP_NAME}.lnk"
	RMDir /r "$SMPROGRAMS\${APP_NAME}"
	Delete "$DESKTOP\${APP_NAME}.lnk"

	; Cela instalacni slozka vcetne SDK (soubory vytvorene uzivatelem
	; v $INSTDIR by byly smazany take - Program Files, bezne se tam nepise).
	RMDir /r "$INSTDIR"

	; Volitelne smazani uzivatelskych dat (nastaveni, layouty, datove logy).
	; Pri tiche odinstalaci (/S) se data ponechavaji.
	SetShellVarContext current
	${If} ${FileExists} "$LOCALAPPDATA\Qenex\${APP_NAME}\*.*"
		MessageBox MB_YESNO|MB_ICONQUESTION "$(RemoveUserData)" /SD IDNO IDNO keepUserData
		RMDir /r "$LOCALAPPDATA\Qenex\${APP_NAME}"
		RMDir "$LOCALAPPDATA\Qenex"
		keepUserData:
	${EndIf}
	SetShellVarContext all

	DeleteRegValue HKLM "Software\Classes\${ASSOC_EXT}\OpenWithProgids" "${ASSOC_PROGID}"
	DeleteRegKey /ifempty HKLM "Software\Classes\${ASSOC_EXT}\OpenWithProgids"
	DeleteRegKey /ifempty HKLM "Software\Classes\${ASSOC_EXT}"
	DeleteRegKey HKLM "Software\Classes\${ASSOC_PROGID}"
	DeleteRegKey HKLM "${UNINST_KEY}"
	System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd
