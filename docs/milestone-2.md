# Milestone 2 log

Work and evidence for [plan.md Milestone 2](../plan.md#milestone-2---first-complete-cli-to-popover-flow-and-setup-wizard): the first complete Claude Code CLI-to-popover flow and the setup wizard. Upstream behaviour comes from the pinned upstream source (`588775e`); each departure is recorded in [parity.md](parity.md). Evidence types follow parity.md: automated tests, manual checks in fixture mode, and live-account checks, kept separate.

Recorded 2026-10-01 on the development machine (Windows 11 26200, two 3840 × 2160 monitors at the same scale, apps in light mode, taskbar in dark mode, Claude Code 2.1.286).

## Status

| Plan item | Status | Evidence |
| --- | --- | --- |
| Single-instance startup, tray lifecycle, popover, sidebar settings | Implemented | Automated (toggle, placement, tray identity, model tests); manual in fixture mode (screenshots below) |
| Setup wizard: provider choice, CLI discovery/linking, connection test, review, save; edit/cancel/retry; non-destructive failures | Implemented for the Claude Code route. Browser sign-in, session keys, the Console and Codex are shown as not yet available | Automated (`SetupWizardViewModelTests`, `SetupServiceTests`); manual in fixture mode |
| CLI file linking plus read-only OAuth route | Implemented | Automated (`CliLinkServiceTests`, `UsageClientTests`); **live check pending** |
| Secure persistence, no plaintext shortcut; mirror CLI-owned credentials; waiting-for-refresh state | Implemented (DPAPI per-profile file holding only `claudeAiOauth`; never refreshes) | Automated (`SecretStoreTests`, `CliLinkServiceTests`, `PopoverModelTests`) |
| Session/weekly/model usage, reset times, pace, service status, disconnected/stale/error states | Implemented; **extra usage is not shown** for CLI profiles (see open questions) | Automated (`ClaudeUsageResponseTests`, `PopoverModelTests`, `UsageDisplayTests`); manual in fixture mode |
| Polling, manual refresh, cancellation, backoff, per-profile coordinator, 30 s default, server retry guidance, late results dropped | Implemented | Automated (`RefreshCoordinatorTests`, `RefreshPolicyTests`), run five times without a flake |
| G2: read-only usage for other account types | Pro and Team verified in Milestone 1; Max and Enterprise still need such accounts | — |
| G3: rate limits and sustained 30-second polling | 429 and `Retry-After` handling implemented and tested with fixtures; **multi-hour live soak pending** | Automated |
| D1: mixed DPI, keyboard selection, anchoring from icon rectangles | Implemented (physical-pixel placement per monitor, `NIN_KEYSELECT` with focus moved into the popover); **manual checks pending** | Automated (`PopoverPlacementTests`) |

**Acceptance is not yet met.** The milestone requires one real Claude account to complete setup, refresh to the UI and survive a restart. That is a live-account check for the user (see "Live checks still to do"). Everything below the status table that says "fixture" used synthetic data only.

## What was built

The app is a tray application (`ShutdownMode.OnExplicitShutdown`) in `src/ClaudeUsageTracker.App`. In outline:

- **Startup** (`App.xaml.cs`): a per-user mutex keeps one instance. A second launch sets a per-user named event and exits, and the running instance opens its popover. Damaged settings are reported in a dialog and the app stops without touching the file. The wizard opens on first launch and whenever no profile is linked, as upstream does.
- **Storage** (`Storage/`): `%LOCALAPPDATA%\ClaudeUsageTracker\settings.json` (versioned, upstream key names, unknown members kept), `secrets\<profile>.secret` (DPAPI `CurrentUser`, owner-only protected DACL, atomic replace, read-back verified) and `cache\<profile>.usage.json` (last usage with its observation time, source and account identity). Polling writes only the cache.
- **Claude Code** (`ClaudeCode/`): an inventory of the default `%USERPROFILE%\.claude`, the app's `CLAUDE_CONFIG_DIR`, linked folders and folders the user chooses. Files are read without locking (`FileShare.ReadWrite | Delete`), a torn read is retried once after 250 ms, and nothing is ever written. Every poll compares the account (`accountUuid`, then email, else an unchanged refresh token) with the linked one. The same account's rotated tokens update the encrypted copy. A different account is reported and never polled or shown.
- **Usage** (`Usage/`): `UsageClient` sends only `GET https://api.anthropic.com/api/oauth/usage` with upstream's headers and classifies network, timeout, 429 (with `Retry-After`), 5xx, challenge (`cf-mitigated` or the HTML challenge page), rejected credentials (401/403) and invalid responses. `RefreshCoordinator` runs one sequential loop per profile. A link change cancels the in-flight check and bumps a generation, so stale results are dropped. Failures back off exponentially to 15 minutes, and a 429 waits at least the server's `Retry-After`, even for a manual refresh. A rejected token is not resent until Claude Code's token changes. An interval change re-times the wait without an extra request.
- **Tray** (`Tray/`): owned `Shell_NotifyIcon` icons on a hidden top-level callback window (so `TaskbarCreated` arrives after Explorer restarts), path-and-slot-derived GUIDs, 127-character tooltips, `NIN_SELECT` / `NIN_KEYSELECT` / `WM_CONTEXTMENU` and a native Refresh / Settings / Quit menu. Icons are drawn per DPI and per taskbar colour mode.
- **Popover** (`Views/PopoverWindow.xaml`): upstream's header, banner and cards at 280 DIP. It is placed in physical pixels on the icon's monitor before it is shown, then re-anchored when its content changes height. It closes on deactivation through the toggle state machine, closes on Escape, and detaches when its header is dragged.
- **Settings** (`Views/SettingsWindow.xaml`): upstream's sidebar with only the sections that work in this milestone: CLI Account, Appearance, General (refresh interval), Popover, About and Quit.
- **Wizard** (`Setup/`): provider choice, detection, a read-only test, review and save. Only Save writes; it re-reads the folder first and refuses a login whose account changed since the test.
- **Text** (`Localisation/`): upstream's `en.lproj/Localizable.strings`, embedded unchanged and looked up by key. Repeated keys take their last value, as CoreFoundation does; upstream's English file repeats five keys. Port-only text lives in `Windows.strings` under `windows.*` keys. A test checks that every key the app uses exists.
- **Fixture mode** (`Diagnostics/FixtureMode.cs`): `--fixture <folder>` uses `<folder>\home` as the user profile, `<folder>\data` as the data root and canned responses from `<folder>\responses`, with no network access, and labels the popover and tooltips "Fixture data". `--software-render` exists only for screen captures (see the rendering note).

## Verified facts

- **Where Windows Claude Code keeps `.claude.json`** (automated probe, 2026-10-01). This was run with a scratch `CLAUDE_CONFIG_DIR`, a dummy API key and `ANTHROPIC_BASE_URL` pointing at a closed local port, so no account and no network were involved. Claude Code 2.1.286 wrote `<dir>\.claude.json`. The default directory's file is `%USERPROFILE%\.claude.json`. The port therefore reads account metadata per directory and never falls back from an override directory to the default file. Upstream does fall back, which can label one account's login with another's email.
- **Upstream's English strings file repeats keys** (`setup.step.enter_session_key`, `wizard.migration_skipped` and three with identical values). Other languages repeat up to 30. The parser takes the last value, as CoreFoundation's does.

## Automated tests

498 pass (191 before this milestone), with no build warnings. New suites: `StringsTableTests`, `AppStringsTests`, `SecretStoreTests`, `SettingsStoreTests`, `UsageCacheStoreTests`, `CliLoginTests`, `CliLinkServiceTests`, `ClaudeUsageResponseTests`, `UsageDisplayTests`, `RefreshPolicyTests`, `ServiceStatusTests`, `UsageClientTests`, `RefreshCoordinatorTests`, `PopoverModelTests`, `PopoverToggleTests`, `TrayIdentityTests`, `PopoverPlacementTests`, `FunnyNamesTests`, `SetupServiceTests` and `SetupWizardViewModelTests`. The upstream cases scheduled for this milestone are mapped in [parity.md](parity.md#upstream-test-inventory). The coordinator tests use simulated time (`Microsoft.Extensions.TimeProvider.Testing`); they and the other timing-sensitive suites passed in five consecutive runs.

## Code review

An independent read-only review of the Milestone 2 code found ten defects. Each was confirmed against the code, fixed and given a regression test where one was practical:

1. A connection lost while a response body was being read (`HttpIOException`, an `IOException`) escaped the usage client and stopped that profile's polling for good, with the spinner left running. It now counts as a network failure, and the coordinator catches any unexpected exception, shows it ("Refreshing failed unexpectedly: …") and keeps polling.
2. An encrypted copy that could not be read (written by a newer build, or damaged) was silently overwritten with a fresh mirror. It is now reported on the CLI Account page and left untouched.
3. A failed setup save could leave an existing profile linked to one account while holding another account's copy. The settings are now written first and the copy second, and the settings are restored if the copy cannot be written.
4. `Retry-After` was honoured only on 429. A 503 or other 5xx with retry guidance now holds polling, including a manual refresh, and the hold survives a re-link.
5. A plain click on the popover header detached it, so it stopped closing on deactivation. It now detaches only when the drag actually moved it.
6. Every 403 counted as rejected credentials. Only 401 and 403 `authentication_error` do now; an HTML 403 is a block in front of the API, and other API 403s (such as a permission error for an account type) are reported without marking the token rejected.
7. The wizard's test and Retry reused the token read at detection. The folder is now re-read before each test, so a login Claude Code renewed meanwhile is used, and one that expired is reported as expired.
8. A check of a superseded link could recreate the encrypted copy just after it was removed. Writes and deletes of the copy are now serialised and re-check cancellation, and removal stops in-flight checks first. Separately, when the token lineage changes, both Claude Code files are read twice and must agree, which avoids attributing a half-written `/login` to the old account.
9. A failure to delete the copy while removing a link crashed the app. It is now reported.
10. `.claude.json` larger than 16 MB read as "account unknown", and it was re-parsed on every poll. It now has a 64 MB limit and is parsed again only when its size or write time changes, once the file is a few seconds old.

## Manual checks in fixture mode

All data is synthetic: a `fixture@example.com` account, a token that is not a real token, and canned usage and status responses. Checked on 2026-10-01 through UI Automation and screen captures.

- The wizard opened on first launch, detected the synthetic login as "fixture@example.com — Fixture Org · default", ran the read-only test, showed the review (profile, account, organisation, subscription, folder, usage now) and saved. A new profile got an upstream random name.
- The popover then showed the profile name with the "Fixture data" tag, "All Systems Operational", Session 39 % (red with a purple runaway marker, as pace colouring gives at 17 % elapsed), All models 60 % with the Weekly tag, and Sonnet 12 % from `limits[]`, with reset texts. Screenshot: [media/popover-fixture-light.png](media/popover-fixture-light.png).
- Settings showed the CLI Account page (linked state, account, folder, status, masked token prefix, subscription, scopes), Appearance with its six toggles and metric cards, General with the 10-300 s slider, Popover with both segmented pickers, and About.
- Tray icons were rendered at 16, 24 and 40 px for every style, available, critical and unavailable, on dark and light taskbars.
- Two defects found and fixed this way: a resource-key clash (a brush and a style both named `Text.Secondary`) that crashed the first launch, and dates mixing the regional month name ("10月") into English text. Composite buttons also lacked accessible names; they now have them.

### Rendering note

Screen captures (GDI screen copy and `PrintWindow` with full content) of hardware-rendered WPF windows came back blank on this machine. That included a stock `dotnet new wpf` app, both when started from the agent shell and when started through Explorer. Software-rendered windows captured normally, and the UI Automation tree of the blank windows was complete. The likeliest cause is the capture method missing content that the GPU presents outside the composed primary plane, but that is not established. **Whether the app paints correctly with hardware rendering on this display is unverified** and is the first thing to check by eye. If it does not paint, the app should switch to software rendering, which costs little for these small windows.

## Live checks still to do

These need the user, a real account or both:

1. **Acceptance:** run the app normally, complete the wizard with the default Claude Code login, confirm the popover and tray show the same usage as `claude` `/usage`, quit, relaunch, and confirm usage reappears (labelled stale until the first refresh) and refreshes. Check `diagnostics\requests.jsonl` for read-only `oauthUsage` requests only.
2. **Rendering:** confirm the wizard, popover and settings paint normally without `--software-render`, in both light and dark mode.
3. **Waiting state:** leave Claude Code unused until its token expires; the popover should say "Waiting for Claude Code to refresh its login", and running any `claude` command should restore live usage on the next poll.
4. **Account change:** switching the default login to another account (the user has `claude-swap`) should show "Claude Code is now signed in to a different account." and stop polling until set up again.
5. **G3 soak:** leave the app polling at 30 s for several hours and summarise `requests.jsonl` (statuses, any 429 and `Retry-After`).
6. **D1:** set one monitor to a different scale and check popover placement from icons on each taskbar; select the icon from the keyboard (Win+B, arrows, Enter) and check focus lands in the popover; click the icon of an open popover and check it closes without reopening; restart Explorer and check the icons return.
7. **Over full-screen apps and virtual desktops** (upstream `NSWindowFullScreenSpacesTests`): the topmost popover should appear above a borderless full-screen window and on the current desktop.

## Open questions

- **Extra usage for CLI profiles.** Upstream shows Extra Usage only from claude.ai's overage endpoints (Milestone 3 browser route) and ignores the `extra_usage` and `spend` objects that `/api/oauth/usage` returns. Their field names are not recorded (Milestone 1 logged only `extra_usage.utilization`, which was null for the Team account). Showing extra usage on the CLI route needs a shape-only observation of those objects first (field names and value kinds, no values), or it can wait for the browser route.
- **Tray identity per profile.** The plan derives each icon's GUID from the profile ID plus metric, so each profile gets its own Explorer placement preference. Upstream's single-profile items are keyed by metric only. This matters once profiles can be switched (Milestone 3).

## Reproducing

From the repository root:

```powershell
dotnet build ClaudeUsageTracker.slnx
dotnet test --solution ClaudeUsageTracker.slnx
# Fixture mode: <folder>\home\.claude\.credentials.json and <folder>\home\.claude.json hold synthetic data,
# <folder>\responses\usage.json and status.json the canned answers (optional usage.status holds an HTTP status).
dotnet run --project src/ClaudeUsageTracker.App -- --fixture <folder>
```
