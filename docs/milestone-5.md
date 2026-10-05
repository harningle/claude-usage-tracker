# Milestone 5 log

Work and evidence for [plan.md Milestone 5](../plan.md#milestone-5---native-launchers-fast-statusline-and-activity-hud): per-profile terminal launchers, the status line helper, the installed helper and its upgrades and repair, the eight HTTP hooks and the floating activity HUD, and the Milestone 1 follow-ups G5, G6, G7 and D3. Upstream behaviour comes from the pinned upstream source (`588775e`): `TerminalLauncherService`, `StatuslineService` (with its bash script), `StatuslineView`, `NotchHookInstaller`, `NotchHookServer`, `HookHTTPParser`, `NotchSessionStore`, `ToolActivityMapper`, `NotchHUDController`, `NotchCompactViews`, `NotchExpandedView` and `NotchHUDSettingsView`. Each departure is recorded in [parity.md](parity.md) (differences 46 to 53). Evidence types follow parity.md: automated tests, manual checks in fixture mode, measurements with the real CLI against a local fake Messages API (no account, no usage), and live-account checks, kept separate.

Recorded 2026-10-03 on the development machine (Windows 11 26200, Claude Code 2.1.288, Git for Windows' bash, Windows PowerShell 5.1 with PSReadLine 2.0.0).

## Status

| Plan item | Status | Evidence |
| --- | --- | --- |
| Per-profile launchers | Implemented. A launcher is a copy of the helper named `claude-<slug>.exe` in `%USERPROFILE%\.local\bin`, recognised by its file name alone. It runs the native `claude.exe` found beside it or on PATH (never itself, another launcher or the helper) with `CLAUDE_CONFIG_DIR` set to `%USERPROFILE%\.claude-<slug>`, passes every argument through `ArgumentList`, cancels Ctrl+C and Ctrl+Break in itself before the child starts, and returns the child's exit code (127 for its own failures). PATH is never edited: the card warns when `.local\bin` is not on it. Removal keeps the login folder | Automated (`LauncherPathsTests`, `LauncherNamingTests`, launcher cases of `IntegrationServiceTests`, and `LauncherProcessTests` with the real NativeAOT helper under PowerShell 5.1, Command Prompt and Git Bash in a pseudo console); fixture: installed from General, with its folder, status line and hooks, and the PATH warning; live (the user, 2026-10-03): installed, signed in and linked, then used under PowerShell, Command Prompt and Git Bash with arguments, exit codes and Ctrl+C; removal kept its folder |
| Status line helper | Implemented: a port of upstream's bash script (every component, the four colour modes, the pace marker, reset rounding) in the NativeAOT helper. Usage comes from a fresh tracker cache, else Claude Code's `rate_limits` on stdin, else a stale cache labelled "(stale)" (never after its reset), else nothing; cost only from a fresh cache. The branch is read from `.git/HEAD`, without running git. It reads no login, uses no network and writes nothing | Automated (`StatuslineRendererTests`, `StatuslineUsageTests`, `StatuslineInputTests`, `StatuslineCommandTests`, `StatuslineSyncTests`); fixture: Apply installed the helper and gave the same line through Git Bash and PowerShell; real CLI with the fake API: Claude Code drew the production helper's line (D3 below); live (the user, 2026-10-03): Apply and Reset in the real Claude Code folders |
| Installed helper, manifest, upgrades and repair | Implemented. The helper is installed at `%LOCALAPPDATA%\ClaudeUsageTracker\bin\ClaudeUsageTracker.Helper.exe` and recorded with every launcher in `install.json`. Upgrades stage, verify and rename aside, for the helper and each launcher copy; an interrupted install is finished or rolled back at the next start; retained images are deleted once free. A newer helper is never downgraded, and a file the tracker did not install is never overwritten. Startup checks show a banner in the popover and a Repair action | Automated (`FileInstallerTests`, `IntegrationServiceTests`, `IntegrationCoordinatorTests`; `StatusLinesKeepWorkingWhileTheHelperIsUpgradedUnderThem`, `ARunningLauncherSurvivesRemovalAndUpgradeAndStaysAttributedToItsFolder`); live: first install at the fixed path. **An upgrade with a launcher alive has been tested only automatically** |
| Eight HTTP hooks | Implemented in upstream's configuration (difference 48 for the lifecycle). An exclusive `127.0.0.1` listener on a persistent random port outside the dynamic and excluded ranges (read from `netsh`), a 256-bit path secret kept encrypted, `200 {}` before parsing, caps on size, connections and time. The listener starts before the hooks are written to every known Claude Code folder, and a clean exit removes them before the listener stops. Foreign hooks and settings are kept; edits are conflict-aware | Automated (`HookHttpParserTests`, `HookServerTests`, `HookPortsTests`, `HookSettingsTests`, `HudServiceTests`); fixture: both fixture folders got the eight hooks beside a foreign Stop hook, and the clean exit removed them; real CLI with the fake API: G5, G6 and G7 below; live (the user, 2026-10-03): installed while the HUD was on, gone from `settings.json` after quitting, the user's own hooks unchanged |
| Activity HUD | Implemented: a floating pill at the top centre of the primary display (no notch on a PC), compact or expanded by a click, never activated or focused, hidden after 5 s of idleness when auto-hide is on. The reducer adds the plan's ordering rules | Automated (`HudOrderingTests`, `HudSessionStoreTests`, `MilestoneFiveViewTests`); fixture: synthetic events gave the compact and expanded views and the idle auto-hide; live (the user, 2026-10-03): real sessions, "Needs input" at a permission prompt, the idle hide, and the pill over a full-screen window and on another virtual desktop |
| Session activity for the switching guard | Implemented. Launcher processes, retained images included, attribute their `claude.exe` to the launcher's folder. A HUD session can only turn an idle folder into one in use; an empty HUD proves nothing | Automated (`ASessionTheHudObservesMakesItsFolderInUseAndNothingElseIdle`, the launcher attribution case above). **Not exercised live:** Claude Code was already on the account the check would have switched to |
| G5: hook gate on CLI 2.1.288 | **Passed again** (below) | Real CLI with the fake API |
| G6: Notification delivery | **Closed:** delivered over HTTP in an interactive session, both `permission_prompt` and `idle_prompt` | Real CLI with the fake API |
| G7: hooks in interactive sessions | **Closed:** every event but SessionStart is delivered, and edits to the user settings reload in about a second | Real CLI with the fake API |
| D3: status line shell, cancellation, production timing | **Closed:** Claude Code runs the command through Git Bash, a newer update ends one still running, and the production helper is well inside the targets | Real CLI with the fake API; automated timing |

**Acceptance met** on automated tests, fixture checks and real-CLI runs against the fake API:

- **Shells.** In PowerShell, Command Prompt and Git Bash the launcher passes arguments with spaces and metacharacters unchanged and returns the exit code. Three Ctrl+C reach Claude Code, which keeps running, and the prompt does not return while it runs. The launcher is in a folder whose path has a space (`LauncherProcessTests`).
- **The status line is secret-free and responsive.** The cache holds usage numbers and a profile name only, and the helper reads no login (`TheCacheHoldsNoCredentialAndNoAccountIdentity`, `TheHelperItselfReadsNoLoginAndAnswersItsVersion`). Through Git Bash, which Claude Code uses, it takes 33.5 ms at p95.
- **Hook overhead with the tracker down.** The stopped listener cost +3.7 ms p95 per tool call on 2.1.288 (fake API), against the 100 ms budget. The approved real-model run of Milestone 1 measured +14.7 ms on 2.1.286; it was not repeated.
- **HTTP lifecycle and ports** are measured and documented below and in plan section 2.
- **No hook emits decisions or context:** the answer is always `{}` (`TheAnswerIsAnEmptyObjectThatDecidesNothing`), and the entries are upstream's passive HTTP hooks (`TheEntriesAreUpstreamsPassiveHttpHooksAndNothingElse`).

Your live checks on the evening of 2026-10-03 then passed: the status line, the HUD with its hooks, and a launcher under all three shells (see "Live checks so far").

## Decisions

These follow plan section 2; none needed a new choice from you.

- **Hooks live while the tracker runs, in every known folder.** Upstream wrote them to `~/.claude/settings.json` only and left them there when it quit. Here they go to every Claude Code folder the tracker knows (the default, each linked folder and each launcher's folder) and come out on a clean exit, as plan section 2 requires for HTTP. A crash leaves them until the tracker starts again, at a measured cost of a few milliseconds per tool call.
- **The status line also goes to every known folder,** and stays when the tracker quits, since it needs no running tracker. Another tool's `statusLine` is replaced only when you agree, and Reset puts it back.
- **One executable.** The launcher is a copy of the helper, so one NativeAOT binary is built, signed later and upgraded.
- **The helper ships beside the app** in `helper\`. It is published separately (NativeAOT needs the C++ tools) and copied in by the app's build when it exists. A build without it says so and installs nothing.

## What was built

New and changed parts, in outline:

- **Core** (`Core/Statusline/`, `Core/Launchers/LauncherPaths.cs`, `Core/Integration/HelperInfo.cs`, `Core/Hud/`):
  - the status line configuration, cache, stdin parser, usage precedence, Git branch and renderer, with source-generated JSON
  - launcher naming, layout and real-CLI resolution
  - the helper's fixed paths and protocol version (1)
  - upstream's HTTP request parser, hook payloads carrying `tool_use_id`, `prompt_id`, `cwd` and the session's folder (from `transcript_path`), and a reducer with the ordering rules
- **Helper** (`ClaudeUsageTracker.Helper`): `statusline`, `version`, and launcher mode by file name. Invariant globalisation; 3.1 MB.
- **Integration** (`App/Integration/`):
  - `InstallManifest` and `FileInstaller`: rename-aside installs and their recovery
  - `ClaudeSettingsFile`, `HookSettings` and `StatuslineSettings`: conflict-aware edits of Claude Code's `settings.json`
  - `IntegrationService`: helper, launchers and status line entries
  - `StatuslineFiles` and `StatuslineSync`: the configuration and per-folder caches, written only from usage fetched with that folder's own verified login
  - `IntegrationCoordinator`: start, clean exit, folder changes, problems and repair
- **HUD** (`App/Hud/`): `HookPorts`, `HookServer`, `HudService` (listener, secret, hook lifecycle), `HudPresentation` and `HudWindow`.
- **Settings and popover:**
  - Claude CLI page: upstream's live preview, colour modes, components and labels, with Apply and Reset.
  - Activity HUD page: switch, auto-hide, hook status per folder and the listener.
  - Terminal Launcher card on General.
  - A popover banner when something installed needs repair.
- **Process guard** (`CliProcessGuard`): the optional HUD observation, which can only add "in use".
- **Test tools:** `tests/FakeClaude`, a stand-in `claude.exe` driven by environment variables, and `PseudoConsole`, which runs a shell as a terminal does so that tests can type Ctrl+C.
- **Spike harness** (`spikes/HookBench`), for the follow-ups:
  - `http-app`, which points the benchmark at a running tracker's own listener
  - `interactive`, the real CLI in a pseudo console
  - `time-statusline`, which times the production helper

## Automated tests

1,296 pass (942 at the end of Milestone 4; 1,285 before the code review below), with no build warnings and none skipped once the helper is published. New suites:

- `StatuslineRendererTests`, `StatuslineUsageTests`, `StatuslineInputTests`, `StatuslineCommandTests`: upstream's script output for each component and colour mode, pace tiers, reset rounding; the precedence of fresh cache, stdin and stale cache; a cache under another folder's key never used; damaged configuration failing loudly; reading while the tracker replaces the file; branch from a worktree.
- `StatuslineSyncTests`, `ShellEnvironmentTests`: each folder follows its own account, after a switch too; no cache for an unknown or signed-out folder; nothing from an earlier run passed off as fetched; Git Bash found where Claude Code looks for it.
- `HookHttpParserTests` (upstream's nine cases and seven more), `HookServerTests`, `HookPortsTests`: tokens, 404/405, malformed and oversize bodies, an answer before an unauthorised body arrives, loopback-only exclusive binding, a stopped listener refusing at once, eligible ports from real and canned `netsh` output.
- `HookSettingsTests`: upstream's installer cases, foreign loopback hooks left alone, unexpected shapes and duplicate members refused untouched, a change made meanwhile not overwritten.
- `HudServiceTests`, `HudOrderingTests`: listener first, hooks removed before the listener stops, the secret encrypted, port reuse and repair, and the ordering rules (late starts, duplicates, parallel tools, ended sessions, prompts).
- `FileInstallerTests`, `IntegrationServiceTests`, `IntegrationCoordinatorTests`: installs and each interruption point, a running image replaced without ending it, no downgrade, launchers and their ownership, status line consent and restore, repair, a damaged manifest that stops everything.
- `MilestoneFiveViewTests`: the settings pages, the launcher card, the HUD texts, ANSI rendering of the preview, and the process guard with the HUD.
- `LauncherProcessTests` (needs the published helper; skipped otherwise): arguments and exit codes directly and under each shell, Ctrl+C and Ctrl+Break, a missing `claude.exe`, a launcher outside `.local\bin`, the status line command through Git Bash and PowerShell from a path with spaces and `&`, and status lines during ten upgrades between two builds of the helper (270 to 290 lines, 0 to 2 failing in the rename gap).

**Mutation check.** Eleven guards were each broken in turn and their suites run:

- the hook answer staying empty
- only the tracker's own ports counting as its hooks
- duplicate members refused
- a change made meanwhile not overwritten
- no downgrade of a newer helper
- consent before replacing another tool's status line
- no HUD with a damaged manifest
- an empty HUD never making a folder idle
- no stale value after its reset
- no cost from a stale cache
- the launcher cancelling Ctrl+C in itself

Each break made at least one test fail. One needed its test sharpened first: a duplicate at the top level was refused anyway, so a nested duplicate was added.

## Code review

A review of the commit (`a1fed15`) on 2026-10-04 found three defects. Follow-up reviews found a fourth, a flaw in its first fix, and on 2026-10-05 two more in the status line entry. Each was confirmed against the code and now has a test that fails without its fix. All fourteen parts of the fixes were removed or reversed in turn; one test needed sharpening first.

1. **[P1] A replaced launcher could vouch for a session.** The process guard attributed a `claude.exe` to a launcher's folder by its parent's path alone.
   - The failure: another program put at a launcher's path could start Claude Code on the default folder. That folder then counted as idle, so a takeover or switch could write the login while the session ran.
   - The fix: every launcher image now carries the hash the tracker installed it with, renamed-aside images included (after an upgrade, a removal while running, or recovery at start-up). The guard hashes the file at a running launcher's path before attributing anything to it. A file that does not match, cannot be read, or has no recorded hash makes every folder "unknown", which blocks every takeover and switch.
   - Cost: the file is hashed only while a launcher session runs.
   - Tests: `AnotherFileAtTheLaunchersPathVouchesForNothing` and `ALauncherImageWithoutARecordedHashVouchesForNothing`. Real hashes are checked through the guard in `ARunningLauncherSurvivesRemovalAndUpgradeAndStaysAttributedToItsFolder` and the interrupted-install test.
2. **[P2] Repair replaced a helper the tracker had not installed.** A file that had taken the helper's place, or one at the helper's path with no record, was renamed aside and then deleted as a retained image. That contradicted the rule this log states.
   - The fix: the launchers' rule now applies. Such a file is left as it is, and the problem tells you to move it away before Repair (`AHelperTheTrackerDidNotInstallIsNeverReplaced`, `RepairNeverOverwritesWhatIsNoLongerTheTrackers`).
   - The upgrade test used to force its reinstalls by spoiling the recorded hash, which now correctly counts as a file the tracker did not install. It now alternates two real builds.
3. **[P2] The status line could show another source's usage.** The sync checked only the profile's current source, not where the last fetched usage came from. After a claude.ai link was removed, that link's last result was written as Claude Code usage until the next check.
   - The fix: usage reaches the cache only when it was read from Claude Code and for the account the folder is signed in to (`UsageFromAnotherSourceOrForAnotherAccountIsNotPassedOn`).
4. **[P2] A relinked profile kept the former account's usage.** An existing cache was reused whenever its profile matched.
   - The failure: after the profile was relinked to another account in the same folder, the new account's first status lines showed the former account's values, as fresh while they were recent. The fix for 3 had only stopped the former account's result from being written again.
   - Storing the account in the file would break its no-identity rule. Instead the tracker remembers, for each folder, the profile and account whose usage it wrote there during this run.
   - The fix: a relink to another account starts the values empty (`RelinkingTheProfileToAnotherAccountDropsTheFormerAccountsUsage`). So does a new run, since nothing says whose usage an earlier run left (`UsageAnEarlierRunWroteIsNotCarriedOver`).
   - A further review of that fix found that the account was remembered before the file was written. A write that failed during a relink left the former account's values in the file, now recorded as the new account's. The account is now recorded only once the write has succeeded (`ARelinkWhoseWriteFailedStillDropsTheFormerAccountsUsage`, which holds the file open so the write fails).
5. **[P2] A status line command the user had changed was overwritten without asking.** Any command containing the helper's path counted as the tracker's, so `<helper> statusline && echo custom` was silently reset to the plain command, and its custom part was lost without a backup.
   - The fix: only a command the tracker writes, exactly, for either shell, or the one it recorded writing in that folder, is the tracker's. Anything else, a changed command included, is replaced only with consent and put back on removal (`ACommandTheUserChangedIsTheirsEvenWhenItStillRunsTheHelper`).
6. **[P2] A failed save of `install.json` lost the status line that Apply replaced.** The backup of the replaced line was saved only after Claude Code's settings had been changed. With the manifest locked, the original was gone, and Reset after a restart deleted the entry instead of putting it back.
   - The fix: the backup is saved before the line is replaced. If it cannot be saved, nothing is replaced. If the line then cannot be written, or has changed since it was read, the backup is withdrawn again (`AStatusLineIsReplacedOnlyOnceItsBackupIsSaved`).
   - The check against a value changed between the read and the write has no test, because nothing can change the file at that moment from a test.
   - Both cases were first raised as probe tests in the working tree, which these tests replace.

## Manual checks in fixture mode

All data is synthetic, and every file the app wrote was under the fixture folder (`data` for the tracker, `home` for the Claude Code folders and `.local\bin`). Checked on 2026-10-03 through UI Automation, with window images rendered from the window itself (`PrintWindow`).

1. **Claude CLI page:**
   - Apply installed the helper in the fixture data folder and wrote `statusline\config.json` and a cache per folder.
   - Each folder's `settings.json` gained the status line entry, and the default folder's foreign Stop hook stayed.
   - The installed command, run through Git Bash and through PowerShell 5.1 with a sample stdin, printed the same line. Usage came from the fresh cache (39 %) over stdin's 77 %, and the branch from `.git/HEAD`.
2. **Activity HUD page:** switching it on started a listener on 127.0.0.1 and wrote the eight hooks to both fixture folders, beside the foreign Stop hook.
   - Synthetic hook events gave the compact pill and the expanded list. A permission notification put its session first.
   - Once every session was idle, the HUD hid itself after 5 s.
   - Quitting removed the hooks, cleared the manifest's ports and folders, and kept the foreign hook.
3. **Terminal Launcher:** Install on General created `claude-fixture-work.exe` and `.claude-fixture-work`. The new folder got the status line and the hooks. The card warned that the fixture's `.local\bin` is not on PATH.

Screenshots (fixture data): the Claude CLI page, [media/settings-claude-cli-fixture.png](media/settings-claude-cli-fixture.png), and the HUD, compact [media/hud-compact-fixture.png](media/hud-compact-fixture.png) and expanded [media/hud-expanded-fixture.png](media/hud-expanded-fixture.png). The Activity HUD page and the launcher card are not included because they show the fixture folder's full path.

## Measurements with the real CLI

Each run used Claude Code 2.1.288 with an isolated config folder, a dummy API key and [HookBench](../spikes/HookBench)'s local fake Messages API, so no account was used and no usage consumed. The real hook settings were not touched.

### G5: the hook gate

Three runs of 25 tool calls per condition, all successful. Overhead per complete PreToolUse/PostToolUse pair over the run without hooks (9.8 ms p50, 20.3 ms p95):

| Condition | p50 | p95 |
| --- | ---: | ---: |
| The tracker's own listener (fixture app, `http-app`) | +4.7 ms | +2.7 ms |
| Benchmark listener up | +2.3 ms | +5.7 ms |
| Listener stopped | +2.2 ms | +3.7 ms |
| Listener restarting | +2.1 ms | +6.1 ms |

Every condition is far inside the 100 ms budget, so HTTP stays. Per run with the listener up, the events were UserPromptSubmit 1, PreToolUse 26, PostToolUse 25, PostToolUseFailure 1, Stop 1 and SessionEnd 1. SessionStart is still skipped by the CLI for HTTP hooks ("HTTP hooks are not supported for SessionStart"). The payloads carry the fields the reducer relies on: `session_id`, `cwd`, `transcript_path`, `prompt_id` and, for tools, `tool_use_id`.

### G6 and G7: an interactive session

`HookBench interactive` runs `claude` in a pseudo console, as a terminal does, and types into it:

- Turn 1 reads files and ends with one outside the working folder, which needs permission.
- Turn 2 runs after the user settings were pointed at a second path secret; turn 3 after the hooks were removed.
- `/exit` comes after the hooks were added back with a third secret.

| Moment | Hook events |
| --- | --- |
| Turn 1 | UserPromptSubmit, PreToolUse and PostToolUse for each read, PreToolUse for the outside read, then **Notification (`permission_prompt`) 6.0 s after it** while the prompt waited, PostToolUseFailure after "yes" (the file is missing), Stop |
| Idle | **Notification (`idle_prompt`) 60.0 s after Stop** |
| Turn 2 (second secret) | UserPromptSubmit and Stop, both on the second secret |
| Turn 3 (no hooks) | none |
| `/exit` (third secret) | SessionEnd (`prompt_input_exit`) on the third secret |

So the Notification hook is delivered over HTTP (G6), and an interactive session delivers the same events as `-p` and follows edits to the user settings. The CLI's debug log shows "Settings changed from userSettings" about a second after each write, as Milestone 1 found for `-p` (G7). As in upstream, a Notification makes its session "Needs input", so a session left idle for a minute shows that.

### D3: the status line

- **The shell.** The same run used a probe as the status line command; it recorded the processes above it. Claude Code ran it as `bash.exe < bash.exe < claude.exe`, Git for Windows' launcher and its bash, and its debug log names `C:\Program Files\Git\bin\bash.exe`.
- **Replacement and cancellation.** The probe slept 1.5 s before finishing.
  - 8 of its 12 runs never finished: each was followed by a newer run within its 1.5 s. The other four, which nothing followed that soon, all finished, and none of them overlapped a newer run. So a newer update ends the one still running.
  - A probe started 0.35 to 0.4 s after the hook event that triggered it, including the shell's and the probe's own start.
- **What stdin carries.** With an API key, stdin carried no `rate_limits`, which is why the helper falls back to the cache and treats missing values as missing.
- **The production helper end to end.** With `--helper`, the status line was the installed helper, with the command the tracker writes. Claude Code drew `work │ ⎇ main │ Haiku 4.5 │ Ctx: 0% │ Usage: 39% ▓▓▓▓┃░░░░░ → Reset: 08:18 PM`, with usage from the fresh cache and the branch from `.git/HEAD`.
- **Timing** (`HookBench time-statusline`). The helper was installed with a fresh cache and a repository; 50 warm runs per case, from .NET `Process.Start` to exit:

| Runner | p50 | p95 |
| --- | ---: | ---: |
| Direct | 10.7 ms | 12.6 ms |
| Git Bash `bash -c '<helper> statusline'` (Claude Code's) | 31.8 ms | 33.5 ms |
| `cmd /d /s /c` | 19.7 ms | 21.1 ms |
| PowerShell 5.1 `-NoProfile -Command` | 136.4 ms | 138.6 ms |
| First run of a freshly copied helper (direct) | 35.7 ms | 37.4 ms |

Against the targets (warm p95 below 100 ms, first run below 300 ms), every runner but PowerShell passes. PowerShell's own start-up takes the rest, as in Milestone 1. The tracker writes the PowerShell form only when Git Bash is not installed.

## Defects found during checking

1. **The launcher's folder got neither status line nor hooks in the app.** The app's folder list was the one computed at start-up, so a launcher installed later was missed. It is now computed when needed and refreshed whenever the integrations change.
2. A session was named "Session sessi" until its first tool. Any event with a `cwd` now names it.
3. The HUD could not be clicked by assistive tools, and lacked the fixture label. It is now a button that never takes the focus, and fixture mode labels it.
4. The preview did not wrap, and switch rows without a description were too tall.
5. Six more were found while the tests were written, before the fixture run, and are fixed with tests:
   - a status line command with an apostrophe in its path was not recognised as the tracker's
   - removing the hooks could drop another tool's empty event list
   - stopping the HUD kept its port in the manifest
   - folders no longer known kept their entries
   - a damaged manifest could be overwritten when the HUD started
   - Apply with only foreign status lines reported "Applied to 0" and switched itself on
6. **The PowerShell Ctrl+C test failed once the test process was warm,** and the PowerShell tests wrote what they typed to your PowerShell history.
   - Windows PowerShell drops keys typed just before its prompt comes back, with or without the launcher (checked both ways). The test now asks for the exit code until the shell answers.
   - PowerShell now starts with history saving off.
   - The 61 lines already added (from line 401 of `%APPDATA%\Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt`: the test commands with `ClaudeUsageTracker.Tests` paths and `echo "EXIT:$LASTEXITCODE"`) were removed by you the same evening.

## Open questions and known limits

- **SessionStart is still skipped** for HTTP hooks on 2.1.288, so an idle session appears at its first prompt, as for upstream.
- **A Notification after a minute of idleness** marks the session "Needs input" and keeps the HUD up. That is upstream's reducer on the same CLI.
- **The rename gap.** During an upgrade a status line can fail once (0 to 2 of 270 to 290 in the test). Claude Code then shows a blank line until its next update.
- **The status line after a start.** When the tracker starts, the cache holds no usage until its first check of that profile, normally within seconds. Until then the line takes usage from Claude Code itself, or shows "~".
- **The status line while the tracker is closed.** The helper does not know whose usage the cache holds. If Claude Code signs in to another account then, the line shows the former account's last values for as long as they are fresh (twice the polling interval). After that, Claude Code's own usage comes first, and the old values appear only, labelled "(stale)", when Claude Code reports none. The next start of the tracker removes the cache.
- **A crash leaves the hooks installed** until the tracker starts again. Each tool call then costs a few milliseconds more (+3.7 ms p95 against the fake API).
- **Hook payloads reach whatever holds the port** while the tracker is closed. This is accepted for single-user PCs (plan section 1).
- **Claude Code installed with npm** (`claude.cmd`) is not found by launchers; native Claude Code is the supported target.
- **The PowerShell runner** misses the 100 ms target on its own start-up. It is used only without Git Bash.

## Live checks so far

Run by the user on the evening of 2026-10-03 with this build. Beforehand the helper was published and the app built in the default output folder. The user reported every check below as passing. The tracker's own files and its request log agree where they can show anything.

1. **Status line.** Apply put the line into Claude Code, with the folder, branch, model, context and usage, and Reset took it out again. Afterwards no folder had the tracker's status line (`install.json` lists no entries, and the setting is off).
2. **Activity HUD:**
   - With the HUD on, real sessions appeared in the pill, and a permission prompt showed "Needs input".
   - The pill hid when Claude Code was idle, and stayed visible over a full-screen window and on another virtual desktop. This is the Windows equivalent of upstream's `NSWindowFullScreenSpacesTests`, now passed for the popover and the HUD.
   - After quitting, `%USERPROFILE%\.claude\settings.json` had no `127.0.0.1` hooks, and the user's own hooks were unchanged. The manifest recorded no hook ports or folders afterwards.
3. **Launcher:**
   - `claude-laucher-test` was installed for a test profile. `/login` in that launcher signed its own folder in, and Check Login linked the profile to it. The settings show the profile following `%USERPROFILE%\.claude-laucher-test`.
   - In PowerShell, Command Prompt and Git Bash, arguments and exit codes came through, and Ctrl+C returned the prompt only after Claude Code had exited.
   - Remove deleted the launcher and kept its folder. `.local\bin` now holds no tracker file, only another tool's `claude-swap.exe`, which was never touched.
4. **The helper** was installed once, at `%LOCALAPPDATA%\ClaudeUsageTracker\bin\ClaudeUsageTracker.Helper.exe`.
5. **Requests.** Between the new build's start and the end of the checks there were 196 requests, all ordinary polling: 44 Claude Code usage reads, all 200, plus claude.ai, Codex and status. There was no token request, and the status line and hooks sent nothing. The claude.ai check failed with "network" once after each start: the known start-up race.

Not exercised: the switch beside a launcher-only session. Claude Code's folder was already signed in to Jazzy Jaguar's account, so "Use in Claude Code" was disabled, as expected, and no switch was made (no new backup).

## Live checks to do

1. **The switch beside a launcher session** (optional). It needs Claude Code on another account than the profile you switch to. With only a launcher session open, "Use in Claude Code" for the default folder should switch at once instead of waiting.
2. **A helper upgrade with a launcher alive** happens only when a later build brings a new helper version. Its automated test runs real processes; the first live upgrade should be noted then.

The test profile "Laucher Test" still follows `%USERPROFILE%\.claude-laucher-test`, a second login to Jazzy Jaguar's account. Delete it (Manage Profiles), and the folder if you like, before Milestone 4's overnight run, so that only Claude Code's own folder is renewed in it.

## Reproducing

From the repository root:

```powershell
dotnet publish src/ClaudeUsageTracker.Helper/ClaudeUsageTracker.Helper.csproj -c Release -r win-x64   # before the build, so the app bundles it
dotnet build ClaudeUsageTracker.slnx
dotnet test --solution ClaudeUsageTracker.slnx
# Fixture mode as before (docs/milestone-3.md). The helper, status line files and install.json go to the fixture's data
# folder, and launchers and Claude Code folders to its home folder. The HUD listener is a real loopback listener.
dotnet run --project src/ClaudeUsageTracker.App -- --fixture <folder>
# The follow-ups (real CLI, fake API, no usage):
dotnet run --project spikes/HookBench -c Release -- run --helper <HelperSpike.exe> --out <scratch>\bench --conditions none,http-up,http-down,http-restart
dotnet run --project spikes/HookBench -c Release -- interactive --out <scratch>\interactive
dotnet run --project spikes/HookBench -c Release -- time-statusline --helper <published ClaudeUsageTracker.Helper.exe> --out <scratch>\d3
dotnet run --project spikes/HookBench -c Release -- interactive --out <scratch>\d3 --helper <scratch>\d3\data\bin\ClaudeUsageTracker.Helper.exe --idle-wait-s 5 --calls 3
```

`http-app` adds the running tracker's own listener: `--conditions http-app --hooks-from <a settings.json it wrote>`.
