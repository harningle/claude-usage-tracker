# Milestone 3 log

Work and evidence for [plan.md Milestone 3](../plan.md#milestone-3---browser-setup-profiles-codex-billing-and-history): browser and Console setup, profiles, Codex, credential ownership with the guarded takeover, billing, history and the network log. Upstream behaviour comes from the pinned upstream source (`588775e`); each departure is recorded in [parity.md](parity.md). Evidence types follow parity.md: automated tests, manual checks in fixture mode, and live-account checks, kept separate.

Recorded 2026-10-01 on the development machine (Windows 11 26200, WebView2 Runtime 154.0.4258.48, Claude Code 2.1.286).

## Status

The user ran the first live checks on the evening of 2026-10-01 (see "Live checks so far"). The two checks that need the Claude Code login to expire, the waiting message and the idle takeover, are still to do, after the Milestone 2 overnight soak.

| Plan item | Status | Evidence |
| --- | --- | --- |
| Browser and manual-key wizard routes, organisation selection, separate Console sign-in | Implemented: claude.ai sign-in in the profile's own WebView2 context or a pasted key tested there; Console sign-in in a throw-away context whose cookie is kept encrypted; organisation choice; test and review before every save | Automated (`SetupWizardViewModelTests`, `SetupServiceTests`, `ClaudeAiClientTests`, `ConsoleClientTests`); manual in fixture mode; signed-out WebView2 smoke check; live: claude.ai sign-in reported good by the user on 2026-10-01. **The Console has not been checked live** (skipped for now) |
| Reconnect and expiry handling | Implemented: a signed-out or other-account claude.ai session, an expired Console session and a renewed Console cookie are each reported or saved; a rejected session is not asked again until it changes | Automated (`RefreshCoordinatorTests`, `ConsoleClientTests`, `PopoverMilestoneThreeTests`). **Live not checked** |
| Profile lifecycle, independent settings, simultaneous monitoring, Codex sections, no borrowed CLI login | Implemented: create, rename, activate, delete; multi-profile tray; each displayed profile polled in its own loop with its own credentials | Automated (`ProfileServiceTests`, `RefreshCoordinatorTests`, `TrayModelTests`); manual in fixture mode with three profiles; live: two Claude profiles and a Codex profile reported good, including the popover position after its fix |
| CLI-linked / app-owned / unresolved ownership, one credential lock, guarded idle takeover with write-back, atomic secrets, recoverable removal, process attribution and store guard | Implemented; takeover is on (plan section 2). Until launchers exist (Milestone 5), any running `claude.exe` blocks every takeover | Automated (`CliOwnershipTests`, `CliProcessGuardTests`, `ProfileSecretsTests`, `CliLinkServiceTests`, `ProfileServiceTests`). **Idle refresh and hand-back not checked live; the refresh request shape is upstream's and unverified** |
| Monthly, daily, per-key and per-model costs; usage and billing history; interactive charts; CSV/JSON export | Implemented | Automated (`ConsoleBillingTests`, `UsageHistoryTests`, `HistoryViewModelTests`, `ViewControlTests`); manual in fixture mode (screenshots below); live: export reported good. **Console billing not checked live** |
| Redacted network log view with clear and export | Implemented; always on, allowlisted fields only | Automated (`RequestLogTests`, plus "no secrets in files" tests); manual in fixture mode |
| G4: three browser profiles polling at once | **Measured**: about 180 MB in total for the WebView2 processes with three claude.ai profiles | Live, by the user |

**Acceptance not yet met.** The automated and fixture evidence is in place, and most live checks passed on 2026-10-01. Still to do: the waiting message and the idle takeover (after the Milestone 2 overnight soak), refresh failure in the two-account check, and the Console, which the user has skipped for now. Everything below that says "fixture" used synthetic data only.

## What was built

New and changed parts of `src/ClaudeUsageTracker.App`, in outline:

- **Secrets** (`Storage/ProfileSecrets.cs`, `CredentialLock.cs`): one bundle per profile, version 2 (`claudeCli` with ownership and any pending write-back, `console`, `codex`), encrypted with DPAPI and replaced as a whole through the existing `SecretStore`. Milestone 2's version 1 copy is read and upgraded. Unknown members are kept. Removing the last credential removes the file. Every change to any bundle or Claude Code file happens under one app-wide asynchronous lock.
- **Ownership and takeover** (`ClaudeCode/CliLinkService.cs`, `CliProcessGuard.cs`, `Interop/ProcessTable.cs`):
  - A profile follows an account, not a folder. While a known Claude Code folder holds the account, its login is Claude Code's: read through and mirrored.
  - When the linked folder's token has expired, the tracker renews it only when no Claude Code process could be using any folder that holds the same token. Every known folder must be readable, the login must not be marked "used elsewhere", and the file must not have changed since the check (re-read under the lock). The renewed login is saved first, with a pending write-back record, then written to every folder still holding the used token, merged into the file and verified by reading it back.
  - A folder that was signed out, or now holds a newer login, is left alone. A used or rejected refresh token is never sent again. A failed renewal waits five minutes. A renewed login that cannot be saved stays in memory, and nothing else is renewed until it is saved.
  - When no known folder holds the account any more and nothing is in doubt, the copy becomes the tracker's own ("app-owned") and the tracker renews it itself.
  - The process guard reads a Toolhelp snapshot: owner, executable path and creation time only, never a process's environment. A `claude.exe` belongs to a profile's folder only when its live parent is an app-owned launcher image created before it. Otherwise it counts against the default folder and every folder no launcher owns. Unknown details count as in use. The launcher manifest is empty until Milestone 5.
- **Sources** (`Usage/UsageSource.cs`, `UsageSources.cs`): one interface, three sources:
  - Claude Code (read-only `GET /api/oauth/usage`, as before).
  - claude.ai (`ClaudeAiClient` over the profile's browser): usage, extra-usage limit and credit grant, all same-origin GETs.
  - Codex: the Codex CLI's `auth.json` is read through and never renewed. A rejected token waits for the CLI. Pasted credentials are the tracker's own; they are renewed once on rejection, saved, and the request is retried once.
  
  Console billing (`ConsoleClient` over `HttpClient` at `platform.claude.com`, the session cookie set per request) is a separate step of the same loop, at most every five minutes unless refreshed by hand.
- **Browser** (`Browser/BrowserSessions.cs`, `BrowserSignIn.cs`): one WebView2 user-data folder per profile under `browser\<profile>`. The background view has no window (a controller parented to the message-only window) and is parked on `https://claude.ai/robots.txt`. Requests are allowlisted operations sent as a script `fetch`; only the fixed path enters the script. Sign-in windows close themselves once the site's `sessionKey` cookie appears. If the context is already signed in, they ask whether to continue or use another account. Removing the link, or deleting the profile, closes the browser and deletes its folder after its processes exit.
- **Coordinator** (`Usage/RefreshCoordinator.cs`): unchanged rules (one loop per profile, generations, backoff, `Retry-After`), now over any source plus billing and history. It records history after each successful fetch, and the rate-limit hold is per account. The app polls the active profile and, in multi-profile mode, every displayed profile.
- **History** (`Usage/UsageHistoryService.cs`, `Storage/UsageHistoryStore.cs`, Core `History/UsageHistory.cs`): upstream's sampling (session every 10 minutes, week every 2 hours), retention (1000 and 500), reset snapshots and billing-cycle snapshots, one file per profile. Upstream's JSON and CSV export formats are kept. A damaged history file is reported and never overwritten.
- **Profiles** (`Profiles/ProfileService.cs`): create (random upstream name when blank), rename, activate, show or hide in multi-profile mode (at least one stays shown), delete (never the last one). Removal is recoverable: the settings are saved first with a pending-cleanup entry, and whatever could not be deleted yet is deleted at the next start. Activating only changes which profile is viewed; it never writes Claude Code's files (that is Milestone 4).
- **UI**:
  - Popover: profile switcher menu; extra-usage balance line; Codex plan and credits; upstream's API Credits and API Cost cards, with the cost per day this month and the cost per key or per model.
  - Tray: the API credits metric; multi-profile icons (circles, bars, dots, percent; active-profile underline) whose tooltips carry the profile and values; a click shows that profile.
  - Settings: Claude.ai, API Console, CLI Account (ownership, notices, "used elsewhere"), Codex Account, History (5h/24h/7d/30d, earlier/later/now, pointer read-out, billing chart, JSON/CSV export), Manage Profiles (with multi-profile options) and Debug Tools (network log with clear and export).
  - Wizard: the claude.ai, Console and Codex routes.
  - The charts are drawn by hand (`Views/Charts.cs`); no charting library is added.
- **Fixture mode** (`Diagnostics/FixtureMode.cs`) now also answers claude.ai (`claude-ai-*.json`), Console (`console-*.json`), Codex (`codex-usage.json`) and token requests from files. Its sign-in "succeeds" at once, and its process guard sees no process.

## Automated tests

801 pass (526 at the end of Milestone 2), with no build warnings. New or rewritten suites:

- `CliOwnershipTests`: idle renewal and hand-back to every folder of the lineage; running or unreadable processes, "used elsewhere" and an unreadable folder each block it; `invalid_grant` never resent; failed renewal held five minutes; a hand-back that cannot be written stays pending and is finished after a restart without a second grant; a newer CLI login is never replaced; a signed-out folder is not recreated; two profiles of one login renew it once; app ownership and its own renewal; a renewed login that cannot be saved.
- `CliProcessGuardTests`: launcher attribution, reused parent PIDs, exited and unreadable parents, other users' processes, a failed snapshot, and a live snapshot read.
- `ProfileSecretsTests`: round trip, no plaintext, Milestone 2 upgrade, newer or damaged bundles reported and untouched, unknown members kept, a 1 MB bundle, a failed replacement leaving the old bundle and no temporary file, a crash leftover ignored, wrong-profile decryption, per-profile and per-field isolation.
- `ProfileServiceTests`, `SetupServiceTests` (all four routes), `SetupWizardViewModelTests` (all routes, retries after a fresh sign-in, pasted-key undo), `RefreshCoordinatorTests` (two profiles at once, unwatching, signed-out browser sessions, billing spacing, billing failure, history, damaged history, Codex read-through, no secrets in any plain file).
- Clients: `ClaudeAiClientTests` (classification, challenge never an authentication verdict, organisation IDs never reach a URL unchecked), `ConsoleClientTests`, `OAuthTokenClientTests`, `RequestLogTests`, `CodexUsageSourceTests`.
- Views: `PopoverMilestoneThreeTests`, `TrayModelTests`, `HistoryViewModelTests` (the export equals the stored history), `FixtureResponseTests`, `ViewControlTests`, and `PopoverWindowTests`, which opens the real popover for a tall, a short and again a tall profile, as tray clicks do, and checks each placement (it fails on the code before defect 4 was fixed; the popover flashes briefly on the primary monitor while it runs). The WPF tests run on their own, never alongside other tests.
- Core (from the first part of this milestone): `ConsoleBillingTests`, `UsageHistoryTests`, `ClaudeAiResponsesTests`, `ClaudeOAuthTests`, `CodexOAuthTests`.

The upstream cases scheduled for this milestone are mapped in [parity.md](parity.md#upstream-test-inventory). The timing-sensitive suites (coordinator, ownership, wizard, Codex source) passed in three further consecutive runs.

## Defects found during checking

1. Opening Settings ended the app with `XamlParseException: The type initializer for 'ClaudeUsageTracker.App.Views.HistoryChart' threw an exception`, seen in fixture mode (and by the user, as an error window). Two date properties were registered with a null default. Fixed; `ViewControlTests` now initialises every control type and renders the charts, and fails on the original code.
2. After any edit in Settings, screen readers (and UI Automation) could no longer reach the profile list's buttons. List rows were records compared by value, so WPF matched rebuilt rows to containers that no longer existed. List items now compare by reference (`ListItem`), with a test.
3. A pending hand-back finished at startup was followed by a false "unreadable" state for one poll, because the folder was read before the hand-back wrote it. The folder is now read again after a hand-back (caught by `CliOwnershipTests`).
4. In multi-profile mode, a popover whose height differed from the previous one was placed by the previous height: it floated above the taskbar or ran into it (found by the user in the live check). Opening it read the window's size before WPF had resized it for the new profile's cards. It is now placed by the measured height of the content it is about to show, and on resize by the new layout size. The user's retest passed, and `PopoverWindowTests` covers it. See "Live checks so far".
5. While writing that test, a draft created the app's own `App` object inside the test process. WPF queues an application's startup when it is created, so the app's real startup ran in the test host. Because the user's tracker was running, the draft only asked it to open its popover (five times, shortly before midnight on 2026-10-01) and then stopped; without a running tracker it would have started a real tracker polling the real account inside the test run. The test now uses a plain WPF application object with only the app's styles, and the app's startup now refuses to run unless the tracker itself is the program being run.

## Code review

The user reviewed the committed milestone three times on 2026-10-02. Each defect was reproduced and now has a test that failed before its fix. The first review found three defects; fixing the first exposed two more of the same kind.

1. **[P1] Two concurrent takeovers could send one refresh token twice.** Both checks passed the used-token check before taking the credential lock. When the first renewal's hand-back failed, Claude Code's file still held the token, so the second check, re-reading the file under the lock, sent it again. The used and refused tokens are now checked again under the lock (`CliOwnershipTests.ConcurrentTakeoverNeverReusesATokenWhenTheHandBackFails`).
2. **[P1] Renewed Codex credentials that could not be saved were never saved.** They stayed in memory, blocking further renewals, and a restart lost them. Each check now retries the save under the credential lock and reports the failure until it succeeds (`CodexUsageSourceTests.RenewedCredentialsThatCouldNotBeSavedAreSavedLater`).
3. **[P2] A refused Codex refresh token was sent again at every check** once pasted credentials reached the eight-day renewal age. A refused token is now remembered until the credentials change (`RejectedRefreshTokenIsNotSentAgainUntilTheCredentialsChange`). In the same way, a Codex renewal that got no usable answer is now retried only after five minutes, as for Claude Code (`FailedRenewalIsTriedAgainOnlyAfterFiveMinutes`).
4. **[P1, found while fixing 1] Two profiles owning copies of the same account's login.** When one renewed, the other kept the used token. Only the in-memory list of used tokens stopped it, so after a restart it would have sent that token, which can make the login service revoke the login. A renewal is now given to every other app-owned copy holding the token it used (`ProfilesOwningTheSameAccountShareOneRenewal`).
5. **[P1, found while fixing 1] A restart before a pending hand-back finished.** Another profile linked to the same folder could take over first and send the token the pending renewal had used, because the in-memory list was empty. Before renewing, the tracker now also checks every profile's saved pending hand-back records, which survive a restart (`AfterARestartATokenAnotherProfileIsHandingBackIsNotSentAgain`).

The second review found three more, and fixing the second exposed one more:

6. **[P1] A renewal did not reach a profile still marked as Claude Code's.** Two profiles shared a login and Claude Code signed out. When only the first had been checked, only it became the tracker's own and renewed, and item 4's sharing skipped the second. That profile kept the used token: it stalled for the rest of the run and would have sent the token after a restart. A renewal is now given to every other profile's copy holding the used token, whatever its ownership; each keeps its own ownership until its next check settles it (`RenewalReachesAProfileThatHasNotYetSeenTheLoginBecomeTheTrackersOwn`). A profile linked to a folder that is still waiting for its hand-back now polls with the renewal it was given instead of waiting.
7. **[P1] A partial hand-back allowed another renewal and lost the folder still waiting.** Two Claude Code folders held the token, and the hand-back reached the linked folder but not the other. At the next expiry the linked folder's login was renewed again and the new pending record replaced the old one, so the other folder kept a used token for good: two grants instead of one. Mirroring the linked folder could also drop the record. Nothing in a lineage with a pending hand-back, saved by any profile, is now renewed until every folder has it, and mirroring the same account keeps the record (`NoFurtherRenewalWhileAFolderOfTheLineageStillAwaitsItsHandBack`).
8. **[P1, found while fixing 7] A pending hand-back could pass on a token Claude Code had already used.** If Claude Code renewed the linked folder's login itself before the other folder was free, the hand-back wrote the tracker's earlier renewal there. The hand-back now waits until the copy holds Claude Code's newer login and passes that on (`PendingHandBackPassesOnALoginClaudeCodeRenewedMeanwhile`).
9. **[P1] An unsaved Codex renewal could overwrite credentials pasted later.** After a renewal failed to save, pasting another account's credentials did not discard it, so the next check saved the old account's renewal over the new one. A renewal is now kept only while the saved credentials are still the ones it renewed; credentials pasted or removed meanwhile win (`CodexUsageSourceTests.CredentialsPastedAfterAnUnsavedRenewalAreNotOverwritten`).

The third review found two more:

10. **[P1] An app-owned copy skipped every later ownership check.** Once a copy had become the tracker's own, each check went straight to renewing it, without looking at the other known folders or the "used elsewhere" setting. It was renewed after the user marked the login as used elsewhere, and after its token was put back into another known folder with Claude Code running there. Ownership is now settled again at every check: the copy goes back to Claude Code (and is saved as linked) when a known folder holds the account again or the login is marked as used elsewhere, is not renewed while another folder is in doubt, and is renewed only if it is still app-owned under the credential lock (`OwnedLoginMarkedAsUsedElsewhereIsNotRenewed`, `OwnedLoginBackInAnotherKnownFolderIsClaudeCodesAgain`).
11. **[P1] An unreadable pending record allowed a used-up token to be sent again after a restart.** The check for pending hand-backs skipped a profile whose saved copy could not be read. With the first profile's file locked after a restart, another profile mirrored the used-up token from Claude Code's file and renewed it. A saved copy of any profile that cannot be read now blocks every renewal, with its own notice in the CLI Account settings ("Another profile's saved login could not be read, so the tracker will not renew any login until it can.") (`UnreadableCopyOfAnotherProfileBlocksEveryRenewal`).

Known limits, not changed: unlinking or relinking a profile drops its pending hand-back, so a folder still waiting keeps the used token until Claude Code signs in again there. A renewed login that cannot be saved, whether the profile's own or one shared with another profile, is kept in memory and saved at the next check; a restart before then loses it.

## Manual checks in fixture mode

All data is synthetic: `fixture@example.com`, a `fixture-chatgpt` Codex account, made-up tokens and canned responses. Checked on 2026-10-01 through UI Automation and screen captures (`--software-render`, as in Milestone 2).

- Three profiles (Claude Code, claude.ai, Codex) in multi-profile mode were all polled from the first second, each through its own source; the request log showed one loop per profile and the status check for the viewed one.
- The Console route from Settings: sign in (fixture), one organisation offered and preselected, review "$12.34 spent, $87.66 remaining", Done. The page then showed the organisation and live billing, and the key was in neither `settings.json` nor any other plain file.
- The popover then showed upstream's API Credits card and the API Cost card with the daily chart and the two keys, expandable to their models: [media/popover-api-fixture.png](media/popover-api-fixture.png).
- The claude.ai route: sign in (fixture), two organisations, review with the usage source, Done; the Claude.ai page then noted that the claude.ai session takes precedence over the profile's Claude Code login, as upstream.
- History drew the session area and dashed weekly line, the range label, the navigation and the billing bars: [media/history-fixture.png](media/history-fixture.png).
- Manage Profiles: created "Fixture New", activated it (the sidebar followed), deleted it after the confirmation; no file of it remained and no cleanup was pending. On restart with an empty active profile the wizard opened, as upstream.
- Debug Tools listed the requests (operation, method, fixed route, transport, status, duration) and nothing else.
- A scan of every plain file under the fixture's data folder found none of the synthetic tokens or keys.

## Signed-out WebView2 smoke check

A throw-away harness (not part of the solution) used `BrowserSessions` with a fresh user-data folder and no cookies, on 2026-10-01:

```text
runtime 154.0.4258.48
organizations: status=403 type=application/json cfMitigated=False bodyChars=214 error= ms=799
classified: organizations= failure=CredentialsRejected status=403
usage of a made-up organisation: FetchFailed CredentialsRejected
folder exists before clear: True
cleared=True folder exists after: False
```

So the windowless background view loads, the parked same-origin `fetch` reaches claude.ai, a signed-out session is a JSON 403 classified as rejected credentials (not data, not a challenge), and clearing removes the folder after the browser exits. No account was involved.

## Open questions

- **The Claude Code refresh request is unverified.** It is upstream's form-encoded `refresh_token` grant to `https://platform.claude.com/v1/oauth/token` with Claude Code's public client ID. The first live takeover is its test (live check 3). If the endpoint answers with anything but a token or `invalid_grant`, the tracker keeps waiting and retries every five minutes without touching Claude Code's files.
- **Takeover is on by default.** The plan asks for it; the guard is conservative (any running Claude Code blocks it). It runs only when a linked login has expired while no Claude Code runs.
- **Codex takeover is deferred.** The Codex CLI's `auth.json` is never renewed by the tracker (plan section 2).
- **G4 memory.** Three claude.ai profiles each keep a background browser. The cost is measured in live check 8.
- **Extra usage from two sources.** claude.ai's `overage_spend_limit` now feeds the Extra Usage card for browser profiles, as upstream. Comparing it with the CLI endpoint's `spend` (Milestone 2's open question) needs an account with extra usage enabled.

## Live checks so far

Run by the user on the evening of 2026-10-01 with this build, two Claude profiles and one Codex profile, a Claude Code window kept open so that no renewal could happen. The Milestone 2 data folder was backed up first, for the overnight soak.

- Reported good: the existing profile (check 1), claude.ai sign-in and usage (check 4), Codex (check 6), several profiles at once with switching and restart (check 7, after the popover-position fix below), export (check 9), removal (check 10) and the scan for secrets in plain files (check 11).
- Skipped for now: the Console (check 5).
- G4 (check 8): about 180 MB in total for the `msedgewebview2.exe` processes with three claude.ai profiles polling.
- **Popover position in multi-profile mode: failed, fixed, retest passed.** Clicking another profile's icon put the popover at the previous popover's top. A recorder of the popover's window rectangle (physical pixels; work area bottom 2040, so the correct bottom is 2010) showed:

  ```text
  Jazzy Jaguar (Claude)            left=2775 top=1280 bottom=2017
  codex, switched while open       left=2615 top=1273 bottom=1889   floats 121 px above the taskbar
  codex, from closed               left=2615 top=1394 bottom=2010   correct (same height as before)
  Jazzy Jaguar, from closed        left=2775 top=1394 bottom=2131   runs 121 px into the taskbar
  ```

  The horizontal position followed each icon (about 160 px apart), but the vertical position used the previous profile's height (defect 4). After the fix, clicking the fixture app's icons in the same order (tall, short, tall, each from closed) put every popover centred on its own icon with its bottom at 2009-2010, and the user's retest with the live profiles passed.

## Live checks to do

These touch the real accounts, so they are the user's to run. Before starting:

- **Let the Milestone 2 overnight soak finish first**, from its preserved build (`%LOCALAPPDATA%\ClaudeUsageTracker.Build\m2-soak-b886ae4\ClaudeUsageTracker.App.exe`). This build upgrades the saved login copy to the new bundle format; Milestone 2 cannot read that format and would then report its copy as unreadable (it keeps polling from Claude Code's files, and never overwrites the copy).
- Quit any running tracker (both builds use the same single-instance lock), then start this build: `dotnet run --project src/ClaudeUsageTracker.App`.
- Settings → Debug Tools shows every request the app makes, including any renewal (`claudeTokenRefresh`).

1. **Existing profile.** The Milestone 2 profile appears unchanged with its usage. Settings → CLI Account shows "Renewed by: Claude Code".
2. **Waiting while Claude Code runs.** Leave an interactive `claude` session open until its login expires (Settings → About → Run Setup Wizard → Anthropic shows the expiry on the folder's line; close the wizard without saving). Expected: "Waiting for Claude Code to refresh its login", labelled stale usage, and no `claudeTokenRefresh` in the log. Using Claude Code renews its login; the next poll picks it up.
3. **Idle takeover (the refresh request's first live test).** Close every Claude Code window and let the login expire with the tracker running. Expected: one `claudeTokenRefresh` (status 200), usage stays current, and afterwards `claude` works without `/login`. If the log shows repeated failures instead, note the status code; Claude Code's own login is untouched in that case. If `claude` asks you to sign in after a successful renewal, report it at once: that would mean the hand-back failed.
4. **claude.ai.** Create a second profile (Settings → Manage Profiles), activate it, then Settings → Claude.ai → Sign in to Claude.ai. Sign in within the window, choose the organisation, review, Done. Compare with claude.ai's usage page. If extra usage is enabled on that account, check the Extra Usage card and balance line.
5. **Console.** On a profile, Settings → API Console → Configure API Tracking → sign in at platform.claude.com → organisation → review → Done. Compare API Credits and API Cost (total, per day, per key) with the Console's billing and usage pages.
6. **Codex** (only if the Codex CLI is installed and signed in). Run the wizard → OpenAI Codex. Expected: the detected account, a review with the plan, and usage. The tracker never changes `~\.codex\auth.json`.
7. **Two accounts and switching.** With two profiles on different accounts (for example the CLI account and a claude.ai sign-in with another account), turn on Settings → Manage Profiles → Show Multiple Profiles. Expected: one icon per profile with its own values, and a click shows that profile. Switch the active profile from the popover's name menu. Quit and relaunch: each profile shows its own usage again. Disconnect the network briefly: each profile reports the failure on its own and recovers on its own.
8. **G4.** With three claude.ai profiles displayed, note the total memory of the `msedgewebview2.exe` processes in Task Manager (Details tab, add the "Memory (private working set)" column) after ten minutes.
9. **Export.** History → Export as CSV and Export as JSON; spot-check a few rows against the chart.
10. **Removal.** Remove the claude.ai link of a test profile, then delete that profile. Expected: confirmations, the profile's folder under `%LOCALAPPDATA%\ClaudeUsageTracker\browser` gone, and the other profiles unaffected.
11. **No secrets in plain files.** In PowerShell, this prints only the names of plain files that contain an Anthropic key prefix (expected: none). The `secrets` folder is encrypted and the `browser` folders are WebView2's own storage, so both are skipped:

    ```powershell
    Get-ChildItem "$env:LOCALAPPDATA\ClaudeUsageTracker" -Recurse -File | Where-Object { $_.FullName -notmatch '\\(secrets|browser)\\' } | Select-String -Pattern 'sk-ant-' -List | Select-Object Path
    ```

## Reproducing

From the repository root:

```powershell
dotnet build ClaudeUsageTracker.slnx
dotnet test --solution ClaudeUsageTracker.slnx
# Fixture mode: <folder>\home holds synthetic Claude Code (.claude, .claude.json) and Codex (.codex\auth.json) files,
# <folder>\data the app's data root, and <folder>\responses the canned answers: usage, status, token, claude-ai-organizations,
# claude-ai-usage, claude-ai-overage-spend-limit, claude-ai-overage-credit-grant, console-organizations, console-current-spend,
# console-prepaid-credits, console-usage-cost, console-api-keys, codex-usage, codex-token (each <name>.json, optional <name>.status).
dotnet run --project src/ClaudeUsageTracker.App -- --fixture <folder>
```
