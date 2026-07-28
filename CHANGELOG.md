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
