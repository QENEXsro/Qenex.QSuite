; NSIS script pro QInsight (QENEX s.r.o.)
; Kompilace: makensis.exe QInsightSetup.nsi
; Vyzaduje predchozi Release build solution:
;   dotnet build ..\Qenex.QSuite.sln -c Release

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
!define EXAMPLES_SRC    "${SRC_ROOT}\Examples"

!define UNINST_KEY      "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"

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
; Licence = QENEX Software License Agreement v1.1 (EN/CZ dle zvoleneho jazyka instalatoru);
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

LangString RemoveUserData ${LANG_ENGLISH} "Do you also want to remove the QInsight user settings, layouts and data logs?$\r$\n$\r$\n$LOCALAPPDATA\Qenex\QInsight$\r$\n$\r$\nChoose No to keep them for a future installation."
LangString RemoveUserData ${LANG_CZECH}   "Chcete odstranit také uživatelská nastavení, rozložení oken a datové logy QInsight?$\r$\n$\r$\n$LOCALAPPDATA\Qenex\QInsight$\r$\n$\r$\nVolbou Ne je ponecháte pro případnou budoucí instalaci."

;--------------------------------
; Instalace

Section "-Install"
	SetRegView 64
	SetShellVarContext all

	; Kompletni Release vystup vcetne pluginu (Controls, Drivers, Protocols),
	; runtimes a SyntaxHighlighting. PDB a datove logy se neinstaluji.
	SetOutPath "$INSTDIR"
	File /r /x "*.pdb" /x "*.qilog" /x "DataLogs" "${BUILD_DIR}\*.*"

	; Ukazkove projekty
	SetOutPath "$INSTDIR\Examples"
	File "${SETUP_DIR}\Examples\*.qproj"

	; Ukazkove drivery a protokoly (pluginy z Examples projektu)
	SetOutPath "$INSTDIR\Drivers"
	File "${EXAMPLES_SRC}\IssDriver\bin\Release\net10.0\Qenex.QSuite.Examples.IssDriver.dll"
	File "${EXAMPLES_SRC}\TempSensorDriver\bin\Release\net10.0\Qenex.QSuite.Examples.TempSensorDriver.dll"
	SetOutPath "$INSTDIR\Protocols"
	File "${EXAMPLES_SRC}\IssJsonProtocol\bin\Release\net10.0\Qenex.QSuite.Examples.IssJsonProtocol.dll"
	File "${EXAMPLES_SRC}\TempSensorProtocol\bin\Release\net10.0\Qenex.QSuite.Examples.TempSensorProtocol.dll"

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

	; Zastupce v nabidce Start
	CreateShortCut "$SMPROGRAMS\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

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
	Delete "$DESKTOP\${APP_NAME}.lnk"

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
