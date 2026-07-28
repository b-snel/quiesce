# Quiesce

**A true Windows game mode.** Quiesce quiets the apps, services and Windows bloat you don't need during a
gaming session, then puts your machine back exactly as it was.

> *To quiesce: to bring a system to a quiet, consistent state before an operation — and resume it afterward.*

> [!WARNING]
> Pre-release and under active development. Not yet usable.

## What to actually expect

Quiesce will not raise your average FPS, and it says so on its own dashboard. Here is the measured reality:

| Comparison | Result |
| --- | --- |
| Game Mode on/off, GPU-bound Cyberpunk | 82.4 → 82.9 avg FPS |
| Game Mode on/off, Forza Horizon 5 **with background apps open** | 1% lows 48.2 → **71.5** |
| AtlasOS (fully debloated Windows) vs stock, i5-13600K / 3070 Ti | 444/401.5 vs 444/403 FPS |
| Seven commercial "game boosters", controlled test | median **+2.4 FPS** — two measured *negative* |

So the honest pitch is narrow and specific: **fewer frame-time spikes and better 1% lows.** That is what
background work actually costs you, and it is what Quiesce targets. There is no FPS counter anywhere in the UI,
because Quiesce doesn't measure frames and won't imply it does.

The mechanism it's most confident about: browsers, Electron apps and Discord routinely request 0.5–1 ms global
timer resolution, which raises timer-interrupt and DPC load and measurably degrades frame pacing. Quiesce
suppresses that per-process with a documented, reversible API call.

## The actual differentiator: the undo works

Every tool in this space gets undo wrong the same way — it stores a hardcoded "default" value in its config and
writes *that* back, instead of what your machine actually had. Quiesce does not.

- **Nothing is assumed.** Prior state is read off your machine, written to a journal *before* the change, and
  the revert path reads only that journal — never the catalog.
- **"Absent" is not "zero".** Most of these registry values don't exist on a clean install. Restoring them by
  writing `0` is a permanent, undetectable behaviour change. Quiesce deletes what it created.
- **Crash-safe.** A write-ahead journal plus recovery at boot and logon, so a BSOD mid-session doesn't leave you
  stranded. Recovery triggers on *"is this machine dirty"*, not *"did apply finish"* — those are different facts.
- **Four independent ways back**, the last of which needs no Quiesce binary at all: a generated `revert.cmd` of
  plain `reg.exe` / `sc.exe` / `powercfg` commands.
- **It never lies about success.** Every write is verified by re-reading the source, and a write blocked by
  Tamper Protection or Group Policy is reported as blocked, not as done.

## Honesty by construction

- Every tweak carries a required `evidence` field — `Measured`, `Situational`, `A-B`, `Cosmetic`, `NoEvidence`,
  `NotRecommended` — and the UI shows it. Cargo-cult tweaks either don't ship or ship visibly labelled and off.
- Telemetry toggles are presented as a **privacy** feature, not a performance one, because that's what they are.
- Services Quiesce refuses to touch are shown **locked with the reason**, not hidden.
- There is a **"What Quiesce won't do"** page listing what was deliberately left out and why.

### Deliberately not implemented

No Defender disable. No permanent Windows Update disable. No pagefile disable. No Appx or provisioned-package
removal. No registry key deletion. No registry "cleaning". No DLL injection, D3D hooking or global hooks. No
process suspension. No `RealTime` priority. No standby-list purging or `EmptyWorkingSet` theatre.

Process suspension and injection are absent for a concrete reason: kernel anti-cheats treat them as cheating,
and an EasyAntiCheat ban propagates across every EAC title tied to your hardware ID. Quiesce contains zero
injection primitives, and CI fails the build if any appear.

## Requirements

- Windows 10 2004+ / Windows 11 (developed against Windows 11 25H2, build 26200)
- x64
- Administrator rights — it reconfigures services and writes `HKLM`

## Building

```bash
winget install --id Microsoft.DotNet.SDK.10 --exact
```

```bash
dotnet build -c Release
```

```bash
dotnet test -c Release
```

## Layout

| Path | What it is |
| --- | --- |
| `src/Quiesce.Core` | The engine: catalog, journal, ops, guardrails. No UI. |
| `src/Quiesce.Cli` | `quiesce.exe` — every CLI verb, and the panic-revert path. |
| `src/Quiesce.App` | The WPF GUI. |
| `catalog/` | The tweak catalog, as data. New tweaks need no code. |
| `tests/` | xunit, against mockable platform seams. |

`Quiesce.App` is a `WinExe` and therefore has no console, so all CLI verbs live in `Quiesce.Cli` — otherwise
none of the acceptance tests could observe an exit code.

## Licence

MIT. See [LICENSE](LICENSE).
