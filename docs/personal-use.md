# Using the personal build

The personal build of Claude Usage Tracker for Windows is a folder: copy it somewhere permanent and run it. Each [release on GitHub](https://github.com/harningle/claude-usage-tracker/releases) carries a per-user installer and this folder as a portable ZIP ([README](../README.md)). There is no automatic update and no code signature yet; those belong to the public release ([plan.md Milestone 7](../plan.md#milestone-7---public-distribution)). The app is in English only. This guide was checked with the build of Milestone 6 ([milestone-6.md](milestone-6.md)).

## What the PC needs

- **Windows 11, x64**, with one person using it (plan.md section 1).
- **Microsoft Edge WebView2 Runtime.** Windows 11 includes it, and it is listed under Settings → Apps → Installed apps. Without it, claude.ai profiles and the API Console sign-in do not work; Claude Code and Codex profiles do. If it is missing, install the Evergreen Runtime from [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/).
- **Windows notifications switched on** for your user (Settings → System → Notifications), if you want the alerts. Windows accepts notifications while they are switched off, but shows none.
- **Native Claude Code** (`claude.exe`, usually in `%USERPROFILE%\.local\bin`) for Claude Code profiles, launchers and the status line. Claude Code runs status lines through Git for Windows' bash.

Nothing else: the folder carries its own .NET 10 runtime and Windows App SDK, and the app does not use any installed copy of them. It does not need the Windows App SDK runtime packages, including the separate push-notification ("Singleton") package.

## Building it

On the build PC only, with the .NET SDK 10.0.401 and Visual Studio Build Tools with the C++ workload (for the NativeAOT helper), from the repository root:

```powershell
dotnet publish src/ClaudeUsageTracker.App/ClaudeUsageTracker.App.csproj
```

This publishes the helper first, then the app (Release, self-contained), into `%LOCALAPPDATA%\ClaudeUsageTracker.Build\<checkout-id>\publish\ClaudeUsageTracker.App\release_win-x64`. The folder holds 326 files and 184 MiB (73 MiB zipped). If the shell has `NoDefaultCurrentDirectoryInExePath` set, unset it first, or the helper's link step fails with MSB3073.

## Installing

1. Copy the published folder to a permanent place of your own, for example `%LOCALAPPDATA%\Programs\ClaudeUsageTracker`. Not the build folder, which the next build replaces, and not a synced folder such as Dropbox.
2. Run `ClaudeUsageTracker.App.exe`. The setup wizard opens and links your first account.
3. Windows puts a new tray icon in the overflow (^). Drag it onto the taskbar to keep it in view.
4. To start the tracker when you sign in to Windows, switch on Launch at login on the Settings page, under App in the settings window's sidebar.

## Upgrading

1. Quit the tracker: Settings → Quit, or Quit in the tray icon's menu.
2. Replace the contents of the folder with the new build, keeping the same folder. At the same path Windows keeps the tray icon's place, the notification registration and the launch-at-login entry.
3. Start the tracker. Settings, logins and history carry over.
4. If the new build carries a different helper, Settings → Claude CLI shows Update beside the helper. Nothing is replaced until you choose it.
   - Launchers that are running keep their old file, which is deleted at a later start once nothing uses it.
   - If a file is in use, the tracker says so and leaves everything as it was; try again later.
   - An older build never replaces a newer helper.

## Moving the folder

Moving or renaming the folder works, but Windows treats the moved copy as a new app:

- **Tray icon:** it starts in the overflow again, because the icon's identity comes from the program's path.
- **Notifications:** they are registered again for the new path, and the old copy's registration is removed at the next start. Old notifications still in the notification centre do nothing when clicked.
- **Launch at login:** the entry still names the old path. The Settings page then says that another copy's entry is there; switch it on again in the moved copy.
- **Status line, hooks and launchers:** unaffected. They use the helper installed in the data folder, never the app folder, so they keep working after the app folder is moved or deleted. If a piece goes missing, the popover shows "Claude Code integration needs repair", which leads to Repair.

## Where things are

The tracker's data is in `%LOCALAPPDATA%\ClaudeUsageTracker`:

| Path | What it holds |
| --- | --- |
| `settings.json` | Profiles and settings; no secrets |
| `secrets\<profile>.secret` | Each profile's logins, encrypted (below) and readable only by your user |
| `secrets\backups\` | Encrypted backups of Claude Code's files from account switches, the newest five |
| `secrets\integration\` | The encrypted secret in the hook address |
| `browser\<profile>\` | Each claude.ai profile's own WebView2 browser data, with its session; managed by WebView2 |
| `cache\`, `history\` | Last usage and usage history per profile; no secrets |
| `statusline\` | The status line settings and the usage it shows, per Claude Code folder; no secrets |
| `bin\ClaudeUsageTracker.Helper.exe`, `install.json` | The installed helper, and the record of what the tracker installed |
| `automation.json` | What notifications and auto-start remember; no secrets |
| `cli-switch.json` | Only while a switch of Claude Code's account is unfinished |
| `diagnostics\requests.jsonl`, `diagnostics\crash.log` | The request log (operation, route, status and timing only, plus the target profile's local ID for a switch of Claude Code's account) and the type and place of a crash, never its message |
| `notification-icon.png` | The icon the app draws for its notifications |

Outside it:

| Place | What it holds |
| --- | --- |
| `%USERPROFILE%\.local\bin\claude-<profile>.exe` | Launchers |
| `%USERPROFILE%\.claude-<profile>\` | A launcher's Claude Code login folder; Claude Code's own, kept when the launcher is removed |
| `settings.json` in each Claude Code folder the tracker knows | The status line command, and the eight hooks while the tracker runs |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `ClaudeUsageTracker` | Launch at login |
| `HKCU\Software\Classes\AppUserModelId` and `\CLSID` | The notification registration of each copy that has run |
| `HKCU\Control Panel\NotifyIconSettings` | Explorer's record of each tray icon; Explorer's, not the tracker's |

## Encrypted logins stay on this PC

Logins are encrypted with Windows DPAPI for your Windows user on this PC, and the browser data with WebView2's own encryption, which also uses DPAPI.

- A copy of the data folder is no use on another PC or to another Windows user: the tracker reports the logins as unreadable, and you sign in again.
- An administrator resetting your Windows password (not you changing it) can make them unreadable too.
- A backup of the data folder can be restored only by the same user on the same PC.

Claude Code's and Codex's own login files are theirs. The tracker never writes Codex's. It writes Claude Code's only to hand back a login it renewed, or to switch Claude Code's account: when you pick a Claude profile or choose Use in Claude Code, or through auto-switch if you have switched it on. Every other setting in them is kept.

## Removing it

1. In the tracker, undo what it put outside its folder:
   - The Settings page, under App: switch off Launch at login.
   - General: Remove the profile's Terminal Launcher (repeat for each profile that has one).
   - Settings → Claude CLI: Reset. This takes the status line out of every Claude Code folder and puts back a status line it replaced.
2. Quit the tracker with Settings → Quit. A clean exit takes its hooks out of Claude Code's settings. If it crashed instead, start it and quit it once more.
3. Delete the app folder.
4. To delete your data and the tracker's copies of your logins, delete `%LOCALAPPDATA%\ClaudeUsageTracker`. Claude Code's and Codex's own logins are not affected.
5. A launcher's login folder, `%USERPROFILE%\.claude-<profile>`, is Claude Code's. Delete it only if you no longer want that login.
6. Remove the notification registrations of copies that no longer exist, with this command in PowerShell. It touches only registrations of `ClaudeUsageTracker.App.exe` files that are gone, as the tracker itself does at each start:

   ```powershell
   $classes = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes', $true)
   $aumids = $classes.OpenSubKey('AppUserModelId', $true)
   foreach ($name in $aumids.GetSubKeyNames()) {
       $activator = $aumids.OpenSubKey($name).GetValue('CustomActivator')
       if (-not $activator) { continue }
       $server = $classes.OpenSubKey("CLSID\$activator\LocalServer32")
       if (-not $server) { continue }
       if ($server.GetValue('') -match '^"(.+\\ClaudeUsageTracker\.App\.exe)" ----AppNotificationActivated:$' -and -not (Test-Path -LiteralPath $Matches[1])) {
           $exe = $Matches[1]
           $aumids.DeleteSubKeyTree($name)
           $classes.DeleteSubKeyTree("CLSID\$activator")
           $aumids.DeleteSubKeyTree($exe.Replace('\', '.'), $false)
           "Removed the notification registration of $exe"
       }
   }
   ```

Explorer keeps its record of the tray icon (`NotifyIconSettings`), as it does for every program that has shown one. The tracker does not edit Explorer's records. The record is harmless, and the icon may stay listed under Settings → Personalisation → Taskbar → Other system tray icons.
