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

**Acceptance met on 2026-10-27.** The plan asks for daily personal use across the included features. The published build has been in daily use since 2026-10-06, as the installed copy at `176374c` since 2026-10-13: two claude.ai profiles and a Codex profile polled all day, notifications, launch at login, the switch of Claude Code's account, and the claude.ai and Codex auto-starts, which the overnight monitoring of 2026-10-14/15 (below) records. That copy predates the two fixes of 2026-10-15 (defects 12 and 13) and the switch log, which have not run live; the first release carries them. The open live checks stay as listed in [parity.md](parity.md#verification-limits). Nothing here claims a public release or full parity.

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

1,373 tests pass, none skipped, with no build warnings. That is 1,296 before, less the 5 language cases, plus the 8 new ones below, 21 for defects 4 to 10, 1 for defect 11 and 52 for the Codex auto-start:

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

You found defect 11 in daily use of the published build (2026-10-06):

11. **The auto-start switch looked usable on a profile without a claude.ai sign-in.**
    - The effect: of two Claude profiles with auto-start on, only one started sessions. The other had only a Claude Code login, and auto-start sends through a claude.ai session, so it was skipped at every check. The card's note was easy to miss.
    - The fix: without a claude.ai sign-in the switch is greyed, showing what is stored (your choice), and the note says the profile can't auto-start. A profile switched on earlier starts sending once it is signed in. Parity difference 39.
    - Checked by `TheAutoStartSwitchIsGreyedWithoutAClaudeAiSignIn`, which realises the settings window and failed with the binding removed. The Tab check's settings now include a claude.ai sign-in, so the switch is still reached there.

The overnight monitoring of the installed build's auto-start that you asked for (read-only, hourly, 2026-10-14 to 15) found defects 12 and 13. Its results are under [Live checks to do](#live-checks-to-do), item 5. The fixes add 6 tests; 1,392 pass, none skipped, with no build warnings.

12. **A window that the provider ended early was not started again until its old reset time.**
    - What happened: at 06:13 UTC on 15 October, OpenAI reset the Codex limits early, both the 5-hour window and the weekly one. The window the tracker had started at 02:37:47 would have run until 07:37:46. The wait after a start held until then, so Codex had no window until the tracker started one at 07:42:53.
    - The fix: during that wait, the tracker watches a polled profile's window on its polling loop's reads, with no request of its own. Once a read at least five minutes after the start has shown the window with the reset time the wait runs to, each check applies the usual rules, so a window that has ended is started again.
    - What still waits in full:
      - a start whose window never shows, so a message the provider did not count is not repeated at every check;
      - a failure;
      - a profile that is not polled, since watching it would need a request at every check, through a browser for claude.ai.
    - Parity difference 39.
    - Checked by `APolledWindowThatEndsEarlyIsStartedAgainBeforeTheWaitIsOver` for claude.ai and Codex, which failed before the fix. `AStartWhoseWindowNeverShowsKeepsItsWait`, `AStartWhoseWindowKeepsMovingKeepsItsWait` and `TheWaitAfterAFailureIsKeptInFullForAPolledProfile` cover the waits that must still hold. A mutation check removed the five-minute margin, then the failure guard; each was caught.
13. **"Session Reset" was announced mid-window.**
    - What happened: "personal - Session Reset" came at 12:30:48 and 17:14:50 UTC, while that window ran until 16:50 and 21:50.
      - Both came at the first use of a window the tracker had started. One read showed some use, then the next showed 0 %.
      - The 0 % read kept the window's reset time: had it changed or gone missing, the history would have recorded a reset, and it did not.
      - The alert followed upstream's rule, which announces any step from above 0 % to 0 %.
    - The fix: a read at 0 % that still shows the open window, with its reset time, is no reset. It sends no alert and keeps the thresholds already notified in that window. A reset time that has passed or moved, or a window no longer reported, is still a reset. Parity difference 38.
    - Checked by `AReadAtZeroThatStillShowsTheOpenWindowIsNoReset`, which failed before the fix.

Daily use of the installed build found defect 14 on 2026-11-05. The fix adds 2 tests; 1,400 pass, none skipped, with no build warnings.

14. **A switch back to a profile with a claude.ai sign-in was refused as a rejected login.**
    - What happened: on 4 and 5 November each pick of the other profile was refused, asking for a new sign-in. Debug Tools showed `cliSwitch` with `loginRejected`, each time after a token renewal had answered `invalid_grant`.
      - Both profiles get their usage from claude.ai, so their Claude Code link was never checked. Their copies of the Claude Code login were not updated when Claude Code renewed it, and each renewal retires the refresh token before it.
      - A switch made by the tracker saves the outgoing account's newest login first, which hid the gap. A `/login` in Claude Code, which the refusal asks for, does not. At 23:33:51 UTC on 5 November one replaced the personal profile's newest login. That profile's copy dated from 19:51 UTC on 4 November, and the account had been in use since, so the next pick of it was refused. Each new sign-in set up the next refusal.
    - The fix: a profile whose usage comes from claude.ai has its Claude Code link checked at each poll, without renewing anything. The check follows Claude Code's renewals while Claude Code holds the account. It neither takes over an expired login nor makes the copy the tracker's own, and a switch back renews the copy if it has expired. A failure to save the copy shows on the CLI Account page. Parity differences 28 and 41.
    - Checked by `ClaudeAiProfileFollowsClaudeCodesRenewalsOfItsLogin`, which failed before the fix, and `ClaudeAiProfileNeverRenewsItsClaudeCodeLogin`. A mutation check removed the guard against a takeover, then the one against renewing the copy as the tracker's own; each was caught.
    - Not established: how the first copy went out of date before 4 November. The request log reached back only to 18:05 UTC that day, and the switches the tracker made from 23 to 30 October all went through.
    - Live on 2026-11-06, with the fix installed as a local build labelled 1.0.0-beta.1.1:
      - A pick of the personal profile at 00:35 UTC was still refused, as expected. Its copy held the refresh token Claude Code had used up on 4 and 5 November, and no fix can renew that.
      - After one `/login` with the personal account, picks of the work profile, then personal, then work again switched Claude Code each time, between 00:38 and 00:39 UTC.
      - Still open: a switch back after Claude Code has renewed the login of the profile that is in use, which is the case the defect needed ([parity.md](parity.md#verification-limits)).

## Codex auto-start (added 2026-10-06)

At your request, a Codex profile can now auto-start its session as well. Upstream's Codex provider cannot do this, so it is a deliberate addition: parity difference 39 has the details.

- **Why it is worth doing:** Codex's 5-hour window starts at first use, as Claude's does. In four windows of your Codex history, the reset came exactly five hours after the first use. For example, on 5 October first use fell between the samples at 12:32 and 12:43 UTC, and the reset was at 17:42:44.
- **How an unstarted window is recognised:** while idle, Codex reported 0 % with a reset time five hours after the read, moving on with the clock. At 15:02 UTC it read 20:02:29; after first use at about 15:21 it stayed at 20:21:41. After that window ended, the tracker's cache confirmed it: at 20:34:05 and 20:35:06 UTC Codex read 0 %, resetting 4 h 59 min 57 s after each read. A window started a minute ago looks the same in a single read, so the reset must be seen moving between two reads at 0 %, at least a minute apart and within 15 seconds of the clock. The latest read must also put it within two minutes of five hours away. A missing or past reset also counts as unstarted. Just before the message, a confirmation read a minute later must show the reset still moving, which catches a window started shortly before the deciding read. A start within about 15 seconds before the confirmation read still looks unstarted, because Codex reports both alike. The first start therefore comes one check and a minute after Codex is first seen idle.
- **How it sends:** the tracker runs your own Codex CLI once: `codex exec --json --ephemeral --skip-git-repo-check --ignore-user-config --sandbox read-only Hi`.
  - It runs in a new empty folder, with the profile's `CODEX_HOME` and without API-key variables. Its input is closed, and it is stopped with its children after two minutes.
  - Exit code 0 with a `turn.completed` event counts as a start, followed by a five-hour wait. Anything else is a failure: it is notified once and waits 30 minutes.
  - Debug Tools logs it as `codexAutoStart` with the exit code.
- **Who can use it:** only a profile that reads the Codex CLI's login. Pasted credentials are excluded and their switch is greyed. The CLI sends with its own login, which such a profile does not follow; running it with the pasted credentials instead could renew them in the CLI's copy and use up the refresh token the tracker holds. Just before the run, the CLI's login is checked again; if it now belongs to another account, nothing runs.
- **Fixture mode** never runs the CLI. It answers from `codex-auto-start.jsonl` (the events) and `codex-auto-start.exit` (the exit code) in its responses folder; without them the CLI counts as not found.
- **Tests:** 52 new.
  - `RefreshCoordinatorTests` gained three cases for `CheckNowAsync`, through which a polled profile's loop makes the confirmation read.
  - `CodexAutoStartTests` (32) run the service with the real Codex usage source, scripted answers and a recorded stand-in for the CLI run. They cover a start and its wait, the rule for an unstarted window, an open window, pasted credentials, another account before and during the read, changes while the usage is read, each kind of failure and its wait, and the polling loop's result.
  - `CodexExecTests` (15) run the CLI as a real process, against a stand-in `codex.exe` (`tests/FakeCodex`). They cover the fixed arguments, the Codex home, the empty folder, the closed input, the removed API keys, what counts as a start, a run stopped with its children, a child that keeps the output open, the removal of earlier runs' folders and finding `codex.exe` on PATH.
  - Fixture mode and the settings card have one case each.
  - A mutation check broke twelve guards in turn, and each was caught. One was missed at first, the exclusion of pasted credentials, because its test had saved none; it now does. After your reviews, six more breakages of the revised rule were each caught: the moving reset, the minimum gap, the earlier read at 0 %, the five hours ahead, keeping the earlier read, and judging against the read's time. Five breakages of the confirmation read were also caught: leaving it out, the check that the profile is still wanted after it, its minute's wait, judging it by the rule, and honouring a wait the server asked the polling loop for. The six were checked again with the confirmation read in place. For the last review's fixes, six more breakages were each caught: the check after the minute's wait, the loop making the confirmation read, and four parts of `CheckNowAsync` (making the check due, answering only after the check, answering from it, and answering when polling stops).
- **Live check (passed, the evening of 2026-10-06):** with the published build, you switched auto-start on for your Codex profile while Codex was idle.
  - At the next check the tracker ran the CLI: `codexAutoStart` at 22:02:57 UTC, exit status 0, 4.6 s. The "Session Auto-Started" notification followed.
  - The reset time stopped moving: it stayed at 03:02:54 UTC, five hours after the run, an hour and more later. The tracker then waits until about that time.
  - The start cost too little to show: the session still read 0 % and the weekly use stayed at 63 %.
- **Not checked:** an npm install, whose `codex.cmd` is not run, and a `chatgpt_base_url` in `config.toml`, which `--ignore-user-config` skips.
- **Your reviews (2026-10-07)** found five defects, all fixed:
  - **A session started a minute earlier was started again.** The rule judged a single read: 0 % with a reset about five hours away. A window you had just started still reads like that, so the tracker sent a needless message and announced a start. The rule now needs the reset to move with the clock between two reads, as above. `ASessionStartedAMinuteAgoIsNotStartedAgain` reproduces it: the CLI ran before the fix and does not now. `AFutureResetCountsAsUnstartedOnlyWhenItMovedWithTheClock` covers the rule's parts.
  - **A session started just before the deciding read was still started again.** The two-read rule allows 15 seconds for timing, so a window started 10 seconds before the second check passed as unstarted; for a polled profile the deciding read can also be up to 30 seconds old when the message goes. The tracker now reads once more, a minute after the deciding read and just before the message, and sends only if the reset has moved with the clock again. `ASessionStartedJustBeforeTheDecidingReadIsNotStartedAgain` reproduces your case: the CLI ran before this fix and does not now. The remaining limit is a start within about 15 seconds before that last read, which no read can tell apart.
  - **Removing the Codex link during the confirmation read's minute crashed the app.** After the wait the read went ahead without checking the profile again, met the missing link and threw inside the auto-start's timer handler. It also still read a profile switched off or deleted meanwhile. The profile is now checked again straight after the wait, and nothing is read or sent if it is no longer eligible with the same login. `NothingIsReadOrRunWhenTheProfileChangesDuringTheConfirmationWait` reproduces it: before the fix it threw for the unlinked case and read twice for two others.
  - **A rate limit on the confirmation read did not reach polling.** For a polled profile the confirmation was a request of its own beside the polling loop, and a `Retry-After` on it stayed with the auto-start, so the loop kept checking. The loop now makes that read itself when asked (`RefreshCoordinator.CheckNowAsync`), so its schedule and server-requested waits apply, and the auto-start again never requests beside a loop. Its tests show the check made at once and answered only once done, nothing sent during a server-requested wait, and an answer when the profile stops being polled. `APolledProfileIsDecidedAndConfirmedByItsPollingLoopAlone` and `AConfirmationTheLoopCouldNotMakeStartsNothing` cover the auto-start's side.
  - **A project-level test run did not build the stand-in `codex.exe`,** so all `CodexExecTests` failed unless the whole solution was built. The test project now references `tests/FakeCodex` as it does `tests/FakeClaude`, without referencing its assembly. A run of the test project alone, with the stand-in's output deleted first, passed.

## Claude Code follows the picked profile (added 2026-10-13)

At your request after daily use, picking a profile now switches Claude Code to its account too, as upstream's activation did. This replaces your decision of 2026-10-02, under which only "Use in Claude Code" switched it (plan.md section 2; parity differences 29 and 41).

- **What prompted it:** on 2026-10-13 you reported that switching did not work after a few days away. The settings showed Activate pressed at 20:15:06 and 20:16:06 UTC, which then changed only the viewed profile, and no "Use in Claude Code" in that run.
- **What asks for the switch:**
  - The profile menus of the popover and the settings window, Activate in Manage Profiles and the next-profile shortcut.
  - Picking the active profile again asks again, which is how to ask after a restart dropped a waiting switch.
  - Only a Claude profile linked to the folder the plain `claude` command uses asks. A profile linked to a launcher's folder is used through its launcher.
- **What does not:**
  - Clicking a tray icon in multi-profile mode.
  - A notification click, which would otherwise move Claude Code onto a profile nearing its limit.
  - Auto-switch, which is unchanged: only when Claude Code was on the profile that reached its limit.
  - Finishing the setup wizard.
- **The wait** is the existing queue of "Use in Claude Code". While any Claude Code may be running, the switch waits in memory, is tried every five seconds and is dropped by a restart. The popover now shows it under the banner, whichever profile it shows, with Cancel Switch. Picking the profile Claude Code is already on drops it too, and it ends once Claude Code is signed in to its account.
- **Notifications:** a picked switch is announced when it starts to wait ("Claude Code is running, so it keeps its account until it closes. Then it switches to the account of profile …", as for auto-switch), and again when it is made or refused, also when it did not have to wait. The wait is announced once. A pick made while another switch is still being made waits its turn; it is announced at its first attempt that finds Claude Code running, or, if that attempt switches at once, only when it is made. A switch made at once from the CLI Account page is still shown only there.
- **Untracked logins:** a picked switch never signs out a login that no profile follows. It leaves it and says so in its notification; "Use in Claude Code" keeps its confirmation.
- **Fixture mode** sees one running Claude Code while a `claude-running` file exists in its responses folder, so the wait can be shown there.
- **Tests:** 13 new; 1,386 pass, none skipped, with no build warnings.
  - `CliSwitcherTests`: `OnlyAPickedSwitchIsAnnouncedWhenItHappensAtOnce` (2 cases), `APickedSwitchThatWaitsIsAnnouncedWhenQueuedAndWhenMade`, `APickedSwitchNeverSignsOutALoginNoProfileFollows` (at once and after a wait), `AnAutomaticRequestThatNoLongerAppliesRedrawsTheWaitingSwitchItDropped`, `PickingTheAccountClaudeCodeIsOnWhileItRunsDropsTheWaitingSwitch`, `AWaitingSwitchEndsOnceClaudeCodeIsSignedInToItsAccount` and `APickMadeWhileAnotherSwitchRunsIsAnnouncedOnceItWaitsForClaudeCode`. `TheNewestRequestReplacesOneThatWaits` now starts with Claude Code on a third account, since a switch to the account it is on no longer waits.
  - `MilestoneFourSettingsTests`: `PickingAProfileAsksForClaudeCodeToFollowItEachTime` and `APickThatCouldNotBeSavedAsksForNoSwitch`.
  - `PopoverWindowTests`: `AWaitingSwitchOfClaudeCodeIsShownWithItsCancelWhicheverProfileIsShown`. `AccessibilityTests` now audits the popover with a waiting switch, so its Cancel Switch button is checked too.
  - `FixtureModeTests`: `AClaudeCodeIsSeenRunningOnlyWhileItsMarkerFileExists`.
  - A mutation check broke fourteen guards in turn, and each was caught: the notification of a picked switch when it is made and when it starts to wait, the report of a wait found at a later attempt, reporting it only once (three ways), the untracked-login message, keeping the flag on a waiting switch and on its later attempt, the redraw after a dropped switch, the settings menu asking at all, asking only after a successful save, asking again for the active profile, and the popover's line.
- **Fixture check (2026-10-13),** with synthetic accounts A and B, driven through UI Automation:
  1. With B in the fixture folder and `claude-running` present, picking Fixture A in the popover's menu showed "Claude Code switches to Fixture A when it closes." with Cancel Switch, below the existing banner ([media/popover-switch-pending-fixture.png](media/popover-switch-pending-fixture.png)).
  2. After the marker was removed, the folder held A's login and account record within 8 seconds, with `mcpOAuth` kept, one encrypted backup and no journal. The line was gone, and Windows' notification history held "Claude Code Account | Fixture data: Claude Code now uses the account of profile Fixture A."
  3. Picking Fixture B with no marker switched at once and was announced the same way.
  4. With the marker back, picking Fixture A and then Cancel Switch in the popover removed the line. After the marker was removed, the folder still held B 8 seconds later.
  5. From Manage Profiles, with the marker present: Activate on Fixture A showed the popover's line, and after the marker was removed the folder held A and the notification followed. Activate on Fixture B, the account Claude Code was on, left nothing waiting.
  6. After your review asked for a notification when a picked switch is queued (and, in a second review, for one queued while another switch ran, covered by the automated test above): with the marker present, picking Fixture B in the popover posted "Claude Code is running, so it keeps its account until it closes. Then it switches to the account of profile Fixture B." After the marker was removed, the folder held B and "Claude Code now uses the account of profile Fixture B." followed. Steps 1 to 5 ran before that change.
- **Found by the fixture check, and fixed:** picking the profile Claude Code was already on, while Claude Code ran, still queued a switch. The popover then said "Claude Code switches to Fixture B when it closes" while Claude Code was on B. The switch would have ended without writing anything once Claude Code closed, because the process check came before the check of the folder's account. The queue now reads the folder first and leaves nothing waiting when it already holds the account. The two tests above failed before the fix.
- **Not checked:** the next-profile shortcut, beyond its one changed call. Nothing of this has run live; your installed copy predates it until it is republished.

## Switch events in Debug Tools (added 2026-10-15)

You asked for this after you could not tell whether a switch to `work`, which you had cancelled the evening before, was still waiting.

- **Why the evidence was missing:** the queue is in memory only, a cancel posts no notification, and a waiting switch is made only once no Claude Code runs. A Claude Code session had run without a break since then, so Claude Code still being on `personal` proved nothing either way.
- **What is logged now:** each change of the waiting switch, and each attempt's outcome, goes to the network log as operation `cliSwitch`.
  - **Method:** `QUEUE`, `CANCEL` (your Cancel Switch), `DROP`, or `RUN` (an attempt that ended the request).
  - **Route:** who asked: `picked`, `automatic`, or `manual` (Use in Claude Code).
  - **Category:**
    - why it waits: `claudeCodeRunning`, or `busy` behind another switch;
    - why it was dropped: `superseded` by a newer request, `profileUnlinked`, `noLongerApplies` for an automatic switch, or `appClosed`;
    - or the attempt's outcome: empty for a switch made, otherwise e.g. `alreadyInUse` or the refusal.
  - **Target profile:** its local ID, in a new field written only for these entries (your choice; plan section 2 and parity difference 33). Debug Tools shows the profile's current name, or the first 8 characters of the ID once the profile is deleted. The export carries the ID.
- **Reading it:** each `QUEUE` ends with a later `CANCEL`, `DROP` or `RUN` for that switch. One without an end is still waiting, and the popover shows it.
  - **An attempt already under way runs on.** If a switch is cancelled or replaced while its attempt runs (as while the attempt waits for the credential lock during a renewal), the attempt is not stopped and may still switch Claude Code. Its `RUN` then comes after the `CANCEL` or `DROP`, and after a new switch's `QUEUE`, so the account change is never hidden.
  - **A crash leaves no end.** An app that stopped without closing (a crash, or ended from Task Manager) has lost its waiting switch, but wrote no `DROP`.
- **Not logged:**
  - retries while Claude Code still runs, which change nothing;
  - the settlement at start-up of a switch an earlier run left unfinished.
- **Tests:** 5 new (one with two cases), with log checks added to 4 existing switcher tests; 1,398 pass, none skipped, with no build warnings.
  - The new ones are `EveryChangeOfTheWaitingSwitchIsLogged`, `AnAttemptThatSwitchesAfterItsSwitchWasCancelledOrReplacedIsStillLogged`, `AWaitingSwitchToADeletedProfileIsLoggedAsDropped`, `AProfileIdIsWrittenOnlyForAnEntryAboutAProfile` and `DebugToolsNamesTheProfileOfASwitchEntry`.
  - A mutation check removed each of the seven log calls in turn; each removal was caught.
  - Your review found that the first version left out the `RUN` of an attempt whose switch was cancelled or replaced while it ran, although that attempt still switched Claude Code. `AnAttemptThatSwitchesAfterItsSwitchWasCancelledOrReplacedIsStillLogged` failed in both cases before the fix: the account changed, and the log showed only the `CANCEL` or `DROP`.

## Hook gate and status line on Claude Code 2.1.312 (added 2026-10-27)

The plan asks for the hook gate to be measured again when the CLI changes. Claude Code had updated itself from 2.1.288, the version Milestone 5 measured, to 2.1.312. The same [HookBench](../spikes/HookBench) runs were repeated on 2026-10-27: the real CLI against the local fake Messages API, with an isolated config folder and a dummy key, so no account was used and no usage consumed. The changelog entries of the 22 releases in between mention no change to the hook or status line settings the tracker writes; 2.1.295 added an opt-in `onFailure: "block"` for hooks, which the tracker does not set, so a failing hook still lets the action through.

**G5, the hook gate** (3 runs of 25 tool calls per condition, all successful). The run without hooks took 24.1 ms p50 and 38.8 ms p95 per tool call, more than on 2.1.288 (9.8 and 20.3 ms); the hooks' own share stayed where it was, far inside the 100 ms budget:

| Condition | p50 | p95 |
| --- | ---: | ---: |
| Benchmark listener up | +3.4 ms | +6.6 ms |
| Listener stopped | +3.7 ms | +4.9 ms |
| Listener restarting | +3.3 ms | +2.1 ms |

With the listener up each run delivered UserPromptSubmit 1, PreToolUse 26, PostToolUse 25, PostToolUseFailure 1, Stop 1 and SessionEnd 1, and a PreToolUse event reached the listener 3.0 ms p50 after the response. SessionStart is still skipped for HTTP hooks.

**G6 and G7, an interactive session.** Notification arrived for a permission prompt and, after a minute idle, as `idle_prompt`; SessionEnd arrived at `/exit`; a settings edit had taken effect by the next prompt, 3.5 s later, and a settings file without hooks delivered none.

**D3, the status line.** Claude Code still runs the command as `bash.exe < bash.exe < claude.exe`. Of nine probe runs, five were ended by a newer update before they finished, and none overlapped the next. The production helper's line was drawn (`work │ Haiku 4.5 │ Ctx: 0% │ Usage: ~`: the harness folder had no cache, so no usage). Its timing (`time-statusline`, 50 warm runs per case):

| Runner | p50 | p95 |
| --- | ---: | ---: |
| Direct | 13.8 ms | 14.8 ms |
| Git Bash (Claude Code's) | 40.2 ms | 43.0 ms |
| `cmd /d /s /c` | 24.2 ms | 25.4 ms |
| PowerShell 5.1 | 173.3 ms | 180.0 ms |
| First run of a fresh copy, direct | 42.4 ms | 43.7 ms |

Every runner but PowerShell is inside the targets (warm p95 under 100 ms, first run under 300 ms), as in Milestone 5.

Run the harness through its apphost, `HookBench.exe`. Started as `dotnet HookBench.dll`, it writes its own path, `C:/Program Files/dotnet/dotnet.exe`, unquoted into the probe's status line command; Git Bash splits it at the space, Claude Code logs status 127, and the probe never runs, which looks like a CLI that no longer runs status lines.

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
- **The start-up fix was seen live on 2026-10-27.** The installed copy (`176374c`) started at 15:39:44 UTC, and its first claude.ai check answered 200 for both profiles within a second. The one 403 was one profile's `claudeAiCreditGrant`, which that profile gets on every check since its sign-in and which only hides its extra-usage balance.
- **Explorer's tray records** of moved or removed copies stay in the registry, and may stay listed under Settings → Personalisation → Taskbar → Other system tray icons.
- **Claude Code 2.1.312** is installed now (2026-10-27; 2.1.289 on 2026-10-05). The hook gate, hook delivery and the status line were measured again on it on 2026-10-27 ([above](#hook-gate-and-status-line-on-claude-code-21312-added-2026-10-27)); later versions are not measured until the harness is run again.
- **Whether the Console replaces its session cookie** has not been seen live; defects 7 and 8 rest on automated tests.
- **A pasted Codex login that was renewed outside the tracker** (by the Codex CLI on another PC, or an old copy of `auth.json`) cannot be recognised. Its first renewal sends a used token. Defect 4 covers only copies the tracker itself holds.
- **Cancel Switch does not stop an attempt already under way** (left as it is by your decision of 2026-10-15, as too rare to fix).
  - **When:** only in the seconds after the last Claude Code closes. While Claude Code runs, every attempt stops at once without changing anything, so a cancel then always holds. An attempt normally takes milliseconds. It lasts seconds only while it waits for the credential lock (as during a renewal in a usage check), or renews the target profile's saved login.
  - **What happens:** a Cancel Switch pressed during that time removes the waiting switch from the popover, but the attempt still writes the target's login into Claude Code's folder. No notification announces it. Debug Tools shows `CANCEL`, then `RUN` for that profile ([Switch events in Debug Tools](#switch-events-in-debug-tools-added-2026-10-15)).
  - **A newer pick instead of a cancel** corrects itself: the old attempt still writes, and the next check, five seconds later, switches to the newer pick.
  - **The fix, if ever wanted:** the check that automatic switches already make under the lock, just before writing ("is this switch still wanted?"), would be made for picked and manual switches too. It would ask, from the switch's thread, whether the switch is still the one waiting.
- **A renewal that no profile could save before the tracker quits is lost,** for pasted Codex logins as for Claude Code ones (plan section 2): the next start has only the used token. The tracker tries to save it again at every check until then. Once any profile holding the login has saved it, the others are given it from there (defect 4).

## Live checks to do

These need your accounts or your PC, so they are yours to run. Items 1, 2 and 5 passed by 2026-10-27; 3 and 4 remain:

1. **The published build in daily use: passed.** Published, copied to `%LOCALAPPDATA%\Programs\ClaudeUsageTracker` and in daily use since 2026-10-06, republished there with `176374c` on 2026-10-13 ([personal-use.md](personal-use.md)). Notifications appear from it (the monitoring of 2026-10-14/15 saw reset and rejected-login notifications; a click on one of its notifications is not recorded). The first claude.ai check after a start succeeds: on 2026-10-27 the copy started at 15:39:44 UTC and its first check answered 200 for both profiles within a second (Debug Tools). On 2026-10-27 the per-user installer built from `82db616` (labelled 1.0.0-rc.1) replaced this copy in place: it closed the tracker, replaced the files, registered the uninstaller and restarted the app, and the helper was then updated from Settings → Claude CLI. Both passed.
2. **Its tray icon and launch at login: passed.** The installed copy has its own tray icon and starts with Windows: on 2026-10-13 it was started at sign-in and ran the Codex auto-start from there (item 5).
3. **The open checks of earlier milestones,** in [parity.md](parity.md#verification-limits):
   - the Console
   - refresh failure in the two-account check
   - an auto-switch at a real 100 % and a threshold alert on real usage
   - remote desktop
   - a switch beside a launcher-only session
   - a helper upgrade while Claude Code is using the helper (the upgrade itself ran on the installed copy on 2026-10-27, with nothing using the helper)
4. **A clean PC** (D2), if you have one or a virtual machine: copy the published folder there, start it, set up a profile and confirm that a notification shows.
5. **The Codex auto-start: passed,** on the evening of 2026-10-06 (see above) and over the following days, which showed one start per new window and none while Codex was in use:
   - On 2026-10-13 the installed copy, started with Windows, ran it at 15:15:28 UTC with status 0, and Codex's weekly window then reset at 2026-10-20T15:15:16Z. That 5-hour window ended at 20:15:16. Your own Codex use at 20:16:41 came before the next five-minute check, so it started the following window itself.
   - **Overnight monitoring, 2026-10-14 21:47 to 2026-10-15 20:08 UTC** (installed copy with `176374c`, read-only hourly checks, no sleep). Every start that came due passed: four each for both claude.ai profiles and for Codex. Each claude.ai start came under 2 minutes after its window ended (201, 200, 204). Each Codex start came 5 minutes after its window's scheduled end, with status 0 in 3.8 to 5.9 s, and the reset then stayed put. Codex was read at least every 38 seconds throughout. Two defects came to light, 12 and 13 above.

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
