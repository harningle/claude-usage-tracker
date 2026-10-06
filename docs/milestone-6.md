# Milestone 6 log

Work and evidence for [plan.md Milestone 6](../plan.md#milestone-6---personal-use-delivery): the personal build, its delivery checks, accessibility, the comparison with upstream's media, the full parity matrix, the guide to data and removal, and the Milestone 1 follow-ups D2, D4, D5 and D6. The plan's localisation was replaced by your decision to support English only. Upstream behaviour comes from the pinned upstream source (`588775e`): `LanguageManager`, `LanguageSettingsView`, `SettingsView`, `PopoverContentView`, `MobileAppView`, `SupportView`, `HeartbeatService`, `UpdateManager` and `RELEASING.md`. Each departure is recorded in [parity.md](parity.md) (differences 7, 20 and 54 to 56). Evidence types follow parity.md: automated tests, manual checks in fixture mode and live-account checks, kept separate. No live check was made in this milestone.

Recorded 2026-10-05 on the development machine (Windows 11 26200, WebView2 Runtime 154.0.4258.53, Claude Code 2.1.289). The published build carries .NET 10.0.12, Windows App SDK Foundation 2.3.12 and the WebView2 SDK 1.0.4258.31.

## Status

| Plan item | Status | Evidence |
| --- | --- | --- |
| Localisation | **Replaced by English only** (your decision, plan section 1). The unused 14-language list and its tests were removed. Upstream's other `.lproj` files are not shipped, and the frameworks' own text stays English (`SatelliteResourceLanguages=en`) | Automated (every key the app uses exists in the English tables); parity difference 54 |
| Accessibility | Every control a user can operate has an accessible name that is more than an icon glyph or a type name. This covers the popover, each settings page and the wizard's steps, including the selectable lists and their choices. On every settings page, Tab reaches every control, a list through its choices, and arrow keys change the choice in every list. Your review found that the first version of the audit skipped the lists; extended, it found one keyboard defect, now fixed (defect 3) | Automated (`AccessibilityTests`, with key presses sent through WPF's input system), mutation-checked; the fixture-mode checks below drove every step through these names |
| Source and image comparisons | Done against `popover.png`, frames of `settings.gif`, `statusline.png` and `icon.jpg` | Source/image: [parity.md](parity.md#source-and-image-comparison) |
| Full parity matrix with verification limits | Every upstream feature listed with its status; new differences 54 to 56; difference 7 completed with the Support, Mobile App, sponsor and heartbeat omissions | [parity.md](parity.md#upstream-features), [verification limits](parity.md#verification-limits) |
| Self-contained Windows x64 build with the helper | `dotnet publish` of the app; WebView2 prerequisite documented | Manual in fixture mode (below); [personal-use.md](personal-use.md) |
| Tests without a developer SDK, relocation, repair, upgrades | Passed in fixture mode on this PC, with no SDK in the app's environment | Manual (below). **No clean PC** |
| Data and secret locations, DPAPI limits, removal | Documented; the removal steps and their registry command were run on a fixture copy | [personal-use.md](personal-use.md) |
| AGENTS.md build commands | Updated with the validated publish command and the separate build root used while the tracker runs | AGENTS.md |
| D2, D4, D5, D6 | D4, D5 and D6 closed; D2 closed on this PC only | Below |

**Acceptance is not yet met.** The plan asks for daily personal use across the included features, which only your use of the published build can show. The open live checks stay as listed in [parity.md](parity.md#verification-limits). Nothing here claims a public release or full parity.

## Decisions

- **English only** (you, 2026-10-05). Upstream's 13 further languages cover all 820 of its keys; the port's 310 own strings exist only in English. Rather than translate those or mix languages, the port supports English only, as a deliberate departure from upstream. The 14-language list from Milestone 0, used only by its tests, was removed with them. Upstream's five language test cases are excluded rather than scheduled.
- **Removal is documented, not coded** (you). The existing controls undo launch at login, launchers, the status line and the hooks. Deleting the data folder removes the installed helper. One PowerShell command removes the notification registrations of copies that no longer exist ([personal-use.md](personal-use.md#removing-it)). An uninstaller belongs to Milestone 7.
- **The start-up race was in scope** (you). It cost one missed claude.ai check at every start.
- **Publishing the app publishes the helper first.** The helper's version is a constant, so a helper left over from an older checkout would not show in its number. The build still uses a helper published separately, as before.
- **Self-contained only when publishing.** A self-contained build would stop the test project referencing the app, so `SelfContained` is set only under `dotnet publish`.
- **Explorer's tray records are left alone** (D6). `NotifyIconSettings` belongs to Explorer, which keeps a record for every program that has shown an icon.

## What was changed

- **Browser route** (`Browser/BrowserSessions.cs`): requests on a view that is not yet on claude.ai share one navigation. A test can now park the view on its own page.
- **Localisation**: `Core/Localisation/SupportedLanguages.cs` and its tests removed; comments in `Strings.cs` updated.
- **Manage Profiles** (`ViewModels/SettingsViewModel.cs`, `Views/SettingsWindow.xaml.cs`): a profile's row stays the same object and changes in place (defect 3). After a rename, the box shows the name as saved, and a refused display change puts its switch back.
- **Project** (`ClaudeUsageTracker.App.csproj`):
  - `SelfContained` under `dotnet publish`.
  - English satellites only.
  - The helper published before the app is.
  - `Publish="true"` on the two Windows App SDK packages (below).
- **Tests:**
  - `BrowserSessionsTests`, with a real WebView2 runtime and a loopback site.
  - `AccessibilityTests`.
  - `WpfApplication`: one shared WPF application on its own thread, which the popover window test now uses too.
- **Docs:**
  - [personal-use.md](personal-use.md): new.
  - parity.md: features, comparison, limits, differences, test inventory.
  - plan.md: English only, statuses.
  - The D items in milestone-1.md, the defect in milestone-4.md, and AGENTS.md.

## Automated tests

1,320 tests pass, none skipped, with no build warnings. That is 1,296 before, less the 5 language cases, plus the 8 new ones below and 21 for defects 4 to 10:

- **`TheFirstCheckOnANewViewSucceedsAndNavigatesOnce`.**
  - The setup: a real WebView2 context on its own dispatcher thread, parked on a page of a small HTTP server on 127.0.0.1, sends the three requests of a usage check at once.
  - Before the fix all three came back as `navigation`, which the request log records as `network`: the live symptom.
  - After the fix all three return 200 and the page is loaded once. Five runs in a row passed and left no browser process or folder behind.
  - It needs no account and no network; without the WebView2 Runtime it is skipped.
- **`EveryPopoverControlIsNamedAndReachable`, `EverySettingsPageIsNamedAndReachable`, `EveryWizardStepIsNamedAndReachable`.**
  - What is checked: each window is laid out off screen with synthetic data and its visual tree walked. Every visible button, toggle, text box, combo box, slider, list and list choice must have a UI Automation name. The name needs a character outside the icon font's private-use range, and must not be the type name that a bound record gives a choice nothing else names. If enabled, the control must also be focusable and a tab stop, except a list, which is entered through its choices.
  - Template parts of a control (a scroll bar's arrows, a combo box's drop-down button) are left to that control.
  - Coverage: the popover; every settings page, with the Codex page under a Codex profile; and the wizard's provider choice, Claude Code found and not found, claude.ai key, organisation choice, review and Codex steps. Each step is asserted to be reached.
  - A page where no control is checked fails, so a window that did not lay out cannot pass.
- **`TabReachesEveryControlOfEverySettingsPage`.** The settings window shows every optional part: the API metric, notifications, and two profiles in multi-profile display. Tab presses go through WPF's input system as a keyboard sends them, round each page from its first tab stop. They must reach every enabled control the audit above finds, a list through one of its choices, and must never drop the focus to the window.
- **`ArrowKeysChangeTheChoiceInEveryList`.**
  - The behaviour: from the selected choice, the arrow key that points at a neighbouring choice selects it, and the opposite key selects the first again.
  - The lists: the three icon-style lists, the notification sound, the status line's colours and the multi-profile style.
  - Every list in the window must be exercised, so one that the settings hide fails rather than passing unchecked.
- **`RenamingFromTheKeyboardKeepsTheFocusAndShowsTheSavedName`, `ARefusedDisplayChangeShowsTheProfileStillDisplayed`.** These cover what the rebuilt rows used to give for free:
  - Tab from a renamed profile moves on, and the box shows the trimmed name, or the old one when a blank name is refused.
  - Hiding the last displayed profile with Space is refused, and its switch shows it displayed.
- **Mutation check.** Eight deliberate breakages were each caught, then undone:
  - the Refresh button without its name (it then exposes only the glyph U+E72C)
  - the History tab with `IsTabStop=False`
  - the style choices without their names (they then expose the record's type name)
  - the sound list with `SelectionMode="Multiple"`, where arrows move without selecting
  - the colour choices with `IsTabStop=False`
  - the profile rows rebuilt at every read, which brings back defect 3
  - the rename box not refreshed, and the refused switch not reset

## Defects found

1. **The first claude.ai check after each start failed with "network"** (seen live since Milestone 3).
   - The cause: a usage check sends its usage, overage-limit and credit-grant requests at once. On a new background view each found it still on `about:blank` and called `Navigate`, so the second navigation cancelled the first. The cancelled navigation's `NavigationCompleted`, with `IsSuccess` false, then answered every request waiting for it. The next check found the view already parked and succeeded.
   - The fix: a navigation in progress is shared by the requests that arrive meanwhile.
   - Reproduced and checked by `TheFirstCheckOnANewViewSucceedsAndNavigatesOnce`. **Not yet seen live.**
2. **The published app failed at start for want of the Windows App SDK assemblies.**
   - What happened: its first start in fixture mode showed `FileNotFoundException: Could not load file or assembly 'Microsoft.Windows.AppNotifications.Projection, Version=2.0.0.0'`. `crash.log` recorded its type and place, as designed.
   - The cause: the SDK gives a `PackageReference` with `PrivateAssets="all"` and no `Publish` metadata `Publish="false"` (`Microsoft.NET.Sdk.Shared.targets`). The project used `PrivateAssets` to keep the Windows App SDK out of the test host, so a publish left out 20 of its files: 17 projection assemblies, the notification projection among them, its background-task DLL and its two bootstrap DLLs.
   - The fix: `Publish="true"` on the two packages. The publish now holds every file of the build output except the app's empty resource index (`ClaudeUsageTracker.App.pri`), which the app does not use; notifications register without it.
   - Before this milestone the app had only been built, never published, so no earlier check could see it.
3. **On Manage Profiles, Tab from a profile's name dropped the keyboard focus to the window** (found by the extended audit, after your review).
   - The effect: everything below the first name box (the other rows, Delete, the multi-profile switches and its style list) could not be reached with Tab. By the same cause, Space on a profile's display switch would lose the focus too.
   - The cause: leaving the box commits the rename, and every settings edit raises a change for every property. `Profiles` built new row objects each time, and rows compare by reference, so both lists bound to it rebuilt every row. That included the control Tab was moving to, whose removal left the focus with the window.
   - The fix: the rows stay the same objects and change in place, so the lists keep their controls.
   - Checked by `TabReachesEveryControlOfEverySettingsPage`, which failed with "Tab from TextBox drops the keyboard focus to the window" before the fix and passes after it.

Your code review before publishing (2026-10-06) found defects 4 to 7; fixing them turned up 8 and 9. Your second review found that the first fix of defect 4 still lost a renewal at a restart, and found defect 10. The tests named for defects 4 to 7 and 9 failed before the fixes, except `RenewedCookieDoesNotBringBackARemovedKey`, which keeps behaviour that was already right. Defect 8's tests cover an event and a method that are new. A mutation check undid each new guard in turn: all eleven breakages of the first round and all ten of the second were caught.

4. **The same pasted Codex login in two profiles could send a used refresh token after a restart.**
   - The cause: a renewal saved the new tokens in the renewing profile only. The record of used tokens is in memory, so after a restart the other profile sent the old one, and OpenAI revokes a login whose token is sent twice.
   - The first fix saved the renewal in every profile whose pasted credentials held the token it used up, as Claude Code logins already were (difference 13). It also made renewals wait while another profile's saved credentials cannot be read, since they might hold that token.
   - What your second review found: a profile whose file could not be written at that moment kept the renewal in memory only. After a restart it sent the used token, so you saw two renewals instead of one. The failure was also not shown.
   - The fix now: each profile saves the renewal with an encrypted record of the token used up and of the other profiles that held it, as Claude Code's pending hand-back does. A profile still holding the used token never sends it: it is given the renewal from a record, at the next check of a profile that saved one or before any renewal. Until every profile has it, nothing in that login is renewed again, and both profiles' Codex pages say why. Parity difference 32.
   - Checked by `LoginPastedInTwoProfilesIsRenewedOnceForBothAndNeverResentAfterARestart`, `PastedLoginIsNotRenewedWhileAnotherProfilesCopyCannotBeRead`, `RenewalThatCannotBeSavedInAnotherProfileIsSavedAtItsNextCheck`, `CopyThatCouldNotBeSavedNeverSendsTheUsedTokenAfterARestart` (your reproduction: without the record it sends two renewals), `CopyThatStillCannotBeSavedAfterARestartWaitsAndSaysSo`, `NothingIsRenewedAgainUntilEveryCopyHasTheRenewal`, `RenewalSavedOnlyInTheCopyReachesTheRenewingProfileAfterARestart`, `SettledRenewalLeavesNoRecordBehind` and `CodexRenewalRecordIsKeptWithItsCredentialsAndDroppedWhenTheyAreReplaced`.
5. **The wizard saved to whichever profile was active at Done.**
   - The effect: the wizard does not block the rest of the app. If the Next Profile shortcut or the profile switcher changed the active profile while it was open, a sign-in made for one profile was tested and saved for another. Closing it then cleaned up the wrong profile's browser data.
   - The fix: the wizard fixes its profile when it opens and uses it for the sign-in, the test, the save and the clean-up. A profile deleted meanwhile is not recreated; Done says so. The saved profile becomes the active one, as before.
   - Checked by `ProfileSwitchedWhileTheWizardIsOpenKeepsTheSignInAndSaveOnTheProfileItOpenedFor`, `ConsoleKeyIsSavedForTheProfileTheWizardOpenedFor` and `ProfileDeletedWhileTheWizardIsOpenIsNotRecreatedOrReplaced`.
6. **Closing the wizard while a Console or Codex key was being saved left the link without its key.**
   - The cause: the settings are saved first and the key second. Closing the wizard while the key waited for the credential lock cancelled the save, and the undo ran only for storage errors.
   - The fix: any failure of the second step undoes the first, cancellation included; the undo runs on the UI thread, which owns the settings.
   - Checked by `SaveCancelledWhileWaitingToWriteTheKeyRestoresTheSettings`.
7. **A late Console answer could overwrite a newer sign-in.** A renewed session cookie replaced whatever key was saved. It now replaces only the key that the request sent. Checked by `RenewedCookieNeverReplacesANewerSignIn` and `RenewedCookieDoesNotBringBackARemovedKey`.
8. **A renewed Console cookie kept the old expiry in the settings,** so the settings page showed it and the expiry warning came a day before a date that no longer applied. The renewed cookie's expiry now becomes the link's, unless the link was replaced meanwhile. Checked by `RenewedConsoleCookieIsAnnouncedWithTheLinkItWasFetchedFor` and `RenewedConsoleCookieMovesTheExpiryOfTheLinkItWasFetchedForOnly`.
9. **The Codex page never showed a storage error,** although a pasted login whose renewal could not be saved was meant to say so. The Codex page now shows it, as the Claude Code page does, and also when a renewal waits for another profile's credentials. Checked by `PastedCodexCredentialsThatCannotBeSavedOrRenewedSaySo`.
10. **A settings edit whose save failed was applied anyway.**
    - The effect: the error was shown, but the app went on with the unsaved choice until it was restarted, which then quietly brought back the old one. Some of these choices act outside the app: session auto-start sends messages, a global shortcut is registered, and the HUD installs hooks in Claude Code's settings.
    - The fix: the settings pages, the status line choices and the HUD switches apply an edit only once it is saved. A failed save is shown and the edit undone, as profile actions already were. Records of what has already been done outside the app, such as an applied status line or an installed launcher, still follow what was done.
    - Checked by `AnEditWhoseSaveFailsIsUndone` and `ChoicesWhoseSaveFailsAreUndoneAndNotApplied`.

## Delivery checks in fixture mode

Run by Claude on 2026-10-05 with the published build and a synthetic fixture folder. The steps were driven through UI Automation, by the accessible names above.

- **Environment:** each start had `PATH` without any `dotnet` folder, and no `DOTNET_*`, `MSBuild*` or `NUGET_*` variables.
- **Placement:** the build was copied to a scratch folder outside the build tree, so the original build output played no part.
- **Data:** the fixture's data and home folders took the place of the real ones, as fixture mode does. No real account, login or Claude Code folder was touched.
- **Your tracker** kept running throughout, from the Debug output. The checks' builds went to a separate root (AGENTS.md) and never touched it.

1. **Start without an SDK.** The first start opened the setup wizard.
   - Every loaded module came from the app's folder or Windows. Nothing came from an installed .NET (`\dotnet\`) or Windows App SDK package (`\WindowsApps\`); `coreclr.dll`, `hostfxr.dll` and `Microsoft.WindowsAppRuntime.dll` were the folder's own.
2. **Fresh setup.** The wizard's Claude Code route found the fixture login and tested it. Its review showed the account, the organisation, the plan and 39 %/60 %. Done saved the profile with its encrypted secret.
3. **Notification registration (D5).** `Register()` succeeded from the published copy: an `AppUserModelId` entry with its `LocalServer32` command for the copy's path.
4. **Integrations from copy A.**
   - Apply on the Claude CLI page installed the helper in the data folder's `bin` and wrote the status line command, which points there, not at the app.
   - Install Launcher on General put `claude-electric-eel.exe` in the fixture's `.local\bin`, recorded in `install.json`.
5. **Moving and deleting the app.** Copy A was quit with Quit, moved to B, and its folder deleted. With no tracker running:
   - The status line command ran through Git Bash and drew `demo │ Opus │ Usage: 21% ▓▓░░░░░░░░` from the installed helper.
   - The launcher started the stand-in `claude.exe` (`tests/FakeClaude`) with the profile's folder and the arguments `--print` and `two words` intact, and returned its exit code, 7.
6. **The moved copy (D6).** At B's first start:
   - A's notification registration was removed and B's added.
   - The live build's and an older build's registrations were left alone, since their executables still exist.
   - Explorer kept A's `NotifyIconSettings` entries, as expected.
7. **Missing helper and repair.** With B quit and the installed helper deleted:
   - The next start showed "Claude Code integration needs repair." in the popover.
   - The banner led to the Claude CLI page, which named the missing file and offered Repair.
   - Repair installed the helper again from B's folder, with the same hash, and the banner went.
8. **A manual replacement upgrade beside a running launcher.** A release 2 was published with the helper's version raised to 2, then the source change was undone.
   - The setup: a launcher session was started, B was quit, and its files were replaced in place with release 2.
   - **Nothing was replaced at start:** the installed helper and the launcher kept release 1's hash until Update.
   - **A refused rename:** with the installed helper held open without delete sharing, Update left the helper, the launcher and the manifest exactly as they were.
   - **The upgrade:** once the handle was closed, Update replaced the helper and the launcher with release 2's file while the session ran. The session's image stayed in use as `claude-electric-eel.exe.old-209444fc`, recorded as retained.
   - **Cleanup:** after the session ended, the next start deleted that file and emptied the retained list.
9. **No downgrade, and the message for a refused rename.**
   - Release 1, started over release 2's helper, offered no update and said "A newer build of the app installed a later helper; it is kept". Nothing changed.
   - A release 3 (version 3, again undone in the source) with the helper held open reported "A file is in use and was left as it is. Try again later: The process cannot access the file because it is being used by another process." The installation stayed intact, with nothing pending.
   - With the handle closed, Update installed release 3 and the page said "The helper is up to date."
10. **Removal, as documented.**
    - In the app: Reset took the status line out (`settings.json` became `{}`), and Remove took the launcher out while its login folder stayed. The manifest then listed no launcher, status line or hook folder.
    - After Quit, the copy's folder was deleted.
    - The guide's PowerShell command then removed exactly that copy's three registry keys. The live build's and the older build's registrations were untouched.

Not covered here:

- **Interrupted promotion** rests on the automated tests (`FileInstallerTests` and `IntegrationServiceTests`), which interrupt each step. A rename cannot be interrupted at a chosen point by hand.
- **Notification clicks** on the published build were not tried; Milestone 4 checked them on a build.
- **The claude.ai and Console routes** need a browser, which fixture mode never starts.

## D2, D4, D5 and D6

- **D2 (clean machine without the Singleton package): closed on this PC only.**
  - This PC has the Windows App SDK runtime packages, the Singleton package among them. The published build used none of them: no module came from `\WindowsApps\`, and registration and setup worked.
  - A PC without them has not been tried. Windows 11 Home has no Windows Sandbox, so a second PC or a virtual machine would be needed.
- **D4 (output size): closed.**
  - The publish is 326 files and 184 MiB, 73 MiB zipped, with the 3.1 MB helper. The notification spike was 470 files, 186 MB.
  - Leaving out the frameworks' 13 other languages saved 156 files and 8.7 MB. The largest files are the Windows SDK projection (24 MB), `System.Private.CoreLib` and `PresentationFramework` (16 MB each).
- **D5 (the Insights resource workaround): re-checked.** The published build still needs the extracted `Microsoft.WindowsAppRuntime.Insights.Resource.dll`, carries it, and registers. Check again at each Windows App SDK update.
- **D6 (stale entries after a move): closed.**
  - Notification registrations of moved or deleted copies are removed at the next start (Milestone 4), as step 6 showed for a published copy.
  - Explorer's `NotifyIconSettings` records are left to Explorer and documented as a harmless leftover.

## Open questions and known limits

- **A clean PC** (D2) and **a screen reader session** remain untried.
- **The start-up fix** needs a live start to be seen.
- **Explorer's tray records** of moved or removed copies stay in the registry, and may stay listed under Settings → Personalisation → Taskbar → Other system tray icons.
- **Claude Code 2.1.289** is installed now. The hook gate and status line were measured on 2.1.288.
- **Whether the Console replaces its session cookie** has not been seen live; defects 7 and 8 rest on automated tests.
- **A pasted Codex login that was renewed outside the tracker** (by the Codex CLI on another PC, or an old copy of `auth.json`) cannot be recognised. Its first renewal sends a used token. Defect 4 covers only copies the tracker itself holds.
- **A renewal that no profile could save before the tracker quits is lost,** for pasted Codex logins as for Claude Code ones (plan section 2): the next start has only the used token. The tracker tries to save it again at every check until then. Once any profile holding the login has saved it, the others are given it from there (defect 4).

## Live checks to do

These need your accounts or your PC, so they are yours to run:

1. **The published build in daily use.**
   - Publish it, copy it to a permanent folder and start it there ([personal-use.md](personal-use.md)). It shares your data folder and logins with the Debug build, so quit that first.
   - Check that notifications appear and that a click opens the popover.
   - Check that the first claude.ai check after a start succeeds; Debug Tools shows it.
   - Leave it in daily use.
2. **Its tray icon and launch at login.** As a new path it has a new tray icon, in the overflow. Its notification registration is new, and launch at login must be switched on again from it.
3. **The open checks of earlier milestones,** in [parity.md](parity.md#verification-limits):
   - the Console
   - refresh failure in the two-account check
   - an auto-switch at a real 100 % and a threshold alert on real usage
   - remote desktop
   - a switch beside a launcher-only session
   - a helper upgrade with real Claude Code
4. **A clean PC** (D2), if you have one or a virtual machine: copy the published folder there, start it, set up a profile and confirm that a notification shows.

## Reproducing

From the repository root:

```powershell
dotnet build ClaudeUsageTracker.slnx
dotnet test --solution ClaudeUsageTracker.slnx
# The personal build: the helper, then the app, self-contained (docs/personal-use.md).
dotnet publish src/ClaudeUsageTracker.App/ClaudeUsageTracker.App.csproj
# While the tracker runs from the Debug output, use a separate build root for all three, e.g.
# -p:ArtifactsPath=$env:LOCALAPPDATA\ClaudeUsageTracker.Build\<checkout-id>-m6
# Fixture mode on a copy of the published folder, as in docs/milestone-3.md and docs/milestone-5.md:
<copy>\ClaudeUsageTracker.App.exe --fixture <folder>
```
