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

### Fixed

- **M2** — `InvariantGlobalization=true` (set during M0 as a size optimization) crashed WPF at startup with a
  `CultureNotFoundException` from the font cache. Removed, with a comment so it does not come back.
- **M2** — The selected navigation item rendered as a solid block of the *system* accent colour, which reads as
  an error state on machines whose accent is red. Retemplated with a fixed logo-blue pill.
- **M1** — `revert-all` refused to run when the catalog was missing, even though revert reads only the journal.
  The panic button must work with the catalog gone; the CLI now resolves the catalog lazily. Regression-tested.
- **M1** — An early elevation gate refused *revert* without admin, which could strand a user with an engaged
  machine they were unable to undo. Only `engage` is gated now: Quiesce is strict about creating obligations and
  never refuses to discharge one.
