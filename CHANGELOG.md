# Changelog

All notable changes to Claude Usage Tracker for Windows are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The release workflow copies a version's section into its GitHub release notes, so every tag `vX.Y.Z` needs a `## [X.Y.Z] - YYYY-MM-DD` section here first.

The port follows upstream [Claude Usage Tracker](https://github.com/hamed-elfayome/Claude-Usage-Tracker) at commit [`588775e`](https://github.com/hamed-elfayome/Claude-Usage-Tracker/tree/588775e4540757443691fa1bcb33457315e05f86) of 2026-08-31, five commits after its v3.3.0. [docs/parity.md](docs/parity.md) records every upstream feature's status and each deliberate difference by number.

## [Unreleased]

Nothing yet.

## [1.0.0-beta.2] - 2026-11-06

A bug-fix beta, still run on one PC only. Install it over 1.0.0-beta.1; the installer closes and restarts the tracker and keeps its data.

### Fixed

- **Switching Claude Code to a profile with a claude.ai sign-in could be refused, asking for a new sign-in.** Such a profile gets its usage from claude.ai, so its saved copy of the Claude Code login was never updated when Claude Code renewed that login, which retires the old refresh token. A switch made by the tracker saved the newest login first, but a `/login` in Claude Code, which the refusal asked for, did not, so the next switch back to the other profile was refused in turn. The copy now follows Claude Code's renewals at every check, and the tracker still renews nothing for such a profile.

### Security

- **Built on .NET runtime 10.0.14** (SDK 10.0.403) instead of 10.0.12. The app and its helper now include the .NET security fixes of October 2026, among them CVE-2026-111497, an out-of-bounds write in the Brotli decoder that the app's HTTP client uses for compressed responses. After installing, Settings → Claude CLI offers to update the installed helper.

## [1.0.0-beta.1] - 2026-10-28

The first public build, a beta: it has run on one PC only. Everything below was built between 2026-09-30 and 2026-10-28 in the milestones of [plan.md](plan.md), whose logs are in [docs/](docs/).

### Added

- **Tray icon and popover.** Five icon styles with monochrome and per-metric variants, icons for several profiles at once, and a popover with the session, weekly and per-model usage, the API Console credits and costs, the Codex usage, the service status, a profile switcher and refresh (Milestones 2 and 3).
- **Profiles.** Any number of Claude and Codex profiles, each with its own login encrypted with Windows DPAPI and its own settings, and a setup wizard with the Claude Code, claude.ai, API Console and Codex routes (Milestones 2 and 3).
- **Claude Code account.** Links Claude Code's own login for read-only usage polling, and renews an expired login and hands it back only while no Claude Code runs. Picking a profile, or Use in Claude Code, switches Claude Code's account to that profile's, waiting for Claude Code to close and keeping encrypted backups (Milestones 2 to 4, and 2026-10-13).
- **claude.ai and API Console.** Sign-in through a per-profile WebView2 browser context, organisation choice and session expiry; Console credits and monthly, daily, per-key and per-model costs (Milestone 3).
- **Codex.** Reads the Codex CLI's login or pasted credentials, polls Codex usage, and can auto-start a Codex session through the Codex CLI (Milestones 3 and 6).
- **History.** Session, weekly and billing snapshots with charts over 5 hours, 24 hours, 7 days and 30 days, and JSON and CSV export (Milestone 3).
- **Notifications and automation.** Threshold and reset notifications with Windows sounds and click routing, launch at login, global shortcuts, checks on resume and network changes, opt-in session auto-start and profile auto-switch (Milestone 4).
- **Claude Code status line.** A NativeAOT helper renders the status line from a cache without secrets, with upstream's components, colour modes and per-element colours; the tracker installs, upgrades and repairs it from its data folder (Milestone 5).
- **Terminal launchers.** A `claude-<profile>` command per profile that opens Claude Code with that profile's own login folder, under PowerShell, Command Prompt and Git Bash (Milestone 5).
- **Activity HUD.** A floating window that shows what each Claude Code session is doing, fed by Claude Code's hooks over a local HTTP listener while the tracker runs (Milestone 5).
- **Debug Tools.** A log of requests with their operation, route, status and timing and no secrets, including switches of Claude Code's account (Milestones 3 and 6).
- **Accessibility.** Accessible names and keyboard access for every control, checked by an automated audit (Milestone 6); no screen reader has been tried.
- **Fixture mode** (`--fixture <folder>`), which runs the app on synthetic files and canned responses without touching a real account; the screenshots come from it (Milestone 2).
- **Self-contained Windows x64 build** with .NET 10 and the Windows App SDK included, and the personal-use guide (Milestone 6).
- **Public repository:** README, this changelog, Windows CI, and a tag-driven release workflow that makes an unsigned per-user installer and a portable ZIP. The uninstaller asks whether to delete the data folder too (settings, history and saved logins), keeps it by default, and deletes it unasked only when run with `/DELETEDATA=1`.

### Changed

Deliberate differences from upstream, each explained under its number in [docs/parity.md](docs/parity.md#deliberate-differences-so-far):

- English only: upstream's 13 other translations and its language page are not ported.
- Windows DPAPI files replace the macOS Keychain, and macOS app data is not imported.
- A floating HUD window replaces the notch "Dynamic Island".
- claude.ai requests go through each profile's own WebView2 context.
- No telemetry, heartbeat, star, feedback or support prompts, sponsor slot, mobile-app page or automatic updates.
- Usage polling never generates model requests; session auto-start, which does, stays opt-in.
