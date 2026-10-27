; Inno Setup script for the per-user installer of Claude Usage Tracker for Windows.
;
; The release workflow (.github/workflows/release.yml) compiles it with two defines:
;   /DAppVersion=<version>   the release version, 1.2.3 or 1.2.3-beta.1
;   /DPublishDir=<folder>    the app's self-contained publish folder (dotnet publish of the App project)
; The installer is not code-signed, so the app keeps its path-derived tray identity (plan.md Milestone 7).

#ifndef AppVersion
  #error Pass /DAppVersion=<version>
#endif
#ifndef PublishDir
  #error Pass /DPublishDir=<the app's publish folder>
#endif
; VersionInfoVersion takes numbers only, so a prerelease suffix is dropped there.
#if Pos("-", AppVersion) > 0
  #define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
#else
  #define NumericVersion AppVersion
#endif

[Setup]
AppId={{519C08E7-06CB-42BD-8580-3A74C9DE278C}
AppName=Claude Usage Tracker
AppVersion={#AppVersion}
AppVerName=Claude Usage Tracker {#AppVersion}
AppPublisher=harningle
AppPublisherURL=https://github.com/harningle/claude-usage-tracker
AppSupportURL=https://github.com/harningle/claude-usage-tracker/issues
AppUpdatesURL=https://github.com/harningle/claude-usage-tracker/releases
AppCopyright=Copyright (c) 2025 Claude Usage Tracker Contributors, (c) 2026 harningle
VersionInfoVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
; Per user and without administrator rights, like the personal build (docs/personal-use.md): the app lives in
; %LOCALAPPDATA%\Programs\ClaudeUsageTracker and keeps its data, logins and installed integrations under
; %LOCALAPPDATA%\ClaudeUsageTracker, which this installer and its uninstaller never touch.
PrivilegesRequired=lowest
DefaultDirName={autopf}\ClaudeUsageTracker
DefaultGroupName=Claude Usage Tracker
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
LicenseFile=..\LICENSE
OutputBaseFilename=ClaudeUsageTracker-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; A running tracker holds its files: Setup asks it to close (Restart Manager) and starts it again only through
; the option on the final page.
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=Claude Usage Tracker
UninstallDisplayIcon={app}\ClaudeUsageTracker.App.exe

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Claude Usage Tracker"; Filename: "{app}\ClaudeUsageTracker.App.exe"

[Run]
Filename: "{app}\ClaudeUsageTracker.App.exe"; Description: "Start Claude Usage Tracker"; Flags: nowait postinstall skipifsilent
