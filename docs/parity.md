# Parity matrix

This file tracks how the Windows port relates to the upstream macOS app, fixed at [`588775e4540757443691fa1bcb33457315e05f86`](https://github.com/hamed-elfayome/Claude-Usage-Tracker/tree/588775e4540757443691fa1bcb33457315e05f86). [plan.md](../plan.md) holds the design; this file records what exists, what backs each claim, and where the port differs on purpose.

Evidence types, kept separate throughout:

- **Automated**: deterministic tests in [`tests/ClaudeUsageTracker.Tests`](../tests/ClaudeUsageTracker.Tests).
- **Manual**: checked by a person on Windows.
- **Live account**: exercised against a real account through the app.
- **Source/image**: compared only against upstream source or media.

Nothing here claims live authentication, Mac runtime parity or full feature parity. At Milestone 0 all evidence is automated or source-based. Milestone 1 experiment results, including one live-account observation, are in [milestone-1.md](milestone-1.md); they come from spike harnesses, not app features.

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
| Tray, popover, settings window, setup wizard | 1 spike, then 2 | Spike: path-derived tray GUIDs, overflow and taskbar clicks, icon rectangles, Explorer-restart re-add and a move (new identity; placement reset) verified; no app UI | Automated harness; manual | Plan section 2; unsigned GUID reuse re-binds rather than fails on Windows 11 26200 |
| CLI linking and read-only OAuth usage | 1 gate, then 2 | Gate passed: `/api/oauth/usage` returned usage for a Pro and a Team account (2026-09-30/10-01); linking not started | Live account (spike probe) | Difference 11 |
| Browser sign-in, organisations, Console billing, API costs | 1 gate, then 3 | Gate decided: claude.ai uses same-origin WebView2 fetch (replay challenged, browser 200); Console uses `HttpClient` at `platform.claude.com` (replay 200). 30-minute renewal soak and two-account isolation passed | Live account (manual, spike harness) | Plan section 2 |
| Profiles, secure storage, credential ownership | 2 and 3 | Not started | None | DPAPI files replace Keychain |
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

## Upstream test inventory

The pinned upstream has 16 test files containing 211 test cases. Dispositions:

- **Ported**: translated; the Windows test is named. Method names drop the `test` prefix and use British spelling.
- **Ported, changed**: translated with a deliberate difference listed above.
- **Scheduled Mn**: needs Windows functionality first; its Windows acceptance test lands with milestone n.
- **Conditional**: applies only if the Milestone 1 gate selects the HTTP hook transport. If named pipes win, these are marked inapplicable and replaced by pipe ACL, framing, deadline, offline, burst and ordering tests. The Milestone 1 gate selected HTTP ([milestone-1.md](milestone-1.md) section 6), so these are due in Milestone 5.
- **Excluded**: not applicable, with the reason.

| Upstream file | Cases | Ported | Scheduled | Conditional | Excluded |
| --- | ---: | ---: | ---: | ---: | ---: |
| ClaudeUsageTests.swift | 18 | 3 | 10 | 0 | 5 |
| CodexProviderTests.swift | 28 | 28 | 0 | 0 | 0 |
| DateExtensionsTests.swift | 10 | 10 | 0 | 0 | 0 |
| LanguageManagerTests.swift | 3 | 3 | 0 | 0 | 0 |
| NSWindowFullScreenSpacesTests.swift | 3 | 0 | 3 | 0 | 0 |
| NotchHUDCoreTests.swift | 23 | 14 | 0 | 9 | 0 |
| NotchHookInstallerTests.swift | 6 | 0 | 5 | 0 | 1 |
| NotchHookServerTests.swift | 6 | 0 | 0 | 6 | 0 |
| ProfileKeychainMigrationTests.swift | 8 | 0 | 6 | 0 | 2 |
| ProfileProviderTests.swift | 13 | 4 | 7 | 0 | 2 |
| SessionKeyValidatorTests.swift | 33 | 25 | 0 | 0 | 8 |
| SharedDataStoreTests.swift | 16 | 0 | 9 | 0 | 7 |
| SupportReminderServiceTests.swift | 4 | 0 | 0 | 0 | 4 |
| TerminalLauncherServiceTests.swift | 8 | 3 | 4 | 0 | 1 |
| URLBuilderTests.swift | 20 | 17 | 0 | 0 | 3 |
| UsageStatusCalculatorTests.swift | 12 | 12 | 0 | 0 | 0 |
| **Total** | **211** | **119** | **44** | **15** | **33** |

### ClaudeUsageTests.swift

| Upstream case | Disposition | Windows test, milestone or reason |
| --- | --- | --- |
| `ClaudeUsageTests.testStatusLevelSafe` | Excluded | The deprecated `ClaudeUsage.statusLevel` has no production caller; `UsageStatusCalculator` supersedes it. The upstream case also asserts the same instance three times. |
| `ClaudeUsageTests.testStatusLevelModerate` | Excluded | As above |
| `ClaudeUsageTests.testStatusLevelCritical` | Excluded | As above |
| `ClaudeUsageTests.testEmptyUsage` | Excluded | No zero-filled placeholder usage (difference 1) |
| `ClaudeUsageTests.testEncodeDecode` | Scheduled M2 | Round trip of the Windows usage cache format |
| `ClaudeUsageTests.testDecodeLegacyJSONWithoutDesignAndFableKeys` | Scheduled M2 | Older Windows cache versions load. macOS data is not imported. |
| `ClaudeUsageTests.testEncodeDecodeFableFields` | Scheduled M2 | Round trip of per-model fields |
| `ProfileCustomKeychainTests.testDecodesOldPlistWithoutCustomKeychainField` | Scheduled M3 | Windows equivalent: a CLI-linked config-directory reference in profile settings |
| `ProfileCustomKeychainTests.testHasAnyCredentialsRecognizesCustomKeychainPin` | Scheduled M3 | Windows equivalent: a CLI-linked store counts as a credential source |
| `ClaudeCodeSyncServiceLogicTests.testSha256HexPrefixMatchesClaudeCodeKeychainHashAlgorithm` | Excluded | macOS Keychain naming; Windows Claude Code uses `<config dir>/.credentials.json` |
| `ClaudeCodeSyncServiceLogicTests.testMergeRefreshedCredentialsUpdatesAccessTokenAndExpiresAt` | Ported | `ClaudeCliCredentialsTests.MergeRefreshedCredentialsUpdatesAccessTokenAndExpiresAt` |
| `ClaudeCodeSyncServiceLogicTests.testMergeRefreshedCredentialsRotatesRefreshTokenWhenProvided` | Ported | `ClaudeCliCredentialsTests.MergeRefreshedCredentialsRotatesRefreshTokenWhenProvided` |
| `ClaudeCodeSyncServiceLogicTests.testMergeRefreshedCredentialsRejectsInputWithoutClaudeAiOauth` | Ported | `ClaudeCliCredentialsTests.MergeRefreshedCredentialsRejectsInputWithoutClaudeAiOauth` |
| `ClaudeCodeSyncServiceLogicTests.testDisplayLabelEmailAndOrg` | Scheduled M2 | Windows equivalent: labels for discovered CLI config directories in setup |
| `ClaudeCodeSyncServiceLogicTests.testDisplayLabelEmailOnly` | Scheduled M2 | As above |
| `ClaudeCodeSyncServiceLogicTests.testDisplayLabelDefaultShorthand` | Scheduled M2 | As above |
| `ClaudeCodeSyncServiceLogicTests.testDisplayLabelSubscriptionFallback` | Scheduled M2 | As above |
| `ClaudeCodeSyncServiceLogicTests.testDisplayLabelRawSvcFallback` | Scheduled M2 | As above |

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
| `testEnableDisplayJoinsAllSpacesAndFullScreenSpaces` | Scheduled M2 and M5 | Windows equivalent: the popover and HUD stay available over full-screen apps and across virtual desktops (manual acceptance) |
| `testEnableDisplayClearsMoveToActiveSpace` | Scheduled M2 and M5 | As above |
| `testEnableDisplayPreservesUnrelatedCollectionBehavior` | Scheduled M2 and M5 | As above; window style changes preserve unrelated flags |

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
| `testV311ProfileWithPreDesignUsageCacheSurvivesUpgrade` | Scheduled M2 | Windows equivalent: older Windows settings versions load without losing profiles |
| `testCorruptUsageCacheDegradesToNilNotProfileLoss` | Scheduled M2 | Windows equivalent: a corrupt, re-fetchable cache never loses profiles; corrupt settings are reported, not silently reset |
| `testDecodeMinimalLegacyProfileAppliesDefaults` | Scheduled M2 | Windows profile defaults: 30-second refresh, overage check on, shown in the tray |
| `testProfileSecretRoundTrip` | Scheduled M2 | Windows equivalent: DPAPI per-profile secret file round trip |
| `testFieldsAreIsolatedPerProfileAndField` | Scheduled M3 | Windows equivalent: per-profile secret isolation and deletion |

### ProfileProviderTests.swift

| Upstream case | Disposition | Windows test, milestone or reason |
| --- | --- | --- |
| `testLegacyProfileWithoutProviderDecodesAsAnthropic` | Excluded | Pre-provider profiles exist only in upstream macOS storage |
| `testUnknownProviderRawValueDegradesToAnthropic` | Scheduled M2 | Profile settings from a newer version load without losing the list. Whether an unknown provider should degrade to Anthropic is an open decision. |
| `testNonStringProviderValueDegradesToAnthropic` | Scheduled M2 | As above |
| `testCodexProviderRoundTrips` | Scheduled M3 | Codex profile persistence |
| `testCodexCredentialsExcludedFromPlistEncoding` | Scheduled M3 | Codex credentials only in the DPAPI secret file |
| `testCodexCredentialsIncludedWithSecretsUserInfo` | Excluded | No plaintext fallback encoding |
| `testClaudeUsageLegacyJSONDecodesWithNilPlanAndCredits` | Scheduled M2 | Usage cache compatibility across Windows versions |
| `testClaudeUsagePlanAndCreditsRoundTrip` | Scheduled M2 | Usage cache round trip |
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
| `testHasCompletedSetup` | Scheduled M2 | Setup wizard completion state |
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
