# Claude Usage Tracker for Windows

## Purpose and source of truth

Build a faithful Windows port of the upstream app, preserving its useful behaviour and interface. Deliver a personal-use build first, then a public release. Use C# / .NET 10 LTS and WPF. Follow the user's global instructions and keep changes surgical.

[plan.md](plan.md) is the single source of truth for the upstream baseline, architecture, platform adaptations, experiment gates, milestones and acceptance criteria. Read the relevant section before implementation. Keep detailed decisions there rather than duplicating them here; update the plan when an accepted decision changes.

- Scope and evidence: [section 1](plan.md#1-target-scope-and-evidence).
- Designs and safety boundaries: [section 2](plan.md#2-architecture-and-deliberate-adaptations), especially credentials, CLI attribution, helper installation and hook transport.
- Work order and gates: [section 3](plan.md#3-implementation-sequence-and-acceptance-gates). Milestone 1 contains experiments; production persistence/ownership work belongs to later milestones.
- Verification: [section 4](plan.md#4-verification-matrix-and-completion-rules).

## Lasting rules

- Read the corresponding pinned upstream implementation and tests before porting a feature. Preserve copyright/licence notices and identify this as a Windows port; do not change the repository's origin.
- Record features, evidence and deliberate differences in `docs/parity.md` when implementation begins. Never claim live authentication, Mac runtime parity or full feature parity from fixtures, screenshots or compilation alone.
- Never present fabricated usage, placeholder controls or no-op actions as implemented functionality. Distinguish missing values, stale data and actual errors.
- Never expose secrets in settings, history, statusline scripts, logs, fixtures or source control. Follow the plan's secure-storage rules and keep diagnostics to allowlisted metadata.
- Never silently generate model requests during usage polling. The real-CLI hook benchmark consumes usage and requires the user's explicit go-ahead for the run; general implementation approval is insufficient. Follow the isolation/model/trial-cap procedure in the plan and never edit real hooks for that experiment.
- Never overwrite newer CLI state, consume a known rotated refresh token again, or treat an empty HUD/expired token as proof that the CLI is idle. Use the documented mutation guard and conflict handling.
- Never let activity hooks approve prompts, inject context or send user input. Follow the measured transport gate rather than assuming HTTP or command hooks are universally suitable.
- Never point installed integrations into movable application or build directories. Preserve unrelated settings, commands, hooks and account logins during repair/removal; do not kill CLI sessions to upgrade a helper.
- Never add telemetry, promotional prompts, a cloud service or a general-purpose plugin/server framework. Only the narrowly scoped local IPC selected in the plan is permitted.
- Never redistribute Apple-only fonts, symbols or sounds without appropriate rights, or imply that this port is signed by the upstream author.

## Build and repository hygiene

The solution is `ClaudeUsageTracker.slnx`: `src/ClaudeUsageTracker.Core` (`net10.0`), `src/ClaudeUsageTracker.App` (WPF, `net10.0-windows10.0.19041.0`), `src/ClaudeUsageTracker.Helper` (`net10.0`, NativeAOT) and `tests/ClaudeUsageTracker.Tests` (xUnit v3 on Microsoft.Testing.Platform, `net10.0-windows10.0.19041.0`). `global.json` pins SDK 10.0.401 (patch roll-forward) and selects the Microsoft.Testing.Platform runner. Validated from the repository root on 2026-09-30:

```powershell
dotnet restore ClaudeUsageTracker.slnx
dotnet build ClaudeUsageTracker.slnx
dotnet test --solution ClaudeUsageTracker.slnx
```

NativeAOT publishing of the helper was validated on 2026-10-01 with `dotnet publish src/ClaudeUsageTracker.Helper/ClaudeUsageTracker.Helper.csproj -c Release -r win-x64`. If `NoDefaultCurrentDirectoryInExePath` is set in the shell (some agent shells set it), unset it first; otherwise `VsDevCmd.bat` cannot run `vswhere.exe` and linking fails with MSB3073. App publishing is not yet validated; do not advertise it as a passing check.

To see the app without touching a real account, run it in fixture mode, `dotnet run --project src/ClaudeUsageTracker.App -- --fixture <folder>`, with synthetic Claude Code files and canned responses ([docs/milestone-2.md](docs/milestone-2.md)). Screenshots for documentation come only from fixture mode, which labels them. Launching the app normally reads the real Claude Code login and polls with it, so do that only when the user asks for a live check.

`spikes/` holds Milestone 1 experiment harnesses. They are outside the solution and are not production code; [docs/milestone-1.md](docs/milestone-1.md) records how they were run and what they showed.

This checkout is in Dropbox. `Directory.Build.props` sends all restore/build/test/publish output, including IDE builds and test results, to `%LOCALAPPDATA%\ClaudeUsageTracker.Build\<checkout-id>`, outside both the sync tree and the app's data root. Keep it that way. `.gitignore` does not exclude files from Dropbox sync, so check that no `bin`, `obj`, `TestResults` or similar folder appears in the checkout. `.vs\` is marked ignored by Dropbox; if it is recreated, mark it again with `Set-Content -Path .vs -Stream com.dropbox.ignored -Value 1`. Do not create repository-local Python environments.

## Working conventions

Use British English, standard C# naming conventions and upstream/external field names where exact matching matters. Do not hard-wrap Markdown. Follow the user's Python conventions if Python tooling is needed.

State assumptions and a brief plan with observable checks for multi-step work. Prefer minimal code and proven test seams over speculative abstractions. Surface real failures; do not silently reset storage or report fake success. Expected offline observation loss must follow the explicit hook policy in the plan.

Run checks appropriate to each change and inspect the diff. Separate automated, manual, live-account and source/image evidence in reports. Keep unresolved gates visible, and continue independent work when an approval-dependent experiment is pending.
