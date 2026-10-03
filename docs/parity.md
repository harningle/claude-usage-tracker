# Parity matrix

This file tracks how the Windows port relates to the upstream macOS app, fixed at [`588775e4540757443691fa1bcb33457315e05f86`](https://github.com/hamed-elfayome/Claude-Usage-Tracker/tree/588775e4540757443691fa1bcb33457315e05f86). [plan.md](../plan.md) holds the design; this file records what exists, what backs each claim, and where the port differs on purpose.

Evidence types, kept separate throughout:

- **Automated**: deterministic tests in [`tests/ClaudeUsageTracker.Tests`](../tests/ClaudeUsageTracker.Tests).
- **Manual**: checked by a person on Windows.
- **Live account**: exercised against a real account through the app.
- **Source/image**: compared only against upstream source or media.

Nothing here claims live authentication, Mac runtime parity or full feature parity. At Milestone 0 all evidence is automated or source-based. Milestone 1 experiment results, including one live-account observation, are in [milestone-1.md](milestone-1.md); they come from spike harnesses, not app features. Milestone 2's app features are verified by automated tests, by manual checks with synthetic fixture data and by the user's live checks on 2026-10-01 ([milestone-2.md](milestone-2.md)); the overnight soak and the waiting-state check are still pending. Milestone 3's features are verified by automated tests, by manual checks with synthetic fixture data, by one signed-out WebView2 smoke check and by the user's first live checks on 2026-10-01 ([milestone-3.md](milestone-3.md)); the idle takeover and the Console have not been checked live yet. Milestone 4's features are verified by automated tests, by manual checks with synthetic fixture data, and by live checks on 2026-10-02 ([milestone-4.md](milestone-4.md)):
- notification registration and clicks
- launch at login across a sign-in
- shortcuts across an Explorer restart
- network and sleep handling
- a switch of Claude Code's account and back

The session auto-start passed live on 2026-10-03. An auto-switch at a real 100 % and remote desktop remain.

## Environment and references

Recorded on 2026-09-30.

- Upstream licence: MIT, "Copyright (c) 2025 Claude Usage Tracker Contributors", preserved in [LICENSE](../LICENSE).
- Claude Code 2.1.285, native install at `%USERPROFILE%\.local\bin\claude.exe`, from `claude --version`. No credential or configuration files were read. By Milestone 1 (2026-09-30) it had auto-updated to 2.1.286, the version all Milestone 1 measurements use.
- .NET SDK 10.0.401, pinned in [global.json](../global.json) with patch roll-forward. Tests use xUnit v3 4.0.1 on Microsoft.Testing.Platform.
- Public references: [Claude Code statusline](https://code.claude.com/docs/en/statusline), [Claude Code hooks](https://code.claude.com/docs/en/hooks), [.NET artifacts output layout](https://learn.microsoft.com/en-us/dotnet/core/sdk/artifacts-output), [Microsoft.Testing.Platform configuration](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-config). Further references are in plan.md.

## Feature status

| Area | Milestone | Status | Evidence | Windows differences |
| --- | --- | --- | --- | --- |
| Usage status colours, display percentage, pace colouring | 0 | Core logic ported; no UI | Automated | None |
| Reset times (next Monday 12:59, time remaining) | 0 | Core logic ported | Automated | Explicit time zone and current time instead of machine state |
| URL construction | 0 | Core logic ported | Automated | Differences 6 and 8 |
| Session-key format validation | 0 | Core logic ported; no setup UI | Automated | Difference 6 |
| Provider identity, names and capabilities | 0 | Core logic ported | Automated | Branding, status page and settings sections arrive with their UI |
| Usage values model | 0 | Core model ported | Automated | Difference 1 |
| History snapshots at resets | 0 and 3 | Snapshot creation, upstream sampling and retention, per-profile files, charts, JSON/CSV export | Automated; manual with fixture data | Differences 34 and 35 |
| Codex auth.json, `wham/usage` decoding, window normalisation, mapping, base URL | 0 and 3 | Pure logic ported; Milestone 3 adds reading the Codex CLI's `auth.json`, usage polling, pasted credentials and their renewal, and the wizard route | Automated; manual with fixture data; live pending | Differences 1, 2, 3, 10 and 32 |
| Claude Code credential merge after refresh | 0 and 3 | Merge ported; Milestone 3 adds the guarded idle takeover and write-back | Automated; live pending (the refresh request is unverified) | Differences 3 and 13 |
| Activity HUD events, tool mapping, session reducer | 0 | Core logic ported; no transport or window | Automated | Difference 5; transport gate in Milestone 1, stricter ordering in Milestone 5 |
| Launcher slugs | 0 | Naming ported | Automated | Difference 9; executable aliases in Milestone 5 |
| Supported languages | 0 | List ported | Automated | Explicit `zh-cn` to `zh-Hans` mapping; resources in Milestone 6; flag emoji rendering on Windows unverified |
| UI text | 2 | Upstream English `Localizable.strings` embedded unchanged and looked up by key; port-only text in `Windows.strings`; other languages in Milestone 6 | Automated (every key used exists) | Differences 21 and 22 |
| Tray, popover, settings window, setup wizard | 1 spike, then 2 and 3 | Milestone 2: single instance, tray icons and menu, popover, sidebar settings, the Claude Code wizard route; checked live by the user on 2026-10-01, including hardware rendering, dark mode, mixed DPI, keyboard selection, Explorer restart, full screen and virtual desktops. Milestone 3: profile switcher, API cards, multi-profile icons, every credential page, History, Manage Profiles, Debug Tools and the claude.ai, Console and Codex wizard routes; checked live by the user on 2026-10-01 except the Console | Automated (including a real-window placement test); manual with fixture data; live account (manual) | Differences 14-20, 23 and 28-37; plan section 2 |
| CLI linking and read-only OAuth usage | 1 gate, then 2 | Implemented: file linking with account checks, encrypted mirror of the CLI-owned login, read-only polling with backoff and `Retry-After`. Live setup, polling, restart and an account change checked on 2026-10-01; the overnight soak (G3) and the waiting state are pending | Automated; manual with fixture data; live account (manual) | Differences 11-13, 16, 24 and 27 |
| Browser sign-in, organisations, Console billing, API costs | 1 gate, then 3 | Implemented on the decided transports: claude.ai through each profile's own WebView2 context (same-origin `fetch`), the Console through `HttpClient` at `platform.claude.com`. Sign-in, pasted keys, organisation choice, reconnect and expiry, monthly/daily/per-key/per-model costs. The gate's 30-minute renewal soak and two-account isolation passed in Milestone 1 (spike) | Automated; manual with fixture data; signed-out WebView2 smoke check; live: claude.ai passed on 2026-10-01, Console not yet checked | Differences 30, 31 and 37; plan section 2 |
| Profiles, secure storage, credential ownership | 2 and 3 | Profile lifecycle, multi-profile display and simultaneous polling; one versioned DPAPI bundle per profile; CLI-linked, app-owned and unresolved ownership under one credential lock; parent-snapshot process guard; recoverable removal | Automated; manual with fixture data; live pending | DPAPI files replace Keychain; differences 13, 17, 28, 29 and 36 |
| History persistence, charts, CSV/JSON export, network log | 3 | Implemented | Automated; manual with fixture data | Differences 33-35 |
| Notifications, automation, safe CLI switching | 1 spike, then 4 | Spike: unpackaged self-contained registration and posting (one extracted runtime DLL), cold activation and running-instance clicks verified. Milestone 4: threshold and reset notifications for every polled profile with Windows sounds and click routing, stale-registration cleanup, launch at login, global shortcuts, checks on resume, network and session changes, opt-in session auto-start, profile auto-switch, and the guarded "Use in Claude Code" switch of the shared login | Automated; manual with fixture data (notifications in Windows' history, shortcuts, launch-at-login scratch key, a switch of a fixture folder); live: notification clicks, launch at login across a sign-in, shortcuts across an Explorer restart, network and sleep, a switch of Claude Code's account and back, one session auto-start; a real auto-switch pending | Differences 29 and 38-45; after a move, notifications posted from the old path still do nothing when clicked, but their registration is now removed at the next start |
| Launchers, statusline, hooks and HUD | 1 gate, then 5 | NativeAOT helper timing passed; hook transport decided: HTTP for all eight events as upstream; the SessionStart entry is inert on CLI 2.1.286, as it is for upstream | Automated harness (real CLI, fake Messages API); live account (40 Haiku tool calls, stopped listener) | Secret-free statusline; transport by measurement |
| Localisation, accessibility, packaging | 6 and 7 | Not started | None | Plan section 3 |

## Deliberate differences so far

1. **Missing values stay missing.** `UsageData` fields are nullable. Where upstream writes 0, `false` or a placeholder limit for an unreported value, the port keeps null. This applies to Codex token counts and limits, a missing weekly window, a missing `used_percent` and malformed credit flags.
2. **No estimated Codex reset times.** Without `reset_at`, the port reuses the previous still-future reset time, as upstream does, and otherwise leaves the time unknown. Upstream estimates it as the current time plus the window length.
3. **JSON write-back keeps key order.** Codex `auth.json` output (indented) and the Claude Code credential merge (compact) keep the existing key order and literal token characters; upstream sorts keys. Unknown fields survive in both, as upstream.
4. **Explicit time inputs.** Reset-time, pace and refresh-age calculations take the time zone and current time as arguments instead of reading machine state, so tests do not depend on the test machine.
5. **HUD task text is not localised in Core.** A task carries upstream's localisation key plus its argument (file name, command or message); the App formats it, so the upstream string resources can be reused.
6. **Only helpers upstream production uses are ported.** Not ported: the relaxed validator configuration, `sanitizeForStorage`, `validateResult`, the session-key `String` extensions, `buildResult` and the dictionary query-parameter builder. Upstream's own code never calls them.
7. **No promotional prompts.** The GitHub-star prompt, support reminder and feedback prompt are omitted, as plan.md requires.
8. **Bare origin URLs.** A base URL whose only path is `/` builds without the trailing slash, because `System.Uri` cannot distinguish the two.
9. **Launcher ownership from a manifest.** Slug collisions are checked against an owner map, which the Milestone 5 install manifest will provide, instead of a marker line in a shell script.
10. **Unreadable Codex credentials are never replaced.** Codex `auth.json` serialisation creates a new document only when there is no existing content. Existing content that is malformed or not a JSON object throws, rather than being silently replaced by a new document as upstream does.
11. **CLI profiles poll the read-only usage endpoint.** Upstream's active CLI profile posts a one-token Haiku request to `/v1/messages` on every refresh and reads rate-limit headers (up to 2,880 generating requests a day at 30 seconds); its background profiles use `GET /api/oauth/usage`, which upstream's comment calls disabled. The port uses `/api/oauth/usage` for all CLI profiles and never falls back to `/v1/messages`. Verified with Claude Code 2.1.286 for a Pro account (7 of 7 requests, 2026-09-30) and a Team account (5 of 5, 2026-10-01). Both returned 200 with the claude.ai usage shape, and no throttling was seen at a 30-second cadence (spike probe; implementation in Milestone 2). Windows and fields vary by account and over time, so all are optional. Max and Enterprise accounts are unverified.
12. **Account checks on every poll.** Upstream used whatever account the CLI was logged in as for the active profile, with no identity check, so it could show one account's usage under another profile. The port compares `accountUuid`, then email, or else an unchanged refresh token, before each request. A different account is reported ("Claude Code is now signed in to a different account.") and is neither polled nor shown. Cached usage records its account, and another account's cache is never shown.
13. **Guarded takeover of Claude Code's login.** Upstream either took over an expired token, rotating it without process or file checks, or silently skipped the refresh (upstream issue #268). In Milestone 2 the port never refreshed and showed "Waiting for Claude Code to refresh its login". From Milestone 3 it renews an expired login of the linked account itself, but only when:
    - no Claude Code process could be using any folder that holds the same token (until Milestone 5's launchers, any running `claude.exe` blocks it, and so does a process whose details cannot be read);
    - every known Claude Code folder is readable and the file is unchanged when re-read under the credential lock;
    - the login is not marked as used elsewhere (a new setting on the CLI Account page) and the profile's encrypted copy is usable.

    The renewed login is saved before it is written back, then merged into every folder still holding the used token and read back, and given to every other profile's copy holding the used token. A folder that was signed out or now holds a newer login is never overwritten. A used or rejected refresh token is never sent again: this is checked again under the credential lock, and against every profile's saved pending hand-back, so it also holds across a restart. Nothing in a lineage with a pending hand-back is renewed until every folder has it, and if Claude Code renews the login itself meanwhile, its newer login is what is handed back. A saved copy of any profile that cannot be read blocks every renewal, because its pending record cannot be ruled out. An app-owned copy is checked again each time: it goes back to Claude Code when a known folder holds the account again or the login is marked as used elsewhere, and is not renewed while another folder is in doubt. A failed renewal waits five minutes, then longer each time the login service refuses it (10, 20 and 40 minutes, then an hour), and a renewed login that cannot be saved blocks further renewals until it is saved. Otherwise the port keeps showing the waiting state. The encrypted copy holds only the `claudeAiOauth` object, not other logins in the file such as MCP servers'.
14. **The wizard tests before saving.** Upstream's Claude Code route copied the login without a test, a review or the setup-completed flag. It fell back to the manual route silently on any failure, and re-read a possibly different login at save time. The port:
    - runs one read-only usage request, then shows a review (profile, account, organisation, plan, folder, usage)
    - re-reads the folder on Done and refuses a login whose account has changed since the test
    - saves nothing after a failed test, and offers Back and Retry
    - says an expired login is expired rather than "not found"
    - from Milestone 3, does the same for the claude.ai, Console and Codex routes (difference 37)

    Upstream's step-1 Cancel button, which did nothing, is not ported.
15. **Honest popover states.** Upstream showed 0 % cards with made-up reset times when nothing was known, never labelled cached data from a previous run, and in multi-profile mode treated cached data as a success. The port:
    - shows "Loading usage…" or "No usage data yet." instead of zero cards, and an unavailable value as "—"
    - shows a per-model card whenever the model is reported, including at 0 %
    - says "Window reset, waiting for new data" instead of "Resets in Reset now"
    - bases the stale banner on the data's own observation time, so a previous run's cache counts as stale
    - adds banners for not set up, waiting for Claude Code, a changed or unverifiable account, a signed-out or unreadable CLI, rate limits (with the next check time) and challenges
    - says "Claude Code's login was rejected. Sign in again in Claude Code." for rejected credentials, rather than upstream's "Credentials expired."
    - spins the refresh icon for the real request (upstream spun it for a fixed second), and re-renders countdowns every 30 seconds
16. **Polling coordinator.** Upstream's refreshes could overlap, were never cancelled and ignored `Retry-After`; a late result could be saved to whichever profile was then active. The port:
    - polls each profile in one sequential loop and drops results from a superseded link
    - backs off exponentially to 15 minutes after failures
    - waits at least the server's `Retry-After` after a 429 or a 5xx, which neither a manual refresh nor a setup test overrides
    - does not resend a rejected token until it changes
    - applies a changed interval at once, where upstream waited for a profile switch or relaunch
17. **Storage.** Upstream silently replaced an undecodable profile list with a new default profile. The port reports damaged or newer-format settings and leaves the file untouched. It keeps unknown members, so an older build does not drop a newer one's settings. An unknown provider value is kept as written while it behaves as Anthropic. The usage cache is a separate, re-fetchable file per profile, so polling never rewrites settings or secrets.
18. **Tray icons.** macOS status items are wide images; Windows tray icons are square.
    - Each of upstream's five styles (battery, progress bar, percentage, ring, dot) is drawn in a square form without its text labels; only the ring keeps its centre letter, from 20 px. The "time until next session" option is therefore not offered.
    - Icons are dimmed when their data is stale or failed.
    - Tooltips, which upstream did not have, give the profile, metric, value, reset time and any banner within 127 characters.
    - Right-click opens a native Refresh / Settings / Quit menu.
    - The logo shown before setup is a drawn ring, not upstream's artwork.
    - Icon GUIDs include the profile ID, as the plan requires.
19. **Popover.**
    - The profile switcher (Milestone 3) is a native menu, so it follows the system theme. It activates the chosen profile, as upstream; it does not write Claude Code's files (difference 29).
    - The status link opens status.claude.com, or status.openai.com for Codex profiles.
    - In light mode the background is a chosen neutral, because upstream has no light screenshot. Moderate, critical and caution text use darker shades than upstream's, for contrast.
    - Dragging the header detaches the popover, the same gesture as upstream; any tray click then closes it.
    - Opened from the keyboard, focus moves into the popover; upstream cleared focus.
20. **Settings window.** Standard Windows chrome replaces upstream's borderless window and traffic-light buttons, and only the sections whose features exist are listed (from Milestone 4: Claude.ai, API Console, CLI Account or Codex Account, Appearance, General, History, Settings, Manage Profiles, Shortcuts, Popover, Debug Tools and About).
    - About identifies the Windows port and links the original project, this repository and its issues. It omits the GitHub star link, the feedback form, the contributors fetch and the update check.
    - Removing the CLI account asks for confirmation, which upstream did not.
    - Re-sync runs the wizard's test and review instead of copying the login immediately.
21. **Upstream strings, Windows wording.** Upstream's English strings are used unchanged, by key. Where they describe macOS ("menu bar", "like macOS battery"), the port uses its own `windows.*` strings. Text that upstream hard-coded in English ("Today", "Tomorrow", "Status Unknown", the icon style names) is also in `Windows.strings`, ready for Milestone 6.
22. **Dates in the UI language.** Upstream formatted month names in the system locale inside English sentences. The port formats them in the UI language (English, `en-GB`, for now), while the 12/24-hour choice still follows the user's regional format.
23. **Single instance.** Upstream relied on macOS for this. The port uses a per-user mutex, and a second launch opens the running instance's popover.
24. **Account metadata per directory.** Upstream read `oauthAccount` from the first `.claude.json` that existed, so a `CLAUDE_CONFIG_DIR` without its own file was labelled with the default login's account. The port reads `%USERPROFILE%\.claude.json` for the default directory and `<dir>\.claude.json` for any other, as Claude Code 2.1.286 does on Windows ([milestone-2.md](milestone-2.md#verified-facts)).
25. **Usage parsing.** In addition to difference 1:
    - Timestamps parse with or without fractional seconds. Upstream's parser required them and replaced whole-second times with placeholders.
    - A `limits[]` entry with no percentage does not override a reported value; upstream wrote 0.
    - No token counts are derived from percentages; upstream fabricated them from a 1,000,000 limit.
26. **Which responses count as rejected credentials.** Upstream treated every 401 and 403 from the usage endpoint as unauthorised and showed "Credentials expired". The port treats only a 401 or a 403 `authentication_error` as a rejected login. An HTML 403 is reported as a block in front of the API, and other API 403s, such as a permission error for an account type, are reported as refusals without marking the token rejected.
27. **Extra usage for CLI profiles.** Upstream showed the Extra Usage card only from claude.ai's `overage_spend_limit` endpoint, so a CLI profile never had one. The port fills it from the usage endpoint's `spend` object, when `spend.enabled` is true and `used` and `limit` are both present in the same currency. The card is otherwise upstream's: title, "used / limit currency" subtitle, used/limit percentage, no reset time, and shown only for a positive limit. Differences:
    - Amounts are exact: `amount_minor` scaled by the given `exponent`, printed with that many decimal places. Upstream stored minor units as a `Double` and always divided by 100 and printed two decimals, which is wrong for currencies without minor units.
    - The overage balance line under the card is not shown, because the usage endpoint's `spend.balance` has only been seen as null and its shape is unknown.
    - `extra_usage`, whose units are not stated, is not used.
    - As upstream, disabled extra usage hides the card even when a limit is set. The user's account reports `disabled_reason` `out_of_credits` with a positive limit, so the card has not been seen live ([milestone-2.md](milestone-2.md#open-questions)).

28. **Profiles follow accounts.** Upstream tied a profile's CLI credentials to whatever the CLI was signed in to. The port's profile follows the account it was linked to. If Claude Code switches to another account, the profile keeps polling with its own copy while it lasts and says that Claude Code is now on another account. Once no known Claude Code folder holds the account and nothing is in doubt, the copy becomes the tracker's own ("app-owned") and the tracker renews it itself. The CLI Account page shows who renews it.
29. **Activating a profile does not switch Claude Code.** Upstream's profile switch wrote the new profile's login into the CLI's storage. In the port, activating only changes which profile is viewed and configured. Switching Claude Code's account is the separate "Use in Claude Code" action of Milestone 4 (difference 41), which reuses the takeover's guards.
30. **claude.ai sessions stay in the browser.** Upstream kept the `sessionKey` in the Keychain and sent it with `URLSession`. The port keeps each profile's session in its own WebView2 user-data folder and sends usage requests as same-origin `fetch` calls there, because every replay was challenged (Milestone 1). A pasted session key is put into that browser as its cookie and tested there; a key that fails is taken out again, and one that is never saved is removed when the wizard closes. Sign-in windows close themselves once signed in and ask before reusing a session that is already signed in. Removing the link or the profile deletes the browser folder after its processes exit. Organisation IDs are checked before they reach a URL.
31. **Console billing.** Upstream fetched billing with every refresh (four requests). The port fetches it at most every five minutes, or when refreshed by hand, with the session key from the encrypted bundle. A session cookie that the Console renews in an answer is saved at once. A failed billing fetch keeps the last values with a note and never affects usage polling. The Console sign-in uses a throw-away browser context whose data is deleted after the cookie is taken. Amounts are decimal, and a missing reset time stays missing (upstream substituted the current time).
32. **Codex credentials.** The Codex CLI's `auth.json` is read for each check and never renewed or written by the tracker; a rejected token waits for the CLI ("Waiting for the Codex CLI to refresh its login"), as plan section 2 requires until a Codex takeover is verified. Credentials pasted in the wizard are the tracker's own, kept in the encrypted bundle and renewed as upstream does, with the same rules as difference 13: a used token is never sent again, a refused one not until the credentials change, a failed renewal waits as in difference 13, and a renewal that cannot be saved is retried at each check unless the credentials were replaced or removed meanwhile. A profile whose `auth.json` is now signed in to another account says so instead of showing that account's usage.
33. **Network log.** Upstream's logger recorded URLs and request and response bodies during timed logging sessions. The port logs every request, always, but only its operation, method, fixed route (organisation IDs replaced by `{org}`), status, duration, transport and error category. Debug Tools shows the newest 200 entries, clears them after a confirmation and exports them as JSON lines with the same fields only.
34. **History.** A reset is detected when a window's reset time appears, disappears or moves by a minute or more against the previous usage, instead of upstream's in-memory per-profile maps compared to the minute. Each profile's history is its own file, loaded once; a damaged or newer-format file is reported on the History page and never overwritten. The weekly sample also records the design and Fable weekly values when present. Upstream's sampling intervals, retention limits, validity filter and JSON/CSV export formats are kept.
35. **Charts.** Drawn by hand rather than with a charting library: the combined usage chart (5h/24h/7d/30d, half-window navigation, Now), the billing chart (last 12 cycles, scale of at least 10 units) and the popover's cost per day (this month to date). Moving the pointer shows the values at that point, which upstream's charts did not.
36. **Removal is recoverable.** Upstream deleted a profile's data in place. The port saves the settings first with a pending-cleanup entry. Whatever cannot be deleted at once (a file in use, a browser still running) is deleted at the next start, and the app says so. The last profile cannot be deleted, as upstream.
37. **Every setup route tests before it saves.** Upstream's claude.ai and Console routes saved after listing organisations, and its Codex route saved without a usage request. Each route in the port runs one read-only request and shows a review before Done. The claude.ai route retries a freshly issued session that is refused for a moment (after 1.5, 3 and 4.5 seconds), as upstream. An organisation the profile already tracks stays selected. Settings pages open the wizard at the matching route rather than embedding a second form, and each removal asks for confirmation.
38. **Notifications.** Upstream's alerts, texts and thresholds (75, 90 and 95 %, custom ones, the session reset), through Windows notifications. Differences:
    - Every polled profile is notified, each with its own settings. Upstream notified the active profile in single-profile mode only and no profile in multi-profile mode.
    - Each threshold is notified once per session window, and that memory (`automation.json`) survives a restart. Upstream kept a list it cleared only when it saw a reset itself, so after a restart across a reset a threshold was never notified again. A session percentage that was not reported causes nothing, and the reset alert needs an observation from the same run, as upstream's in-memory comparison did.
    - Sounds are Windows' notification sound events (Default, Message, Mail, Reminder, Text Message) or None; the preview plays the file the user's sound scheme assigns. Upstream listed Apple's system sounds, which are not redistributed (plan.md section 2); such a name in a settings file plays the default sound.
    - "Usage Updated", the Console session-expiry warning and "Notifications Enabled" are upstream's, but follow the profile's notification switch; upstream posted the first two regardless. The expiry warning is posted by the running app during the last 24 hours instead of being scheduled with the system.
    - A click opens the popover on the notification's profile, also when it starts the app; upstream only brought the app forward. A profile that is not displayed is activated first, as the profile switcher does. A click on a fixture-mode notification never starts the app in live mode.
    - The app registers its own executable with its name and an icon it draws (no artwork ships). Registrations of moved or deleted copies are removed at startup (Milestone 1, D6); registrations of copies that still exist are kept.
39. **Session auto-start.** Upstream's opt-in feature, prompt, model and requests (create a conversation, send "Hi" to Claude Haiku, delete the conversation), sent through the profile's browser like every claude.ai request (difference 30). It is the only model request the app sends and is never part of polling. Differences:
    - It decides only on usage fetched by this run within the last five minutes; a polled profile uses its polling loop's result and is never fetched beside it.
    - It starts only when the session is reported at 0 % with no open window. Upstream also started when no session was reported at all.
    - After a start it waits until the new window's reset (or five hours), and after a failure 30 minutes, kept on disk across a restart. A failure is notified once until a start succeeds. Upstream tried again at every five-minute check, with a notification each time.
    - The conversation is deleted even when the message failed; upstream left it.
    - The settings card says that it uses usage.
40. **Profile auto-switch.** Upstream's trigger (the active profile at 100 %, once until it drops), target choice (the next profile with credentials whose effective session is below 100 %, wrapping round) and notification. Upstream marked the profile before looking for a target, so when no other profile had room it never switched until its own session dropped. Here it is marked only when it switches, so a later check switches once another profile's window has reset, and the mark is cleared whenever the profile is seen below its limit, not only while it is the active one. Upstream's activation also wrote the target's login into the CLI. Here Claude Code is switched only when it was on the profile that reached the limit, through the switch of difference 41, which waits while Claude Code runs; that wait is announced in a notification. The waiting switch is dropped if auto-switch is turned off or Claude Code leaves that account before it closes.
41. **Switching Claude Code's account.** A separate action, "Use in Claude Code" on the CLI Account page, writes the profile's login into the folder the plain `claude` command uses (plan.md section 2; the user's decision of 2026-10-02):
    - **When it runs:** only while no Claude Code process could be using the folder; until Milestone 5's launchers, any running `claude.exe` makes it wait. It waits, cancellably and in memory, and is tried every five seconds.
    - **Refusals:** a login marked as used elsewhere, an account another known folder holds, an unreadable folder or copy, a pending hand-back, a rejected login, an expired login that could not be renewed just now. The files are read again after any renewal; if Claude Code wrote or started meanwhile, nothing is written and the switch is tried again.
    - **Untracked logins:** a login no profile follows is replaced only after the user confirms, and never by an automatic switch.
    - **Order of work:**
      1. Encrypted backups of both files, the newest five kept.
      2. The outgoing account's latest login, saved to the profiles that follow it.
      3. An expired target renewed once by the tracker, which owns it at that point.
      4. A journal, then each file replaced only if unchanged since it was read, with every other member kept in its order.
      5. Read-back verification.

      A write that cannot be completed is undone only while the file still holds what the switch wrote.
    - **The journal:** while it exists, nothing is mirrored from or renewed in its folder, nothing links that folder, and no login is renewed anywhere. It is settled from the folder's own state: the target's record means the switch landed; the target's login beside the old record is completed over the file as it is; an unchanged login means nothing was written; a changed record means a newer sign-in, left alone. An unknown login beside the old record cannot be told apart from a new sign-in to the old account, so it stays blocked until the user signs in again in Claude Code and confirms it on the CLI Account page.
    - **Upstream's version:** it wrote the system keychain and `oauthAccount` on every activation, without any of these checks. The port writes the account's own `oauthAccount` record, which each copy now keeps, unknown fields included; a copy saved before Milestone 4 has only the link's identity fields until its next mirror.
    - **Afterwards:** the outgoing profile's copy becomes the tracker's own at its next check, and the switched profile's is Claude Code's again.
42. **Global shortcuts.** Upstream's four actions (toggle the popover, refresh, open settings, next profile) through Win32 `RegisterHotKey` on the tray's window instead of Carbon hot keys. Differences:
    - A shortcut needs Ctrl, Alt or the Windows key; upstream also accepted Shift alone, which would stop that character being typed anywhere.
    - A shortcut already used for another action is refused; upstream let two actions share one.
    - A shortcut Windows refuses because another app holds it is marked on the page; upstream only logged it.
    - Shortcuts are written the Windows way (for example Ctrl+Shift+U), with the key named in the keyboard layout's language.
43. **Launch at login.** A per-user `Run` entry for the copy that switched it on, instead of macOS `SMAppService`. A portable copy has no fixed path, so another copy's entry is reported and replaced only when switched on here. Task Manager's own on/off choice is reported and never changed. Fixture mode writes a scratch key instead.
44. **Wake, network and sessions.** Upstream refreshed after waking (with a 10-second debounce and a 3-second delay) and when the network became available. Here resume, the display coming back on (how Modern Standby shows a resume), a network becoming available and a console or remote desktop connect or unlock each trigger an early check of every polled profile, debounced by 10 seconds per profile. That check is not a manual refresh, so it never resends a rejected login, and a server-requested wait still holds. A connect also redraws the tray icons for the new display, and a disconnect or lock closes an attached popover. Upstream's retry of its status item after a headless start corresponds to the tray's `TaskbarCreated` handling (Milestone 2).
45. **The CLI Account page describes only the profile's own login.** The access-token type, subscription and scopes are shown only while the linked folder holds the profile's account; before, they were read from whatever login the folder held, which after a switch is another profile's.

## Upstream test inventory

The pinned upstream has 16 test files containing 211 test cases. Dispositions:

- **Ported**: translated; the Windows test is named. Method names drop the `test` prefix and use British spelling.
- **Ported, changed**: translated with a deliberate difference listed above.
- **Scheduled Mn**: needs Windows functionality first; its Windows acceptance test lands with milestone n.
- **Conditional**: applies only if the Milestone 1 gate selects the HTTP hook transport. If named pipes win, these are marked inapplicable and replaced by pipe ACL, framing, deadline, offline, burst and ordering tests. The Milestone 1 gate selected HTTP ([milestone-1.md](milestone-1.md) section 6), so these are due in Milestone 5.
- **Excluded**: not applicable, with the reason.

| Upstream file | Cases | Ported | Scheduled | Conditional | Excluded |
| --- | ---: | ---: | ---: | ---: | ---: |
| ClaudeUsageTests.swift | 18 | 13 | 0 | 0 | 5 |
| CodexProviderTests.swift | 28 | 28 | 0 | 0 | 0 |
| DateExtensionsTests.swift | 10 | 10 | 0 | 0 | 0 |
| LanguageManagerTests.swift | 3 | 3 | 0 | 0 | 0 |
| NSWindowFullScreenSpacesTests.swift | 3 | 0 | 3 | 0 | 0 |
| NotchHUDCoreTests.swift | 23 | 14 | 0 | 9 | 0 |
| NotchHookInstallerTests.swift | 6 | 0 | 5 | 0 | 1 |
| NotchHookServerTests.swift | 6 | 0 | 0 | 6 | 0 |
| ProfileKeychainMigrationTests.swift | 8 | 6 | 0 | 0 | 2 |
| ProfileProviderTests.swift | 13 | 11 | 0 | 0 | 2 |
| SessionKeyValidatorTests.swift | 33 | 25 | 0 | 0 | 8 |
| SharedDataStoreTests.swift | 16 | 1 | 8 | 0 | 7 |
| SupportReminderServiceTests.swift | 4 | 0 | 0 | 0 | 4 |
| TerminalLauncherServiceTests.swift | 8 | 3 | 4 | 0 | 1 |
| URLBuilderTests.swift | 20 | 17 | 0 | 0 | 3 |
| UsageStatusCalculatorTests.swift | 12 | 12 | 0 | 0 | 0 |
| **Total** | **211** | **143** | **20** | **15** | **33** |

### ClaudeUsageTests.swift

| Upstream case | Disposition | Windows test, milestone or reason |
| --- | --- | --- |
| `ClaudeUsageTests.testStatusLevelSafe` | Excluded | The deprecated `ClaudeUsage.statusLevel` has no production caller; `UsageStatusCalculator` supersedes it. The upstream case also asserts the same instance three times. |
| `ClaudeUsageTests.testStatusLevelModerate` | Excluded | As above |
| `ClaudeUsageTests.testStatusLevelCritical` | Excluded | As above |
| `ClaudeUsageTests.testEmptyUsage` | Excluded | No zero-filled placeholder usage (difference 1) |
| `ClaudeUsageTests.testEncodeDecode` | Ported, changed | `UsageCacheStoreTests.EncodeDecode`: the Windows usage cache file |
| `ClaudeUsageTests.testDecodeLegacyJSONWithoutDesignAndFableKeys` | Ported, changed | `UsageCacheStoreTests.OlderCacheWithoutNewerKeysLoadsWithThemMissing`: missing keys are null, not 0 (difference 1). macOS data is not imported. |
| `ClaudeUsageTests.testEncodeDecodeFableFields` | Ported | `UsageCacheStoreTests.EncodeDecodeFableFields` |
| `ProfileCustomKeychainTests.testDecodesOldPlistWithoutCustomKeychainField` | Ported, changed | `SettingsStoreTests.MilestoneTwoSettingsWithoutTheNewerLinksLoad`: settings from before the newer link fields load with them missing |
| `ProfileCustomKeychainTests.testHasAnyCredentialsRecognizesCustomKeychainPin` | Ported, changed | `SettingsStoreTests.UsageSourceFollowsUpstreamPrecedence`: a linked Claude Code folder is a usage source |
| `ClaudeCodeSyncServiceLogicTests.testSha256HexPrefixMatchesClaudeCodeKeychainHashAlgorithm` | Excluded | macOS Keychain naming; Windows Claude Code uses `<config dir>/.credentials.json` |
| `ClaudeCodeSyncServiceLogicTests.testMergeRefreshedCredentialsUpdatesAccessTokenAndExpiresAt` | Ported | `ClaudeCliCredentialsTests.MergeRefreshedCredentialsUpdatesAccessTokenAndExpiresAt` |
| `ClaudeCodeSyncServiceLogicTests.testMergeRefreshedCredentialsRotatesRefreshTokenWhenProvided` | Ported | `ClaudeCliCredentialsTests.MergeRefreshedCredentialsRotatesRefreshTokenWhenProvided` |
| `ClaudeCodeSyncServiceLogicTests.testMergeRefreshedCredentialsRejectsInputWithoutClaudeAiOauth` | Ported | `ClaudeCliCredentialsTests.MergeRefreshedCredentialsRejectsInputWithoutClaudeAiOauth` |
| `ClaudeCodeSyncServiceLogicTests.testDisplayLabelEmailAndOrg` | Ported, changed | `CliLoginTests.DisplayLabelEmailAndOrg`: a config folder name replaces the keychain service suffix |
| `ClaudeCodeSyncServiceLogicTests.testDisplayLabelEmailOnly` | Ported, changed | `CliLoginTests.DisplayLabelEmailOnly` |
| `ClaudeCodeSyncServiceLogicTests.testDisplayLabelDefaultShorthand` | Ported | `CliLoginTests.DisplayLabelDefaultShorthand`: "default" names the default config directory |
| `ClaudeCodeSyncServiceLogicTests.testDisplayLabelSubscriptionFallback` | Ported, changed | `CliLoginTests.DisplayLabelSubscriptionFallback` |
| `ClaudeCodeSyncServiceLogicTests.testDisplayLabelRawSvcFallback` | Ported, changed | `CliLoginTests.DisplayLabelRawFallback`: the full directory path replaces the full service name |

### CodexProviderTests.swift

All ported to `CodexTests`, with the same method names apart from the changes noted.

| Upstream case | Disposition | Windows test or note |
| --- | --- | --- |
| `testParseOAuthTokensSnakeCase` | Ported | `ParseOAuthTokensSnakeCase` |
| `testParseOAuthTokensCamelCase` | Ported | `ParseOAuthTokensCamelCase` |
| `testParseAPIKeyOnlyShape` | Ported | `ParseApiKeyOnlyShape` |
| `testParseGarbageReturnsNil` | Ported | `ParseGarbageReturnsNil` (3 rows) |
| `testNeedsRefreshWhenLastRefreshOld` | Ported | `NeedsRefreshWhenLastRefreshOld` |
| `testNoRefreshWhenRecent` | Ported | `NoRefreshWhenRecent` |
| `testNeedsRefreshWhenLastRefreshMissing` | Ported | `NeedsRefreshWhenLastRefreshMissing` |
| `testAccountIdFromNestedAuthClaim` | Ported | `AccountIdFromNestedAuthClaim` |
| `testAccountIdFromTopLevelClaim` | Ported | `AccountIdFromTopLevelClaim` |
| `testExplicitAccountIdWinsOverJWT` | Ported | `ExplicitAccountIdWinsOverJwt` |
| `testSerializePreservesUnknownTopLevelKeys` | Ported | `SerialisePreservesUnknownTopLevelKeys` |
| `testDecodeFullResponse` | Ported | `DecodeFullResponse` |
| `testDecodeMissingSecondaryWindowAndCredits` | Ported | `DecodeMissingSecondaryWindowAndCredits` |
| `testDecodeToleratesUnknownAndMalformedFields` | Ported, changed | `DecodeToleratesUnknownAndMalformedFields`: a malformed `has_credits` is null, not `false` (difference 1) |
| `testNormalizeStandardOrder` | Ported | `NormaliseStandardOrder` |
| `testNormalizeSwappedOrder` | Ported | `NormaliseSwappedOrder` |
| `testNormalizeLoneWeeklyWindow` | Ported | `NormaliseLoneWeeklyWindow` |
| `testNormalizeUnknownDurationDefaultsToSession` | Ported | `NormaliseUnknownDurationDefaultsToSession` |
| `testNormalizeUnknownPrimaryWithSessionSecondary` | Ported | `NormaliseUnknownPrimaryWithSessionSecondary` |
| `testMapToUsagePopulatesPercentagesAndZeroTokens` | Ported, changed | `MapToUsagePopulatesPercentagesAndLeavesTokensMissing` (difference 1) |
| `testMapToUsageMissingWeeklyGivesZeroPercent` | Ported, changed | `MapToUsageMissingWeeklyLeavesWeeklyMissing` (difference 1) |
| `testMapToUsageMissingResetAtReusesPreviousResetTimes` | Ported | `MapToUsageMissingResetAtReusesPreviousResetTimes` |
| `testMapToUsageMissingResetAtIgnoresExpiredPreviousResetTimes` | Ported, changed | `MapToUsageMissingResetAtIgnoresExpiredPreviousResetTimes`: the result is unknown rather than estimated (difference 2) |
| `testParseChatGPTBaseURLFromConfig` | Ported | `ParseChatGptBaseUrlFromConfig` |
| `testParseChatGPTBaseURLIgnoresCommentedOutLine` | Ported | `ParseChatGptBaseUrlIgnoresCommentedOutLine` |
| `testUsageURLDefaultsToWhamPath` | Ported | `UsageUrlDefaultsToWhamPath` (with and without a config file) |
| `testUsageURLNonBackendBaseUsesCodexPath` | Ported | `UsageUrlNonBackendBaseUsesCodexPath` |
| `testUsageURLBareChatGPTHostGetsBackendAPI` | Ported | `UsageUrlBareChatGptHostGetsBackendApi` |

### DateExtensionsTests.swift

Ported to `ResetTimeExtensionsTests`, using fixed time zones (difference 4).

| Upstream case | Disposition | Windows test |
| --- | --- | --- |
| `testNextMondayFromSunday` | Ported | `NextMondayFromSunday` |
| `testNextMondayFromMonday` | Ported | `NextMondayFromMonday` |
| `testNextMondayFromWednesday` | Ported | `NextMondayFromWednesday` |
| `testTimeRemainingHoursAndMinutes` | Ported | `TimeRemainingString` row "3h 45m" |
| `testTimeRemainingHoursOnly` | Ported | `TimeRemainingString` row "2h" |
| `testTimeRemainingMinutesOnly` | Ported | `TimeRemainingString` row "30m" |
| `testTimeRemainingDays` | Ported | `TimeRemainingString` row "3 days" |
| `testTimeRemainingOneDay` | Ported | `TimeRemainingString` row "1 day" |
| `testTimeRemainingPast` | Ported | `TimeRemainingString` row "Reset now" |
| `testTimeRemainingLessThanMinute` | Ported | `TimeRemainingString` row "< 1m" |

### LanguageManagerTests.swift

| Upstream case | Disposition | Windows test |
| --- | --- | --- |
| `testVietnameseCaseExists` | Ported | `SupportedLanguagesTests.VietnameseCaseExists` |
| `testVietnameseProperties` | Ported | `SupportedLanguagesTests.VietnameseProperties` |
| `testSupportedLanguageCountIs14` | Ported | `SupportedLanguagesTests.SupportedLanguageCountIs14` |

### NSWindowFullScreenSpacesTests.swift

| Upstream case | Disposition | Windows milestone |
| --- | --- | --- |
| `testEnableDisplayJoinsAllSpacesAndFullScreenSpaces` | Scheduled M5 | Windows equivalent: the popover and HUD stay available over full-screen apps and across virtual desktops (manual acceptance). The popover part passed on 2026-10-01 ([milestone-2.md](milestone-2.md#live-and-manual-checks)); the HUD part follows in M5 |
| `testEnableDisplayClearsMoveToActiveSpace` | Scheduled M5 | As above |
| `testEnableDisplayPreservesUnrelatedCollectionBehavior` | Scheduled M5 | As above; window style changes preserve unrelated flags |

### NotchHUDCoreTests.swift

| Upstream case | Disposition | Windows test or condition |
| --- | --- | --- |
| `HookHTTPParserTests.testCompleteRequestSinglePacket` | Conditional | HTTP transport only (Milestone 1 gate, then M5) |
| `HookHTTPParserTests.testMultiPacketBody` | Conditional | As above |
| `HookHTTPParserTests.testNonPOSTRejected` | Conditional | As above |
| `HookHTTPParserTests.testMissingContentLengthRejected` | Conditional | As above |
| `HookHTTPParserTests.testOversizedBodyReportedWithRemainingCount` | Conditional | As above |
| `HookHTTPParserTests.testUnauthorizedPathRejectedBeforeBodyArrives` | Conditional | As above |
| `HookHTTPParserTests.testUnauthorizedPathBeatsOversizeStatus` | Conditional | As above |
| `HookHTTPParserTests.testAuthorizedPathStillParses` | Conditional | As above |
| `HookHTTPParserTests.testOversizedHeaderRejected` | Conditional | As above |
| `ToolActivityMapperTests.testBashMapsToRunningCommandWithTruncatedCommand` | Ported | `ToolActivityMapperTests.BashMapsToRunningCommandWithTruncatedCommand` |
| `ToolActivityMapperTests.testEditorToolsMapToWritingCode` | Ported | `ToolActivityMapperTests.EditorToolsMapToWritingCode` (difference 5) |
| `ToolActivityMapperTests.testReadToolsMapToReadingFiles` | Ported | `ToolActivityMapperTests.ReadToolsMapToReadingFiles` |
| `ToolActivityMapperTests.testUnknownToolFallsBackToThinkingWithToolName` | Ported | `ToolActivityMapperTests.UnknownToolFallsBackToThinkingWithToolName` |
| `NotchHookEventTests.testEventRequiresSessionId` | Ported | `HookEventTests.EventRequiresSessionId` |
| `NotchHookEventTests.testUnknownPathReturnsNil` | Ported | `HookEventTests.UnknownPathReturnsNil` |
| `NotchHookEventTests.testKnownEventsDecode` | Ported | `HookEventTests.KnownEventsDecode` |
| `NotchSessionStoreTests.testOutOfOrderPreToolUseAutoCreatesSession` | Ported | `HudSessionStoreTests.OutOfOrderPreToolUseAutoCreatesSession` |
| `NotchSessionStoreTests.testNotificationSetsAttentionAndNextActivityClearsIt` | Ported | `HudSessionStoreTests.NotificationSetsAttentionAndNextActivityClearsIt` |
| `NotchSessionStoreTests.testStopSetsIdleAndSessionEndRemoves` | Ported | `HudSessionStoreTests.StopSetsIdleAndSessionEndRemoves` |
| `NotchSessionStoreTests.testToolFailureSetsErrorFlagAndPostToolUseClearsIt` | Ported | `HudSessionStoreTests.ToolFailureSetsErrorFlagAndPostToolUseClearsIt` |
| `NotchSessionStoreTests.testPrimarySessionPrefersAttention` | Ported | `HudSessionStoreTests.PrimarySessionPrefersAttention` |
| `NotchSessionStoreTests.testStaleSweepRemovesOldSessionsWithAttentionGrace` | Ported | `HudSessionStoreTests.StaleSweepRemovesOldSessionsWithAttentionGrace` |
| `NotchSessionStoreTests.testPromptTruncatedTo80` | Ported | `HudSessionStoreTests.PromptTruncatedTo80`. The pipe transport in plan.md does not forward prompts, so this path may carry no data in production. |

The reducer is upstream's. Milestone 5 adds the plan's stricter rules (tool-use correlation, no late `PreToolUse` reopening a completed tool, no revival of an ended session without corroborating activity) with their own tests.

### NotchHookInstallerTests.swift

| Upstream case | Disposition | Windows milestone or reason |
| --- | --- | --- |
| `testFreshInstallCreatesEightTokenedHooks` | Scheduled M5 | Eight hooks for the selected transport; the token-in-URL form applies only to HTTP |
| `testInstallPreservesForeignSettingsAndHooks` | Scheduled M5 | Transport-independent |
| `testLegacyTenHookConfigDetectedAndReplaced` | Excluded | Detects hooks from an abandoned upstream branch; no Windows installation wrote them |
| `testUninstallRemovesOnlyOurHooks` | Scheduled M5 | Transport-independent |
| `testUninstallOnMissingFileSucceeds` | Scheduled M5 | Transport-independent |
| `testPartialDetection` | Scheduled M5 | Feeds the degraded-integration state and repair action |

### NotchHookServerTests.swift

| Upstream case | Disposition | Condition |
| --- | --- | --- |
| `testTokenedEventReachesStore` | Conditional | HTTP transport only |
| `testLegacyTokenlessPathGets404AndIsDropped` | Conditional | HTTP transport only |
| `testWrongTokenGets404` | Conditional | HTTP transport only |
| `testGETRejected405` | Conditional | HTTP transport only |
| `testMalformedJSONStillGets200` | Conditional | HTTP transport only; the pipe route needs its own malformed-input test |
| `testOversizeBodyGets200AndIsDropped` | Conditional | HTTP transport only; the pipe route needs its own size-bound test |

### ProfileKeychainMigrationTests.swift

| Upstream case | Disposition | Windows milestone or reason |
| --- | --- | --- |
| `testEncodeExcludesSecretsByDefault` | Ported, changed | `RefreshCoordinatorTests.NoSecretReachesSettingsHistoryCacheOrLogFiles`, `SettingsStoreTests.EveryLinkAndDisplayOptionRoundTripsWithoutSecrets` |
| `testEncodeIncludesSecretsWithUserInfoFlag` | Excluded | The port has no plaintext fallback encoding (plan section 3, Milestone 2) |
| `testDecodeLegacyPlistWithSecrets` | Excluded | macOS data is not imported |
| `testV311ProfileWithPreDesignUsageCacheSurvivesUpgrade` | Ported, changed | `UsageCacheStoreTests.OlderCacheWithoutNewerKeysLoadsWithThemMissing` and `SettingsStoreTests.UnknownFieldsSurviveASave`: the cache is a separate file, and settings from other versions keep their members |
| `testCorruptUsageCacheDegradesToNilNotProfileLoss` | Ported, changed | `UsageCacheStoreTests.UnusableCacheDegradesToNull`; corrupt settings are reported and left unchanged instead (`SettingsStoreTests.DamagedSettingsAreReportedAndLeftUnchanged`, difference 17) |
| `testDecodeMinimalLegacyProfileAppliesDefaults` | Ported, changed | `SettingsStoreTests.DecodeMinimalLegacyProfileAppliesDefaults`: 30-second refresh, shown in the tray, session icon on; the overage-check flag arrives with extra usage |
| `testProfileSecretRoundTrip` | Ported, changed | `SecretStoreTests.ProfileSecretRoundTrip`: the DPAPI per-profile file; deletion is `CliLinkServiceTests.DeleteMirrorRemovesTheSecretButNotTheCliLogin` |
| `testFieldsAreIsolatedPerProfileAndField` | Ported, changed | `ProfileSecretsTests.CredentialsAreIsolatedPerProfileAndField` |

### ProfileProviderTests.swift

| Upstream case | Disposition | Windows test, milestone or reason |
| --- | --- | --- |
| `testLegacyProfileWithoutProviderDecodesAsAnthropic` | Excluded | Pre-provider profiles exist only in upstream macOS storage |
| `testUnknownProviderRawValueDegradesToAnthropic` | Ported, changed | `SettingsStoreTests.UnknownProviderRawValueDegradesToAnthropicButIsKept`: behaves as Anthropic, but the raw value is kept on save (difference 17) |
| `testNonStringProviderValueDegradesToAnthropic` | Ported | `SettingsStoreTests.NonStringProviderValueDegradesToAnthropic` |
| `testCodexProviderRoundTrips` | Ported | `SettingsStoreTests.CodexProviderRoundTrips` (the persisted value; Codex usage arrives in M3) |
| `testCodexCredentialsExcludedFromPlistEncoding` | Ported, changed | `SetupServiceTests.PastedCodexCredentialsAreEncryptedAndRemovedWhenFollowingTheCliAgain` |
| `testCodexCredentialsIncludedWithSecretsUserInfo` | Excluded | No plaintext fallback encoding |
| `testClaudeUsageLegacyJSONDecodesWithNilPlanAndCredits` | Ported | `UsageCacheStoreTests.OlderCacheWithoutNewerKeysLoadsWithThemMissing` |
| `testClaudeUsagePlanAndCreditsRoundTrip` | Ported | `UsageCacheStoreTests.PlanAndCreditsRoundTrip` |
| `testLegacyUsageSnapshotDecodesWithNilProvider` | Ported | `UsageHistoryTests.LegacyUsageSnapshotDecodesWithNilProvider` |
| `testCodexSnapshotSuppressesTokenCounts` | Ported | `ProviderTests.CodexSnapshotSuppressesTokenCounts` |
| `testAnthropicSnapshotKeepsTokenCounts` | Ported | `ProviderTests.AnthropicSnapshotKeepsTokenCounts` |
| `testEveryProviderHasADescriptor` | Ported | `ProviderTests.EveryProviderHasADescriptor` |
| `testCapabilityExpectations` | Ported | `ProviderTests.CapabilityExpectations` |

### SessionKeyValidatorTests.swift

Ported cases keep their names in `SessionKeyValidatorTests`.

| Upstream case | Disposition | Windows test or reason |
| --- | --- | --- |
| `testValidSessionKey` | Ported | `ValidSessionKey` |
| `testValidSessionKeyWithUnderscores` | Ported | `ValidSessionKeyWithUnderscores` |
| `testValidSessionKeyWithHyphens` | Ported | `ValidSessionKeyWithHyphens` |
| `testEmptySessionKey` | Ported | `EmptySessionKey` |
| `testWhitespaceOnlySessionKey` | Ported | `WhitespaceOnlySessionKey` |
| `testSessionKeyWithLeadingTrailingWhitespace` | Ported | `SessionKeyWithLeadingTrailingWhitespace` |
| `testInvalidPrefix` | Ported | `InvalidPrefix` |
| `testMissingPrefix` | Ported | `MissingPrefix` |
| `testTooShort` | Ported | `TooShort` |
| `testTooLong` | Ported | `TooLong` |
| `testInvalidCharacters` | Ported | `InvalidCharacters` |
| `testInternalWhitespace` | Ported | `InternalWhitespace` |
| `testNullBytes` | Ported | `NullBytes` |
| `testPathTraversal` | Ported | `PathTraversal` |
| `testScriptInjection` | Ported | `ScriptInjection` |
| `testValidFormat` | Ported | `ValidFormat` |
| `testInvalidFormatNoSeparators` | Ported | `InvalidFormatNoSeparators` |
| `testRelaxedConfiguration` | Excluded | Difference 6 |
| `testSanitization` | Excluded | Difference 6 |
| `testValidationStatusValid` | Ported | `ValidationStatusValid` |
| `testValidationStatusInvalid` | Ported | `ValidationStatusInvalid` |
| `testValidateResultSuccess` | Excluded | Difference 6 |
| `testValidateResultFailure` | Excluded | Difference 6 |
| `testStringExtensionValid` | Excluded | Difference 6 |
| `testStringExtensionInvalid` | Excluded | Difference 6 |
| `testMixedCasePrefix` | Ported | `MixedCasePrefix` |
| `testUnicodeCharacters` | Ported | `UnicodeCharacters` |
| `testErrorMessages` | Ported | `ErrorMessages` (3 rows) |
| `testRecoverySuggestions` | Ported | `RecoverySuggestions` |
| `testValidationPerformance` | Excluded | Timing measurement with no assertion |
| `testSanitizationPerformance` | Excluded | Timing measurement with no assertion; helper not ported |
| `testTypicalUserInput` | Ported | `TypicalUserInput` |
| `testTypicalMistakes` | Ported | `TypicalMistakes` (3 rows) |

### SharedDataStoreTests.swift

| Upstream case | Disposition | Windows milestone or reason |
| --- | --- | --- |
| `testLanguageCode` | Scheduled M6 | Persisted UI language |
| `testLanguageCodeNil` | Scheduled M6 | As above |
| `testStatuslineShowDirectory` | Scheduled M5 | Statusline settings default to shown and round-trip |
| `testStatuslineShowBranch` | Scheduled M5 | As above |
| `testStatuslineShowUsage` | Scheduled M5 | As above |
| `testStatuslineShowProgressBar` | Scheduled M5 | As above |
| `testStatuslineShowResetTime` | Scheduled M5 | As above |
| `testStatuslineShowModel` | Scheduled M5 | As above, including the default of shown |
| `testHasCompletedSetup` | Ported, changed | `SettingsStoreTests.HasCompletedSetup`: an isolated settings file, without upstream's legacy `~/.claude-session-key` check; set by every successful setup route |
| `testFirstLaunchDate` | Excluded | Used only by the GitHub-star and feedback prompts (difference 7) |
| `testLastGitHubStarPromptDate` | Excluded | Difference 7 |
| `testHasStarredGitHub` | Excluded | Difference 7 |
| `testNeverShowGitHubPrompt` | Excluded | Difference 7 |
| `testShouldShowGitHubStarPrompt` | Excluded | Difference 7 |
| `testShouldNotShowGitHubPromptWhenTooEarly` | Excluded | Difference 7 |
| `testResetGitHubStarPromptForTesting` | Excluded | Difference 7 |

### SupportReminderServiceTests.swift

| Upstream case | Disposition | Reason |
| --- | --- | --- |
| `testNotDueWithinFirstWeek` | Excluded | Difference 7 |
| `testDueAfterFirstWeekWhenNeverShown` | Excluded | Difference 7 |
| `testNotDueAgainWithinThirtyDaysOfLastShow` | Excluded | Difference 7 |
| `testDueAgainAfterThirtyDays` | Excluded | Difference 7 |

### TerminalLauncherServiceTests.swift

| Upstream case | Disposition | Windows test, milestone or reason |
| --- | --- | --- |
| `testSlugLowercasesAndDashesNonAlphanumerics` | Ported | `LauncherNamingTests.SlugLowercasesAndDashesNonAlphanumerics` (4 rows) |
| `testSlugFallsBackToIdForUnusableNames` | Ported | `LauncherNamingTests.SlugFallsBackToIdForUnusableNames` |
| `testInstallWritesExecutableScriptWithConfigDirAndMarker` | Scheduled M5 | Windows equivalent: `claude-<slug>.exe` alias, manifest entry and config directory |
| `testIsInstalledTracksMarkerOwnership` | Scheduled M5 | Ownership from the install manifest |
| `testCollidingNamesGetIdSuffixedSlug` | Ported | `LauncherNamingTests.CollidingNamesGetIdSuffixedSlug` (difference 9) |
| `testUninstallRemovesOnlyOwnScriptAndKeepsConfigDir` | Scheduled M5 | Removal keeps the login directory |
| `testExpectedKeychainServiceUsesPinnedHashAlgorithm` | Excluded | macOS Keychain naming |
| `testTerminalLauncherSlugSurvivesEncodeDecode` | Scheduled M5 | Slug persistence in the manifest |

### URLBuilderTests.swift

Ported cases keep their names in `UrlBuilderTests`, with `URL` written as `Url`. Upstream's "contains" assertions are exact here.

| Upstream case | Disposition | Windows test or reason |
| --- | --- | --- |
| `testValidBaseURL` | Ported | `ValidBaseUrl` |
| `testInvalidBaseURL` | Ported | `InvalidBaseUrl` |
| `testMissingScheme` | Ported | `MissingScheme` |
| `testAppendSinglePath` | Ported | `AppendSinglePath` |
| `testAppendMultiplePaths` | Ported | `AppendMultiplePaths` |
| `testAppendPathComponents` | Ported | `AppendPathComponents` |
| `testPathTraversalPrevention` | Ported | `PathTraversalPrevention` |
| `testAddSingleQueryParameter` | Ported | `AddSingleQueryParameter` |
| `testAddMultipleQueryParameters` | Ported | `AddMultipleQueryParameters` |
| `testAddQueryParametersDictionary` | Excluded | Difference 6 |
| `testEmptyParameterName` | Ported | `EmptyParameterName` |
| `testClaudeAPIBuilder` | Ported | `ClaudeApiBuilder` |
| `testConsoleAPIBuilder` | Ported | `ConsoleApiBuilder` |
| `testClaudeStatusBuilder` | Ported | `ClaudeStatusBuilder` |
| `testComplexClaudeURL` | Ported | `ComplexClaudeUrl` |
| `testBuildResultSuccess` | Excluded | Difference 6 |
| `testBuildResultFailure` | Ported | `BuildResultFailure`: upstream only asserts that appending an embedded `..` throws |
| `testURLWithSpecialCharacters` | Ported | `UrlWithSpecialCharacters` |
| `testURLWithMultipleSlashes` | Ported | `UrlWithMultipleSlashes` |
| `testBuilderPerformance` | Excluded | Timing measurement with no assertion |

### UsageStatusCalculatorTests.swift

All 12 cases are ported to `UsageStatusCalculatorTests` with the same names; multi-assert cases became theory rows.

| Upstream case | Disposition | Windows test |
| --- | --- | --- |
| `testUsedBasedThresholds_Safe` | Ported | `UsedBasedThresholds_Safe` |
| `testUsedBasedThresholds_Moderate` | Ported | `UsedBasedThresholds_Moderate` |
| `testUsedBasedThresholds_Critical` | Ported | `UsedBasedThresholds_Critical` |
| `testRemainingBasedThresholds_Safe` | Ported | `RemainingBasedThresholds_Safe` |
| `testRemainingBasedThresholds_Moderate` | Ported | `RemainingBasedThresholds_Moderate` |
| `testRemainingBasedThresholds_Critical` | Ported | `RemainingBasedThresholds_Critical` |
| `testGetDisplayPercentage_UsedMode` | Ported | `GetDisplayPercentage_UsedMode` |
| `testGetDisplayPercentage_RemainingMode` | Ported | `GetDisplayPercentage_RemainingMode` |
| `testBoundaryConditions_UsedMode` | Ported | `BoundaryConditions_UsedMode` |
| `testBoundaryConditions_RemainingMode` | Ported | `BoundaryConditions_RemainingMode` |
| `testNegativePercentage` | Ported | `NegativePercentage` |
| `testOverOneHundredPercentage` | Ported | `OverOneHundredPercentage` |

### Windows tests without an upstream case

These characterise upstream source that upstream did not test, or pin a Windows-specific decision:

- `UsageStatusCalculatorTests`: `PaceAwareStatus`, `ElapsedFractionWithinPeriod`, `ElapsedFractionAfterReset`, `ElapsedFractionClampsWhenResetIsBeyondThePeriod` and `ElapsedFractionIsUnknownWithoutResetOrDuration`.
- `ResetTimeExtensionsTests`: `NextMondayAcrossDaylightSavingStart`, `NextMondayUsesTheGivenTimeZone` and the "1d 2h" row of `TimeRemainingString`.
- `UrlBuilderTests`: `AppendedPathEscapesReservedCharacters`, `FragmentCharacterInPathDoesNotSwallowQuery` and `EncodedTraversalStaysInsideBasePath`. Upstream gets path escaping from `URLComponents.path`.
- `CodexTests`: `SerialiseWithoutExistingJsonCreatesDocument` and `SerialiseRejectsInvalidExistingJson` (difference 10).
- `ProviderTests.WeeklySnapshotGatesTokensAndModelsByCapability`.
- `LauncherNamingTests.OwnerKeepsItsInstalledSlug`.
- `SupportedLanguagesTests.ChineseCodesMapToExplicitCultures`.
- Milestone 4 (upstream has no tests for these features): `UsageAlertsTests`, `AutoSwitchTests`, `AutomationServiceTests`, `AutoStartServiceTests`, `CliSwitchTests`, `NotificationRegistrationsTests`, `LaunchAtLoginTests`, `MilestoneFourSettingsTests` and the `UsageFetched` and `CheckSoon` cases of `RefreshCoordinatorTests`.
