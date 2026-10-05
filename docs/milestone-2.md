# Milestone 2 log

Work and evidence for [plan.md Milestone 2](../plan.md#milestone-2---first-complete-cli-to-popover-flow-and-setup-wizard): the first complete Claude Code CLI-to-popover flow and the setup wizard. Upstream behaviour comes from the pinned upstream source (`588775e`); each departure is recorded in [parity.md](parity.md). Evidence types follow parity.md: automated tests, manual checks in fixture mode, and live-account checks, kept separate.

Recorded 2026-10-01 on the development machine (Windows 11 26200, two 3840 × 2160 monitors at the same scale, apps in light mode, taskbar in dark mode, Claude Code 2.1.286).

## Status

| Plan item | Status | Evidence |
| --- | --- | --- |
| Single-instance startup, tray lifecycle, popover, sidebar settings | Implemented | Automated (toggle, placement, tray identity, model tests); manual in fixture mode (screenshots below); **live, checked by the user on 2026-10-01** |
| Setup wizard: provider choice, CLI discovery/linking, connection test, review, save; edit/cancel/retry; non-destructive failures | Implemented for the Claude Code route. Browser sign-in, session keys, the Console and Codex are shown as not yet available | Automated (`SetupWizardViewModelTests`, `SetupServiceTests`); manual in fixture mode; live setup with the default Claude Code login |
| CLI file linking plus read-only OAuth route | Implemented | Automated (`CliLinkServiceTests`, `UsageClientTests`); live: setup, polling, restart and an account change checked by the user on 2026-10-01 |
| Secure persistence, no plaintext shortcut; mirror CLI-owned credentials; waiting-for-refresh state | Implemented (DPAPI per-profile file holding only `claudeAiOauth`; never refreshes) | Automated (`SecretStoreTests`, `CliLinkServiceTests`, `PopoverModelTests`); the live waiting-state check is scheduled for the night of 2026-10-01 |
| Session/weekly/model usage, reset times, pace, service status, disconnected/stale/error states | Implemented, including the Extra Usage card from the endpoint's `spend` object (parity difference 27) | Automated (`ClaudeUsageResponseTests`, `PopoverModelTests`, `UsageDisplayTests`); manual in fixture mode; live: matches Claude Code's `/usage`. **The Extra Usage card has not been seen live**: the user's account reports extra usage as disabled (`out_of_credits`), so the card is hidden there, as upstream would hide it |
| Polling, manual refresh, cancellation, backoff, per-profile coordinator, 30 s default, server retry guidance, late results dropped | Implemented | Automated (`RefreshCoordinatorTests`, `RefreshPolicyTests`), run five times without a flake |
| G2: read-only usage for other account types | Pro and Team verified in Milestone 1; Max and Enterprise still need such accounts | — |
| G3: rate limits and sustained 30-second polling | 429 and 5xx `Retry-After` handling implemented and tested with fixtures; sustained 30-second polling verified live: on the night of 2026-10-05, 1,696 Claude Code usage reads over 14 hours, all 200, with no rate limit ([milestone-4.md](milestone-4.md#the-night-of-2026-10-05)). A live 429 has never occurred, so the `Retry-After` handling rests on automated tests | Automated; live account |
| D1: mixed DPI, keyboard selection, anchoring from icon rectangles | **Verified** (physical-pixel placement per monitor, `NIN_KEYSELECT` with focus moved into the popover) | Automated (`PopoverPlacementTests`); manual by the user on 2026-10-01 |

**Acceptance met on 2026-10-01.** The user completed setup with a real Claude account, saw the popover and tray match Claude Code's `/usage`, then quit and relaunched; usage reappeared at once and refreshed (checks 1-3 under "Live and manual checks"). The overnight soak and the waiting state (check 9) were overtaken by later runs: sustained polling ran overnight without a failure (G3), and the waiting state cannot be produced while Claude Code is open, which renews its own login first. Everything below that says "fixture" used synthetic data only.

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

526 pass (191 before this milestone), with no build warnings. New suites: `StringsTableTests`, `AppStringsTests`, `SecretStoreTests`, `SettingsStoreTests`, `UsageCacheStoreTests`, `CliLoginTests`, `CliLinkServiceTests`, `ClaudeUsageResponseTests`, `UsageDisplayTests`, `RefreshPolicyTests`, `ServiceStatusTests`, `UsageClientTests`, `RefreshCoordinatorTests`, `PopoverModelTests`, `PopoverToggleTests`, `TrayIdentityTests`, `PopoverPlacementTests`, `FunnyNamesTests`, `SetupServiceTests`, `SetupWizardViewModelTests` and `FixtureModeTests`. The upstream cases scheduled for this milestone are mapped in [parity.md](parity.md#upstream-test-inventory). The coordinator tests use simulated time (`Microsoft.Extensions.TimeProvider.Testing`); they and the other timing-sensitive suites passed in five consecutive runs.

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

A second review, by the user on 2026-10-01, found three more. Each fix has a regression test that failed before it:

11. A state notification checked the link generation only when it was posted to the UI thread. If a link was removed between a check committing its result and posting it, the old usage could replace the disconnected display. The generation is now checked again when the notification runs.
12. `--fixture` without a folder was ignored, so the app started in live mode with the real login and, if linked, polled the real account. A missing, repeated or misspelt fixture argument, or a folder that does not exist, now stops startup with a message.
13. The wizard showed the server's `Retry-After` but kept no hold, so Retry sent another request at once. The hold now lives in the shared usage client and binds every usage request: polling, a manual refresh and setup tests, including a wizard reopened during the wait. A 429 without guidance holds for the same 5 minutes as polling. The hold is app-wide; whether it should be per account is a Milestone 3 question, once several profiles poll.

## Manual checks in fixture mode

All data is synthetic: a `fixture@example.com` account, a token that is not a real token, and canned usage and status responses. Checked on 2026-10-01 through UI Automation and screen captures.

- The wizard opened on first launch, detected the synthetic login as "fixture@example.com — Fixture Org · default", ran the read-only test, showed the review (profile, account, organisation, subscription, folder, usage now) and saved. A new profile got an upstream random name.
- The popover then showed the profile name with the "Fixture data" tag, "All Systems Operational", Session 39 % (red with a purple runaway marker, as pace colouring gives at 17 % elapsed), All models 60 % with the Weekly tag, and Sonnet 12 % from `limits[]`, with reset texts. Screenshot: [media/popover-fixture-light.png](media/popover-fixture-light.png).
- Settings showed the CLI Account page (linked state, account, folder, status, masked token prefix, subscription, scopes), Appearance with its six toggles and metric cards, General with the 10-300 s slider, Popover with both segmented pickers, and About.
- With a synthetic `spend` object of 1.86 of 5.00 USD, the Extra Usage card appeared below Sonnet as "1.86 / 5.00 USD" at 37 %, with no reset line.
- Tray icons were rendered at 16, 24 and 40 px for every style, available, critical and unavailable, on dark and light taskbars.
- Two defects found and fixed this way: a resource-key clash (a brush and a style both named `Text.Secondary`) that crashed the first launch, and dates mixing the regional month name ("10月") into English text. Composite buttons also lacked accessible names; they now have them.

### Rendering note

Screen captures (GDI screen copy and `PrintWindow` with full content) of hardware-rendered WPF windows came back blank on this machine. That included a stock `dotnet new wpf` app, both when started from the agent shell and when started through Explorer. Software-rendered windows captured normally, and the UI Automation tree of the blank windows was complete. The likeliest cause is the capture method missing content that the GPU presents outside the composed primary plane, but that is not established. **Hardware rendering was confirmed by eye** on 2026-10-01: the user saw the wizard, popover and settings paint normally in light and dark mode. The blank captures are therefore a limitation of the capture method on this machine, and `--software-render` remains only for screen captures.

## Live and manual checks

The user ran checks 1-8 on the evening of 2026-10-01, with the real default Claude Code login and following a step-by-step guide. All behaved as expected:

1. **First launch and setup.** The windows painted normally with hardware rendering. The wizard detected the default login, ran the read-only test, showed the review and saved.
2. **Popover and tray.**
   - The tooltip read correctly, and the popover opened just above the icon.
   - Its values matched Claude Code's `/usage`.
   - Clicking the icon of an open popover closed it without reopening it; an outside click and Escape also closed it.
   - The refresh button refreshed, and the context menu and settings pages worked.
   - Turning on the Week metric and changing icon styles updated the tray at once.
3. **Restart.** After Quit and a relaunch, the wizard did not reappear, usage showed at once and then refreshed.
4. **Dark mode.** The popover and settings followed the switch, and the tray icons stayed readable.
5. **Keyboard and Explorer.**
   - Win+B, the arrow keys and Enter opened the popover; Tab moved between its buttons and Escape closed it.
   - Shift+F10 opened the menu.
   - After an Explorer restart the icon returned.
6. **Mixed DPI (D1).** With the tray monitor at a different scale from the other, the popover sat above the icon, fully on screen and sharp. The settings window moved between the monitors cleanly.
7. **Full screen and virtual desktops.** A second launch opened the popover above a full-screen browser, and on a new virtual desktop the popover opened there.
8. **Account change.** Switching Claude Code's default login to the other account showed "Claude Code is now signed in to a different account." with dimmed icons and none of the other account's numbers. Switching back restored live usage within about 30 seconds.

Still to do, scheduled for the night of 2026-10-01 (superseded: the soak's polling part was covered by later overnight runs, see G3 above; the waiting state cannot arise while Claude Code is open, which renews its own login, and Milestone 3 replaced it by the takeover, which passed live on 2026-10-05):

9. **G3 soak and waiting state.** Leave the app polling overnight with Claude Code unused. After the token expires, the popover should say "Waiting for Claude Code to refresh its login" and send no usage requests, and live usage should return after Claude Code's next use. Then summarise `requests.jsonl`.

## Usage response shape (live, 2026-10-01)

To decide how extra usage could be shown, one read-only request was made with the user's approval through `OAuthUsageProbe --shape true`, which prints field names and value types only, never values. It used the default Claude Code login (subscription `pro`) at 17:02 UTC; the response was 200 `application/json`, 2193 bytes, 366 ms. The outline:

```text
five_hour, seven_day: object
  utilization: number (decimal); resets_at: string (timestamp)
  limit_dollars, used_dollars, remaining_dollars, locked_reason: null
seven_day_oauth_apps, seven_day_opus, seven_day_sonnet, seven_day_cowork, seven_day_omelette: null
tangelo, iguana_necktie, omelette_promotional, nimbus_quill, cinder_cove, copper_kite, brass_thimble,
  harbor_lantern, wattle_ember, amber_ladder, amber_cistern, juniper_tide, cedar_ember, amber_gauge: null
extra_usage: object
  is_enabled: boolean; monthly_limit: number (integer); used_credits: number (decimal)
  utilization: number (decimal); currency: string (three capital letters); decimal_places: number (integer)
  disabled_reason: string; user_disabled, spend_limit_reached, credits_ever_enabled: boolean
  daily, weekly: null
limits: array[2] of object
  kind, group, severity: string; percent: number (integer); resets_at: string (timestamp)
  scope: null; is_active: boolean
spend: object
  used, limit: object { amount_minor: number (integer); currency: string (three capital letters); exponent: number (integer) }
  percent: number (integer); severity, disabled_reason, disclaimer: string
  enabled, can_purchase_credits, can_toggle: boolean
  cap: object { money: null; credits: object { amount_minor: number (integer); exponent: number (integer) } }
  balance, auto_reload: null
member_dashboard_available: boolean
seven_day_breakdown: object
  as_of, window_started_at: string (timestamp)
  rows: array[4] of object { key, display_name: string; percent: number (integer) }
```

Compared with Milestone 1:

- `five_hour` and `seven_day` now also carry dollar fields and `locked_reason`, all null here.
- More code-named windows exist, and all were null.
- The `limits[]` entries had a null `scope`.
- `seven_day_breakdown` rows carry a key, a display name and a percentage.

The parser ignores everything it does not use, as its tests require.

For extra usage, `spend.used` and `spend.limit` are the precise source: each gives an integer amount in minor units with its own `exponent` and `currency`, so no cents-versus-dollars assumption is needed. `extra_usage` reports `used_credits` (decimal) and `monthly_limit` (integer) in units it does not state, beside `decimal_places`.

The Extra Usage card was then added from `spend` (parity difference 27). The user said their account allows extra spending and shows 0.00 USD spent, but the card did not appear in the running app. A second read-only request at 17:23 UTC, through `OAuthUsageProbe --spend true`, printed only flags, reason codes, currencies and the sign of each amount:

```text
spend: enabled false; disabled_reason "out_of_credits"; severity "normal"; can_toggle false; can_purchase_credits false
  used: amount_minor zero, USD; limit: amount_minor positive, USD; cap.credits positive; balance, auto_reload null
extra_usage: is_enabled false; disabled_reason "out_of_credits"; user_disabled false; spend_limit_reached false
  credits_ever_enabled true; monthly_limit positive; used_credits zero; currency "USD"
```

So the endpoint reports extra usage as configured (a positive monthly limit, 0 used) but disabled because the account is out of credits. The card is hidden because `spend.enabled` is false, not because nothing has been spent: with `enabled` true, 0 spent shows as "0.00 / <limit> USD" (`PopoverModelTests.ZeroExtraUsageIsShownAsZero`). This differs from Fable: its card is absent when the endpoint reports no Fable window, as the user has seen after a week without using it. Upstream applies the same rule to claude.ai's overage endpoint (`overage.isEnabled == true`), so it would hide the card too. This is upstream's behaviour rather than a defect in the port, but it is a gap: a configured but disabled extra usage is indistinguishable from none. See "Open questions".

## Open questions

- **Extra usage for CLI profiles (left to a later stage).** The card now comes from `spend` (parity difference 27), but it has not been seen live. The user's account reports extra usage as disabled (`out_of_credits`), so the card is hidden there, as upstream would hide it. Two things remain:
  - Whether to show configured but disabled extra usage, for example the limit with the reason. That would depart from upstream and needs the possible `disabled_reason` values.
  - A comparison with claude.ai's usage page once the account has credit and the endpoint reports extra usage enabled.

  The Milestone 3 browser route reads claude.ai's own overage endpoint, so it is the natural place to compare the two sources and decide.
- **Tray identity per profile.** The plan derives each icon's GUID from the profile ID plus metric, so each profile gets its own Explorer placement preference. Upstream's single-profile items are keyed by metric only. This matters once profiles can be switched (Milestone 3).

## Reproducing

From the repository root:

```powershell
dotnet build ClaudeUsageTracker.slnx
dotnet test --solution ClaudeUsageTracker.slnx
# Fixture mode: <folder>\home\.claude\.credentials.json and <folder>\home\.claude.json hold synthetic data,
# <folder>\responses\usage.json and status.json the canned answers (optional usage.status holds an HTTP status).
dotnet run --project src/ClaudeUsageTracker.App -- --fixture <folder>
# One read-only request with the default Claude Code login, printing field names and types only:
dotnet run --project spikes/OAuthUsageProbe -c Release -- --repeat 1 --shape true
# The same, printing only extra-usage flags, reason codes, currencies and the sign of each amount:
dotnet run --project spikes/OAuthUsageProbe -c Release -- --repeat 1 --spend true
```
