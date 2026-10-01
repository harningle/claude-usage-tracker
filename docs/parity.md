# Parity matrix

This file tracks how the Windows port relates to the upstream macOS app, fixed at [`588775e4540757443691fa1bcb33457315e05f86`](https://github.com/hamed-elfayome/Claude-Usage-Tracker/tree/588775e4540757443691fa1bcb33457315e05f86). [plan.md](../plan.md) holds the design; this file records what exists, what backs each claim, and where the port differs on purpose.

Evidence types, kept separate throughout:

- **Automated**: deterministic tests in [`tests/ClaudeUsageTracker.Tests`](../tests/ClaudeUsageTracker.Tests).
- **Manual**: checked by a person on Windows.
- **Live account**: exercised against a real account through the app.
- **Source/image**: compared only against upstream source or media.

Nothing here claims live authentication, Mac runtime parity or full feature parity. At Milestone 0 all evidence is automated or source-based. Milestone 1 experiment results, including one live-account observation, are in [milestone-1.md](milestone-1.md); they come from spike harnesses, not app features. Milestone 2's app features are verified by automated tests, by manual checks with synthetic fixture data and by the user's live checks on 2026-10-01 ([milestone-2.md](milestone-2.md)); the overnight soak and the waiting-state check are still pending.

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
| History snapshots at resets | 0 | Snapshot creation ported | Automated | Persistence, retention, charts and export in Milestone 3 |
| Codex auth.json, `wham/usage` decoding, window normalisation, mapping, base URL | 0 | Pure logic ported; no file access or network | Automated | Differences 1, 2, 3 and 10 |
| Claude Code credential merge after refresh | 0 | Pure merge ported | Automated | Difference 3; write-back flow in Milestone 3 |
| Activity HUD events, tool mapping, session reducer | 0 | Core logic ported; no transport or window | Automated | Difference 5; transport gate in Milestone 1, stricter ordering in Milestone 5 |
| Launcher slugs | 0 | Naming ported | Automated | Difference 9; executable aliases in Milestone 5 |
| Supported languages | 0 | List ported | Automated | Explicit `zh-cn` to `zh-Hans` mapping; resources in Milestone 6; flag emoji rendering on Windows unverified |
| UI text | 2 | Upstream English `Localizable.strings` embedded unchanged and looked up by key; port-only text in `Windows.strings`; other languages in Milestone 6 | Automated (every key used exists) | Differences 21 and 22 |
| Tray, popover, settings window, setup wizard | 1 spike, then 2 | Implemented for the Claude Code route: single instance, tray icons and menu, popover, sidebar settings (CLI Account, Appearance, General, Popover, About), wizard with test and review. Checked live by the user on 2026-10-01, including hardware rendering, dark mode, mixed DPI, keyboard selection, Explorer restart, full screen and virtual desktops | Automated; manual with fixture data; live account (manual) | Differences 14-20 and 23; plan section 2 |
| CLI linking and read-only OAuth usage | 1 gate, then 2 | Implemented: file linking with account checks, encrypted mirror of the CLI-owned login, read-only polling with backoff and `Retry-After`. Live setup, polling, restart and an account change checked on 2026-10-01; the overnight soak (G3) and the waiting state are pending | Automated; manual with fixture data; live account (manual) | Differences 11-13, 16 and 24 |
| Browser sign-in, organisations, Console billing, API costs | 1 gate, then 3 | Gate decided: claude.ai uses same-origin WebView2 fetch (replay challenged, browser 200); Console uses `HttpClient` at `platform.claude.com` (replay 200). 30-minute renewal soak and two-account isolation passed | Live account (manual, spike harness) | Plan section 2 |
| Profiles, secure storage, credential ownership | 2 and 3 | Milestone 2: versioned settings, per-profile usage cache and DPAPI secret files; one profile at a time. Profile lifecycle and ownership in Milestone 3 | Automated | DPAPI files replace Keychain; difference 17 |
| History persistence, charts, CSV/JSON export, network log | 3 | Not started | None | Redacted log only |
| Notifications, automation, safe CLI switching | 1 spike, then 4 | Spike: unpackaged self-contained registration and posting (one extracted runtime DLL), cold activation and running-instance clicks verified | Automated harness; manual | Plan section 2; after a move, notifications posted from the old path do nothing when clicked, and their registration stays until cleaned |
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
13. **CLI-owned tokens are never refreshed in Milestone 2.** Upstream either took over an expired token, rotating it without process or file checks, or silently skipped the refresh. The port shows "Waiting for Claude Code to refresh its login" (a new string), keeps the labelled stale usage and re-reads the file at each interval. The guarded takeover is Milestone 3. The encrypted mirror holds only the `claudeAiOauth` object, not other logins in the file such as MCP servers'.
14. **The wizard tests before saving.** Upstream's Claude Code route copied the login without a test, a review or the setup-completed flag. It fell back to the manual route silently on any failure, and re-read a possibly different login at save time. The port:
    - runs one read-only usage request, then shows a review (profile, account, organisation, plan, folder, usage)
    - re-reads the folder on Done and refuses a login whose account has changed since the test
    - saves nothing after a failed test, and offers Back and Retry
    - says an expired login is expired rather than "not found"
    - labels browser sign-in, session keys, the Console and Codex as arriving later

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
    - waits at least the server's `Retry-After` after a 429 or a 5xx, which a manual refresh does not override
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
    - There is no profile switcher menu until profiles can be managed (Milestone 3); with one profile it would have nothing to switch to.
    - The status link opens status.claude.com, as upstream's does.
    - In light mode the background is a chosen neutral, because upstream has no light screenshot. Moderate, critical and caution text use darker shades than upstream's, for contrast.
    - Dragging the header detaches the popover, the same gesture as upstream; any tray click then closes it.
    - Opened from the keyboard, focus moves into the popover; upstream cleared focus.
20. **Settings window.** Standard Windows chrome replaces upstream's borderless window and traffic-light buttons, and only the sections that work in this milestone are listed.
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

## Upstream test inventory

The pinned upstream has 16 test files containing 211 test cases. Dispositions:

- **Ported**: translated; the Windows test is named. Method names drop the `test` prefix and use British spelling.
- **Ported, changed**: translated with a deliberate difference listed above.
- **Scheduled Mn**: needs Windows functionality first; its Windows acceptance test lands with milestone n.
- **Conditional**: applies only if the Milestone 1 gate selects the HTTP hook transport. If named pipes win, these are marked inapplicable and replaced by pipe ACL, framing, deadline, offline, burst and ordering tests. The Milestone 1 gate selected HTTP ([milestone-1.md](milestone-1.md) section 6), so these are due in Milestone 5.
- **Excluded**: not applicable, with the reason.

| Upstream file | Cases | Ported | Scheduled | Conditional | Excluded |
| --- | ---: | ---: | ---: | ---: | ---: |
| ClaudeUsageTests.swift | 18 | 11 | 2 | 0 | 5 |
| CodexProviderTests.swift | 28 | 28 | 0 | 0 | 0 |
| DateExtensionsTests.swift | 10 | 10 | 0 | 0 | 0 |
| LanguageManagerTests.swift | 3 | 3 | 0 | 0 | 0 |
| NSWindowFullScreenSpacesTests.swift | 3 | 0 | 3 | 0 | 0 |
| NotchHUDCoreTests.swift | 23 | 14 | 0 | 9 | 0 |
| NotchHookInstallerTests.swift | 6 | 0 | 5 | 0 | 1 |
| NotchHookServerTests.swift | 6 | 0 | 0 | 6 | 0 |
| ProfileKeychainMigrationTests.swift | 8 | 4 | 2 | 0 | 2 |
| ProfileProviderTests.swift | 13 | 9 | 2 | 0 | 2 |
| SessionKeyValidatorTests.swift | 33 | 25 | 0 | 0 | 8 |
| SharedDataStoreTests.swift | 16 | 1 | 8 | 0 | 7 |
| SupportReminderServiceTests.swift | 4 | 0 | 0 | 0 | 4 |
| TerminalLauncherServiceTests.swift | 8 | 3 | 4 | 0 | 1 |
| URLBuilderTests.swift | 20 | 17 | 0 | 0 | 3 |
| UsageStatusCalculatorTests.swift | 12 | 12 | 0 | 0 | 0 |
| **Total** | **211** | **137** | **26** | **15** | **33** |

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
| `ProfileCustomKeychainTests.testDecodesOldPlistWithoutCustomKeychainField` | Scheduled M3 | Windows equivalent: a CLI-linked config-directory reference in profile settings |
| `ProfileCustomKeychainTests.testHasAnyCredentialsRecognizesCustomKeychainPin` | Scheduled M3 | Windows equivalent: a CLI-linked store counts as a credential source |
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
| `testEncodeExcludesSecretsByDefault` | Scheduled M3 | Windows equivalent: settings, history, cache and log files never contain secrets |
| `testEncodeIncludesSecretsWithUserInfoFlag` | Excluded | The port has no plaintext fallback encoding (plan section 3, Milestone 2) |
| `testDecodeLegacyPlistWithSecrets` | Excluded | macOS data is not imported |
| `testV311ProfileWithPreDesignUsageCacheSurvivesUpgrade` | Ported, changed | `UsageCacheStoreTests.OlderCacheWithoutNewerKeysLoadsWithThemMissing` and `SettingsStoreTests.UnknownFieldsSurviveASave`: the cache is a separate file, and settings from other versions keep their members |
| `testCorruptUsageCacheDegradesToNilNotProfileLoss` | Ported, changed | `UsageCacheStoreTests.UnusableCacheDegradesToNull`; corrupt settings are reported and left unchanged instead (`SettingsStoreTests.DamagedSettingsAreReportedAndLeftUnchanged`, difference 17) |
| `testDecodeMinimalLegacyProfileAppliesDefaults` | Ported, changed | `SettingsStoreTests.DecodeMinimalLegacyProfileAppliesDefaults`: 30-second refresh, shown in the tray, session icon on; the overage-check flag arrives with extra usage |
| `testProfileSecretRoundTrip` | Ported, changed | `SecretStoreTests.ProfileSecretRoundTrip`: the DPAPI per-profile file; deletion is `CliLinkServiceTests.DeleteMirrorRemovesTheSecretButNotTheCliLogin` |
| `testFieldsAreIsolatedPerProfileAndField` | Scheduled M3 | Windows equivalent: per-profile secret isolation and deletion |

### ProfileProviderTests.swift

| Upstream case | Disposition | Windows test, milestone or reason |
| --- | --- | --- |
| `testLegacyProfileWithoutProviderDecodesAsAnthropic` | Excluded | Pre-provider profiles exist only in upstream macOS storage |
| `testUnknownProviderRawValueDegradesToAnthropic` | Ported, changed | `SettingsStoreTests.UnknownProviderRawValueDegradesToAnthropicButIsKept`: behaves as Anthropic, but the raw value is kept on save (difference 17) |
| `testNonStringProviderValueDegradesToAnthropic` | Ported | `SettingsStoreTests.NonStringProviderValueDegradesToAnthropic` |
| `testCodexProviderRoundTrips` | Ported | `SettingsStoreTests.CodexProviderRoundTrips` (the persisted value; Codex usage arrives in M3) |
| `testCodexCredentialsExcludedFromPlistEncoding` | Scheduled M3 | Codex credentials only in the DPAPI secret file |
| `testCodexCredentialsIncludedWithSecretsUserInfo` | Excluded | No plaintext fallback encoding |
| `testClaudeUsageLegacyJSONDecodesWithNilPlanAndCredits` | Ported | `UsageCacheStoreTests.OlderCacheWithoutNewerKeysLoadsWithThemMissing` |
| `testClaudeUsagePlanAndCreditsRoundTrip` | Ported | `UsageCacheStoreTests.PlanAndCreditsRoundTrip` |
| `testLegacyUsageSnapshotDecodesWithNilProvider` | Scheduled M3 | History compatibility across Windows versions |
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
