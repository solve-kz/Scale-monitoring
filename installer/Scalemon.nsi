Unicode true
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!ifndef VERSION
  !error "VERSION required"
!endif
!ifndef PAYLOAD
  !error "PAYLOAD required"
!endif
Name "Scalemon"
OutFile "ScalemonSetup-${VERSION}-win-x64.exe"
InstallDir "$PROGRAMFILES64\Scalemon"
RequestExecutionLevel admin
ShowInstDetails show
BrandingText "Scalemon — учёт взвешиваний"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
Page custom SettingsPage SettingsLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Infrastructure\Maintenance\Scalemon.Maintenance.exe"
!define MUI_FINISHPAGE_RUN_PARAMETERS "--open"
!define MUI_FINISHPAGE_RUN_TEXT "Открыть веб-интерфейс Scalemon"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"

Var Dialog
Var ServiceInput
Var ConnectionInput
Var ScaleInput
Var PlcInput
Var PortInput
Var PasswordInput
Var AutoInput
Var FirewallInput
Var ConfirmInput
Var Value
Var Result

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Требуется Windows x64."
    Abort
  ${EndIf}
  SetRegView 64
  InitPluginsDir
  SetOutPath "$PLUGINSDIR\payload"
  File /r "${PAYLOAD}\*.*"
  ExecWait '"$PLUGINSDIR\payload\Infrastructure\Maintenance\Scalemon.Maintenance.exe" --discover "$PLUGINSDIR\discovered.ini"' $Result
FunctionEnd

Function SettingsPage
  nsDialogs::Create 1018
  Pop $Dialog
  ${NSD_CreateLabel} 0 0 100% 20u "Весы должны быть выключены. Для подключения существующей установки укажите имя её службы; учётная запись и данные сохраняются."
  Pop $Value
  ${NSD_CreateLabel} 0 24u 38% 12u "Имя службы Windows"
  Pop $Value
  ${NSD_CreateText} 40% 22u 60% 13u "Scalemon"
  Pop $ServiceInput
  ReadINIStr $Value "$PLUGINSDIR\discovered.ini" "Setup" "Service"
  ${If} $Value != ""
    ${NSD_SetText} $ServiceInput "$Value"
  ${EndIf}
  ${NSD_CreateLabel} 0 41u 38% 12u "SQL: строка подключения"
  Pop $Value
  ${NSD_CreatePassword} 40% 39u 60% 13u ""
  Pop $ConnectionInput
  ${NSD_CreateLabel} 0 58u 38% 12u "COM весов / Arduino"
  Pop $Value
  ${NSD_CreateText} 40% 56u 25% 13u "COM2"
  Pop $ScaleInput
  ReadINIStr $Value "$PLUGINSDIR\discovered.ini" "Setup" "ScalePort"
  ${If} $Value != ""
    ${NSD_SetText} $ScaleInput "$Value"
  ${EndIf}
  ${NSD_CreateText} 70% 56u 30% 13u "COM3"
  Pop $PlcInput
  ReadINIStr $Value "$PLUGINSDIR\discovered.ini" "Setup" "PlcPort"
  ${If} $Value != ""
    ${NSD_SetText} $PlcInput "$Value"
  ${EndIf}
  ${NSD_CreateLabel} 0 75u 38% 12u "Порт веб-интерфейса"
  Pop $Value
  ${NSD_CreateText} 40% 73u 25% 13u "5000"
  Pop $PortInput
  ReadINIStr $Value "$PLUGINSDIR\discovered.ini" "Setup" "Port"
  ${If} $Value != ""
    ${NSD_SetText} $PortInput "$Value"
  ${EndIf}
  ${NSD_CreateLabel} 0 92u 38% 12u "Пароль admin (новая установка)"
  Pop $Value
  ${NSD_CreatePassword} 40% 90u 60% 13u ""
  Pop $PasswordInput
  ${NSD_CreateCheckbox} 0 110u 100% 12u "Автоустановка Stable с 22:00 до 05:00 при отключённых весах"
  Pop $AutoInput
  ${NSD_Check} $AutoInput
  ${NSD_CreateCheckbox} 0 128u 100% 12u "Разрешить доступ к веб-интерфейсу из частной/доменной сети"
  Pop $FirewallInput
  ${NSD_CreateCheckbox} 0 146u 100% 12u "Весы выключены, работа участка остановлена, текущие записи сохранены"
  Pop $ConfirmInput
  ${NSD_CreateLabel} 0 165u 100% 24u "При повторной установке пустая строка SQL сохраняет существующее подключение. SQL Server и драйверы должны быть установлены заранее."
  Pop $Value
  nsDialogs::Show
FunctionEnd

Function SettingsLeave
  ${NSD_GetState} $ConfirmInput $Value
  ${If} $Value != ${BST_CHECKED}
    MessageBox MB_ICONSTOP "Сначала остановите работу участка и выключите весы."
    Abort
  ${EndIf}
  ${NSD_GetText} $ServiceInput $Value
  WriteINIStr "$PLUGINSDIR\options.ini" "Setup" "Service" "$Value"
  ${NSD_GetText} $ConnectionInput $Value
  WriteINIStr "$PLUGINSDIR\options.ini" "Setup" "Connection" "$Value"
  ${NSD_GetText} $ScaleInput $Value
  WriteINIStr "$PLUGINSDIR\options.ini" "Setup" "ScalePort" "$Value"
  ${NSD_GetText} $PlcInput $Value
  WriteINIStr "$PLUGINSDIR\options.ini" "Setup" "PlcPort" "$Value"
  ${NSD_GetText} $PortInput $Value
  WriteINIStr "$PLUGINSDIR\options.ini" "Setup" "Port" "$Value"
  ${NSD_GetText} $PasswordInput $Value
  WriteINIStr "$PLUGINSDIR\options.ini" "Setup" "AdminPassword" "$Value"
  ${NSD_GetState} $AutoInput $Value
  WriteINIStr "$PLUGINSDIR\options.ini" "Setup" "AutoInstall" "$Value"
  ${NSD_GetState} $FirewallInput $Value
  WriteINIStr "$PLUGINSDIR\options.ini" "Setup" "Firewall" "$Value"
FunctionEnd

Section "Scalemon"
  SetOutPath "$INSTDIR"
  ExecWait '"$PLUGINSDIR\payload\Infrastructure\Maintenance\Scalemon.Maintenance.exe" --setup "$PLUGINSDIR\payload" "$PLUGINSDIR\options.ini"' $Result
  Delete "$PLUGINSDIR\options.ini"
  ${If} $Result != 0
    FileOpen $Value "$PLUGINSDIR\result.txt" r
    FileRead $Value $Result
    FileClose $Value
    MessageBox MB_ICONSTOP "$Result"
    SetErrorLevel 1
    Abort
  ${EndIf}
  SetShellVarContext all
  CreateDirectory "$SMPROGRAMS\Scalemon"
  ; URL читается утилитой из установленной конфигурации, а не жёстко задаётся в ярлыке.
  CreateShortcut "$SMPROGRAMS\Scalemon\Открыть Scalemon.lnk" "$INSTDIR\Infrastructure\Maintenance\Scalemon.Maintenance.exe" "--open"
  CreateShortcut "$SMPROGRAMS\Scalemon\Экран участка.lnk" "$INSTDIR\Infrastructure\Maintenance\Scalemon.Maintenance.exe" "--display"
  CreateShortcut "$SMPROGRAMS\Scalemon\Обслуживание.lnk" "$INSTDIR\Infrastructure\Maintenance\Scalemon.Maintenance.exe"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Scalemon" "DisplayName" "Scalemon"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Scalemon" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Scalemon" "UninstallString" '$"$INSTDIR\Uninstall.exe$"'
SectionEnd

Section "Uninstall"
  ExecWait '"$INSTDIR\Infrastructure\Maintenance\Scalemon.Maintenance.exe" --uninstall' $Result
  ${If} $Result != 0
    MessageBox MB_ICONSTOP "Удаление отложено: выключите весы и дождитесь сохранения записей. Данные не удалены."
    Abort
  ${EndIf}
  ; Удалять разрешается только фиксированный каталог установки.
  ${If} $INSTDIR != "$PROGRAMFILES64\Scalemon"
    Abort
  ${EndIf}
  SetShellVarContext all
  RMDir /r "$SMPROGRAMS\Scalemon"
  RMDir /r "$INSTDIR\Versions"
  RMDir /r "$INSTDIR\Infrastructure"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Scalemon"
SectionEnd
