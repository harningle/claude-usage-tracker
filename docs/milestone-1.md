# Milestone 1 experiment log

Results of the platform and authentication experiments in [plan.md section 3](../plan.md#milestone-1---resolve-platform-and-authentication-risks). Harnesses live in [`spikes/`](../spikes); they are experiment code, not production code, and are not part of `ClaudeUsageTracker.slnx`. Evidence types follow [parity.md](parity.md): automated harness runs, manual checks and live-account observations are kept separate.

Recorded 2026-09-30/2026-10-01 on the development machine.

## Environment

- Windows 11 Home 10.0.26200, x64. TCP dynamic port range 1024-15000 (13977 ports); excluded ranges 27339 and 50000-50059.
- .NET SDK 10.0.401, runtime 10.0.12.
- Claude Code **2.1.286** native at `%USERPROFILE%\.local\bin\claude.exe` (it auto-updated from the 2.1.285 recorded in Milestone 0). Git Bash at `C:\Program Files\Git\bin\bash.exe`; Windows PowerShell 5.1; no PowerShell 7.
- Visual Studio Build Tools 2026 (18.10) with the VC x64/x86 tools component and workload, MSVC 14.51.36231, Windows SDK 10.0.26100.
- WebView2 Runtime 154.0.4258.37; WebView2 SDK 1.0.4258.31.
- Windows App SDK Foundation 2.3.12 (the pairing in the `Microsoft.WindowsAppSDK` 2.5.1 metapackage). The `MicrosoftCorporationII.WinAppRuntime.Singleton` 8002.5.1.0 package is already installed on this machine by other software.
- No Claude Code managed settings (`C:\Program Files\ClaudeCode\managed-settings.json` absent) and no `ClaudeCode` policy registry keys.

## Gate summary

| Gate | Status | Evidence |
| --- | --- | --- |
| NativeAOT prerequisites | **Passed** | Automated: production helper and helper spike publish with NativeAOT |
| Statusline helper latency | **Passed** directly and through Git Bash and cmd; PowerShell 5.1 runner misses 100 ms because of its own startup | Automated timing |
| Read-only `/api/oauth/usage` with CLI credentials | **Passed** for a Pro and a Team account | Live accounts, 7 + 5 requests |
| Browser transport (claude.ai) | **Decided by the plan's rule: same-origin WebView2 fetch.** Signed in, the browser route returned 200 for organisations and usage on every attempt, including after an app restart. Every `HttpClient` replay variant was challenged by Cloudflare. A 30-minute polling soak returned 200 on 122 of 122 browser requests across two `__cf_bm` renewals. Two profiles signed in to different accounts were isolated, and a work (Team) account gave the same browser/replay split as the personal (Pro) one | Live accounts; manual |
| Console authentication | **HttpClient works** against `platform.claude.com/api`: 200 with `sessionKey` alone and no Cloudflare challenge | Live account; manual |
| WebView2 cost | Measured: one and three profiles, signed out; one profile signed in; full page and parked | Automated harness; live account |
| Tray identity after move | **Verified**: overflow and taskbar clicks, Explorer restart, and a move (new identity; placement preference reset) | Automated probes; manual |
| Notifications, self-contained, unpackaged | **Verified**: registration and posting (with one packaging workaround), cold activation, clicks in a running instance, and the stale-registration behaviour after a move | Automated harness; manual |
| Hook transport | HTTP **passes** the latency gate: measured in detail with the real CLI against a local fake Messages API, then confirmed on the stopped-listener case with an approved real-model run. **Decided:** HTTP for all eight events; the SessionStart entry is inert on this CLI | Automated harness; live account (40 Haiku tool calls) |
| Hook settings live reload | **Verified** for `<CLAUDE_CONFIG_DIR>\settings.json`; not for `--settings` files | Automated harness |

## 1. NativeAOT prerequisites and helper timing

The VC tools component now resolves through `vswhere` (the Milestone 0 query had returned nothing). `dotnet publish src/ClaudeUsageTracker.Helper/ClaudeUsageTracker.Helper.csproj -c Release -r win-x64` produces a 1.1 MB native executable; the helper spike is 1.9 MB.

One environment trap: when `NoDefaultCurrentDirectoryInExePath` is set (the agent shell that ran these experiments sets it; it is not set at user or machine level here), `VsDevCmd.bat` fails to run `vswhere.exe` from its `pushd` directory, ILCompiler captures that error text into the linker path and linking fails with MSB3073. Unsetting the variable for the publish fixes it.

[`HelperSpike`](../spikes/HelperSpike) is a minimal NativeAOT stand-in: `statusline` parses Claude Code stdin (`JsonDocument`) and prints one line; `hook-pipe` reads bounded stdin and forwards identifiers to a current-user named pipe with a 50 ms connect timeout. [`HookBench time-helper`](../spikes/HookBench/HelperTiming.cs) measured 50 warm runs per case (after 5 warm-ups) from .NET `Process.Start` to exit, with a realistic statusline payload:

| Case | p50 ms | p95 ms | max ms |
| --- | ---: | ---: | ---: |
| Direct `noop` | 7.8 | 9.8 | 10.4 |
| Direct `statusline` | 8.1 | 8.6 | 8.6 |
| Git Bash `bash -c '<exe>' statusline` | 30.5 | 31.9 | 32.8 |
| Git Bash, shell only | 20.4 | 25.1 | 29.4 |
| `cmd /d /s /c` statusline | 17.6 | 18.9 | 20.5 |
| PowerShell 5.1 `-NoProfile -Command "& '<exe>' statusline"` | 156.7 | 161.7 | 165.5 |
| PowerShell 5.1, shell only | 141.3 | 144.9 | 147.4 |
| Direct `hook-pipe`, server up | 8.5 | 10.4 | 15.5 |
| Direct `hook-pipe`, no server | 77.8 | 79.2 | 80.1 |
| First run of a freshly copied executable | 31.6 | 34.7 | 35.3 |

Against the plan's targets (warm p95 below 100 ms, first run below 300 ms): the helper itself and the Git Bash runner that Claude Code uses when Git Bash is installed pass comfortably. The PowerShell runner fails the end-to-end target on PowerShell startup alone. `hook-pipe` without a server takes about 78 ms because .NET's timed `Connect` keeps retrying until its timeout; Milestone 5 can check for the pipe's existence first. How exactly Claude Code spawns its statusline shell, and rapid replacement/cancellation, still need measuring with the interactive CLI (Milestone 5).

## 2. Read-only CLI usage endpoint

[`OAuthUsageProbe`](../spikes/OAuthUsageProbe) reads `%USERPROFILE%\.claude\.credentials.json` without locking or writing it, stops if the access token is expired or expiring (it never refreshes a CLI-owned token), and sends `GET https://api.anthropic.com/api/oauth/usage` with upstream's headers (`Authorization: Bearer`, `anthropic-beta: oauth-2025-04-20`, `User-Agent: claude-code/2.1.5`). It prints only metadata and response shape. No `/v1/messages` request was made.

Live account, 2026-09-30 22:14-22:17 UTC, Claude Code 2.1.286, `subscriptionType` pro, `rateLimitTier` default_claude_ai, scopes including `user:profile` and `user:inference`:

- 7 of 7 requests returned **200 `application/json`** (one single request, then six at a 30-second cadence), 211-337 ms. No `Retry-After`, rate-limit or Cloudflare headers.
- The body has the claude.ai usage shape: `five_hour` and `seven_day` with numeric `utilization` and string `resets_at`; null legacy per-model windows (`seven_day_opus`, `seven_day_sonnet`, `seven_day_omelette` and others); `extra_usage`; `limits[]` entries with `kind`, `group`, `percent`, `severity`, `resets_at`, `scope`, `is_active`; `spend`; `seven_day_breakdown`; and several code-named windows. Utilisation moved during the sample, so the data is live.

Second account type (live account, 2026-09-30 23:59 - 2026-10-01 00:01 UTC): your work account, the default Claude Code login since 00:40 local time on 2026-10-01, with `subscriptionType` team and `rateLimitTier` default_claude_max_5x. All 5 requests at a 30-second cadence returned **200 `application/json`**, 219-412 ms, with no throttling headers. The shape differs from the Pro response, so the parser must treat every window and field as optional:

- `limits[]` had 3 entries, not 2.
- `extra_usage.utilization` was null (extra usage not enabled), and `spend.limit` was null.
- There was no `seven_day_breakdown`.
- The code-named window `nimbus_quill` was present in the first two responses and **absent from the next three** (the body shrank from 2073 to 1954 bytes), so windows can appear and disappear between polls.

Conclusion: the plan's read-only route works for Pro and Team accounts today, so Milestone 2 can begin with it. Not yet established: Max and Enterprise accounts, behaviour at a rate limit, and longer-term throttling of 30-second polling.

## 3. Browser authentication (WebView2)

[`AuthSpike`](../spikes/AuthSpike) hosts one WebView2 per profile, each with its own user-data folder under `%LOCALAPPDATA%\ClaudeUsageTracker.Spikes\auth\profiles`. "Compare" runs the same read-only requests three or four ways and appends allowlisted metadata to `results.jsonl`:

1. Same-origin `fetch` in the page (via DevTools `Runtime.evaluate`), with credentials.
2. `HttpClient` (HTTP/2) with a cookie container built from the profile's `sessionKey`, `cf_clearance`, `__cf_bm` and `anthropic-device-id` cookies (domain, path and expiry preserved), the page's own `navigator.userAgent` and upstream's `Accept`/`Referer`/`Origin` headers.
3. As 2, with `sessionKey` only.
4. As 2, with upstream's hard-coded Safari user agent.

It never logs cookie values, bodies or organisation IDs.

Signed out (automated, 2026-10-01; no account involved), `GET https://claude.ai/api/organizations` gave the same outcome for every profile in five runs of one to three profiles each, on new and reused user-data folders:

| Route | Result |
| --- | --- |
| Browser fetch | 403 `application/json` (an API authentication error), about 136 ms |
| Replay, all four cookies, WebView2 user agent | 403 `text/html`, `cf-mitigated` header, "Just a moment" challenge page |
| Replay, `sessionKey` only | Same challenge |
| Replay, upstream Safari user agent | Same challenge |

Cloudflare let the browser's request through to the API but challenged every replay, even with the browser's own `cf_clearance` and `__cf_bm` and the identical user agent. This points to transport-level fingerprinting rather than missing cookies. It is strong evidence for the plan's browser-fetch route, but the plan's rule needs the signed-in comparison: authenticated data from the browser, and the replay outcome with a valid `sessionKey`.

Cost, measured signed out:

- One profile on the full claude.ai page: 252 MB private (5 processes) at load, growing to **635 MB private** (8 processes) within about 5 s.
- One profile parked on the lightweight same-origin document `https://claude.ai/robots.txt`: **175 MB private** (5 processes). Same-origin fetch still works from there.
- Three parked profiles at once: 462-470 MB private for all browser groups (about 155 MB each, 15 processes); host process 190-340 MB.
- Startup per profile: environment and control ready in 170-210 ms, first navigation complete in 250-300 ms (existing and new user-data folders alike).
- Clearing: disposing the control, waiting for `BrowserProcessExited` and then deleting the user-data folder succeeded for three profiles.

A harness bug initially inflated startup to 54-79 s. A WebView2 on an unselected `TabControl` page is not in the visual tree, so it cannot create its window. Production code must keep background profiles' WebView2 hosts realised, for example hidden rather than collapsed or detached.

### Signed in (live account, manual, 2026-10-01)

You signed in to claude.ai and the Console in an isolated profile, `main`, and ran the comparisons. You signed in to claude.ai **with Google, and it worked in WebView2** using the control's default popup handling: the harness does not handle `NewWindowRequested`, whereas upstream builds its own popup window for Google SSO.

| Request | Browser fetch | `HttpClient` replay (all three variants) |
| --- | --- | --- |
| claude.ai `/api/organizations` | 200 JSON (2 organisations), 185-299 ms | 403 `text/html`, `cf-mitigated`, challenge page |
| claude.ai `/api/organizations/{org}/usage` | 200 JSON (26 keys; numeric `five_hour.utilization`), 242-342 ms | 403 `text/html`, `cf-mitigated`, challenge page |
| Console `/api/organizations` (page on `platform.claude.com`) | 200 JSON (2 organisations), 239 ms | `platform.claude.com`: **200 JSON** with all cookies (227 ms) and with `sessionKey` alone (237 ms). `console.anthropic.com`: 403 JSON (an authentication error, not a challenge), because no cookie is scoped to that host |

- **claude.ai:** three manual comparisons, then every poll, gave the same split. The replays included a valid `sessionKey` with the browser's `cf_clearance`, `__cf_bm` and `anthropic-device-id`, and either the WebView2 or upstream's Safari user agent. By the plan's decision rule (replay challenged while the same browser request succeeds), claude.ai uses **same-origin WebView2 fetch**. OAuth and provider endpoints stay on `HttpClient`.
- **Console:** after sign-in the Console lives at `platform.claude.com`. Its `sessionKey` cookie is scoped to `.platform.claude.com` and lasts 7 days (claude.ai's lasts 28). Replay there is not challenged, so the Console can use a per-profile cookie container with `HttpClient`. Upstream's `console.anthropic.com/api` base was only tried with domain-scoped cookies, which send nothing there. Whether it accepts a `platform.claude.com` session key sent explicitly, as upstream does, is untested and moot if the port uses `platform.claude.com`.
- **Restart:** after closing and relaunching the harness with the same profile, the browser route returned 200 without signing in again. The session persisted in the profile's user-data folder.
- **Memory, signed in:** 476 MB private (6 processes) on the full claude.ai page, and 211-215 MB private (5 processes) parked on `claude.ai/robots.txt` while polling every 30 s. The host process was 259-265 MB.
- **Renewal soak:** the harness polled from the parked page every 30 s from 01:00:33 to 01:30:33 (61 cycles, each finishing within 0.4 s, with no overlap). Browser fetch returned 200 on **122 of 122** requests; replay returned 0. Cloudflare re-issued `__cf_bm` through the browser's own requests whenever 15 of its 30 minutes remained (01:10:33 and 01:25:33), and WebView2's cookie store picked it up automatically, so the cookie never expired. The browser route therefore needs no cookie maintenance of its own.
- **Second account (01:33-01:35):** two profiles ran side by side, both parked, each ready in about 200 ms and using 146-171 MB private. `main` held your personal login from earlier; you signed in to your work account in `second`. "Check isolation" re-observed both on claude.ai at that moment:
  - The session cookies were **different**.
  - The `/api/account` account IDs (200 for both) were **different**.
  - The two accounts shared **no** organisations (2 and 1 respectively).

  The profiles are therefore isolated, each acting as its own account. Both profiles were then cleared: browser processes exited, and the user-data folders were deleted.
- **Work (Team) account comparison (02:02-02:03):** in a fresh `work` profile you signed in to your work account with Google, then ran "Compare claude.ai" twice.
  - Browser fetch returned organisations 200 (1 organisation) and usage 200, with the same 26-key shape and numeric `five_hour.utilization` as the personal account.
  - Every replay variant (2 comparisons × 3 variants × 2 requests) was challenged by Cloudflare.
  - An earlier "Compare Console" without a Console session gave the same claude.ai split. Its Console replays returned 403 JSON authentication errors, because there was no Console session.

  The profile was then cleared. The claude.ai transport decision therefore holds for both a Pro and a Team account.

## 4. Tray identity

[`ShellSpike probe-tray`](../spikes/ShellSpike) adds a `NIF_GUID` icon whose GUID is a version-5 UUID of a product namespace, the lower-cased absolute executable path and a slot name. It then calls `Shell_NotifyIconGetRect` and removes the icon.

- From path A, NIM_ADD succeeded. After moving the folder to path B, the new path's GUID also succeeded.
- **Negative control:** the documentation says a GUID's registered path "cannot be changed" (except for same-publisher Authenticode-signed moves). On this build that was **not enforced** for an unsigned executable. With A's registration persisted (`HKCU\Control Panel\NotifyIconSettings` showed A's path) and the icon held for 5 s, a run from B reusing A's GUID still succeeded, and Explorer rewrote that entry's `ExecutablePath` to B.
- `Shell_NotifyIconGetRect` returned an 80×120 px rectangle at the right-hand end of the taskbar. The icon was probably in the overflow, since new icons are hidden by default; this is not yet confirmed by hand.

Path-derived GUIDs remain the right design, but the risk they avoid is different from the documented one. A fixed GUID shared by two unsigned copies would silently flip one entry between paths rather than fail. Each new path creates a new `NotifyIconSettings` entry, and with it a new visibility preference.

Manual, 2026-10-01, on a 250%-scaled display, with the spike's interactive window launched by a notification click:

- A new icon starts in the **^** overflow, which you want. Left and right clicks there delivered `NIN_SELECT` and `WM_CONTEXTMENU`. While the flyout was open, `Shell_NotifyIconGetRect` returned the icon's position inside it (100×100 px); while closed, an 80×120 px taskbar rectangle, most likely the ^ button.
- After you dragged it onto the taskbar, a click delivered `NIN_SELECT`, and the rectangle was the icon's taskbar slot (80×120 px). Explorer stored `IsPromoted=1` against the icon's GUID and executable path.
- On an Explorer restart, the app received `TaskbarCreated` and re-added the icon, which stayed promoted and kept working.
- After the folder moved, the relaunched app derived a new GUID. The icon returned to the overflow: **the placement preference is lost**, as the plan predicts for a moved portable build.

Not tested: mixed-DPI monitors and keyboard selection (`NIN_KEYSELECT`). These belong with the production tray in Milestone 2.

## 5. Notifications (unpackaged, self-contained Windows App SDK)

`ShellSpike` references only `Microsoft.WindowsAppSDK.Foundation` 2.3.12 with `WindowsPackageType=None` and `WindowsAppSDKSelfContained=true`. It does not reference the Singleton package.

- **Packaging gap:** `AppNotificationManager.Register()` threw `COMException: Unable to load resource dll. Microsoft.WindowsAppRuntime.Insights.Resource.dll`. The DLL exists only inside the runtime MSIX in `Microsoft.WindowsAppSDK.Runtime` 2.5.1. It is absent from every component package's `runtimes-framework` payload, including the full metapackage's output (725 files, 289 MB). The spike's project extracts that one file next to the executable at build and publish time; with it, `Register()` and `Show()` succeed. Register had already written its registry entries before throwing.
- `IsSupported()` is true. `Setting` is **`DisabledForUser`**: notifications are switched off for this Windows user, so posted toasts are accepted (IDs returned) but not displayed.
- `Register()` creates a per-path registration: an `AppUserModelId\{GUID}` with `CustomActivator` pointing to a `CLSID\{GUID}\LocalServer32` of `"<absolute exe path>" ----AppNotificationActivated:`, plus an empty path-named `AppUserModelId` key. After moving the folder, the next `Register()` created a **new** registration for the new path and left the old one pointing at the missing executable. So relocation does leave stale entries.
- The spike's `cleanup-others` mode removed only its own registrations for other paths (8 keys) and left the current copy's alone.
- The self-contained output is 470 files, 186 MB.

Manual, 2026-10-01, after you enabled notifications (`Setting` then reported `Enabled`):

- **Cold activation works.** The app posted a notification, unregistered its running COM server and exited. Clicking the notification in the notification centre launched it with `----AppNotificationActivated: -Embedding`. `GetActivatedEventArgs()` reported kind `AppNotification` with the arguments intact. Windows App SDK 2.x separates arguments with `;`, not the `&` shown in Microsoft's example.
- **Running-instance clicks work.** With the app open, a click raised `NotificationInvoked` in that process about 2.5 s after posting. No second process started.
- **Relocation loses old notifications.** Notification A was posted from the `manual` folder, which was then moved; notification B came from the moved copy. Clicking **A did nothing at all**: no process, no error, no log entry, because its registration still pointed at the missing path. Clicking B cold-launched the moved copy. After the move, `cleanup-others` removed only the stale `manual` registration.
- Toasts stay on screen for about 5-10 s, then remain in the notification centre until clicked.

Pending: a clean-machine check without the Singleton package (Milestone 6).

## 6. Hook transport latency and reload

### Method

The detailed measurement did not use the plan's paid model calls. [`HookBench`](../spikes/HookBench) instead runs the **real installed `claude.exe`** in `-p` mode against a **local fake Messages API**. That API streams deterministic `Read` tool calls: 25 successful, 1 failing to exercise PostToolUseFailure, then a final answer. The run is isolated as the plan requires: a scratch working folder outside the repository, `--settings <per-condition file>`, an empty `--setting-sources` argument, `--model claude-haiku-4-5` (served by the fake API), `--strict-mcp-config` and `--no-session-persistence`. In addition, `CLAUDE_CONFIG_DIR` is a fresh scratch directory, `ANTHROPIC_API_KEY` is a dummy, `ANTHROPIC_BASE_URL` points at the fake API, and inherited `CLAUDE*`/`ANTHROPIC*` variables are removed. No account credentials were loaded, no usage was consumed and the user's real settings were not read or edited.

Effective hooks were checked in the CLI's debug log before measuring. Under no hooks it reported "Registered 0 hooks from 2 plugins" and "Found 0 total hooks in registry". The two plugins are Claude Code's own built-in plugins (`cc-plugin-agents-md`, `cc-plugin-plugin-authoring`, "tier builtin"). They run natively in every condition, so they are part of the baseline, not foreign hooks. No managed or policy settings exist.

Limitations: tool calls come from canned responses, not a model, and the CLI talks to a custom base URL with an API key instead of an OAuth session. Hook execution is the CLI's own code, but paths specific to first-party OAuth sessions are not exercised. The real-model confirmation below covers that gap for the stopped-listener case.

The measured quantity per tool call is the **gap**: time from the end of a `tool_use` response to the CLI's next request. It contains stream parsing, the PreToolUse hook, `Read`, the PostToolUse hook and request building. Model and network latency are excluded by construction.

### Results (3 runs × 25 tool calls per condition, all runs succeeded)

| Condition | Gap p50 ms | Gap p95 ms | Overhead vs none p50 / p95 ms | Hook events received per run |
| --- | ---: | ---: | --- | --- |
| No hooks | 10.0 | 23.1 | — | — |
| HTTP, listener accepting (upstream shape: `type: http`, timeout 3) | 13.7 | 26.6 | +3.7 / +3.6 | UserPromptSubmit 1, PreToolUse 26, PostToolUse 25, PostToolUseFailure 1, Stop 1, SessionEnd 1 |
| HTTP, listener stopped | 13.1 | 25.6 | +3.1 / +2.5 | none (connection refused) |
| HTTP, listener aborted and restarted every 5 calls | 14.0 | 26.7 | +4.0 / +3.6 | delivery resumed after every restart |
| Async command (`args`, `async: true`), pipe server up | 18.0 | 29.4 | +8.0 / +6.4 | SessionStart 1, UserPromptSubmit 1, PreToolUse 26, PostToolUse 25, PostToolUseFailure 1, Stop 1; **SessionEnd lost in 3 of 3 runs** |
| Async command, no pipe server | 20.3 | 29.0 | +10.3 / +6.0 | none (dropped quietly) |

Findings:

- **A stopped listener costs about 3 ms, not seconds.** The CLI's debug log shows each post failing with `connect ECONNREFUSED` within 1-4 ms. The roughly 2-second refusal previously measured with .NET sockets does not apply to Claude Code 2.1.286's runtime.
- **The CLI skips HTTP hooks for SessionStart:** "Skipping HTTP hook … HTTP hooks are not supported for SessionStart". This comes from the CLI, not Windows, so upstream's HTTP SessionStart hook is inert on current Claude Code too. Command hooks do receive SessionStart.
- Async command hooks add one helper process per event (55 per run), 6-10 ms per tool call and 95-150 ms at CLI exit. Their PreToolUse reached the helper about 16 ms (p50) after the response. SessionEnd was lost at teardown every time, as the plan anticipated for async forwarding.
- Notification never fires in `-p` mode, so neither transport's Notification delivery is verified.
- Hook overhead per event and teardown were measured; per-event CPU and bursts for the async route were not (only needed if that route is chosen).

### Real-model confirmation (approved, 2026-10-01)

You approved this run explicitly: `claude-haiku-4-5` (it resolved to `claude-haiku-4-5-20251001`), with the no-hook and stopped-listener conditions, 20 tool calls each and one attempt per condition. It was a cap of 40 tool calls and was run within it.

[`HookBench real`](../spikes/HookBench/Real.cs) used the plan's isolation with your real login:

- A scratch folder of 20 chained files, each naming the next, so the calls are sequential and harmless.
- `--tools Read`, `--settings <file>`, an empty `--setting-sources`, `--strict-mcp-config`, `--no-session-persistence` and the default config directory.
- `--output-format stream-json --verbose`, with every line timestamped on arrival.

The measured quantity is the time from an assistant `tool_use` line to its `tool_result` line: CLI-side hooks plus `Read`, with model latency excluded.

During the approved run, only the prompt and the file chain limited the number of calls. Both runs stayed within the cap, with exactly 20 calls each, but the harness did not enforce it. It now enforces `--cap` (default: the number of files) independently of the prompt, in three ways:

- the CLI's own `--max-turns`, set to the cap plus one turn for the final answer (a hidden flag the CLI accepts)
- killing the process tree as soon as the stream shows more tool uses than the cap
- a 10-minute timeout

Two free dry runs verified it. A cap of 20 against the 20-call workload finished normally. A cap of 5 was stopped at the 6th tool use, by the CLI (`error_max_turns`) and the harness kill at the same point. Any future paid run is therefore bounded at the cap plus one model turn.

Before any usage was spent, a dry run of the identical procedure checked the effective hooks. It used the real config directory, the fake API and a dummy key; the CLI confirmed that the key took precedence over your login. Only Claude Code's built-in plugins loaded (`cc-plugin-agents-md`, `cc-plugin-telemetry`, tier builtin; "Registered 0 hooks from 2 plugins"). None of your installed plugins, user or managed hooks were active, so isolation was complete. The dry run left the API-key-related keys in `~/.claude.json` unchanged.

| Condition (real model) | Tool use → result p50 / p95 ms | Model turn p50 ms | Turns | Outcome |
| --- | --- | ---: | ---: | --- |
| No hooks | 9.4 / 12.6 | 1312 | 21 | success, 20 sequential Read calls |
| HTTP, listener stopped | 15.9 / 27.3 | 1232 | 21 | success, 20 sequential Read calls; 43 hook posts, each `ECONNREFUSED` in ≤4 ms (p95 3.9 ms); SessionStart skipped by the CLI |

Overhead of a stopped listener with a real model and OAuth session: **+6.5 ms p50, +14.7 ms p95** per tool call. With 20 samples per condition the p95 is noisy, but it is two orders of magnitude inside the 100 ms budget. The CLI reported an API-equivalent cost of $0.062 and $0.065. Correction: the run used the default Claude Code login, which by then was your **work (Team) account**. That login replaced the personal Pro login at 00:40, before the run, so the usage counted against the work account's allowance, not Pro as first recorded. Only the 00:14 usage probe in section 2 used the Pro login.

**Gate application.** The plan retains HTTP if it stays under 100 ms p95 overhead in the running, stopped and restart cases, to avoid per-event processes. It does in all three with the fake endpoint, and in the stopped case with a real model. The transport is therefore **HTTP**, which makes the conditional HTTP parser and server tests in parity.md applicable.

### Decision: HTTP for all eight events (accepted 2026-10-01)

HTTP cannot observe SessionStart on the current CLI. That conflicts with the plan's "preserve exactly the eight baseline events", so three configurations were weighed. Each one loses a different event:

- **Without SessionStart**, a freshly opened, idle session does not appear in the HUD until its first prompt. The reducer creates sessions on any first event; upstream behaves this way and tests it. Nothing changes once work starts, and upstream's macOS users see the same on this CLI.
- **Without SessionEnd**, a closed session stays in the HUD until the stale-session sweep removes it: 120 s, or longer if it was waiting for attention.

| Option | Pros | Cons |
| --- | --- | --- |
| **A. HTTP for all eight** (upstream's configuration). **Chosen.** | Cheapest: about 4 ms per tool call and no extra processes. One transport to install, validate, repair and remove. SessionEnd is delivered, so closed sessions leave promptly. Matches upstream's configuration, so its installer tests port directly. The SessionStart entry starts working unchanged if the CLI adds HTTP support. | Idle sessions appear only at their first prompt. The configuration carries one entry the current CLI ignores. HTTP obligations remain: an eligible persistent port, collision handling, a path secret, and crash leftovers (measured as harmless). The CLI sends full hook payloads to the loopback port, which another Windows user's process could receive while the tracker is closed. |
| B. HTTP for seven, plus an async command hook for SessionStart | All eight events arrive. The command hook costs one process per session start. | Two hook formats to install, check, repair and remove. Hooks gain a dependency on the installed helper. Differs from upstream, and becomes a workaround to remove if the CLI adds support. Same payload exposure as A for the other seven events. Its only gain is showing idle sessions sooner. |
| C. Async command hooks with a named pipe for all eight | Only identifiers leave the CLI, over a current-user pipe, so there is no payload exposure and no ports. Hooks cost almost nothing when the tracker is closed. SessionStart arrives. | SessionEnd was lost in 3 of 3 runs, leaving ghost sessions for about 2 minutes. One process per event (about two per tool call). About 6-8 ms extra per tool call and 95-150 ms extra at every CLI exit. Events can arrive out of order. The plan prefers HTTP when it passes the gate. Upstream's HTTP tests stop applying and need pipe replacements. |

A was chosen because the gate selects HTTP, it loses the less important event, it matches upstream's behaviour on the current CLI and it costs the least. Its one serious con, payload exposure to other Windows users on the same machine, does not apply: multi-user PCs are out of scope ([plan.md section 1](../plan.md#1-target-scope-and-evidence)). B remains a small later addition if seeing idle sessions at launch becomes important. Re-measure the gate whenever the minimum supported CLI version changes.

### Live reload

The settings file was atomically replaced between tool calls: endpoint A→B at call 6, hooks removed at call 12, re-added as C at call 18, with a pause after each edit.

| Settings location | 3000 ms pause | 1000 ms pause | 300 ms pause |
| --- | --- | --- | --- |
| `--settings <file>` | never reloaded (A throughout) | — | — |
| `<CLAUDE_CONFIG_DIR>\settings.json` with `--setting-sources user` | each edit applied at the next call | applied within 0-1 calls | no edit applied before the session ended |

User settings, which is where production hooks will be installed, reload after roughly one second. Reload appears debounced, so edits in quick succession postpone it. After an install, removal or repair, events in the following second or so may still use the old configuration. Edits to a `--settings` file are not picked up by a running session.

## 7. Known gaps and follow-ups

The planned sign-in, restart, renewal, isolation, Console, notification and tray checks were done on 2026-10-01 (sections 3-5). None of the gaps below blocks Milestone 2. Each names the milestone where it should be closed; the plan's milestone sections point back here.

### Gaps in what Milestone 1 set out to measure

| # | Gap | Why it matters | Status | Revisit in |
| --- | --- | --- | --- | --- |
| G1 | Work account: claude.ai usage endpoint and replay comparison | A Team or Enterprise account's usage response or Cloudflare treatment may differ from Pro | **Closed 2026-10-01**: same browser/replay split and usage shape as Pro (section 3) | — |
| G2 | Read-only `/api/oauth/usage` verified only for Pro and Team logins | Max and Enterprise responses may differ or be refused | Team verified 2026-10-01 (section 2); Max and Enterprise need access to such accounts | M2, as accounts allow |
| G3 | Read-only usage at a rate limit (429, `Retry-After`) and under sustained 30-second polling: only 7 requests over 3 minutes | Polling, back-off and the 30-second default depend on it | Retry handling implemented and tested in M2; the overnight live soak is scheduled for the night of 2026-10-01 ([milestone-2.md](milestone-2.md#live-and-manual-checks)) | M2 |
| G4 | WebView2 cost with three profiles polling at once. Measured so far: three idle (signed out), two idle (signed in), one polling | Needed before any lightweight-performance claim with several browser profiles | Closed 2026-10-01: about 180 MB in total for the WebView2 processes with three claude.ai profiles polling ([Milestone 3](milestone-3.md#live-checks-so-far)) | M3 (simultaneous monitoring) |
| G5 | Real-model hook runs covered only the stopped listener; running and restart cases used the fake endpoint | Accepted with the HTTP decision; the gate must be re-measured when the CLI changes | Accepted | M5, and whenever the minimum CLI version changes |
| G6 | Notification hook delivery: the event never fires in `-p` mode, so it is unverified for either transport | One of the eight events the HUD uses | Open | M5 (interactive CLI) |
| G7 | Hook delivery and reload were measured only in `-p` sessions | Interactive sessions may schedule hooks or watch settings differently | Open | M5 (interactive CLI) |

### Deferred by design

| # | Item | Revisit in |
| --- | --- | --- |
| D1 | Tray: mixed-DPI monitors, keyboard selection (`NIN_KEYSELECT`) and anchoring the popover from the measured icon/flyout rectangles | **Closed 2026-10-01** in M2 (manual checks by the user, [milestone-2.md](milestone-2.md#live-and-manual-checks)) |
| D2 | Notifications on a clean machine without the Singleton package (this PC has it installed) | M6 |
| D3 | Statusline: how Claude Code actually spawns the command shell, rapid replacement/cancellation and end-to-end timing with the production helper (only the spike helper was timed) | M5 |
| D4 | Self-contained output size (the notification spike was 470 files, 186 MB) | M6 packaging |
| D5 | The extracted `Microsoft.WindowsAppRuntime.Insights.Resource.dll` workaround: build it into the app and re-check on each Windows App SDK update | Built into the app in M4 (2026-10-02; `Register()` succeeded in a fixture run, [milestone-4.md](milestone-4.md)); re-check at each Windows App SDK update and in M6 |
| D6 | Stale notification registrations and `NotifyIconSettings` entries after a move: cleanup of the app's own stale entries | Notification registrations: removed at startup from M4 (only this app's, only for executables that no longer exist). `NotifyIconSettings` entries stay Explorer's; M6 (manual upgrade and cleanup) |
| D7 | Upstream's `console.anthropic.com/api` base with an explicitly sent `platform.claude.com` key: untested, and moot while the port uses `platform.claude.com` | Only if `platform.claude.com` stops working |

### Running the isolation check again

"Check isolation" re-observes each profile at that moment, on claude.ai only. For each pair it reports:

- whether the session cookies differ, which shows storage isolation
- whether the `/api/account` account IDs differ, or "unknown" if that undocumented endpoint gives no ID
- how many organisations are shared, which is informational only, since accounts can share one

Values are compared in memory; only verdicts are logged. The command is `dotnet run --project spikes/AuthSpike -c Release -- --profiles main,second --start https://claude.ai/robots.txt`.

Results go to `%LOCALAPPDATA%\ClaudeUsageTracker.Spikes\auth\results.jsonl`; the tray and notification log is `%LOCALAPPDATA%\ClaudeUsageTracker.Spikes\shell\log.txt`.

## Reproducing

From the repository root. Outputs go to the external artifacts folder; experiment data goes to `%LOCALAPPDATA%\ClaudeUsageTracker.Spikes` or a scratch folder you choose outside the repository.

```powershell
Remove-Item Env:NoDefaultCurrentDirectoryInExePath -ErrorAction SilentlyContinue  # only if set
dotnet publish spikes/HelperSpike/HelperSpike.csproj -c Release -r win-x64
dotnet build spikes/HookBench/HookBench.csproj -c Release
# <helper> is the published HelperSpike.exe; <scratch> is outside the repository
dotnet run --project spikes/HookBench -c Release --no-build -- run --helper <helper> --out <scratch>\hookbench
dotnet run --project spikes/HookBench -c Release --no-build -- reload --out <scratch>\reload --wait-ms 1000
dotnet run --project spikes/HookBench -c Release --no-build -- time-helper --helper <helper> --out <scratch>\timing
dotnet run --project spikes/HookBench -c Release --no-build -- real --out <scratch>\real --cap 20 --dry-run true    # free isolation and cap check
dotnet run --project spikes/HookBench -c Release --no-build -- real --out <scratch>\real --cap 20 --dry-run false   # consumes usage: explicit approval only
dotnet run --project spikes/OAuthUsageProbe -c Release -- --repeat 6 --interval-seconds 30
dotnet run --project spikes/AuthSpike -c Release -- --profiles smoke --start https://claude.ai/robots.txt --auto-compare-and-exit --clear-after
dotnet run --project spikes/AuthSpike -c Release -- --profiles selftest1,selftest2 --start https://claude.ai/robots.txt --self-test-clear   # queued-clear regression check
dotnet publish spikes/ShellSpike/ShellSpike.csproj -c Release   # then copy the publish folder elsewhere and run probe-tray / notify-and-exit
```
