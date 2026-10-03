# Milestone 4 log

Work and evidence for [plan.md Milestone 4](../plan.md#milestone-4---windows-automation-and-safe-cli-synchronisation): notifications, launch at login, global shortcuts, refresh on wake, network and session changes, opt-in session auto-start, profile auto-switching and switching Claude Code's shared login. Upstream behaviour comes from the pinned upstream source (`588775e`): `NotificationManager`, `NotificationSettings`, `GeneralSettingsView`, `LaunchAtLoginManager`, `ShortcutManager` and its views, `NetworkMonitor`, `AutoStartSessionService`, `MenuBarManager` (wake, auto-switch, next profile) and `ProfileManager.activateProfile` with `ClaudeCodeSyncService`. Each departure is recorded in [parity.md](parity.md) (differences 38 to 45). Evidence types follow parity.md: automated tests, manual checks in fixture mode, and live-account checks, kept separate.

Recorded 2026-10-02 on the development machine (Windows 11 26200, Windows App SDK Foundation 2.3.12, Claude Code 2.1.287, auto-updated from 2.1.286).

## Status

| Plan item | Status | Evidence |
| --- | --- | --- |
| Threshold and reset notifications, deduplication, Windows sounds, activation routing | Implemented for every polled profile. Each alert fires once per session window, and that memory survives a restart (`automation.json`). The sound is one of Windows' notification sounds or none. A click opens the popover on the notification's profile | Automated (`UsageAlertsTests`, `AutomationServiceTests`); fixture: both profiles' alerts reached Windows' notification history, once each across a restart, with their profile as the click argument; live: a real notification was posted, and clicks delivered through the app's registered activator opened the popover on the clicked profile in the running app and when the app was closed, and the user clicked one on screen with the same result (see "Live checks so far"). **A threshold alert on real usage has not been seen yet** |
| Launch at login | Implemented: a per-user `Run` entry for this copy of the app. Another copy's entry and Task Manager's "disabled" are reported, never changed | Automated (`LaunchAtLoginTests`, settings tests); fixture: the switch wrote and removed only the fixture's scratch key; live: it wrote this copy's quoted path to the real `Run` key and removed it again, and with it on the app started by itself after the user signed out and in. **Task Manager's "disabled" has not been tried live** |
| Global shortcuts | Implemented: upstream's four actions through `RegisterHotKey`. A shortcut another app holds is shown as such | Automated (shortcut rules and recording tests); fixture: the configured shortcut opened Settings, a shortcut recorded on the Shortcuts page was saved and then triggered a refresh; live: a recorded Toggle Popover shortcut opened and closed the popover from another app, and was cleared again |
| Wake, network and session handling; Explorer restart | Implemented. Resume, the display coming back on, a network becoming available, and a session connect or unlock each check every polled profile soon. That check never resends a rejected login and is skipped within 10 s of a check. A connect redraws the tray icons for the new display; a disconnect or lock closes an attached popover. Explorer restart was already handled in Milestone 2 (`TaskbarCreated`), and shortcuts belong to the app's own window, so they survive it | Automated (`RefreshCoordinatorTests`: `CheckSoonChecksAtOnceAndIsDebounced`, `CheckSoonNeverResendsARejectedLogin`). Live (the user, 2026-10-02): checks resumed on waking from sleep and when the network came back, and after an Explorer restart the icons returned and the shortcut still worked. **A remote desktop session has not been tried** |
| Opt-in session auto-start | Implemented. It is the app's only model request: off by default, only for a profile with a claude.ai sign-in, decided only on fresh usage showing an unstarted window, at most once per window, and waiting 30 minutes after a failure. The conversation is deleted again | Automated (`AutoStartServiceTests`, `SessionAutoStart` parsing); fixture responses exist; live: one auto-start on the user's claude.ai profile on 2026-10-03, all three requests accepted, and claude.ai showed the started session |
| Profile auto-switching | Implemented as upstream: when the active profile reaches 100 %, the next profile with capacity is activated, once per limit. Unlike upstream, a profile with no other to switch to is not marked as switched, so a later check switches once another profile's window has reset. Claude Code is switched too only when it was on the profile that reached the limit, and that waits while Claude Code runs | Automated (`AutoSwitchTests`, `AutomationServiceTests`) |
| Safe switching of Claude Code's shared login | Implemented as a separate "Use in Claude Code" action (your decision, 2026-10-02). Its safeguards: the process guard, encrypted backups, saving the outgoing login first, compare-and-replace of both files keeping every other member, read-back verification, undoing only its own write, and a journal settled at the next start. It waits, cancellably, while Claude Code runs. A login that no profile follows is replaced only after you confirm | Automated (`CliSwitchTests`, 27 cases; `CliSwitcherTests`); fixture: the fixture folder was switched from one synthetic account to the other. Afterwards each profile settled to the right owner: the outgoing account's copy became the tracker's own, and the incoming one Claude Code's again. Live: the CLI Account page reported the linked account in use, with the button disabled. The user then switched Claude Code to a second account and back: each switch waited while Claude Code was open, and `claude` ran as the new account without `/login` |
| D5: the Windows App SDK resource DLL | Built into the app's build and publish | Automated build; fixture and live: `Register()` succeeded in the app, with its name and icon |
| D6: stale notification registrations after a move | Removed at startup: only this app's registrations whose executable no longer exists | Automated (`NotificationRegistrationsTests`, on a scratch registry key). Live: no stale registration of the app existed yet, so nothing was removed |

**Acceptance met.** The automated and fixture evidence covers each acceptance point:

- **Each notification and reset fires once,** across a restart too.
- **No profile's refreshes overlap.** There is one loop per profile, and auto-start uses the loop's result for a polled profile.
- **Stale state cannot trigger automation.** Cached usage never raises the fresh-usage event and never decides an auto-start, and a reset alert needs an observation from this run.
- **Running Claude Code sessions are never changed silently.** A switch waits while any `claude.exe` runs.

The user's live checks late on 2026-10-02 (UTC) back the last point (see "Live checks so far"): a switch waited while Claude Code was open, and went both ways once it was closed. Still unverified live:
- an auto-switch at a real 100 %
- a threshold alert on real usage
- a remote desktop connection

Everything that says "fixture" used synthetic data only.

## Decisions

- **Switching Claude Code is a separate action, and waits while Claude Code runs** (your choice on 2026-10-02, recorded in plan.md section 2). Activating a profile only changes what the tracker shows, as in Milestone 3; upstream's activation also wrote the CLI's login. "Use in Claude Code" on the CLI Account page switches the folder the plain `claude` command uses (`CLAUDE_CONFIG_DIR`, or `%USERPROFILE%\.claude`).
  - While Claude Code may be running, the switch waits. It is shown on the page, can be cancelled, and is tried every 5 seconds until no `claude.exe` is left. The queue is in memory, so quitting the app drops it.
  - Auto-switch uses the same queue.
- **An untracked login is replaced only after you confirm** (your choice on 2026-10-02). If the folder is signed in to an account no profile follows, the manual switch asks first, naming the account that would be signed out. The confirmation holds only while that same login is there. Automatic switches never replace such a login.

## What was built

New and changed parts, in outline:

- **Packaging** (`ClaudeUsageTracker.App.csproj`): the Windows App SDK Foundation 2.3.12, self-contained and unpackaged, with the extraction of `Microsoft.WindowsAppRuntime.Insights.Resource.dll` from the 2.5.1 runtime MSIX after build and publish (D5). The app build is now for `win-x64`, so its output folder is `debug_win-x64`. The packages are private to the app, so the test project's host never runs the Windows App SDK's initialisers beyond loading its runtime DLL.
- **Notifications** (`Notifications/`):
  - `ToastNotifier` registers this executable as the notification handler, with the app's name and an icon it draws at startup. Arguments carry only the profile ID and, in fixture mode, a fixture marker.
  - A click on a fixture notification when the app is closed starts nothing: it never starts live mode with the real login.
  - `NotificationRegistrations` removes stale registrations (D6). `NotificationSounds` maps upstream's sound setting to Windows sound events, and the preview plays the file the user's sound scheme gives that event.
  - `AutomationService` turns fresh usage into upstream's alerts:
    - thresholds at 75/90/95 % and custom ones, plus the session reset
    - the profile auto-switch
    - the brief "Usage Updated" after a profile switch or saved credentials
    - the Console session's 24-hour expiry warning
    - the "Notifications Enabled" confirmation

    Its memory is `automation.json`; a damaged file is reported and never overwritten.
- **Core rules** (`Core/Notifications/`): `UsageAlerts` (the threshold and reset rules, deduplicated per session window), `AutoSwitch.FindNext` (upstream's choice of target), and `SessionAutoStart` (when to start, and the reset time read from the completion's event stream).
- **Coordinator** (`Usage/RefreshCoordinator.cs`): a `UsageFetched` event raised only for usage this run fetched, and `CheckSoon()`. `CheckSoon()` is an automatic early check: not a manual refresh, debounced, and it never resends a rejected login.
- **Automation** (`Automation/`): `LaunchAtLogin`, `Hotkeys` (with `Shortcuts`, the rules and Windows-style names), `SystemWatcher` (resume, display on, network, session) and `AutoStartService`.
- **Auto-start requests**: three allowlisted claude.ai requests through the profile's browser, each with a fixed path and body: create a conversation, send the prompt, delete the conversation (`ClaudeAiClient.StartSessionAsync`, `BrowserSessions.AutoStartRequest`). Only a checked organisation ID and a GUID enter a path. The network log shows them as `claudeAiAutoStart*`.
- **Switching** (`ClaudeCode/CliLinkService.Switch.cs`, `CliSwitchRecords.cs`, `CliSwitcher.cs`): the switch, the journal (`cli-switch.json`, non-secret) and the backups (`secrets\backups`, encrypted like the profile secrets, newest 5 kept). The app-level queue retries every 5 seconds.

  Each copy of a login now keeps the account's whole `oauthAccount` record (in the encrypted bundle), so a switch writes Claude Code's own record back. Copies saved before this milestone gain it at their next mirror. Until then a switch writes the identity fields the link knows.

  While a journal exists, nothing in `CliLinkService` mirrors from, writes to or renews in its folder, and no login is renewed anywhere. The journal is settled at startup and every five seconds while it exists. An unclear state waits for you to sign in again in Claude Code and choose Signed In Again (code review, defect 1).
- **Settings**:
  - General gains upstream's Auto-Start Session card, with the disclosure that it uses usage, and the Notifications card (thresholds, custom thresholds, sound with preview, Windows' own notification setting when it blocks them).
  - New app pages: Settings (launch at login) and Shortcuts (recorders).
  - Manage Profiles gains upstream's Auto-Switch card. CLI Account gains "Claude Code's account" with the switch.

## Automated tests

934 pass (801 at the end of Milestone 3), with no build warnings. New suites:

- `UsageAlertsTests`, `AutoSwitchTests`: threshold choice, jumps past several thresholds, once per window, a restart in the same window, a new window after a restart, reset rules, passed reset times, missing values, custom thresholds, the auto-switch target with wrap-round, unknown and passed-reset candidates.
- `AutomationServiceTests`: alert text, sound and profile; once across a restart; the persisted state holds no usage values; a damaged state file is reported and left alone; auto-switch once until the session drops, only for the active profile and only with a target, and again once another profile has room; the Console expiry warning once; "Usage Updated" only right after a user action, removed after two seconds.
- `AutoStartServiceTests`: the three requests in order, using the server's conversation ID; the wait until the new window's reset, also after a restart; no start for an open window, any session use, a missing session or a profile not opted in; one failure notification and a retry only after the wait; nothing sent when the profile is switched off, signed out or deleted while its usage is fetched, or while its conversation is created (which is then deleted unsent); a later profile signed out during an earlier one's check passed over; the polling loop's fresh usage used without a second request; cached usage never decides, nor a success followed by a failed, broken or empty check; a server-requested wait honoured.
- `CliSwitchTests`:
  - the target login and account record written with every other member kept, in their order
  - the outgoing login's latest renewal saved first, and each profile following its own account afterwards
  - waiting while Claude Code runs
  - confirmation of an untracked login, which must still be the same login
  - refusals: already in use, held by another folder, used elsewhere, a pending hand-back, a rejected login
  - an expired target renewed once before the hand-over
  - a change to either file just before its write
  - an account file that cannot be written, which undoes only the switch's own write
  - a switch cut short between its writes, which blocks mirroring and every renewal until it is settled at the next start
  - a newer login after a cut-short switch, left alone
  - backup pruning
  - the cases added after the code review (below)
- `CliSwitcherTests`: the newest request replaces a waiting one, also when it succeeds at once or when an older switch still running ends up waiting, and cancelling clears it.
- `RefreshCoordinatorTests` (new cases): `UsageFetched` only for fresh usage, `CheckSoon` debounce and no resent rejected login.
- `NotificationRegistrationsTests`, `LaunchAtLoginTests`: on scratch keys under `HKCU\Software\ClaudeUsageTracker.Tests`, never the real registrations.
- `MilestoneFourSettingsTests`: shortcut rules and recording (Escape, a modifier alone, Shift alone, duplicates, clearing), custom thresholds, persistence of the new switches, launch at login, and the CLI Account rows (see defect 2).

**Mutation check.** Twelve guards were each removed in turn and their suite run:

- the journal guard
- the conflict check before the credentials write
- saving the outgoing login first
- undoing only the switch's own write
- the confirmation for an untracked login
- saving the alert memory
- "missing is not 0 %" for auto-start
- the auto-start wait
- "cached usage never decides"
- `CheckSoon` not being manual
- "only fresh usage is announced"
- keeping live copies' registrations

Each removal made at least one test fail. Two needed their tests sharpened first: the cached-usage test also lacked a success time, and the first `CheckSoon` mutation was on the other of its two wait paths.

## Code review

An independent review of the switch code on 2026-10-02 found nine defects. Each was confirmed against the code and now has a test that fails without its fix (checked by removing each fix in turn).

1. **[P1] Settling a cut-short switch needed a byte-identical `.claude.json`.** Claude Code rewrites that file every time it starts. So after a crash between the two writes, settling reported a conflict and closed the journal, leaving the target's tokens beside the outgoing account's record. The outgoing profile then mirrored them as its own.

   Settling now compares the `oauthAccount` record only and completes the switch over the file as it is (`ACutShortSwitchIsCompletedAfterClaudeCodeRewroteItsAccountFile`).

   One state stays genuinely unclear: an unknown login beside the outgoing account's record. It may be the target's login renewed by Claude Code, or a new sign-in to the outgoing account, and the files cannot tell the two apart. It stays blocked: nothing is mirrored from that folder and no login is renewed. The CLI Account page asks you to sign in again in Claude Code and choose Signed In Again (`AnUnclearCutShortSwitchStaysBlockedUntilTheUserHasSignedInAgain`).
2. **[P1] The outgoing login was saved from a read made before the target's renewal.** A renewal by Claude Code during that request was lost. The files are now read again after the renewal; if they changed, nothing is written and the switch is tried again (`ALoginClaudeCodeRenewsDuringTheTargetsRenewalIsNotLost`).
3. **[P1] No process check after the renewal's request.** A Claude Code started meanwhile was ignored. The guard now runs again just before the first write, and settling checks it under the lock (`AClaudeCodeStartedDuringTheTargetsRenewalStopsTheSwitch`).
4. **[P1] The wizard could link a folder whose switch was unfinished**, saving one account's tokens under the other's record. That is now refused (`LinkingAFolderWhoseSwitchIsUnfinishedIsRefused`).
5. **[P2] A credentials file that could not be written left a journal behind**, which blocked every renewal until a restart. When nothing landed, the journal is now closed, and the retry timer keeps settling while a switch is unsettled (`ACredentialsFileThatCannotBeWrittenEndsTheSwitchWithNothingChanged`).
6. **[P2] An older waiting switch could undo a newer one.** The newest request now replaces any waiting one (`ANewerSwitchThatSucceedsAtOnceDropsTheOlderWaitingOne`).
7. **[P3] A request made while another switch ran was dropped, and timer ticks could overlap.** Such a request is now queued, and a tick never runs beside a switch or a settlement. A tick that finishes also leaves a newer request in the queue.
8. **[P3] A renewal that got no answer was reported as "waiting for Claude Code"** and retried for ever. It is now its own refusal (`ARenewalWithoutAnAnswerIsNotWaitingForClaudeCode`).
9. **[P3] A `.claude.json` over 64 MB ended the app** with an unhandled exception. It is now an unreadable folder, and nothing is changed (`AnAccountFileTooLargeToBeClaudeCodesIsRefused`).

Not changed: a check already waiting for the credential lock when a switch finishes can briefly treat the switched profile's copy as the tracker's own. Its next check sets it right.

### Second review

A review of the whole milestone on 2026-10-03 found five defects. Each was confirmed against the code and fixed. Four have tests that fail without their fix (checked by removing each fix in turn); the fifth is in `App.xaml.cs`, which has no unit tests, and was checked in fixture mode.

1. **[P1] Auto-start could send its message after being switched off.** Whether the profile was opted in was checked before its usage was fetched, not again afterwards. Switching auto-start off, signing out or deleting the profile during that request still let the message go out. These are now checked again just before the first auto-start request (`NothingIsSentWhenTheProfileChangesWhileItsUsageIsFetched`), and again just before the message (follow-up item 6).
2. **[P1] An older switch could replace a newer queued one.** A request made while an older switch waited for the credential lock was queued, but if the older switch then had to wait for Claude Code, it replaced that newer request, so the retry would switch Claude Code to the account the user had moved away from. Only the newest request is now queued (`AnOlderSwitchThatEndsUpWaitingNeverReplacesANewerRequest`).
3. **[P2] Auto-start trusted a success followed by a failed check.** Any success in the last five minutes counted, even when the polling loop's next check failed, broke or sent nothing. Its latest check must now be the success (`ASuccessFollowedByAnUnsuccessfulCheckNeverDecides`).
4. **[P2] Auto-switch gave up when no other profile had room.** The profile was marked as switched before a target was found, so it stayed put even after another profile's window reset. Upstream does the same; here it is marked only when it switches (`AutoSwitchTriesAgainOnceAnotherProfileHasRoom`; parity.md difference 40).
5. **[P2] A notification click could open the wrong profile.** The click selected its profile only in multi-profile mode, and only when that profile was displayed. With one profile shown, a click on another profile's notification showed the active one. A profile that is not displayed is now activated first, as the profile switcher does. A failed activation is reported and opens no popover.

   Fixture check, 2026-10-03: in single-profile mode with Fixture B active, a click on a notification for Fixture A was delivered through the app's registered activator. Fixture A became the active profile and the popover opened headed "Fixture A".

A follow-up review of the auto-start fixes the same day found two more. Both now have tests that fail without their fix.

6. **[P1] Switching off while the conversation was being created still sent the message.** The message request followed the conversation request with no check in between. Just before the message, the client now asks again, on the UI thread, whether the profile still wants it. If not, it deletes the conversation unsent, and nothing counts as a start or a failure (`NothingIsSentWhenTheProfileChangesWhileItsConversationIsCreated`).
7. **[P2] Signing out a later profile during an earlier one's check could end the app.** The round listed its profiles once at the start and checked them one after another. A profile signed out meanwhile then met a missing claude.ai link, and the exception escaped the timer's handler. Each profile is now checked again when its turn comes (`AProfileSignedOutWhileAnEarlierOneIsCheckedIsSkipped`).

## Manual checks in fixture mode

All data is synthetic: two accounts, `a@example.com` and `b@example.com`, with made-up tokens, a fixture Claude Code folder and canned responses. Checked on 2026-10-02 through UI Automation, with window images rendered from the window itself (`PrintWindow`), so nothing else on the screen was captured. The fixture also set a test shortcut (Ctrl+Alt+Shift+F9, Open Settings) so the check could drive the app through its own hotkey.

1. With B signed in to the fixture folder, the app mirrored B's login and recorded B's 75 % alert. With A signed in after a restart, it mirrored A, and B became the tracker's own: no known folder held it any more.
2. Both alerts ("Fixture A - Usage Info", "Fixture B - Usage Info", each with "Fixture data:" and "You've used 80.0% of your session limit. Resets in 2h 59m") were in Windows' notification history for the app's registration. Each carried its profile ID and the fixture marker as arguments, and B's was not repeated after the restart.
3. The registration had the app's name and its drawn icon, so `Register()` works in the app with the built-in DLL (D5).
4. The fixture shortcut opened Settings. On Shortcuts, recording Ctrl+Shift+U for Refresh Usage saved it, and pressing it sent one usage and one status request.
5. On Settings, launch at login wrote this copy's quoted path to the fixture's scratch key `HKCU\Software\ClaudeUsageTracker.Fixture\Run` and removed it again. The real `Run` key had no entry before or after. The scratch key was deleted afterwards.
6. On profile B's CLI Account page, "Use in Claude Code" wrote B's login into the fixture folder:
   - `.credentials.json` held B's tokens, with the unrelated `mcpOAuth` member unchanged.
   - `.claude.json` held B's own `oauthAccount` record (with its `billingType`), and its other members kept their order.
   - One encrypted backup was taken, and no journal was left.
   - The page then read "Claude Code (…) is signed in to this account".

   After the next check B was renewed by Claude Code again, and A, now in no folder, by the tracker.

Screenshots (fixture data): the General page's auto-start and notification cards, [media/settings-notifications-fixture.png](media/settings-notifications-fixture.png), and the Shortcuts page after recording, [media/settings-shortcuts-fixture.png](media/settings-shortcuts-fixture.png). The CLI Account page is not included because it shows the fixture folder's full path.

## Defects found during checking

1. The CLI Account page's About text pointed to "the button below" for the switch, which sits above it. Reworded.
2. The CLI Account page showed the access-token type, subscription and scopes of whatever login the linked folder held, even when that was another account. That was already so in Milestone 3, but after a switch it is the normal case: profile A's page showed B's subscription. The rows are now shown only while the folder holds the profile's account (`TheFoldersLoginIsShownOnlyWhileItIsTheProfilesAccount`).
3. Auto-start fetched usage itself when a polled profile's last result was older than five minutes, so its request could overlap the polling loop's. A polled profile now uses only its loop's fresh result, and the round is skipped without one.

## Open questions and known limits

- **Auto-start's requests work live.** They are upstream's (`chat_conversations`, `completion` with "Hi" to `claude-haiku-4-5-20251001`, then the delete), sent through the profile's browser; claude.ai accepted this minimal body on 2026-10-03 (201, 200, 204). How the usage endpoint reports a window that has not started is now known: the live Browser profile reported `five_hour` at 0 % with no reset time (2026-10-02), which is what auto-start waits for. If no session percentage were reported at all, the port would start nothing; upstream would have started one.
- **Until Milestone 5's launchers, any running `claude.exe` makes a switch wait**, because nothing can show that it uses another folder. With Claude Code open all day, a switch happens when its last window closes.
- **A copy saved before this milestone without its account record** is written with the identity fields only (account UUID, email, organisation). Claude Code may show less in `/status` until it fetches the profile again. Copies gain the full record the next time they are mirrored.
- **Notification clicks**: a click opens the popover on its profile. With the app closed, a click starts it and then opens the popover. Moved copies' old notifications still do nothing when clicked, as Milestone 1 found; their registrations are now removed at the next start.
- **The Console expiry warning is posted by the running app** during the last 24 hours. Windows does not hold a scheduled notification, so it is missed if the app is not running then.
- **The switch queue is not kept across a restart**, so a waiting switch must be asked for again after one.
- **The real-CLI hook gate**: Claude Code updated itself to 2.1.287. The plan asks for the gate to be measured again when the minimum supported version changes. That is Milestone 5 (G5), and it is noted here only.

## Live checks so far

Run by Claude on 2026-10-02 at the user's request, with this build and the user's real profiles (one Claude Code, one claude.ai, one Codex), while Claude Code was running. Every setting changed for a check was changed back, and the app was quit at the end as it had been before.

- **Polling.** Started normally, the app polled all three profiles: read-only Claude Code usage, the claude.ai requests, Codex usage, and the status. There was no other request.
  - The first claude.ai check after start failed with "network" and the next one, a minute later, succeeded. This is not new: the log shows the same single failure after each Milestone 3 start on 2026-10-01. It is a start-up race in the claude.ai browser route, still to be fixed.
- **Notifications.**
  - Settings → General showed no Windows notification problem.
  - Switching notifications off and on posted upstream's "Notifications Enabled" toast. Windows' history held it with its profile as the argument, and the registration now named the live data folder's icon.
  - A click delivered through the app's registered activator (the path Windows takes after a click on the toast; Milestone 1 verified the shell side with real clicks) did two things:
    - In the running app, it opened the popover on the clicked profile (Jazzy Jaguar) without a second process.
    - With the app closed, Windows started it with `----AppNotificationActivated: -Embedding`, and it opened the popover on the clicked profile (Browser).
- **Launch at login.** Switched on, it wrote this copy's quoted path to `HKCU\...\Run`, with no Task Manager override. Switched off, it removed the entry again.
- **Shortcuts.** Ctrl+Alt+Shift+F9 recorded for Toggle Popover opened and then closed the popover from another app. It was then cleared.
- **CLI Account** (Jazzy Jaguar): "Signed in and readable", renewed by Claude Code, "Claude Code (…) is signed in to this account", and Use in Claude Code disabled, since Claude Code already uses that account. No switch was attempted.
- **Secrets.** No plain file under `%LOCALAPPDATA%\ClaudeUsageTracker`, outside `secrets` and `browser`, contains an Anthropic key prefix or a token field. `automation.json` holds only window reset times and flags. There was no switch journal and no backup.
- **The build's new path.** The app now builds into `debug_win-x64`, a new executable path. So Windows treats its tray icons as new ones, which start in the overflow (plan section 2), and its notification registration is new.

### The user's checks, 2026-10-02

Run by the user with this build between 23:16 and 23:57 UTC, following the steps given in the conversation. All passed except the session auto-start, which is postponed: every account's session had already started, so there was nothing to start. The request log agrees on each point it can show:

- **Shortcut and Explorer restart.** A recorded shortcut toggled the popover. After restarting Windows Explorer the icons came back and the shortcut still worked.
- **Network.** With the network off, requests failed from 23:20:19, then succeeded again from 23:21:30 once it was back.
- **Sleep.** The log is silent from 23:22 to 23:41, and the checks resumed within a second of each other at 23:41:46, on waking.
- **A notification clicked on screen** opened the popover on its profile: the shell's own click, which the earlier check had to simulate.
- **Launch at login.** After signing out and in again, the tracker started by itself; its first check is at 23:55:35.
- **Switching Claude Code's account:**
  - A second Claude Code profile was set up for another account. "Use in Claude Code" waited while a Claude Code window was open and switched once it was closed. `claude` then ran as that account without `/login`, and switching back worked the same way.
  - Two encrypted backups were taken, one for each switch. No journal was left, and no renewal request was sent.
- **The Claude Code profile was not polled from 23:16 to 23:46.** This fits the designed waiting state ("Waiting for Claude Code to refresh its login"): its login had expired while Claude Code was running, so no request was sent. It was not confirmed on screen.
- **claude.ai failures.** The first claude.ai check after each start failed with "network" again (23:16:03, 23:55:35): the known start-up race. One further claude.ai check failed at 23:18:57, cause unknown; the next one succeeded.

### The night of 2026-10-03

The user left the tracker (commit `211376a`) running overnight with auto-start switched on for the claude.ai profile. The request log keeps a limited number of rows, so only 06:00 to 11:37 UTC was left to read.

- **Auto-start: passed.** After the profile's window ended, the tracker sent `claudeAiAutoStartConversation` (201), `claudeAiAutoStartMessage` (200, 3.2 s) and `claudeAiAutoStartDelete` (204) at 07:23:03 to 07:23:08 UTC, once. The user confirmed the started session on claude.ai.
- **Polling held up.** In those 5.6 hours there was no gap over two minutes: claude.ai 665 of 665, Codex 660 with one timeout, and the Claude Code usage endpoint 205 of 205 with no rate limit.
- **The first live login renewal failed: 46 attempts, all HTTP 429.** This was the tracker renewing its own copy, not a takeover of Claude Code's folder: the folder held the other profile's account, so this profile's login was the tracker's own (an app-owned copy since the switch on 2026-10-02). When it expired at 07:45 UTC the tracker asked for a renewal every five minutes, and each got "rate_limit_error" within about 45 ms. The popover showed "Couldn't renew the login".
  - Cause: the `User-Agent` header. Probes with a made-up refresh token (no real credential) got the same 429 with the tracker's `claude-code/2.1.5`, in both form and JSON bodies. Without a `User-Agent`, or with another value, the same requests reached the service, which answered `invalid_grant` as it should for a made-up token. Upstream sends no `User-Agent` on this request; the port had added the usage request's one.
  - Fix: the renewal request sends no `User-Agent` (`ClaudeGrantIsFormEncodedAndParsed` now checks it). None of the 46 attempts reached the service, so the refresh token was not used up.
  - Retest with the fix, the same day: the tracker was restarted at 11:55:14 UTC, its first `claudeTokenRefresh` a second later got 200 (369 ms), and the profile's usage polling resumed at once. Claude Code's own login file was not touched, as it should not be for the tracker's own copy. This is the first successful live renewal; a takeover of Claude Code's folder with its write-back has still not been seen.
  - The retries themselves were a second defect: one request every five minutes without limit, to a service that kept refusing. A renewal the service answers with a failure now doubles the wait each time (5, 10, 20 and 40 minutes, then an hour); one that got no answer at all (offline, a timeout) still waits five minutes, so a network outage does not delay the renewal afterwards. A success starts over. It applies to Claude Code logins and pasted Codex credentials alike (`RenewalBackoffTests`, `RenewalsTheServiceKeepsRefusingWaitLongerEachTime`, `ARenewalWithoutAnAnswerIsRetriedAfterFiveMinutesEachTime`). A refresh by hand does not shorten the wait; restarting the tracker does.
- **Not checked: the waiting message and the idle takeover of Claude Code's folder.** They need Claude Code signed in to the polled profile's account, which it was not.

## Live checks to do

These touch the real accounts or Windows sign-in, so they are yours to run. Checks 3, 4 and 6 passed on 2026-10-02 and check 8 on 2026-10-03 (above), and the click in 1 and the sign-in in 2; the rest remain. Quit any running tracker first, then start this build: `dotnet run --project src/ClaudeUsageTracker.App`. Settings → Debug Tools shows every request.

1. **Notifications** (registration, click routing and an on-screen click passed, above). Keep the app running: when a session passes 75 %, expect one notification "<profile> - Usage Info" with the sound chosen. It should not come again after quitting and restarting. Click it: the popover should open on that profile.
2. **Launch at login** (the entry and a sign-in passed, above). Switch it off again in Task Manager → Startup apps: the page should say so.
3. **Shortcuts** (passed, above). Set one, then restart Explorer (Task Manager → Windows Explorer → Restart): the shortcut should still work and the icons come back.
4. **Sleep and network** (passed, above). Put the PC to sleep for a few minutes and wake it. Debug Tools should show a check within a few seconds of waking, not one at the next interval. Then turn the network off and on: the same.
5. **Remote desktop** (if you use it): connect to this PC by Remote Desktop and back. The icons should be sharp at the new scale, and a check should follow.
6. **Switch Claude Code's account** (passed both ways, above). You need two profiles with Claude Code logins of different accounts. One is the current `claude` login; for the other, sign in to the second account in Claude Code once, run the wizard, then sign back in to the first.
   - Close every Claude Code window. On the second profile's CLI Account page, choose "Use in Claude Code".
   - Expected: "Claude Code now uses the account of profile …". `claude` then works as the second account without `/login`, and `/status` shows it. The first profile keeps polling, renewed by the tracker.
   - With a Claude Code window open instead, the switch should wait, then happen when the window closes.
   - If `claude` asks you to sign in after a switch, report it at once. The backups are in `%LOCALAPPDATA%\ClaudeUsageTracker\secrets\backups`, encrypted for your Windows account.
7. **Auto-switch** (optional): turn it on in Manage Profiles. When the active profile reaches 100 %, the next profile should be activated, with a notification. Claude Code is switched as well if it was on that profile, waiting while it runs.
8. **Auto-start** (passed, above)**: only with your explicit go-ahead, because it uses usage.** On a profile with a claude.ai sign-in, switch on Auto-Start New Session. Once its session has not started for a while, Debug Tools should show `claudeAiAutoStartConversation`, `claudeAiAutoStartMessage` and `claudeAiAutoStartDelete`, a notification, and a started 5-hour window. Report the statuses if any fail.

## Reproducing

From the repository root:

```powershell
dotnet build ClaudeUsageTracker.slnx
dotnet test --solution ClaudeUsageTracker.slnx
# Fixture mode as before (docs/milestone-3.md). New optional responses for auto-start: claude-ai-auto-start-conversation,
# claude-ai-auto-start-completion (the event stream as text) and claude-ai-auto-start-delete. Fixture notifications are real
# Windows notifications labelled "Fixture data"; launch at login writes only HKCU\Software\ClaudeUsageTracker.Fixture\Run.
dotnet run --project src/ClaudeUsageTracker.App -- --fixture <folder>
```
