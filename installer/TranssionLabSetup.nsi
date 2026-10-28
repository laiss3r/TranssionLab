; ============================================================================
; TranssionLab Universal Android Servicing Suite — First-Time Setup & Installer
; Targets Windows 10 & Windows 11 (x64 / ARM64) | Per-User Standard Execution
; Zero Admin Required • Zero Unsigned Drivers • Bundled Apache-2.0 ADB/Fastboot
; ============================================================================

Unicode True
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!ifndef APP_VERSION
  !define APP_VERSION "1.7.0"
!endif

!ifndef APP_ARCH
  !define APP_ARCH "win-x64"
!endif

!ifndef BUNDLE_DIR
  !define BUNDLE_DIR "bundle"
!endif

!ifndef OUT_EXE
  !define OUT_EXE "TranssionLab-Setup-v${APP_VERSION}-${APP_ARCH}.exe"
!endif

!ifndef ICON_FILE
  !define ICON_FILE "src/TranssionLab.App/Assets/TranssionLab.ico"
!endif

!define APP_NAME "TranssionLab"
!define APP_FULL_NAME "TranssionLab Universal Android Servicing Suite"
!define APP_PUBLISHER "TranssionLab Open Servicing Engineering"
!define APP_EXE "TranssionLab.exe"
!define UNINSTALL_EXE "Uninstall-TranssionLab.exe"
!define UNINSTALL_REG_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\TranssionLab"

Name "${APP_FULL_NAME} v${APP_VERSION}"
OutFile "${OUT_EXE}"
InstallDir "$LOCALAPPDATA\Programs\TranssionLab"
InstallDirRegKey HKCU "${UNINSTALL_REG_KEY}" "InstallLocation"

; Standard user execution by default — never silently require Administrator elevation
RequestExecutionLevel user

VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "${APP_FULL_NAME}"
VIAddVersionKey "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey "FileDescription" "${APP_FULL_NAME} First-Time Setup & Installer (${APP_ARCH})"
VIAddVersionKey "FileVersion" "${APP_VERSION}.0"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 TranssionLab Contributors"

!include "MUI2.nsh"
!include "FileFunc.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON "${ICON_FILE}"
!define MUI_UNICON "${ICON_FILE}"

!define MUI_WELCOMEPAGE_TITLE "Welcome to ${APP_NAME} v${APP_VERSION} First-Time Setup"
!define MUI_WELCOMEPAGE_TEXT "This wizard sets up and installs ${APP_FULL_NAME} (${APP_ARCH}) on your Windows 10 or Windows 11 PC — 100% offline with zero internet required.$\r$\n$\r$\n• Installs the unpacked self-contained .NET 8 + native WPF runtime (no external .NET install or Admin rights needed).$\r$\n• Bundles official Apache-2.0 Android Platform-Tools (adb.exe & fastboot.exe) + 33Hz MediaTek/Unisoc VCOM Catch-and-Hold engine so phones are read before charging starts.$\r$\n• Configures your local workspace & 8 TECNO / Infinix / itel reference profiles.$\r$\n$\r$\nClick Next to continue."

!define MUI_COMPONENTSPAGE_SMALLDESC

!define MUI_FINISHPAGE_TITLE "${APP_NAME} v${APP_VERSION} Is Installed and Ready"
!define MUI_FINISHPAGE_TEXT "${APP_FULL_NAME} has completed first-time setup on your PC.$\r$\n$\r$\nQuick Tip: For powered-OFF MediaTek/Unisoc phones, click 'Read Phone Info' FIRST inside the app, then plug USB so VCOM is held before charging starts!"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${APP_FULL_NAME} now"
!define MUI_FINISHPAGE_NOREBOOTSUPPORT

; Installer Wizard Pages
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

; Uninstaller Wizard Pages
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ============================================================================
; INSTALLER SECTIONS
; ============================================================================

Section "TranssionLab Suite, .NET 8 Runtime & Android Platform-Tools (Required)" SecCore
  SectionIn RO

  DetailPrint "Preparing clean installation directory: $INSTDIR"
  SetOutPath "$INSTDIR"

  ; Remove stale runtime DLLs from any prior installation while preserving user data
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\*.json"
  Delete "$INSTDIR\*.pdb"
  Delete "$INSTDIR\${APP_EXE}"

  DetailPrint "Installing ${APP_FULL_NAME} (${APP_ARCH}), adb/fastboot, profiles, and manuals..."
  File /r "${BUNDLE_DIR}/*"

  DetailPrint "Running first-time local workspace, ADB/Fastboot binding & profile provisioning..."
  nsExec::ExecToLog '"$INSTDIR\${APP_EXE}" --first-run-init'
  Pop $0

  ; Write uninstaller and Windows 'Installed Apps' HKCU registry entry
  WriteUninstaller "$INSTDIR\${UNINSTALL_EXE}"

  ${GetSize} "$INSTDIR" "/S=0K" $1 $2 $3
  IntFmt $1 "0x%08X" $1

  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "DisplayName" "${APP_FULL_NAME}"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE},0"
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "UninstallString" '"$INSTDIR\${UNINSTALL_EXE}"'
  WriteRegStr HKCU "${UNINSTALL_REG_KEY}" "QuietUninstallString" '"$INSTDIR\${UNINSTALL_EXE}" /S'
  WriteRegDWORD HKCU "${UNINSTALL_REG_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_REG_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTALL_REG_KEY}" "EstimatedSize" "$1"
SectionEnd

Section "Desktop & Start Menu Shortcuts" SecShortcuts
  DetailPrint "Creating Desktop and Start Menu shortcuts..."
  CreateDirectory "$SMPROGRAMS\TranssionLab"
  CreateShortcut "$SMPROGRAMS\TranssionLab\TranssionLab.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortcut "$SMPROGRAMS\TranssionLab\Uninstall TranssionLab.lnk" "$INSTDIR\${UNINSTALL_EXE}" "" "$INSTDIR\${UNINSTALL_EXE}" 0
  CreateShortcut "$DESKTOP\TranssionLab.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

; Section Descriptions
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCore} "Installs TranssionLab.exe, the unpacked .NET 8 + native WPF runtime, official Apache-2.0 Android Platform-Tools (adb.exe & fastboot.exe), 8 TECNO/Infinix/itel profiles, and initializes %LOCALAPPDATA%\TranssionLab."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecShortcuts} "Creates Desktop and Windows Start Menu shortcuts for TranssionLab."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ============================================================================
; UNINSTALLER SECTION
; ============================================================================

Section "Uninstall"
  Delete "$DESKTOP\TranssionLab.lnk"
  Delete "$SMPROGRAMS\TranssionLab\TranssionLab.lnk"
  Delete "$SMPROGRAMS\TranssionLab\Uninstall TranssionLab.lnk"
  RMDir "$SMPROGRAMS\TranssionLab"

  RMDir /r "$INSTDIR\platform-tools"
  RMDir /r "$INSTDIR\profiles"
  RMDir /r "$INSTDIR\docs"
  RMDir /r "$INSTDIR\tools"
  Delete "$INSTDIR\*.*"
  RMDir /r "$INSTDIR"

  DeleteRegKey HKCU "${UNINSTALL_REG_KEY}"
SectionEnd
