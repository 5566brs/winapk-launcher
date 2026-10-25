; WinApkLauncher.iss -- the WinApk Launcher installer (Inno Setup 6). One installer with the
; launcher and the Win.apk runtime it runs apps with.
;
;   iscc /DLauncherDir=<dotnet publish output> /DRuntimeDir=<staged Win.apk folder> ^
;        /DAppVersion=0.1.0 [/O<output dir>] installer\WinApkLauncher.iss
;
; LauncherDir: the self-contained `dotnet publish -r win-x64` folder (WinApkLauncher.exe and the
;   .NET runtime). Installed to {app}.
; RuntimeDir:  the staged Win.apk runtime (bin\, lib\, jre\, ...), as windows/scripts/
;   stage_runtime.sh makes it plus the Temurin JRE. Installed to {app}\runtime, where the
;   launcher looks for runtime\bin\winapk-runtime.exe.
; AppVersion:  defaults to the ProductVersion of WinApkLauncher.exe.
;
; Two languages, English and Hebrew (compiler:Languages\Hebrew.isl, which Inno Setup 6 ships): Setup
; asks, with the Windows display language preselected. This file is UTF-8.
;
; Installs per user (no administrator rights) to %LOCALAPPDATA%\Programs\WinApk Launcher, or to
; C:\WinApk Launcher on the system drive when that path has a character the ANSI code page cannot
; hold (AnsiPath.iss).
; Uninstalling removes the program files only: the settings (%APPDATA%\WinApk Launcher) and the
; apps' data, cache and logs (%LOCALAPPDATA%\WinApk Launcher) are kept.

#ifndef LauncherDir
  #error Pass /DLauncherDir=<the dotnet publish output folder>
#endif
#ifndef RuntimeDir
  #error Pass /DRuntimeDir=<the staged Win.apk runtime folder>
#endif

#define AppName "WinApk Launcher"
#define AppExe "WinApkLauncher.exe"
#define AppUrl "https://github.com/5566brs/winapk-launcher"
#define ExePath AddBackslash(LauncherDir) + AppExe

#if !FileExists(ExePath)
  #error LauncherDir has no WinApkLauncher.exe
#endif
#if !FileExists(AddBackslash(RuntimeDir) + "bin\winapk-runtime.exe")
  #error RuntimeDir has no bin\winapk-runtime.exe
#endif
#ifndef AppVersion
  #define AppVersion GetStringFileInfo(ExePath, "ProductVersion")
#endif
; Numeric a.b.c.d for the setup's own version resource (AppVersion may be "0.1.0-beta.1").
#define NumericVersion GetVersionNumbersString(ExePath)

[Setup]
AppId={{FAE68E2D-2CB4-491C-A340-A92E3B5BFB16}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=WinApk Launcher contributors
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (C) 2026 WinApk Launcher contributors. GNU AGPL-3.0.
VersionInfoVersion={#NumericVersion}
VersionInfoProductName={#AppName}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} setup
DefaultDirName={code:DefaultInstallDir}
DisableDirPage=yes
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
OutputBaseFilename=WinApkLauncher-Setup-{#AppVersion}
SetupIconFile=..\assets\WinApkLauncher.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "he"; MessagesFile: "compiler:Languages\Hebrew.isl"

[Messages]
en.ConfirmUninstall=Remove %1 from your computer?%n%nYour settings and your apps' data are kept.
en.UninstalledAll=%1 was removed from your computer.%n%nYour settings and your apps' data were kept, in:%n  AppData\Roaming\WinApk Launcher  (settings)%n  AppData\Local\WinApk Launcher  (apps' data, cache, logs)%n%nDelete those folders if you want to remove them as well.
he.ConfirmUninstall=להסיר את %1 מהמחשב?%n%nההגדרות והנתונים של האפליקציות נשמרים.
he.UninstalledAll=%1 הוסר מהמחשב.%n%nההגדרות והנתונים של האפליקציות נשמרו בתיקיות:%n  AppData\Roaming\WinApk Launcher  (הגדרות)%n  AppData\Local\WinApk Launcher  (נתוני האפליקציות, מטמון, יומנים)%n%nאפשר למחוק את התיקיות האלה כדי להסיר גם אותן.

[CustomMessages]
en.TaskDesktop=Create a &desktop shortcut
en.GroupShortcuts=Shortcuts:
en.TaskFileAssoc=Add "Open with WinApk Launcher" to .apk and .xapk files (opens the Add dialog with the file)
en.GroupFileTypes=File types:
en.ShortcutComment=Run Android apps on Windows with Win.apk
en.OpenWith=Open with WinApk Launcher
en.PackageType=Android app package
en.RunAfter=Open %1
he.TaskDesktop=צור קיצור דרך ב&שולחן העבודה
he.GroupShortcuts=קיצורי דרך:
he.TaskFileAssoc=הוסף "פתח באמצעות WinApk Launcher" לקובצי APK ו-XAPK (פותח את חלון ההוספה עם הקובץ)
he.GroupFileTypes=סוגי קבצים:
he.ShortcutComment=הרצת אפליקציות Android ב-Windows עם Win.apk
he.OpenWith=פתח באמצעות WinApk Launcher
he.PackageType=חבילת אפליקציה של Android
he.RunAfter=פתח את %1

[Tasks]
Name: desktopicon; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:GroupShortcuts}"; Flags: unchecked
Name: fileassoc; Description: "{cm:TaskFileAssoc}"; GroupDescription: "{cm:GroupFileTypes}"; Flags: unchecked

[InstallDelete]
; A new version brings its whole runtime; no stale jars or DLLs from the previous one.
Type: filesandordirs; Name: "{app}\runtime"

[Files]
; Leading "\" = only at the top of the folder: never ship a portable setup's own files.
Source: "{#LauncherDir}\*"; DestDir: "{app}"; Excludes: "\portable.txt,\profiles.json*,\cache\,\logs\,\apps\,\runtime\"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#RuntimeDir}\*"; DestDir: "{app}\runtime"; Excludes: "*.orig,*.prev-*,*.bak,*.log"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{cm:ShortcutComment}"; Check: not WizardNoIcons
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{cm:ShortcutComment}"; Tasks: desktopicon

[Registry]
; "Open with WinApk Launcher" for .apk and .xapk (optional task). The launcher is offered in the
; Open with list and as a right-click command; it does not take over the default app for these
; files (another program, such as Win.apk's own library, may own that).
Root: HKA; Subkey: "Software\Classes\WinApkLauncher.Package"; ValueType: string; ValueName: ""; ValueData: "{cm:PackageType}"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\WinApkLauncher.Package"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "{cm:PackageType}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\WinApkLauncher.Package\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\WinApkLauncher.Package\shell\open"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenWith}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\WinApkLauncher.Package\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassoc

Root: HKA; Subkey: "Software\Classes\.apk"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.apk\OpenWithProgids"; ValueType: string; ValueName: "WinApkLauncher.Package"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.xapk"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.xapk\OpenWithProgids"; ValueType: string; ValueName: "WinApkLauncher.Package"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc

Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".apk"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".xapk"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassoc

Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.apk"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.apk\shell"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.apk\shell\WinApkLauncher.Add"; ValueType: string; ValueName: "MUIVerb"; ValueData: "{cm:OpenWith}"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.apk\shell\WinApkLauncher.Add"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.apk\shell\WinApkLauncher.Add\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.xapk"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.xapk\shell"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.xapk\shell\WinApkLauncher.Add"; ValueType: string; ValueName: "MUIVerb"; ValueData: "{cm:OpenWith}"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.xapk\shell\WinApkLauncher.Add"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.xapk\shell\WinApkLauncher.Add\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassoc

[UninstallDelete]
; Anything the runtime wrote into its own folder. User data lives elsewhere and is kept.
Type: filesandordirs; Name: "{app}\runtime"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:RunAfter,{#AppName}}"; Flags: nowait postinstall skipifsilent

#include "AnsiPath.iss"
