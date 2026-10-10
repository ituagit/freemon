; FreeMon Windows installer (NSIS 3). Built by release.sh:
;   makensis -DVERSION=x.y.z -DSRCDIR=<folder with freemon.exe and docs> -DROOT=<project> -DOUTFILE=<setup.exe> freemon.nsi
Unicode true
SetCompressor /SOLID lzma
RequestExecutionLevel admin

!include "MUI2.nsh"
!include "x64.nsh"

!define APP "FreeMon"
!define PUBLISHER "ITUASUPPORT"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\FreeMon"

Name "${APP} ${VERSION}"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\${APP}"
InstallDirRegKey HKLM "${UNINSTKEY}" "InstallLocation"
BrandingText "${APP} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "FileDescription" "${APP} installer"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "(c) 2026 ${PUBLISHER}, MIT License"

!define MUI_ICON "${ROOT}\assets\freemon.ico"
!define MUI_UNICON "${ROOT}\assets\freemon.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\freemon.exe"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${ROOT}\LICENSE.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Ukrainian"

LangString DataKept ${LANG_ENGLISH} "FreeMon data (device list, settings, log) is NOT removed. It stays in %APPDATA%\FreeMon, C:\ProgramData\FreeMon or the folder you chose. Delete it manually if it is no longer needed."
LangString DataKept ${LANG_UKRAINIAN} "Дані FreeMon (список пристроїв, налаштування, журнал) НЕ видаляються. Вони лишаються в %APPDATA%\FreeMon, C:\ProgramData\FreeMon або у вибраній вами теці. Видаліть їх вручну, якщо вони більше не потрібні."
LangString StillRunning ${LANG_ENGLISH} "FreeMon is running. Exit it (tray icon → Exit) and click OK."
LangString StillRunning ${LANG_UKRAINIAN} "FreeMon запущено. Закрийте його (значок у треї → Вихід) і натисніть OK."

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "FreeMon requires 64-bit Windows 10 or 11."
    Abort
  ${EndIf}
  SetRegView 64
FunctionEnd

Function un.onInit
  SetRegView 64
FunctionEnd

; refuse to overwrite a running freemon.exe (the file would be locked)
!macro CheckRunning
  retry:
  ClearErrors
  IfFileExists "$INSTDIR\freemon.exe" 0 done
  FileOpen $0 "$INSTDIR\freemon.exe" a
  IfErrors 0 closeit
  MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "$(StillRunning)" IDOK retry
  Abort
  closeit:
  FileClose $0
  done:
!macroend

Section "FreeMon" SecMain
  SectionIn RO
  !insertmacro CheckRunning
  SetShellVarContext all
  SetOutPath "$INSTDIR"
  File "${SRCDIR}\freemon.exe"
  File "${ROOT}\LICENSE.txt"
  File "${ROOT}\THIRD_PARTY_NOTICES.txt"
  File "${ROOT}\README.md"
  File "${ROOT}\README.uk.md"
  File "${ROOT}\docs\MANUAL.md"
  File "${ROOT}\docs\MANUAL.uk.md"
  File "${ROOT}\assets\freemon.ico"
  WriteUninstaller "$INSTDIR\uninstall.exe"
  CreateShortcut "$SMPROGRAMS\FreeMon.lnk" "$INSTDIR\freemon.exe" "" "$INSTDIR\freemon.ico" 0
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayName" "${APP}"
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINSTKEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\freemon.ico"
  WriteRegStr HKLM "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTKEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  !insertmacro CheckRunning
  SetShellVarContext all
  Delete "$SMPROGRAMS\FreeMon.lnk"
  ; autostart of the current user (set by FreeMon itself)
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "FreeMon"
  Delete "$INSTDIR\freemon.exe"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\THIRD_PARTY_NOTICES.txt"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\README.uk.md"
  Delete "$INSTDIR\MANUAL.md"
  Delete "$INSTDIR\MANUAL.uk.md"
  Delete "$INSTDIR\freemon.ico"
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKLM "${UNINSTKEY}"
  MessageBox MB_ICONINFORMATION "$(DataKept)"
SectionEnd
