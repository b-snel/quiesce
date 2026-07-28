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
- **M4** — `quiesce inventory` reports whether any session is remote, since that changes which
  guardrails are active and a support bundle that omits it cannot explain a refusal.

- **M5** — Application control. Ops are now polymorphic over three kinds (`registry` | `service` |
  `process`) on the same `kind` discriminator, so plan, journal, verify and revert share one path.
- **M5** — **Graceful close only. There is no force path anywhere in Quiesce.** Closing asks by posting
  `WM_CLOSE` to each top-level window and waits; an application that declines — almost always because it
  is prompting about unsaved work — is left running and reported. Terminating a program discards work
  with no prompt, which is a worse outcome than a slightly less lean machine.
- **M5** — **A close is the one thing Quiesce does not undo, and it says so everywhere.** Restore lists
  what was closed and does not relaunch it: relaunching would mean guessing the command line, and for a
  browser it would restore the window without the tabs, which looks more like data loss than a restore.
  The preflight dialog states it before you approve, the CLI states it as it happens, and `revert.cmd`
  states it in the file.
- **M5** — Reversible throttle. Priority is lowered, never raised, with the prior class captured **per
  process** and written back exactly. Justified by measurement, not caution: on the development machine
  one application's 14 processes sat at Normal, Idle *and* AboveNormal simultaneously, so restoring the
  group to one value would have promoted the idle ones and demoted the busy one — and a byte-level check
  would have called that clean.
- **M5** — Process classification, ordered most-protective-first: the shell, system-critical processes,
  the compositor and the audio graph; anything hosting a Windows service (so the process layer cannot
  reach around the service guardrails, all of which are keyed on service names); anything under a game
  or launcher root; and anything whose image path or creation time cannot be read, which resolves to
  *leave it alone* rather than to a name-based guess.
- **M5** — **Quiesce never closes or throttles whatever launched it**, matched by image path as well as
  PID. This produces the right behaviour in production with no special case: launched from Explorer,
  only Quiesce's own image is protected, so an application the catalog says to close still gets closed.
- **M5** — Three application groups, each individually toggleable. Browsers are closed by default with
  keeping them alive available as a toggle — the only irreversible thing in the shipped default profile,
  gated behind a preflight that names every process by PID. Discord is untouched by default, with a
  throttle-only toggle at `BelowNormal` rather than `Idle`: `Idle` means "runs only when nothing else
  wants the CPU", which during a loading screen is exactly when a voice thread needs to run.
- **M5** — Targeting is **path-based, not name-based**. Each group pairs an image name with the
  directory the real installation lives under, so a `chrome.exe` in a temp directory is not treated as
  the browser you meant. Fragments are delimited at both ends, so a group targeting `\Discord\` cannot
  collect Discord Canary.
- **M5** — A process group fans out to **one plan step per live process**, each with its own journal
  record and its own prior. Processes that start after the plan is built are not touched: the preflight
  list is what was approved, so it is what runs.
- **M5** — `verify-revert` and `scripts/baseline-diff.ps1` both exclude entries that close applications,
  and both say what they excluded. Each asserts a byte-identical round trip, which a close cannot
  satisfy by construction — including one would have meant closing the operator's browser five times
  over in order to assert nothing about it.

- **M6** — **Restart warning.** `requiresReboot` was data nothing read: three catalog entries take effect
  only after a restart, and Quiesce reported them applied and said nothing. A change needing a restart now
  records a marker, and a banner names the waiting entries on every page until the machine actually
  restarts. It survives closing the app, and it survives Restore — putting a reboot-requiring value back
  does not put the running system back, so a restore that says "machine clean" is telling the truth about
  the registry and the wrong thing about the machine. The marker clears only on positive evidence of a
  restart (uptime going backwards), never on a resume from sleep, because a warning that retracts itself
  without a reboot is worse than no warning.
- **M6** — **Select all / Select none / Reset to defaults** on Features, with a summary of what a bulk
  action just did: how many entries close applications Restore will not reopen, how many change the machine
  for every user, how many need a restart, how many Windows will refuse on this machine, and how many were
  already lean. Every count comes from the live plan, not a hand-maintained list of rows to be careful about.
- **M6** — **Switched-off entries sort to the top** of Features and re-sort live as you toggle, so the
  exceptions you made are visible instead of scattered through three dozen rows. Off rows also state what
  the machine's live value is, so "off" and "off, and already lean anyway" are distinguishable.
- **M6** — **Running apps: discover what the catalog does not cover, and add it.** Lists applications running
  right now that Quiesce is permitted to act on, grouped by install directory, marking the ones already
  covered. Adding one writes an ordinary catalog entry pinned to the image name *and* the directory the
  application was found in — path-based targeting is the safety property, so discovery is a way of
  *authoring* a precise entry, never a looser way of matching. Added entries start switched OFF, live in the
  Administrators-only data root, and go through the same validator as the shipped catalog, which means the
  guardrails refuse a user-written entry exactly as they refuse a shipped one. New `quiesce list-apps` verb
  prints the same list.

### Hardening (from the M4 adversarial guardrail review)

- **Remote-session detection rewritten.** `GetSystemMetrics(SM_REMOTESESSION)` reports only on the
  *calling process's own* session, so an elevated helper or scheduled task running in session 0 gets
  `FALSE` while the operator sits on RDP in session 1 — and the network group unlocks. Now enumerates
  every session via `WTSEnumerateSessions` + `WTSQuerySessionInformation`, and fails closed.
- **`SessionGuard.OverrideForTests` is now `internal`** behind `InternalsVisibleTo`. A public mutable
  static that switches off a safety check is a back door, not a test seam.
- **Tier-0 additions:** `NvContainerLocalSystem` and `nvagent` (hosts ShadowPlay, carries a RUN
  PROCESS recovery action), `EasyAntiCheat`/`EasyAntiCheat_EOS`/`BEService`/`vgc` (ban vector), and
  `nordvpn-service`/`NordUpdaterService` — an unclean VPN stop can leave a WFP kill-switch filter
  blocking all traffic with no service left to remove it, severing RDP while every network guardrail
  reported a pass.
- **Per-user service instances** (`CryptSvc_4a2f1`) now inherit their template's protection.
- **Dependents get the full refusal predicate**, not just the tier-0 test: a dependent that is itself
  unstoppable now blocks its parent.
- **A stopped service no longer gets a vacuous co-tenancy pass.** PID 0 means "not evaluable", never
  "no co-tenants".
- **Across a reboot, restore puts back configuration only** and starts nothing. The SCM has already
  started or deliberately not started everything per its start type; forcing a start would run
  services the machine had legitimately left stopped.
- **Two identical copies of the remote-fragile list** collapsed into one. Two copies of a safety list
  is how drift happens.
- **Co-tenancy rationale corrected.** Stopping a service does *not* stop its co-tenants; the hazard is
  a fault during the stop taking the shared host down, after which the co-tenants die without
  reporting `SERVICE_STOPPED` and their failure actions fire — and seven tier-0 services on this
  machine are configured with REBOOT at 30–120s. The false version was easy to disprove, and
  disproving it would have got the check deleted.

### Verified on real hardware

- **M4** — **The elevated acceptance run passed.** 24 registry entries, 5 rounds, no drift; then 8 service
  entries, 5 rounds, no drift. This is the first time any service was actually stopped or reconfigured —
  every earlier check ran against fakes and the real *read* APIs. Start type, delayed-auto flag and run
  state all came back exactly: the four `Automatic (delayed)` services kept their flag, the five with no
  such value still have none, and `MapsBroker` was correctly left Stopped rather than started.
- **M4** — `svc.print-spooler` is deliberately **not** covered. The development machine reaches Windows over
  RDP, and the redirected printer lives in Spooler's store — stopping it drops the queue until the session
  reconnects. Verified by doing it accidentally. It needs a local-console run.

### Added

- **M4** — **Plan-time refusal of registry writes Windows vetoes in the kernel.** `UCPD.sys` (the User
  Choice Protection Driver) registers a `CmRegisterCallbackEx` callback and denies `RegNtPreSetValueKey`
  for an exact, case-insensitive *(key path, value name)* pair. It sits downstream of the security check,
  so opening the key with `KEY_SET_VALUE` succeeds and the refusal only arrives at the write — no ACL can
  produce that, because security descriptors attach to keys, not to value names. Quiesce now declines
  these before touching anything, showing the reason. Gated on the driver actually running, so a tweak
  returns on its own if Microsoft drops the pair. Two pairs measured on build 26200.8875
  (`Dsh!AllowNewsAndInterests`, `Explorer\Advanced!TaskbarDa`), three more listed from the same driver
  table and marked as unmeasured.
- **M4** — Already-lean beats refused in the plan. A value that already holds the target data needs no
  write, so no write can be refused, and "Windows blocks this" about a step that was never going to run is
  simply untrue. `TaskbarDa` is both vetoed *and* already lean here, so the wrong order would turn a
  healthy row into an alarming one.

### Fixed

- **M6** — **Adding a running app created a new entry every time it was pressed.** Throttling
  ApplePhotoStreams four times produced four entries — the base id plus `-2`, `-3`, `-4` — all doing the
  same thing to the same folder, showing up in Features as four identical rows. Two causes, both fixed. The
  page survives the shell's page rebuild on purpose (tearing down a control still inside its own click
  handler would crash) but nothing else refreshed its state, so it kept comparing the machine against the
  catalog as it was *before* the add, kept showing the app as uncovered, and kept offering the button.
  Separately, adding is now an upsert keyed on (directory, action) rather than an append: the id suffix
  exists for two *different* applications sharing a display name, and reaching for it when the same
  application is added twice is what turned a duplicate into four. A rescan that finds executables the
  stored entry does not cover now extends it — which is also the only way an entry added while three
  helpers were running comes to cover the other three. Remove takes every entry the user added for that
  application rather than one of them.
- **M6** — **The first live run of the running-apps list offered `C:\Windows\System32` as an application.**
  Grouping by install directory assumes a directory belongs to one program, which is true of an install tree
  and false of System32 — where eleven unrelated processes were collected into one candidate named
  `ApplicationFrameHost`. Adding it would have pinned `C:\Windows\System32\` and asked all eleven to close,
  including `rdpclip.exe`, the clipboard of the Remote Desktop session driving the machine, plus `ctfmon`,
  `sihost`, `taskhostw` and whatever console was open. Nothing under the Windows directory is offered now,
  structurally rather than by a list of names to spare; Store-packaged applications under `WindowsApps` are
  unaffected because each package genuinely has its own directory. The omitted count is reported rather than
  the list quietly getting shorter.
- **M6** — **Another copy of Quiesce was offered as something to close.** Self-protection is path-based on
  purpose, so a second copy on disk is a different image path and slipped through. Excluded by image name.
- **M6** — **Bulk enable unioned with the built-in default instead of replacing the enabled set.** On a
  profile that had never been saved, "Select all" produced nine enabled ids in a three-entry catalog, and
  "Select none" could not remove the six it had inherited because no row existed to switch off. Bulk actions
  now state the set outright, which also prunes ids a catalog update has renamed away.
- **M6** — A test class that pinned the process ancestry to its own PID passed alone and failed in the full
  run, because two other classes pin the same process-wide static to the empty set and xUnit runs classes in
  parallel. Everything touching it now shares one collection.

- **M5** — **Quiesce reported `machine: clean` about a machine that was engaged.** The data root is
  hardened to Administrators by design — an elevated Quiesce later executes the revert plan it finds
  there — and `File.Exists` returns `false` when the real answer is "you are not permitted to look",
  because it swallows every exception. So an unelevated read of the state file fell through to a default
  state and reported not-dirty. Found on real hardware: with GameDVR and mouse acceleration genuinely
  turned off in the registry at that moment, `inventory` said clean, `restore` said "No active session.
  Nothing to restore.", and `recover` said "Machine is clean". Three reassurances, all false, from one
  swallowed access denial on the only question this tool exists to answer. Every one of these paths now
  opens the file and lets the exception distinguish absent from denied, and reports **UNKNOWN** with a
  non-zero exit code rather than guessing. The same probe is fixed in four more places: the profile store
  (an unelevated `print-plan` silently computed the plan from the shipped defaults while presenting it as
  yours), `revert-all`'s per-session skip (which would have skipped a session holding outstanding changes
  and then reported "machine clean" — the panic button claiming success for work it never looked at),
  the journal-missing check, and the ACL preflight (which passed paths whose ACL it could not read, i.e.
  the one check whose whole job is refusing failed open).
- **M5** — **Perplexity's Comet browser was not in the browser group**, so it sailed straight through an
  Engage with 20 live processes while the plan printed nine confident "nothing matching X is running"
  lines. A list of browsers written from memory is a list of the browsers its author thought of; the
  plan cannot report on what it was never told to look for.
- **M5** — **A PID-based self-protection guard protected 2 of the host application's 14 processes.** A
  Chromium-style application puts its renderers and helpers *beside* the process that spawned the child,
  not above it, so the other 12 classified as ordinary and would have been throttled. Breaking 12 of 14
  processes breaks the application just as thoroughly as touching the main one. Protection is now keyed
  on image path as well as PID: measured at 14 of 14 refused, with zero priority drift.
- **M5** — **Already-throttled processes were reported as guardrail refusals.** "Already at or below the
  target" and "that would be a raise" are arithmetically the same condition, and only the first is a true
  description — a process sitting at Idle is not something Quiesce is declining to touch, it is already
  quieter than asked. The elision is now tested first, which is the M4 registry ordering lesson repeating
  in the opposite direction.
- **M5** — **The close ladder gave a generic reason for refusing its own host.** The self-protection class
  existed and the throttler explained it properly, but the closer's switch had no arm for it, so the
  refusal fell through to "not in a class Quiesce will close". The whole point of a distinct class is that
  the user gets the true reason; a generic one reads like the app being arbitrary rather than the app
  protecting the process performing the change.
- **M5** — **A throttle could create an obligation Quiesce would refuse to discharge.** Lowering a process
  from above the `AboveNormal` ceiling works fine, and then restore would have to raise it back past that
  ceiling — which this codebase cannot even name, by design. Both ends are now closed: the throttle is
  refused up front, and restore refuses a recorded prior above the ceiling rather than honouring a journal
  that a later hand-edit could turn into an arbitrary-priority primitive.
- **M5** — **The preflight dialog would have thrown on a process step.** The row builder tested for a
  service prior and otherwise dereferenced the registry prior, so a step carrying neither was a null
  reference in the middle of the dialog where the user approves changes.
- **M5** — Residue from an entry rollback was discarded while the same residue on the revert path was
  reported. Same fact, same obligation to state it: an unwind that leaves something behind and says
  nothing is how a tool ends up having changed a machine it called clean.
- **M5** — `tor` removed from the browser class. `tor.exe` is the SOCKS daemon, not the browser — Tor
  Browser's UI process is `firefox.exe` — so acting on that name would have cut the browser's networking
  out from under a window left standing.
- **M4** — **A refused write left the machine unrevertable, forever.** The vetoed value was never created,
  so the captured prior was "absent" — and revert then tried to *delete* the absent value, which the same
  callback also refuses. The session reported `machine still DIRTY` over a value that had never changed,
  and no retry could ever clear it because every retry performed the same forbidden no-op. Restore now
  checks whether the end state already holds before mutating, which is also what finally makes revert
  idempotent rather than merely documented as such.
- **M4** — **A refused write crashed the caller mid-apply.** `SetValue` throwing was caught and turned into
  a typed diagnosis, but the *entry rollback* then re-wrote a prior that had never changed, was refused in
  turn, and let that exception escape `Engage`. So "a refused write is an outcome, not a crash" only ever
  held for the forward write. An existing test had been pinning the escaping exception as if it were the
  intended behaviour.
- **M4** — **An empty key Quiesce cannot delete is residue, not a failed revert.** `SetValue` calls
  `CreateSubKey` before writing, so a vetoed write still creates the key — and Quiesce was then refused
  permission to remove the empty key it had just made. That wedged the session permanently. It is now
  reported as `restored-with-residue` and named out loud, because residue nobody mentions is how a tool
  ends up having changed a machine it called clean.
- **M4** — A refused write reported *"the tweak may need elevation, or may be locked by policy or Tamper
  Protection"* while discarding the actual exception — on a run that was demonstrably elevated, with seven
  sibling HKLM policy writes succeeding in the same session. The diagnosis now carries the real message and
  HRESULT and branches on the live elevation state instead of guessing. A diagnosis that cannot be
  falsified is not a diagnosis.
- **M4** — **`scripts/baseline-diff.ps1` could not run elevated at all**, which is to say it could never
  exercise the service path it existed to test. Ops have been polymorphic on `kind` since M4 and a service
  op carries no subkey, so all nine resolved to the path `HKCU:\` — the whole user hive — and the run died
  on the first volatile key that vanished mid-enumeration. Invisible unelevated, because every service
  entry is `requiresAdmin` and got filtered out first.
- **M4** — The diff had **no service coverage** in the service milestone. It now watches each candidate's
  own key recursively (so `Start` and `DelayedAutostart` are covered byte-exactly, catching the
  materialization case) plus run state from the SCM, and **fails** naming any service that engage did not
  move. Twenty-four registry entries applying is more than enough to keep an aggregate change count
  healthy while all nine service steps quietly did nothing.
- **M4** — The diff **deleted its own journal and `revert.cmd` on failure** — the one run that leaves the
  machine dirty had its only means of recovery removed on the way out. Cleanup now happens only on success,
  and `-FaultInject` exists so that path is tested rather than assumed.
- **M4** — `-Skip a,b` silently filtered **nothing** when invoked as `powershell -File` from cmd.exe, which
  takes the remaining arguments as literal strings: the parameter bound as one element and matched no entry
  id. A run meant to cover 23 entries covered all 33 and stopped nine services on an RDP machine. Now
  splits inside each element, echoes the filters **as parsed** with a removal count, and hard-errors on a
  prefix matching no entry id anywhere in the catalog.
- **M4** — A no-op engage was a **warning printed underneath a green `PASS`**. If the machine is already
  engaged, the baseline captures the applied state and "restore returned it to baseline" compares that
  state against itself — trivially byte-identical, five rounds running, testing nothing. It now fails the
  round, and a preflight refuses to start while any journal has applied records without a `revertComplete`.
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
