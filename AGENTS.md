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

The repository currently has planning documents, not a buildable solution. There are no validated application build/test commands yet. Prerequisites and scaffold choices are recorded in the plan; verify the environment with:

```powershell
dotnet --list-sdks
```

When scaffolding lands, replace this paragraph with the actual validated restore/build/test/publish commands and solution paths. Pin the SDK and keep NativeAOT toolchain requirements explicit. Do not advertise planned commands as passing checks.

This checkout is in Dropbox. Keep all restore/build/test/publish artifacts outside the sync tree using the plan's external artifacts layout, including IDE builds. `.gitignore` does not exclude files from Dropbox sync. Do not create repository-local Python environments.

## Working conventions

Use British English, standard C# naming conventions and upstream/external field names where exact matching matters. Do not hard-wrap Markdown. Follow the user's Python conventions if Python tooling is needed.

State assumptions and a brief plan with observable checks for multi-step work. Prefer minimal code and proven test seams over speculative abstractions. Surface real failures; do not silently reset storage or report fake success. Expected offline observation loss must follow the explicit hook policy in the plan.

Run checks appropriate to each change and inspect the diff. Separate automated, manual, live-account and source/image evidence in reports. Keep unresolved gates visible, and continue independent work when an approval-dependent experiment is pending.
