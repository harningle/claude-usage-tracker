; Inno Setup script for the per-user installer of Claude Usage Tracker for Windows.
;
; The release workflow (.github/workflows/release.yml) compiles it with two defines:
;   /DAppVersion=<version>   the release version, 1.2.3 or 1.2.3-beta.1
;   /DPublishDir=<folder>    the app's self-contained publish folder (dotnet publish of the App project)
; Two more exist for testing the installer itself, so that a test install neither takes over the real
; install's uninstall entry nor offers to delete the real data folder:
;   /DAppId={{<GUID>}        another AppId (the doubled brace is Inno's escape for a literal one)
;   /DDataDir=<folder>       the data folder the uninstaller offers to delete
; The installer is not code-signed, so the app keeps its path-derived tray identity (plan.md Milestone 7).

#ifndef AppVersion
  #error Pass /DAppVersion=<version>
#endif
#ifndef PublishDir
  #error Pass /DPublishDir=<the app's publish folder>
#endif
#ifndef AppId
  #define AppId "{{519C08E7-06CB-42BD-8580-3A74C9DE278C}"
#endif
#ifndef DataDir
  #define DataDir "{localappdata}\ClaudeUsageTracker"
#endif
; VersionInfoVersion takes numbers only, so a prerelease suffix is dropped there.
#if Pos("-", AppVersion) > 0
  #define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
#else
  #define NumericVersion AppVersion
#endif

[Setup]
AppId={#AppId}
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
; %LOCALAPPDATA%\ClaudeUsageTracker, which this installer never touches. The uninstaller deletes that folder
; only when asked to ([Code] below).
PrivilegesRequired=lowest
; Inno Setup 6.7.0 and later enable Windows' RedirectionGuard on Setup by default, and a Setup that runs without
; elevation passes it on to what it starts, the tracker from the final page included (6.7.4 and 7.1.1 fixed this
; only for elevated installs). A tracker started so cannot follow junctions that a user made, such as those of the
; Codex CLI's standalone install, so its Codex auto-start found no codex.exe (2026-11-16). Setup runs as the user
; here, so the mitigation guards no privilege boundary; the check keeps older compilers, which lack the directive.
#if Ver >= EncodeVer(6, 7, 0)
RedirectionGuard=no
#endif
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

[Code]
// The uninstaller removes the program files. The data folder (settings, usage history, the encrypted logins
// and the backups of Claude Code's files) goes only when the user answers Yes here, or when the uninstaller
// is run with /DELETEDATA=1; a silent uninstall without that switch keeps it. Launchers, the status line,
// hooks and launch at login are the app's to undo first (docs/personal-use.md, "Removing it").
var
  DeleteData: Boolean;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  DataDir := ExpandConstant('{#DataDir}');
  if CurUninstallStep = usUninstall then
  begin
    DeleteData := False;
    if DirExists(DataDir) then
    begin
      if ExpandConstant('{param:DELETEDATA|0}') = '1' then
        DeleteData := True
      else
        DeleteData := SuppressibleMsgBox(
          'Also delete your settings, usage history and saved logins?' + #13#10#13#10 + DataDir + #13#10#13#10 +
          'Choose No to keep them for a later install. Claude Code''s and Codex''s own logins are not affected either way.',
          mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
    end;
  end
  else if (CurUninstallStep = usPostUninstall) and DeleteData then
  begin
    if not DelTree(DataDir, True, True, True) then
      SuppressibleMsgBox('Some files under ' + DataDir + ' could not be deleted. Delete the folder by hand.', mbError, MB_OK, IDOK);
  end;
end;
