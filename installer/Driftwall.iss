; Driftwall installer — Inno Setup 6 script.
;
; Produces dist\DriftwallSetup-<version>.exe: a per-user install (no UAC prompt) into
; %LOCALAPPDATA%\Programs\Driftwall with Start-menu and optional desktop shortcuts, an optional
; start-with-Windows entry, an Add/Remove Programs listing, and a clean uninstaller.
;
; Built by:   .\installer\build-installer.ps1   (fetches Inno Setup itself if it is not installed)
;         or  .\build.ps1 -Installer            (publishes the exe first, then does the same)
; Or by hand: ISCC.exe /DAppVersion=1.0.0 installer\Driftwall.iss
;
; Not needed for Steam — Steam installs the portable Driftwall.exe itself. This is for direct
; downloads and for your own machine.
;
; Silent use (e.g. from a deployment script):
;   DriftwallSetup-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/MERGETASKS="!desktopicon,!startup"]
;   unins000.exe /VERYSILENT /SUPPRESSMSGBOXES        (keeps the user's collections and settings)

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\Driftwall.exe"
#endif
; Version resources take numbers only; build-installer.ps1 passes the version without any
; pre-release tag here while AppVersion keeps the full string.
#ifndef AppVersionNumeric
  #define AppVersionNumeric AppVersion
#endif

#define AppName "Driftwall"
#define AppExe "Driftwall.exe"
; Driftwall is a Project Max application: the project is the publisher, the app is the product.
#define AppPublisher "Project Max"
#define AppUrl "https://projectmax-org.github.io/driftwall/"
#define AppRepo "https://github.com/projectmax-org/driftwall"
#define AppCopyright "Copyright © 2026 Project Max"

[Setup]
; Never change AppId once released: it is how Inno recognises an existing install to upgrade.
AppId={{6F2E9D0C-3B7A-4E15-9C4D-2A8F1B5D7E30}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://projectmax-org.github.io
AppSupportURL={#AppRepo}/issues
AppUpdatesURL={#AppRepo}/releases
AppCopyright={#AppCopyright}

; What Windows shows for the setup exe itself in Properties > Details and in security prompts.
; A publisher name here is not a verified publisher: that needs the Authenticode signature below.
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductVersion={#AppVersionNumeric}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
VersionInfoCopyright={#AppCopyright}

; Code signing. build-installer.ps1 defines the "driftwall" sign tool on the compiler command line
; and passes /DSignToolName=driftwall when it has a certificate; without one, no SignTool directive
; is emitted and the build is simply unsigned.
#ifdef SignToolName
SignTool={#SignToolName}
SignedUninstaller=yes
#endif

; Per-user by default so there is no UAC prompt; "install for all users" stays available.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
DefaultGroupName={#AppName}

OutputDir=..\dist
OutputBaseFilename=DriftwallSetup-{#AppVersion}
SetupIconFile=..\src\Driftwall\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto

; ---------------------------------------------------------------------------------------------
; Appearance. The wizard is painted in the app's own base colours (Palette.Light/Dark.xaml) and
; follows the Windows light/dark setting; build-installer.ps1 can force either with -Appearance to
; preview it. Forced dark ignores the DynamicDark directives, so the page colour is chosen here.
;
; The only artwork is the brand mark on a transparent ground, drawn by tools\IconGen (--wizard)
; from the same code as the app icon, one file per DPI at the exact size Setup reserves so nothing
; is rescaled. Being transparent, one set serves both appearances; every word stays native text.
; ---------------------------------------------------------------------------------------------
#ifndef WizardAppearance
  #define WizardAppearance "dynamic"
#endif
#if WizardAppearance == "dark"
  #define PageColor "#131318"
#else
  #define PageColor "#F4F4F8"
#endif
#define Sizes(str Stem) \
  "art\" + Stem + "-100.png,art\" + Stem + "-125.png,art\" + Stem + "-150.png,art\" + Stem + "-175.png," + \
  "art\" + Stem + "-200.png,art\" + Stem + "-225.png,art\" + Stem + "-250.png"

WizardStyle=modern {#WizardAppearance} windows11 hidebevels
DisableWelcomePage=no
WizardImageFile={#Sizes("wizard-mark")}
WizardImageFileDynamicDark={#Sizes("wizard-mark")}
WizardSmallImageFile={#Sizes("wizard-small")}
WizardSmallImageFileDynamicDark={#Sizes("wizard-small")}
; With custom images Inno paints a window-coloured box behind them unless told not to.
WizardImageBackColor=none
WizardImageBackColorDynamicDark=none
WizardSmallImageBackColor=none
WizardSmallImageBackColorDynamicDark=none
WizardBackColor={#PageColor}
WizardBackColorDynamicDark=#131318

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

; Everything the wizard says that is not a stock Inno string lives here, once per language, so a
; Korean user is not shown an English task list in an otherwise Korean wizard.
[CustomMessages]
english.StartupGroup=Startup:
english.StartupTask=Start %1 quietly when you sign in to Windows
english.DesktopTask=Add a desktop shortcut
english.LaunchTask=Start %1 now
english.RemoveDataPrompt=Also remove your saved collections, settings and cached photos?%n%n%1
korean.StartupGroup=시작 옵션:
korean.StartupTask=Windows 로그인 시 %1 조용히 시작
korean.DesktopTask=바탕 화면 바로 가기 추가
korean.LaunchTask=지금 %1 시작
korean.RemoveDataPrompt=저장한 컬렉션, 설정, 캐시된 사진도 함께 삭제할까요?%n%n%1

; The wizard's own words, where the stock ones talk about "the Setup Wizard" instead of the app.
[Messages]
english.WelcomeLabel1=Welcome to Driftwall
english.WelcomeLabel2=This will install [name/ver] on your computer.%n%nDriftwall changes your wallpaper from the photo sources you choose and stays out of the way in the notification area.
english.FinishedHeadingLabel=Driftwall is ready
english.FinishedLabel=[name] is installed. It runs from the notification area, next to the clock; open it there to choose your photo sources.
korean.WelcomeLabel1=Driftwall 설치를 시작합니다
korean.WelcomeLabel2=이 프로그램은 [name/ver]을(를) 컴퓨터에 설치합니다.%n%nDriftwall은 선택한 사진 출처에서 배경 화면을 바꿔 주며, 알림 영역에서 조용히 동작합니다.
korean.FinishedHeadingLabel=Driftwall 설치가 완료되었습니다
korean.FinishedLabel=[name] 설치를 마쳤습니다. 시계 옆 알림 영역에서 실행되며, 아이콘을 눌러 사진 출처를 선택할 수 있습니다.

[Tasks]
; Both checked on a first install: a wallpaper app that does not start with Windows is not doing
; its job, and the user asked for it on the desktop.
Name: "desktopicon"; Description: "{cm:DesktopTask}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "startup"; Description: "{cm:StartupTask,{#AppName}}"; GroupDescription: "{cm:StartupGroup}"; Flags: checkedonce

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same key and value the app's own "Start with Windows" switch manages, so the two stay in step.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchTask,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopDriftwall"

[Code]
// A tray app has no window for the Restart Manager to close, so stop it explicitly before the
// files are replaced. taskkill returns non-zero when nothing is running; that is fine.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

// Settings, collections and the photo cache live outside {app}. Ask before touching them: someone
// reinstalling should not lose the collections they built. An uninstall run with /SUPPRESSMSGBOXES
// gets the safe answer (keep everything) instead of a message box nobody is there to click; without
// that switch the question is still asked, even under /VERYSILENT.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\{#AppName}');
    if DirExists(DataDir) then
    begin
      if SuppressibleMsgBox(FmtMessage(CustomMessage('RemoveDataPrompt'), [DataDir]),
                            mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
