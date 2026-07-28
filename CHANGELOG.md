# Changelog

All notable changes to Quiesce are documented here. This file is the source for GitHub Release notes, which are
in turn what the in-app update prompt shows you — so entries are written for users, not for git archaeology.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Quiesce uses
[semantic versioning](https://semver.org/) driven by git tags via MinVer.

## [Unreleased]

### Added

- **M0** — Solution scaffold: `Quiesce.Core` (engine), `Quiesce.Cli` (`quiesce.exe`, all CLI verbs and the
  panic-revert path), `Quiesce.App` (WPF GUI), and the test project. Targets `net10.0-windows10.0.26100.0`
  with the SDK pinned to 10.0.302 and every NuGet version exact-pinned via central package management.
- **M0** — `Guardrails`: the tier-0 never-touch service list, protected process list, launcher/anti-cheat root
  markers, the `AboveNormal` priority ceiling, and the remote-session lockout set — as compile-time constants
  that catalog data can only narrow, never widen.
- **M0** — Two-layer anti-injection gate: `BannedSymbols.txt` driving `BannedApiAnalyzers` at compile time, plus
  a CI grep that catches strings and reflection. Quiesce contains zero code-injection primitives by construction.
- **M0** — CI: build, test, CLI exit-code contract, catalog JSON validation, guardrail grep, and a check that no
  signing material is ever committed.
- **M0** — App icon from the ripple logo (16–256px `.ico`, plus 512px light/dark PNGs for in-app use).
- **M1** — The reversibility engine. Write-ahead journal (append-only, flushed to disk per record, exclusive
  lock, torn-final-line tolerant, hard refusal of future schema versions), tri-state registry priors
  (value-present / value-absent / key-absent), and a `TransactionEngine` doing Plan → Apply → Verify → Revert.
- **M1** — Working verbs: `inventory`, `print-plan`, `engage`, `restore`, `revert-all`, `recover`,
  `verify-revert`, plus `--fault-inject=afterStep<N>` for deterministic crash testing.
- **M1** — 66 tests covering the guarantees that matter: absent restores to absent (never `0`), created keys are
  removed unless someone else used them, already-lean values are elided and never "restored", a value changed by
  someone else after apply is kept rather than clobbered, multi-op entries roll back whole, an unloaded user hive
  defers instead of silently claiming success, and revert works with the catalog deleted.

- **M2** — The WPF shell: a `FluentWindow` with Mica backdrop and four pages — Dashboard (machine state, honest
  framing, environment facts), Features (catalog rendered with evidence badge, impact, risk tier and what it
  breaks), Services (the tier-0 never-touch list, shown locked *with the reason*), and What Quiesce won't do.
  Read-only: Engage and Restore are present but disabled until the M3 wiring.
- **M2** — Single-instance guard on a `Global\` mutex, so a second window cannot race the first.
- **M2** — `run-app.ps1`, which stages the build output to `%TEMP%` before launching. An elevated Quiesce locks
  its own build output and cannot be closed from an unelevated shell; running from a copy avoids the deadlock.

- **M3** — The registry catalog: 24 entries, 38 ops across privacy, debloat, shell and gaming, every target's
  value kind read off this machine rather than assumed. Ships with an exclusions list of everything deliberately
  refused.
- **M3** — Per-entry toggles. Entries are **opt-in**: a catalog row does nothing until a profile enables it, so
  shipping a new catalog can widen what is *available* but never what is *applied*. The default profile is the
  five entries from the plan; everything else is visible and off.
- **M3** — Activation state capture. `SPI_GETMOUSE` records the live acceleration curve before the write and
  revert replays it, because re-broadcasting `SPI_SETMOUSE` would re-apply the *lean* curve while every
  byte-level check reported a clean restore.
- **M3** — `revert.cmd`: a literal reg.exe script written before each mutation, so a session can be undone with
  no Quiesce binary at all. Verified end-to-end by running it as the only revert mechanism.
- **M3** — System Restore integration that compares sequence numbers before and after, and reports
  "no new point was created" plainly rather than trusting an API that returns success while doing nothing.
- **M3** — Preflight dialog rendering the literal planned steps, and Engage/Restore wired in the GUI.
- **M3** — `scripts/baseline-diff.ps1`: recursive snapshot of every catalog subtree plus the live mouse curve,
  engage/restore/diff, repeated N times. Passing at 5 rounds over 16 entries and 1101 values.

- **M4** — Service control. Ops are now polymorphic (`registry` | `service`) on the `kind`
  discriminator the catalog already carried, so plan, journal, verify and revert share one code path.
- **M4** — Three-fact capture: start type, delayed-auto flag and run state are captured and restored
  **independently**, through `QueryServiceConfig`/`QueryServiceConfig2` rather than
  `ServiceController`, which collapses Automatic-Delayed into Automatic and would silently convert
  four of the nine shipping candidates to plain auto — slowing every subsequent boot.
- **M4** — Guardrails: tier-0 never-touch list (enforced at catalog load *and* twice at runtime),
  svchost co-tenancy keyed on live host PID, remote-session lock, stop-capability check,
  transitive-dependent check, and trigger-started services clamped to Manual and never Disabled.
- **M4** — Nine service entries, each individually toggleable and all off by default.
- **M4** — `revert.cmd` now emits `sc.exe` inverses, including the distinct `start= delayed-auto`
  token, and never restarts a service that was stopped.
- **M4** — Refused steps are shown with their reason in both the CLI and the preflight dialog. A
  guardrail the user cannot see is indistinguishable from a tweak that quietly did nothing.

### Fixed

- **M4** — **Ordering bug found in review:** the start type was written before the service was
  stopped. Disabling a service does not stop it, so a stop that then timed out would leave the
  machine `Disabled + Running` — correct-looking for the whole session, after which the service
  silently never returns at next boot. Stop now happens first, and a refused stop leaves the service
  exactly as found.
- **M4** — `DelayedAutostart` is written only when it differs from the live value. Six of the nine
  candidates have no such value at all, and issuing the call materializes one — a silent registry
  mutation that survives revert and quietly breaks the exact-restore promise.
- **M4** — A boot-id sampling race made recovery intermittently believe the machine had rebooted and
  auto-revert a live session, pulling tweaks out from under a running game. `CurrentBootId` samples
  the clock and the uptime counter separately, so two calls in one boot can differ by a second;
  comparison is now tolerance-based. Found via a flaky test that turned out to be a real defect.
- **M4** — Neither the CLI nor the GUI wired `IServiceControl` into the engine, so every service step
  was refused as "unavailable"; and `print-plan` then crashed rendering a refused step through the
  registry branch.
- **M4** — A malformed catalog threw a raw `JsonException` that no caller catches, crashing the CLI
  with a stack trace instead of reporting what was wrong with the file.
- **M4** — `EnumDependentServices` returns the full transitive closure, not direct dependents
  (verified against the registry dependency graph). Documented so a future change does not add
  caller-side recursion and corrupt the stop order.
- **M4** — CLI contract tests shared one registry key across all seven tests, making them
  order-dependent. Each instance now gets its own.
- **M3** — A denied registry write crashed the process mid-apply with an unhandled
  `UnauthorizedAccessException`, leaving the machine dirty and the user holding a stack trace. Refused writes are
  now a typed diagnosis that rolls the entry back, and the reason is surfaced verbatim in both the CLI and GUI.
  Found by the baseline diff on its first round.
- **M3** — `requiresAdmin` was derived from "does it target HKLM", which is wrong: the per-user policy subtree
  `HKCU\...\CurrentVersion\Policies` is owned by Administrators and grants the interactive user read-only. The
  loader now rejects any HKCU policy-subtree op that does not declare `requiresAdmin`.
- **M3** — CLI contract tests shared one registry key across all seven tests, making them order-dependent and
  intermittently flaky. Each test instance now gets its own key.
- **M2** — `InvariantGlobalization=true` (set during M0 as a size optimization) crashed WPF at startup with a
  `CultureNotFoundException` from the font cache. Removed, with a comment so it does not come back.
- **M2** — The selected navigation item rendered as a solid block of the *system* accent colour, which reads as
  an error state on machines whose accent is red. Retemplated with a fixed logo-blue pill.
- **M1** — `revert-all` refused to run when the catalog was missing, even though revert reads only the journal.
  The panic button must work with the catalog gone; the CLI now resolves the catalog lazily. Regression-tested.
- **M1** — An early elevation gate refused *revert* without admin, which could strand a user with an engaged
  machine they were unable to undo. Only `engage` is gated now: Quiesce is strict about creating obligations and
  never refuses to discharge one.
