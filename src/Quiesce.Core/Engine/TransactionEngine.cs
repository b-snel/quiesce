using System.Diagnostics;
using System.Text.Json;
using Quiesce.Core.Catalog;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

namespace Quiesce.Core.Engine;

/// <summary>Build/environment facts stamped into every session's <c>sessionStart</c> record.</summary>
public sealed record EngineInfo
{
    public required string AppVersion { get; init; }

    public required string OsBuild { get; init; }

    /// <summary>SID of the interactive user whose HKCU the plan should resolve to.</summary>
    public required string UserSid { get; init; }
}

/// <summary>
/// The one code path through which every mutation and every revert flows.
/// Plan → Apply → Verify → Revert, with the write-ahead journal as the single source of truth.
/// </summary>
/// <remarks>
/// Design rules enforced here, each traceable to a failure mode found in review:
/// <list type="bullet">
/// <item>Prior state is captured live and journalled durably BEFORE each mutation. Revert accepts
/// ONLY journal records — there is no overload that takes a catalog entry.</item>
/// <item>The <see cref="CatalogEntry"/> is the transaction unit: a step that fails verification
/// rolls back its whole entry, so a 7-op entry can never be left half-applied.</item>
/// <item>No-ops are elided at plan time and never journalled as applied.</item>
/// <item>Dry-run is <see cref="Plan"/> simply not being followed by <see cref="Engage"/> — an
/// engine mode, not a write-dropping decorator that would make verify fail on 31 untouched values.</item>
/// <item>Revert defers (never skips-as-done) per-user steps whose hive is not loaded, and only a
/// revert with zero deferred and zero failed steps clears the dirty flag.</item>
/// </list>
/// </remarks>
public sealed class TransactionEngine(
    IRegistry registry,
    IActivationBroadcaster broadcaster,
    QuiescePaths paths,
    EngineInfo info,
    IActivationCapture? activationCapture = null,
    IServiceControl? services = null,
    IProcessControl? processes = null,
    ProcessClassifier? processClassifier = null,
    IPowerControl? power = null)
{
    private readonly StateStore _state = new(paths.DataRoot);

    /// <summary>
    /// Optional because most activations carry no state. When the broadcaster also implements
    /// capture (the Win32 one does), use it automatically.
    /// </summary>
    private readonly IActivationCapture? _capture = activationCapture ?? broadcaster as IActivationCapture;

    /// <summary>
    /// The close ladder and the throttle, built only when BOTH a process control and a classifier were
    /// supplied.
    /// </summary>
    /// <remarks>
    /// Deliberately not defaulted. A classifier constructed with no arguments knows no game directories,
    /// no service host PIDs and nothing about what launched Quiesce, so defaulting one here would silently
    /// produce an engine that acts on processes with its safety checks switched off — the exact bug the
    /// <see cref="ProcessClassifier.ForMachine"/> factory exists to prevent. A half-wired engine refuses
    /// process ops with a reason instead.
    /// </remarks>
    /// <remarks>
    /// <paramref name="services"/> is passed through for the anti-cheat half of the game-live guard. It is
    /// already optional and already null in the tests that only exercise the process layer, so the guard
    /// degrades the way it documents rather than requiring every caller to change.
    /// </remarks>
    private readonly ProcessCloser? _closer = processes is not null && processClassifier is not null
        ? new ProcessCloser(processes, processClassifier, services)
        : null;

    private readonly ProcessThrottler? _throttler = processes is not null && processClassifier is not null
        ? new ProcessThrottler(processes, processClassifier, services)
        : null;

    private const string ProcessLayerUnavailable =
        "Process control is unavailable in this build, or was wired without a classifier. " +
        "Quiesce will not act on processes without its guardrails.";

    // ---------------------------------------------------------------- Plan

    /// <summary>
    /// Projects the catalog into concrete steps against live state.
    /// </summary>
    /// <param name="enabledIds">
    /// Entry ids switched on. Null means "every entry", which is only appropriate for tests —
    /// production callers pass the active profile so that a catalog update can add available
    /// tweaks without silently applying them.
    /// </param>
    public EngagePlan Plan(CatalogFile catalog, string profile, IReadOnlySet<string>? enabledIds = null)
    {
        var steps = new List<PlannedStep>();
        var stepId = 0;

        var entries = enabledIds is null
            ? catalog.Entries
            : catalog.Entries.Where(e => enabledIds.Contains(e.Id)).ToList();

        // Process enumeration is one call for the whole plan, not one per op. Ten browser ops against
        // ~270 processes would otherwise be ten full sweeps, and — worse — ten different answers, so two
        // ops in the same entry could disagree about what is running.
        var live = _closer is not null ? processes!.Enumerate() : [];

        foreach (var entry in entries)
        {
            foreach (var op in entry.Ops)
            {
                // A process op describes a GROUP and yields one step per live member, so the step count
                // is not known until the machine has been looked at. Registry and service ops yield
                // exactly one step each, which is why this used to be a straight one-to-one loop.
                if (op is ProcessOpSpec processOp)
                {
                    foreach (var step in PlanProcess(entry, processOp, live, () => ++stepId))
                    {
                        steps.Add(step);
                    }

                    continue;
                }

                stepId++;
                steps.Add(op switch
                {
                    RegistryOpSpec r => PlanRegistry(stepId, entry, r),
                    ServiceOpSpec s => PlanService(stepId, entry, s),
                    PowerOpSpec p => PlanPower(stepId, entry, p),
                    _ => throw new NotSupportedException($"Unsupported op kind '{op.GetType().Name}'."),
                });
            }
        }

        return new EngagePlan { Profile = profile, CatalogVersion = catalog.CatalogVersion, Steps = steps };
    }

    /// <summary>
    /// Projects one process group onto the live process list: one step per matching process.
    /// </summary>
    /// <remarks>
    /// Refused members are emitted as refused steps rather than filtered out. During development every
    /// process of the application hosting Quiesce matches and every one of them is refused, and seeing
    /// that is the point — a group that silently shrank to nothing would look identical to a group that
    /// had nothing to do.
    /// </remarks>
    private IEnumerable<PlannedStep> PlanProcess(
        CatalogEntry entry,
        ProcessOpSpec op,
        IReadOnlyList<ProcessSnapshot> live,
        Func<int> nextStepId)
    {
        PlannedStep Step(ProcessSnapshot? process, bool noOp, string? noOpDetail, string? refused) => new()
        {
            StepId = nextStepId(),
            EntryId = entry.Id,
            Scope = entry.Scope,
            Op = op,
            Target = process is null
                ? op.TargetDescription
                : $"{op.TargetDescription} — {process.ImageName} (pid {process.Identity.Pid})",
            RequiresReboot = entry.RequiresReboot,
            ProcessBefore = process,
            ProcessAction = op.Action,
            IntendedPriority = op.ThrottleTo is { } level ? ProcessThrottler.ToPriorityClass(level) : null,
            Activation = entry.Activation,
            NoOp = noOp,
            NoOpDetail = noOpDetail,
            RefusedReason = refused,
        };

        if (_closer is null || _throttler is null)
        {
            yield return Step(null, noOp: false, noOpDetail: null, refused: ProcessLayerUnavailable);
            yield break;
        }

        var matches = live.Where(p => op.Matches(p.ImageName, p.ImagePath)).ToList();

        // Nothing running is a first-class outcome, exactly like a service that is absent on this build.
        // Reported as a no-op WITH a reason, because "already lean" is the wrong sentence for it.
        if (matches.Count == 0)
        {
            yield return Step(
                null,
                noOp: true,
                noOpDetail: $"nothing matching {op.ImageName} is running",
                refused: null);
            yield break;
        }

        foreach (var process in matches)
        {
            // ORDER MATTERS, the same way it does in PlanRegistry and for the same reason. A process
            // already at or below the target needs no write, and the throttler's refusal for that case is
            // "this would be a raise" - true of the arithmetic, false as a description of the situation.
            // A process sitting at Idle is not something Quiesce is declining to touch; it is already
            // quieter than asked. Getting this backwards reported every already-throttled process as a
            // guardrail refusal.
            if (op.Action == Catalog.ProcessAction.Throttle
                && op.ThrottleTo is { } level
                && ProcessThrottler.IsAtOrBelow(process.PriorityClass, ProcessThrottler.ToPriorityClass(level)))
            {
                // Elided for the same reason an already-lean registry value is: nothing is written, so
                // restore can never put back a priority the user chose themselves.
                yield return Step(
                    process,
                    noOp: true,
                    noOpDetail: $"already at {process.PriorityClass}",
                    refused: null);
                continue;
            }

            if (Refuse(process, op, out var reason))
            {
                yield return Step(process, noOp: false, noOpDetail: null, refused: reason);
                continue;
            }

            yield return Step(process, noOp: false, noOpDetail: null, refused: null);
        }
    }

    /// <summary>
    /// Plan-time refusal for one process, asked of the very objects that will act at apply time.
    /// </summary>
    /// <remarks>
    /// Not a copy of the rule — the same method the closer and the throttler consult before they act, so
    /// the reason shown in the preflight list cannot drift from the reason the apply would give. Both
    /// re-check at apply time regardless: the machine is live, and the check that protects it is the
    /// second one.
    /// </remarks>
    private bool Refuse(ProcessSnapshot process, ProcessOpSpec op, out string reason) =>
        op.Action == Catalog.ProcessAction.Throttle && op.ThrottleTo is { } level
            ? _throttler!.WouldRefuse(process, ProcessThrottler.ToPriorityClass(level), out reason)
            : _closer!.WouldRefuse(process, out reason);

    private PlannedStep PlanRegistry(int stepId, CatalogEntry entry, RegistryOpSpec op)
    {
        var target = ResolveTarget(op);
        var prior = registry.Probe(target);
        var intended = new RegistryData { Kind = op.ExpectedKind, Data = op.LeanData };

        var noOp = prior.Presence == RegPresence.ValuePresent
            && prior.Value is not null
            && prior.Value.DataEquals(intended);

        // ORDER MATTERS: already-lean beats refused. A value that already holds the target data
        // needs no write, so no write can be refused, and announcing "Windows blocks this" would be
        // a plain falsehood about a step that was never going to run. This is not hypothetical -
        // TaskbarDa is both vetoed AND already lean on the development machine, so getting the
        // order wrong turns a healthy entry into a scary one.
        string? refused = null;
        if (!noOp && Guardrails.RefuseRegistryWrite(op.Hive.ToString(), op.Subkey, op.Value, out var reason))
        {
            refused = reason;
        }

        return new PlannedStep
        {
            StepId = stepId,
            EntryId = entry.Id,
            Scope = entry.Scope,
            Op = op,
            Target = target.ToString(),
            RequiresReboot = entry.RequiresReboot,
            RegistryTarget = target,
            Prior = prior,
            IntendedNew = intended,
            Activation = entry.Activation,
            NoOp = noOp,
            RefusedReason = refused,
        };
    }

    private PlannedStep PlanService(int stepId, CatalogEntry entry, ServiceOpSpec op)
    {
        PlannedStep Step(ServiceSnapshot? before, ServiceStartType? intended, bool stop, bool noOp, string? refused) => new()
        {
            StepId = stepId,
            EntryId = entry.Id,
            Scope = entry.Scope,
            Op = op,
            Target = $"service {op.Service}",
            RequiresReboot = entry.RequiresReboot,
            ServiceBefore = before,
            IntendedStartType = intended,
            IntendedStop = stop,
            Activation = entry.Activation,
            NoOp = noOp,
            RefusedReason = refused,
        };

        if (services is null)
        {
            return Step(null, null, false, false, "Service control is unavailable in this build.");
        }

        var before = services.Query(op.Service);

        // "Not present on this build" is an outcome, not a failure: service names come and go
        // between Windows releases (Fax is gone on 26200), and a tool that throws on absence is
        // one Windows Update away from being unusable.
        if (!before.Present)
        {
            return Step(before, null, false, noOp: true, refused: null);
        }

        var requested = op.StartMode.ToStartType();

        // Trigger-started services are clamped to Manual. Disabling one does not stop the trigger
        // from firing - activation just fails, and the dependent feature breaks silently weeks
        // later with nothing to connect it to. Data may narrow this, never widen it.
        var clamped = before.TriggerStarted && requested == ServiceStartType.Disabled
            ? ServiceStartType.Manual
            : requested;

        if (Guardrails.RefuseServiceChange(before, services, out var reason))
        {
            return Step(before, clamped, op.StopNow, noOp: false, refused: reason);
        }

        var alreadyLean = before.StartType == clamped
            && (!op.StopNow || before.RunState == ServiceRunState.Stopped);

        return Step(before, clamped, op.StopNow, alreadyLean, refused: null);
    }

    /// <summary>
    /// Plans one power scheme selection against the live scheme list.
    /// </summary>
    /// <remarks>
    /// Four outcomes, and the ordering between them is load-bearing in the same way it is in
    /// <see cref="PlanRegistry"/>:
    /// <list type="number">
    /// <item>The target is not installed — a NO-OP with a reason, exactly like a service absent on this
    /// build. Ultimate Performance really is missing on many machines.</item>
    /// <item>It is already active — a no-op. Checked BEFORE the guardrails, because a scheme the machine
    /// is already on needs no switch, so no switch can be refused, and announcing an RDP hazard about a
    /// step that was never going to run would be a plain falsehood.</item>
    /// <item>A guardrail refuses it.</item>
    /// <item>It will run.</item>
    /// </list>
    /// <para>
    /// One extra refusal has no analogue elsewhere: if the ACTIVE scheme cannot be read, the switch is
    /// refused even though the target is fine. With no prior there is nothing to restore, and an
    /// unrevertable change is the one thing this project will not make.
    /// </para>
    /// </remarks>
    private PlannedStep PlanPower(int stepId, CatalogEntry entry, PowerOpSpec op)
    {
        PlannedStep Step(PowerPrior? prior, string? targetName, bool noOp, string? noOpDetail, string? refused) => new()
        {
            StepId = stepId,
            EntryId = entry.Id,
            Scope = entry.Scope,
            Op = op,
            Target = targetName is null
                ? $"power scheme {op.Scheme:D}"
                : $"power scheme {targetName}",
            RequiresReboot = entry.RequiresReboot,
            PowerPrior = prior,
            IntendedScheme = op.Scheme,
            IntendedSchemeName = targetName,
            Activation = entry.Activation,
            NoOp = noOp,
            NoOpDetail = noOpDetail,
            RefusedReason = refused,
        };

        if (power is null)
        {
            return Step(null, null, false, null, "Power scheme control is unavailable in this build.");
        }

        var live = power.Query();
        var target = live.SchemeOf(op.Scheme);

        // Not present on this machine. A first-class outcome, and the reason Quiesce does not create
        // schemes: the honest answer here is "you do not have this plan", not "let me make you one".
        if (target is null)
        {
            return Step(
                null,
                null,
                noOp: true,
                noOpDetail: $"power scheme {op.Scheme:D} is not installed on this machine",
                refused: null);
        }

        if (live.Active is not { } activeId)
        {
            return Step(
                null,
                target.FriendlyName,
                noOp: false,
                noOpDetail: null,
                refused: "Quiesce could not read which power scheme is active, so it has no prior to " +
                         "restore. It will not switch a setting it cannot put back.");
        }

        var prior = new PowerPrior
        {
            Scheme = activeId,
            FriendlyName = live.ActiveFriendlyName,
            Readable = true,
        };

        // ORDER MATTERS: already-active beats refused, for the reason PlanRegistry documents at length.
        if (activeId == op.Scheme)
        {
            return Step(
                prior,
                target.FriendlyName,
                noOp: true,
                noOpDetail: $"already on {target}",
                refused: null);
        }

        if (Guardrails.RefusePowerSchemeChange(target, live.SchemeOf(activeId), SessionGuard.IsRemoteSession(), out var reason))
        {
            return Step(prior, target.FriendlyName, noOp: false, noOpDetail: null, refused: reason);
        }

        return Step(prior, target.FriendlyName, noOp: false, noOpDetail: null, refused: null);
    }

    // -------------------------------------------------------------- Engage

    public EngageResult Engage(EngagePlan plan, FaultInjector fault)
    {
        var state = _state.Load();
        if (state.IsDirty)
        {
            throw new InvalidOperationException(
                "The machine is already engaged (dirty). Restore first — engaging twice would capture " +
                "the first session's tweaks as if they were your original settings.");
        }

        // Harden before the journal exists: the directory that holds the revert plan must not be
        // standard-user writable, since an elevated Quiesce will later execute what it finds there.
        paths.EnsureDataRootHardened();

        var sessionId = Guid.NewGuid();
        using var journal = JournalWriter.Open(paths.SessionDir(sessionId));

        // Recovery net 4, written alongside the journal: a reg.exe script that undoes this session
        // with no Quiesce binary involved. Grows step-by-step so it is valid even after a crash.
        using var revertScript = RevertScriptWriter.Create(paths.SessionDir(sessionId), sessionId);

        journal.Append(new SessionStartRecord
        {
            SessionId = sessionId,
            BootId = QuiescePaths.CurrentBootId(),
            OsBuild = info.OsBuild,
            AppVersion = info.AppVersion,
            CatalogVersion = plan.CatalogVersion,
            Profile = plan.Profile,
        });

        // Every intended mutation goes to disk before the first one happens. The preflight dialog
        // renders these same records, so user approval and execution cannot diverge.
        var effective = plan.EffectiveSteps.ToList();
        foreach (var step in effective)
        {
            journal.Append(new PlannedRecord
            {
                StepId = step.StepId,
                EntryId = step.EntryId,
                Scope = step.Scope,
                Target = step.Target,
                RequiresReboot = step.RequiresReboot,
                IntendedNew = step.IntendedNew,
                Process = step.ProcessBefore?.ToPrior(),
                IntendedProcessAction = step.ProcessAction,
                IntendedScheme = step.IntendedScheme,
                IntendedSchemeName = step.IntendedSchemeName,
                Activation = step.Activation,
            });
        }

        // Dirty BEFORE the first mutation: if we crash between here and the first write, recovery
        // runs and finds nothing to do — the safe direction to fail in.
        _state.Save(state with { IsDirty = true, ActiveSessionId = sessionId });

        var applied = 0;
        var skipped = plan.Steps.Count(s => s.NoOp);
        var rolledBackEntries = new List<string>();
        var diagnoses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        var rebootEntries = new List<string>();

        foreach (var entryGroup in effective.GroupBy(s => s.EntryId))
        {
            var appliedInEntry = new List<AppliedStep>();
            var entryFailed = false;

            // Compared against the running total after the group rather than counted separately, because
            // "applied" is incremented from five different branches — including the close, which produces
            // no AppliedStep and so is invisible to appliedInEntry.
            var appliedBeforeEntry = applied;

            foreach (var step in entryGroup)
            {
                if (step.Op is ProcessOpSpec processOp)
                {
                    var outcome = ApplyProcess(journal, revertScript, step, processOp, fault);

                    if (outcome.Note is { } note)
                    {
                        notes.Add(note);
                    }

                    if (outcome.Failure is { } processFailure)
                    {
                        RollBackEntry(journal, entryGroup.Key, appliedInEntry, outcome.Applied, processFailure);
                        rolledBackEntries.Add(entryGroup.Key);
                        diagnoses[entryGroup.Key] = processFailure;
                        entryFailed = true;
                        break;
                    }

                    if (outcome.Applied is { } appliedProcess)
                    {
                        appliedInEntry.Add(appliedProcess);
                        applied++;
                        continue;
                    }

                    if (outcome.AppliedWithoutUndo)
                    {
                        // Counted, but never added to the rollback set: a closed application cannot be
                        // reopened, so an entry that fails later must not try.
                        applied++;
                        continue;
                    }

                    // Neither applied nor failed: asked and declined, or refused since the plan was
                    // built. Deliberately NOT counted as an already-lean elision - the machine is not in
                    // the state the plan described, and the note above says which application and why.
                    skipped++;
                    continue;
                }

                if (step.Op is PowerOpSpec powerOp)
                {
                    var outcome = ApplyPower(journal, revertScript, step, powerOp, fault);

                    if (outcome.Skipped)
                    {
                        skipped++;
                        continue;
                    }

                    if (outcome.Failure is { } powerFailure)
                    {
                        RollBackEntry(journal, entryGroup.Key, appliedInEntry, outcome.Applied, powerFailure);
                        rolledBackEntries.Add(entryGroup.Key);
                        diagnoses[entryGroup.Key] = powerFailure;
                        entryFailed = true;
                        break;
                    }

                    appliedInEntry.Add(outcome.Applied!);
                    applied++;
                    continue;
                }

                if (step.Op is ServiceOpSpec serviceOp)
                {
                    var outcome = ApplyService(journal, revertScript, step, serviceOp, fault);

                    if (outcome.Skipped)
                    {
                        skipped++;
                        continue;
                    }

                    if (outcome.Failure is { } serviceFailure)
                    {
                        RollBackEntry(journal, entryGroup.Key, appliedInEntry, outcome.Applied, serviceFailure);
                        rolledBackEntries.Add(entryGroup.Key);
                        diagnoses[entryGroup.Key] = serviceFailure;
                        entryFailed = true;
                        break;
                    }

                    appliedInEntry.Add(outcome.Applied!);
                    applied++;
                    continue;
                }

                var target = step.RegistryTarget!;

                // Re-probe at apply time: the plan-time prior may be stale, and the journalled
                // prior must be the machine's state at the moment of mutation.
                var prior = registry.Probe(target);
                var live = prior.Presence == RegPresence.ValuePresent ? prior.Value : null;

                if (live is not null && live.DataEquals(step.IntendedNew!))
                {
                    skipped++;
                    continue;
                }

                // Capture activation state BEFORE the write, alongside the registry prior: the
                // broadcast that follows will overwrite it, and revert needs the original.
                var activationPrior = step.Activation
                    .Where(k => k != ActivationKind.None)
                    .Select(k => _capture?.Capture(k))
                    .OfType<ActivationState>()
                    .ToList();

                journal.Append(new ApplyingRecord
                {
                    StepId = step.StepId,
                    EntryId = step.EntryId,
                    Scope = step.Scope,
                    Target = step.Target,
                    RequiresReboot = step.RequiresReboot,
                    RegistryTarget = target,
                    Prior = prior,
                    IntendedNew = step.IntendedNew,
                    Activation = step.Activation,
                    ActivationPrior = activationPrior,
                });

                // Script the inverse before performing the mutation, for the same reason the
                // journal is written first: the undo must exist on disk before the change does.
                revertScript.AppendInverse(step.StepId, target, prior);
                foreach (var captured in activationPrior)
                {
                    revertScript.AppendNote(
                        $"step {step.StepId} also needs {captured.Kind} replayed; reg.exe cannot do that. " +
                        "Run 'quiesce revert-all' if you can, or re-apply the setting in Windows.");
                }

                // A refused write is an outcome, not a crash. Windows denies registry writes for
                // reasons entirely outside the app's control - ACLs on policy subtrees (including
                // some under HKCU), Tamper Protection, policy engines - and the correct response is
                // to roll the entry back and report a typed diagnosis. Letting the exception escape
                // would kill the process mid-apply, leaving the machine dirty and the user with a
                // stack trace instead of an explanation.
                string? writeFailure = null;
                try
                {
                    registry.SetValue(target, step.IntendedNew!);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    writeFailure = DescribeWriteFailure(ex);
                }

                // Verify by re-reading the authoritative source. A non-throwing API call is not
                // success: Tamper Protection and policy engines silently swallow writes.
                var check = writeFailure is null ? registry.Probe(target) : null;
                var ok = check is not null
                    && check.Presence == RegPresence.ValuePresent
                    && check.Value is not null
                    && check.Value.DataEquals(step.IntendedNew!);

                journal.Append(new AppliedRecord
                {
                    StepId = step.StepId,
                    Verify = writeFailure ?? (ok ? "ok" : $"mismatch: live={Describe(check!)}"),
                });

                if (!ok)
                {
                    RollBackEntry(journal, entryGroup.Key, appliedInEntry, AppliedStep.ForRegistry(step, target, prior), writeFailure ?? "verify failed");
                    rolledBackEntries.Add(entryGroup.Key);
                    diagnoses[entryGroup.Key] = writeFailure ?? $"verify failed: live={Describe(check!)}";
                    entryFailed = true;
                    break;
                }

                appliedInEntry.Add(AppliedStep.ForRegistry(step, target, prior));
                applied++;

                fault.AfterStepApplied(step.StepId);
            }

            if (!entryFailed)
            {
                foreach (var kind in entryGroup.First().Activation.Where(k => k != ActivationKind.None).Distinct())
                {
                    broadcaster.Broadcast(kind);
                }

                // Only an entry that actually changed something owes a restart. An entry every step of
                // which was already lean changed nothing, so warning about it would send the user to
                // reboot for a machine that is already in the state they asked for.
                if (entryGroup.First().RequiresReboot && applied > appliedBeforeEntry)
                {
                    rebootEntries.Add(entryGroup.Key);
                }
            }
        }

        revertScript.Finish();
        journal.Append(new CommittedRecord { AppliedSteps = applied, SkippedNoop = skipped });

        // The machine is now engaged: committed AND dirty is the steady state. isDirty clears only
        // when a revert completes cleanly.
        if (applied == 0 && rolledBackEntries.Count == 0)
        {
            // `state with` rather than a fresh QuiesceState: a reboot already owed from an earlier
            // session is still owed, and constructing a blank state here would silently retract it.
            _state.Save(state with { IsDirty = false, ActiveSessionId = null });
        }
        else if (rebootEntries.Count > 0)
        {
            // Rebuilt from `state` rather than re-read, so this is exactly what was written before the
            // first mutation plus the marker. Nothing else writes the state file in between.
            _state.Save((state with { IsDirty = true, ActiveSessionId = sessionId })
                .WithRebootPending(rebootEntries));
        }

        return new EngageResult
        {
            SessionId = sessionId,
            Applied = applied,
            SkippedNoop = skipped,
            RolledBackEntries = rolledBackEntries,
            Diagnoses = diagnoses,
            Notes = notes,
            RebootPendingEntries = rebootEntries,
        };
    }

    /// <summary>
    /// Applies one process step: journal the prior, then close or throttle, then verify.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A CLOSE THAT DOES NOT HAPPEN IS NOT A FAILURE. If an application declines — almost always because
    /// it is showing a "save your work?" prompt — or if a guardrail refuses it now that did not refuse it
    /// at plan time, then nothing was done and the process is exactly as it was found. That is a
    /// consistent machine state, not a half-applied entry, so it must not trigger a rollback: there is no
    /// rollback for a close in any case, and treating "left running" as a failure would roll back the
    /// applications that <em>did</em> close, which is impossible, or fail the entry over an outcome the
    /// graceful ladder is specifically designed to accept.
    /// </para>
    /// <para>
    /// A throttle is different and does fail its entry, because a throttle genuinely can be put back.
    /// That asymmetry is why the catalog loader refuses an entry mixing the two actions.
    /// </para>
    /// </remarks>
    private ProcessApplyOutcome ApplyProcess(
        JournalWriter journal,
        RevertScriptWriter revertScript,
        PlannedStep step,
        ProcessOpSpec op,
        FaultInjector fault)
    {
        if (_closer is null || _throttler is null || step.ProcessBefore is not { } planned)
        {
            return new ProcessApplyOutcome { Failure = ProcessLayerUnavailable };
        }

        // Re-read by identity. A PID that now belongs to a different process reports Present = false, so
        // the recycling case lands here as "gone" rather than as an action against a stranger.
        var live = processes!.Query(planned.Identity);
        if (!live.Present)
        {
            return new ProcessApplyOutcome
            {
                Skipped = true,
                Note = $"{planned.ImageName} (pid {planned.Identity.Pid}) exited before Quiesce asked.",
            };
        }

        // Whether the write-ahead record actually reached disk. A refusal that happens before any write
        // journals nothing, and an `applied` line for a step with no `applying` line would be a journal
        // that describes a mutation that never existed.
        ProcessPriorityClass? journalledPrior = null;
        var journalled = false;

        void JournalPrior(ProcessPrior prior, string? intendedPriority)
        {
            journalled = true;

            journal.Append(new ApplyingRecord
            {
                StepId = step.StepId,
                EntryId = step.EntryId,
                Scope = step.Scope,
                Target = step.Target,
                RequiresReboot = step.RequiresReboot,
                Process = prior,
                IntendedProcessAction = op.Action,
                IntendedPriority = intendedPriority,
                Activation = step.Activation,
            });

            revertScript.AppendProcessNote(step.StepId, prior, op.Action, intendedPriority);
        }

        if (op.Action == Catalog.ProcessAction.Throttle)
        {
            var target = ProcessThrottler.ToPriorityClass(op.ThrottleTo!.Value);

            // The prior goes to disk from inside the throttler, between its read and its write, so the
            // journalled value is the one the write actually raced against rather than a second reading
            // of the same fact.
            var outcome = _throttler.Throttle(
                live.Identity,
                target,
                beforeWrite: prior =>
                {
                    journalledPrior = prior;
                    JournalPrior(live.ToPrior() with { PriorityClass = prior.ToString() }, target.ToString());
                });

            if (!outcome.Succeeded)
            {
                // A throttle that did not stick fails its entry: reporting it as applied would leave
                // Restore obliged to write back a priority the process never actually had.
                if (journalled)
                {
                    journal.Append(new AppliedRecord { StepId = step.StepId, Verify = $"ThrottleFailed: {outcome.Detail}" });
                }

                return new ProcessApplyOutcome
                {
                    Failure = $"ThrottleFailed: {outcome.Detail}",

                    // Handed to the rollback ONLY when a write was actually attempted. SetPriorityClass can
                    // report success while the re-read disagrees, which means the class may have moved even
                    // though the step failed - so the failing step has to be unwound with the rest.
                    Applied = journalledPrior is { } prior
                        ? AppliedStep.ForThrottle(step, live, prior)
                        : null,
                };
            }

            if (outcome.NoOp)
            {
                return new ProcessApplyOutcome
                {
                    Skipped = true,
                    Note = $"{live.ImageName} (pid {live.Identity.Pid}) was already at {target}.",
                };
            }

            journal.Append(new AppliedRecord { StepId = step.StepId, Verify = "ok" });
            fault.AfterStepApplied(step.StepId);

            return new ProcessApplyOutcome
            {
                Applied = AppliedStep.ForThrottle(step, live, outcome.Prior!.Value),
            };
        }

        // Write-ahead before the close for the same reason as everywhere else: the request may succeed and
        // the machine may then lose power, and the record of what was closed is the only thing Restore can
        // report from.
        JournalPrior(live.ToPrior(), intendedPriority: null);

        var closed = _closer.Close(live.Identity);
        journal.Append(new AppliedRecord
        {
            StepId = step.StepId,
            Verify = closed.Succeeded ? "ok" : $"{closed.Result}: {closed.Detail}",
        });

        if (!closed.Succeeded)
        {
            return new ProcessApplyOutcome
            {
                Skipped = true,
                Note = closed.Result switch
                {
                    ProcessCloseResult.DeclinedToClose =>
                        $"{live.ImageName} (pid {live.Identity.Pid}) was asked to close and is still running. " +
                        "It is most likely prompting about unsaved work; Quiesce leaves that alone.",
                    ProcessCloseResult.NoWindow =>
                        $"{live.ImageName} (pid {live.Identity.Pid}) has no window to send a close request to, " +
                        "and Quiesce has no less polite option.",
                    ProcessCloseResult.Refused =>
                        $"{live.ImageName} (pid {live.Identity.Pid}) was not closed: {closed.Detail}",
                    _ => $"{live.ImageName} (pid {live.Identity.Pid}): {closed.Result} — {closed.Detail}",
                },
            };
        }

        fault.AfterStepApplied(step.StepId);

        return new ProcessApplyOutcome
        {
            // No AppliedStep, because a close cannot be rolled back and must never enter the rollback
            // set - but real work all the same, so it is counted and stated plainly.
            AppliedWithoutUndo = true,
            Note = $"closed {live.ImageName} (pid {live.Identity.Pid}). Restore will not reopen it.",
        };
    }

    /// <summary>
    /// Applies one power scheme step: journal the prior scheme, select the new one, verify by re-reading.
    /// </summary>
    /// <remarks>
    /// The simplest apply in the engine, and the only one whose undo is guaranteed exact — one GUID in,
    /// one GUID out. The discipline is still the same as everywhere else: re-read at apply time rather
    /// than trusting the plan-time capture, write the prior to disk before touching anything, and verify
    /// by asking the system what the active scheme is now rather than by believing the call's return code.
    /// <para>
    /// That last point is not ceremony here. <c>PowerSetActiveScheme</c> returns a Win32 error code where
    /// every sibling API in this project returns a BOOL, so "it did not throw" and "it worked" are
    /// especially far apart — and a scheme switch is exactly the kind of change a user cannot see happen.
    /// </para>
    /// </remarks>
    private PowerApplyOutcome ApplyPower(
        JournalWriter journal,
        RevertScriptWriter revertScript,
        PlannedStep step,
        PowerOpSpec op,
        FaultInjector fault)
    {
        if (power is null)
        {
            return new PowerApplyOutcome { Failure = "Power scheme control is unavailable in this build." };
        }

        // Re-read: the user may have changed schemes between the preflight dialog and the apply, and the
        // prior that goes in the journal has to be the one the switch actually replaced.
        var live = power.Query();

        var target = live.SchemeOf(op.Scheme);
        if (target is null)
        {
            return new PowerApplyOutcome { Skipped = true };
        }

        if (live.Active is not { } activeId)
        {
            return new PowerApplyOutcome
            {
                Failure = "Could not read the active power scheme, so there is no prior to restore. " +
                          "Quiesce will not switch a setting it cannot put back.",
            };
        }

        if (activeId == op.Scheme)
        {
            return new PowerApplyOutcome { Skipped = true };
        }

        // Re-checked immediately before the mutation, not merely at plan time. A user can connect over
        // RDP while the preflight dialog is open, which would turn a locally-harmless scheme into one
        // that can sleep the machine out from under a live remote session.
        if (Guardrails.RefusePowerSchemeChange(target, live.SchemeOf(activeId), SessionGuard.IsRemoteSession(), out var refusal))
        {
            return new PowerApplyOutcome { Failure = refusal };
        }

        var prior = new PowerPrior
        {
            Scheme = activeId,
            FriendlyName = live.ActiveFriendlyName,
            Readable = true,
        };

        journal.Append(new ApplyingRecord
        {
            StepId = step.StepId,
            EntryId = step.EntryId,
            Scope = step.Scope,
            Target = step.Target,
            RequiresReboot = step.RequiresReboot,
            PowerPrior = prior,
            IntendedScheme = op.Scheme,
            IntendedSchemeName = target.FriendlyName,
            Activation = step.Activation,
        });

        revertScript.AppendPowerInverse(step.StepId, prior);

        try
        {
            power.SetActiveScheme(op.Scheme);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            journal.Append(new AppliedRecord { StepId = step.StepId, Verify = $"PowerFailed: {ex.Message}" });
            return new PowerApplyOutcome { Failure = $"PowerFailed: {ex.Message}" };
        }

        var after = power.Query();
        var ok = after.Active == op.Scheme;

        journal.Append(new AppliedRecord
        {
            StepId = step.StepId,
            Verify = ok ? "ok" : $"mismatch: active={DescribeActive(after)}",
        });

        if (!ok)
        {
            return new PowerApplyOutcome
            {
                Failure = $"Verification failed: the active scheme is {DescribeActive(after)} " +
                          $"rather than {op.Scheme:D}.",

                // Handed to the rollback even though the switch did not verify: the call may have
                // partially taken effect, so the failing step has to be unwound with the rest.
                Applied = AppliedStep.ForPower(step, prior),
            };
        }

        fault.AfterStepApplied(step.StepId);

        return new PowerApplyOutcome { Applied = AppliedStep.ForPower(step, prior) };
    }

    /// <summary>
    /// Reverts one journalled power step: put the previously active scheme back.
    /// </summary>
    /// <remarks>
    /// Same conflict rule as every other kind. If the active scheme is no longer the one Quiesce
    /// selected, the user (or something else) has changed it since, and overwriting that with the stale
    /// capture would throw away a choice Quiesce did not make.
    /// <para>
    /// Note what is deliberately NOT here: no reboot special-case. A service that was running before a
    /// reboot must not be force-started afterwards, because the SCM has already decided what runs — but
    /// the active power scheme is a single machine-wide setting that survives the reboot unchanged, so
    /// restoring it across one is exactly as correct as restoring it within one.
    /// </para>
    /// <para>
    /// Also note there is no Power saver exception. The guardrail forbids <em>selecting</em> Power saver;
    /// putting it back is the correct undo for a user who had it, and a guardrail applied here would
    /// strand them on whatever Quiesce switched them to.
    /// </para>
    /// </remarks>
    private void RevertPowerStep(
        JournalWriter journal,
        ApplyingRecord step,
        PowerPrior prior,
        List<string> messages,
        ref int reverted,
        ref int failed)
    {
        if (power is null)
        {
            messages.Add($"step {step.StepId} (power scheme): power control unavailable; cannot revert.");
            failed++;
            return;
        }

        if (!prior.Readable)
        {
            // Only reachable from a journal written by a build that recorded an unreadable prior. Counted
            // as failed rather than skipped: something was changed and this record cannot say back to what.
            messages.Add(
                $"step {step.StepId} (power scheme): the journal records that the previous scheme could " +
                "not be read, so Quiesce cannot say what to restore. Choose your power plan in Windows.");
            failed++;
            return;
        }

        try
        {
            var live = power.Query();

            if (live.Active is { } activeId
                && step.IntendedScheme is { } intended
                && activeId != intended
                && activeId != prior.Scheme)
            {
                messages.Add(
                    $"step {step.StepId} (power scheme): the active plan changed since apply " +
                    $"(now {DescribeActive(live)}); kept it rather than overwriting your choice.");
                journal.Append(new RevertedRecord { StepId = step.StepId, Outcome = "conflict-kept-current" });
                reverted++;
                return;
            }

            if (live.Active == prior.Scheme)
            {
                journal.Append(new RevertedRecord { StepId = step.StepId, Outcome = "restored-nothing-to-do" });
                reverted++;
                return;
            }

            // The scheme genuinely no longer exists - deleted by the user or by a driver package since
            // apply. Nothing to select, and inventing a substitute would be worse than saying so.
            if (!live.Contains(prior.Scheme))
            {
                messages.Add(
                    $"step {step.StepId} (power scheme): the plan you were on ({prior}) no longer exists " +
                    "on this machine, so Quiesce cannot select it again. Pick a power plan in Windows.");
                failed++;
                return;
            }

            power.SetActiveScheme(prior.Scheme);

            var after = power.Query();
            if (after.Active != prior.Scheme)
            {
                messages.Add(
                    $"step {step.StepId} (power scheme): asked Windows to go back to {prior}, but the " +
                    $"active plan is still {DescribeActive(after)}.");
                failed++;
                return;
            }

            journal.Append(new RevertedRecord { StepId = step.StepId, Outcome = "restored" });
            reverted++;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            messages.Add($"step {step.StepId} (power scheme): revert failed: {ex.Message}");
            failed++;
        }
    }

    /// <summary>
    /// Applies one service step: journal the three-fact prior, reconfigure, optionally stop, verify.
    /// </summary>
    /// <remarks>
    /// Ordering mirrors the registry path — prior state durable on disk before any mutation — but
    /// the stop comes after the start-type change so that a service which refuses to stop is still
    /// left reconfigured rather than in a state neither planned nor recorded.
    /// </remarks>
    private ServiceApplyOutcome ApplyService(
        JournalWriter journal,
        RevertScriptWriter revertScript,
        PlannedStep step,
        ServiceOpSpec op,
        FaultInjector fault)
    {
        if (services is null)
        {
            return new ServiceApplyOutcome { Failure = "Service control is unavailable in this build." };
        }

        // Re-query at apply time. Guardrails were evaluated at plan time and the SCM can change in
        // between - a service can gain a dependent, start running, or move host process. The check
        // that matters is the one taken immediately before the mutation.
        var before = services.Query(op.Service);

        if (!before.Present)
        {
            return new ServiceApplyOutcome { Skipped = true };
        }

        if (Guardrails.RefuseServiceChange(before, services, out var refusal))
        {
            return new ServiceApplyOutcome { Failure = refusal };
        }

        var intended = before.TriggerStarted && op.StartMode == ServiceStartMode.Disabled
            ? ServiceStartType.Manual
            : op.StartMode.ToStartType();

        var needsConfig = before.StartType != intended;
        var needsStop = op.StopNow && before.RunState == ServiceRunState.Running;

        if (!needsConfig && !needsStop)
        {
            return new ServiceApplyOutcome { Skipped = true };
        }

        journal.Append(new ApplyingRecord
        {
            StepId = step.StepId,
            EntryId = step.EntryId,
            Scope = step.Scope,
            Target = step.Target,
            RequiresReboot = step.RequiresReboot,
            Service = op.Service,
            ServicePrior = before.ToPrior(),
            IntendedStartType = intended,
            IntendedStop = needsStop,
            Activation = step.Activation,
        });

        revertScript.AppendServiceInverse(step.StepId, before);

        // Snapshot the hosted process set so processes killed as collateral can be reported. Their
        // loss is real and Quiesce cannot undo it, so the honest move is to record it rather than
        // let "restored exactly" quietly cover a process that never came back.
        var coTenants = before.HostProcessId != 0
            ? services.ServicesInHostProcess(before.HostProcessId).Where(s => !s.Equals(op.Service, StringComparison.OrdinalIgnoreCase)).ToList()
            : [];

        try
        {
            // STOP FIRST, THEN RECONFIGURE. This ordering is load-bearing.
            //
            // Disabling a service does not stop it. If the start type were written first and the
            // stop then timed out, the machine would be left Disabled-and-Running: everything looks
            // correct for the whole session because the service is still up, and then it silently
            // never returns at the next boot, days later, with nothing connecting it to Quiesce.
            // Stopping first means a refused stop leaves the service exactly as it was found.
            if (needsStop && !services.TryStop(op.Service, TimeSpan.FromSeconds(30), out var stopDiagnosis))
            {
                // A service that will not stop is left running and reported. Escalating - killing
                // the host - is how a tool bugchecks a machine, so there is no escalation path.
                journal.Append(new AppliedRecord
                {
                    StepId = step.StepId,
                    Verify = $"StopRefused: {stopDiagnosis}",
                });

                return new ServiceApplyOutcome { Failure = $"StopRefused: {stopDiagnosis}" };
            }

            if (needsConfig)
            {
                // Preserve the delayed-auto flag rather than clearing it: the SCM stores it
                // independently of the start type, so writing false here would orphan a flag that
                // restore is then obliged to put back.
                services.SetStartType(op.Service, intended, before.DelayedAutostart);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            journal.Append(new AppliedRecord { StepId = step.StepId, Verify = $"ServiceFailed: {ex.Message}" });
            return new ServiceApplyOutcome { Failure = $"ServiceFailed: {ex.Message}" };
        }

        // Verify by re-reading the SCM, never by trusting the call's return value.
        var after = services.Query(op.Service);
        var configOk = !needsConfig || after.StartType == intended;
        var stopOk = !needsStop || after.RunState == ServiceRunState.Stopped;

        journal.Append(new AppliedRecord
        {
            StepId = step.StepId,
            Verify = configOk && stopOk
                ? "ok"
                : $"mismatch: startType={after.StartType} runState={after.RunState}",
        });

        if (!configOk || !stopOk)
        {
            return new ServiceApplyOutcome
            {
                Failure = $"Verification failed: the SCM reports startType={after.StartType}, runState={after.RunState}.",
            };
        }

        if (needsStop && coTenants.Count > 0)
        {
            journal.Append(new SideEffectRecord
            {
                StepId = step.StepId,
                Kind = "coHostedServices",
                Detail =
                    $"{op.Service} shared host process {before.HostProcessId} with: {string.Join(", ", coTenants)}. " +
                    "Stopping it may have affected them.",
                Recoverable = true,
            });
        }

        fault.AfterStepApplied(step.StepId);

        return new ServiceApplyOutcome { Applied = AppliedStep.ForService(step, op.Service, before) };
    }

    /// <summary>
    /// Reverts one journalled service step: restore start type, delayed-auto and run state.
    /// </summary>
    /// <remarks>
    /// Conflict handling mirrors the registry path. If the service's start type is no longer what
    /// Quiesce set it to, something else — Windows Update, a driver install, the user — changed it
    /// since, and overwriting that with a stale captured value would destroy configuration Quiesce
    /// did not create. Keep current and say so.
    /// </remarks>
    private void RevertServiceStep(
        JournalWriter journal,
        ApplyingRecord step,
        string serviceName,
        bool rebootedSinceApply,
        List<string> messages,
        ref int reverted,
        ref int failed)
    {
        if (services is null)
        {
            messages.Add($"step {step.StepId} ({serviceName}): service control unavailable; cannot revert.");
            failed++;
            return;
        }

        if (step.ServicePrior is not { } prior || !prior.Present || prior.StartType is not { } priorStartType)
        {
            journal.Append(new RevertedRecord { StepId = step.StepId, Outcome = "skipped-absent" });
            reverted++;
            return;
        }

        try
        {
            var live = services.Query(serviceName);

            if (!live.Present)
            {
                messages.Add($"step {step.StepId} ({serviceName}): service no longer exists; nothing to restore.");
                journal.Append(new RevertedRecord { StepId = step.StepId, Outcome = "skipped-absent" });
                reverted++;
                return;
            }

            if (step.IntendedStartType is { } intended && live.StartType != intended && live.StartType != priorStartType)
            {
                messages.Add(
                    $"step {step.StepId} ({serviceName}): start type changed since apply " +
                    $"(now {live.StartType}); kept current rather than overwriting it.");
                journal.Append(new RevertedRecord { StepId = step.StepId, Outcome = "conflict-kept-current" });
                reverted++;
                return;
            }

            services.SetStartType(serviceName, priorStartType, prior.DelayedAutostart ?? false);

            // Across a reboot, "was running" is not a state to restore — the SCM already started
            // (or deliberately has not yet started) everything according to its start type at boot.
            // Forcing a start here would run services the machine had legitimately left stopped,
            // and a delayed-auto service that simply has not reached its delay yet is the common
            // case. Restore the configuration and let Windows decide what runs.
            if (rebootedSinceApply)
            {
                journal.Append(new RevertedRecord { StepId = step.StepId, Outcome = "restored-config-only" });
                reverted++;
                return;
            }

            // Only restart what was actually running. A Manual service that was stopped must stay
            // stopped, and starting it "to be safe" would leave the machine in a state it was
            // never in.
            if (prior.RunState == ServiceRunState.Running && live.RunState != ServiceRunState.Running)
            {
                if (!services.TryStart(serviceName, TimeSpan.FromSeconds(30), out var startDiagnosis))
                {
                    messages.Add(
                        $"step {step.StepId} ({serviceName}): start type restored, but the service did not " +
                        $"restart ({startDiagnosis}). It may start on demand, or a reboot will restore it.");
                }
            }

            journal.Append(new RevertedRecord { StepId = step.StepId, Outcome = "restored" });
            reverted++;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            messages.Add($"step {step.StepId} ({serviceName}): revert failed: {ex.Message}");
            failed++;
        }
    }

    /// <summary>
    /// Reverts one journalled process step: put a priority back, or report a close that has no undo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only step kind whose revert can be honest and still not restore anything. A close is reported
    /// and counted as reverted, because it is discharged as far as it can ever be — refusing to close the
    /// session over it would leave the machine permanently dirty over an application the user can simply
    /// reopen, which is the wedge this project has already fixed twice. The report is what makes that
    /// acceptable: silent residue is how a tool ends up having changed a machine it called clean.
    /// </para>
    /// <para>
    /// A throttle round-trips exactly, with the same conflict rule as the registry and service paths: if
    /// the class is no longer what Quiesce set, something else changed it since and the current value is
    /// kept rather than overwritten with a stale capture.
    /// </para>
    /// </remarks>
    private void RevertProcessStep(
        JournalWriter journal,
        ApplyingRecord step,
        ProcessPrior prior,
        List<string> messages,
        ref int reverted,
        ref int failed)
    {
        var identity = prior.ToIdentity();
        var name = $"{prior.ImageName} (pid {prior.Pid})";

        if (step.IntendedProcessAction == Catalog.ProcessAction.Close)
        {
            // Present means it never actually closed - it declined, or the close was refused after the
            // record was written. Nothing was done, so there is nothing to undo.
            var stillRunning = processes is not null && processes.Query(identity).Present;

            if (!stillRunning)
            {
                messages.Add(
                    $"step {step.StepId} ({name}): was closed. Quiesce does not relaunch applications - " +
                    "reopen it yourself. Nothing else about it was changed.");
            }

            journal.Append(new RevertedRecord
            {
                StepId = step.StepId,
                Outcome = stillRunning ? "not-closed-nothing-to-do" : "closed-not-relaunched",
            });

            reverted++;
            return;
        }

        if (_throttler is null || processes is null)
        {
            messages.Add($"step {step.StepId} ({name}): process control unavailable; cannot restore its priority.");
            failed++;
            return;
        }

        // A class name the journal carries but this build cannot parse. Refuse rather than guess: the
        // alternative is writing some other priority onto a live process and calling it a restore.
        if (prior.PriorityClass is not { } priorName
            || !Enum.TryParse<ProcessPriorityClass>(priorName, ignoreCase: true, out var priorClass))
        {
            messages.Add(
                $"step {step.StepId} ({name}): the journal records prior priority " +
                $"'{prior.PriorityClass ?? "<none>"}', which this build cannot interpret. Left as it is; " +
                "restarting the application clears any throttle.");
            failed++;
            return;
        }

        var live = processes.Query(identity);

        if (live.Present
            && step.IntendedPriority is { } intendedName
            && Enum.TryParse<ProcessPriorityClass>(intendedName, ignoreCase: true, out var intended)
            && live.PriorityClass != intended
            && live.PriorityClass != priorClass)
        {
            messages.Add(
                $"step {step.StepId} ({name}): priority changed since apply (now {live.PriorityClass}); " +
                "kept current rather than overwriting it.");
            journal.Append(new RevertedRecord { StepId = step.StepId, Outcome = "conflict-kept-current" });
            reverted++;
            return;
        }

        var outcome = _throttler.Restore(identity, priorClass);

        if (!outcome.Succeeded)
        {
            messages.Add($"step {step.StepId} ({name}): {outcome.Detail}");
            failed++;
            return;
        }

        journal.Append(new RevertedRecord
        {
            StepId = step.StepId,

            // An exited process is a clean outcome, not a partial one: a priority class does not outlive
            // the process, so there is genuinely nothing left behind.
            Outcome = outcome.NoOp ? "restored-nothing-to-do" : "restored",
        });

        reverted++;
    }

    /// <summary>Renders a refused registry write as a diagnosis that can actually be acted on.</summary>
    /// <remarks>
    /// The first version returned a fixed sentence that discarded the exception and offered "the
    /// tweak may need elevation" as a guess. On an elevated run that guess is simply false, and
    /// with the real message and error code thrown away there was no way to tell a genuine ACL
    /// denial from a policy engine or a kernel registry filter rejecting the write. It cost real
    /// time on the first elevated run: one entry failed while seven sibling HKLM policy writes
    /// succeeded, and the message pointed at the one explanation already ruled out. A diagnosis
    /// that cannot be falsified is not a diagnosis.
    /// </remarks>
    private static string DescribeWriteFailure(Exception ex)
    {
        if (ex is not UnauthorizedAccessException)
        {
            return $"WriteFailed: {ex.Message} (0x{ex.HResult:X8})";
        }

        var cause = Platform.Elevation.IsElevated()
            ? "The process is already elevated, so this is the key's own ACL, a policy engine, or a " +
              "kernel registry filter - not a missing privilege."
            : "The process is not elevated, and this key requires administrator rights.";

        return $"AccessDenied: Windows refused the write. {cause} [{ex.Message}] (0x{ex.HResult:X8})";
    }

    /// <summary>Undoes one applied step, dispatching on which kind of prior it captured.</summary>
    /// <returns>Null on a clean undo, or a description of residue left behind.</returns>
    private string? UndoApplied(AppliedStep applied)
    {
        if (applied.RegistryTarget is { } target && applied.RegistryPrior is { } prior)
        {
            return RestorePrior(target, prior);
        }

        if (applied.Service is { } service && applied.ServicePrior is { } servicePrior && services is not null)
        {
            if (RestoreService(service, servicePrior) is { } serviceResidue)
            {
                return serviceResidue;
            }
        }

        if (applied.PowerPrior is { } powerPrior && power is not null)
        {
            // Reported rather than discarded, for the reason RestoreService's comment sets out: this is
            // the mid-apply unwind path, and a power plan left on the lean scheme while the entry is
            // reported rolled back is residue the user cannot see and would never think to check.
            try
            {
                power.SetActiveScheme(powerPrior.Scheme);

                if (power.Query().Active != powerPrior.Scheme)
                {
                    return $"the power plan is still not back to {powerPrior}";
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                return $"could not put the power plan back to {powerPrior} ({ex.Message})";
            }
        }

        // A throttle is the only process work that has an inverse. A close never reaches here at all -
        // ApplyProcess deliberately returns no AppliedStep for one - so there is no branch to write.
        if (applied.Process is { } identity && applied.ProcessPriorPriority is { } priorPriority)
        {
            var outcome = _throttler?.Restore(identity, priorPriority);

            if (outcome is { Succeeded: false })
            {
                return $"could not put {applied.ProcessImageName} back to {priorPriority} ({outcome.Detail})";
            }
        }

        return null;
    }

    /// <summary>
    /// Restores a service's three captured facts: start type, delayed-auto flag, and run state.
    /// </summary>
    /// <remarks>
    /// Start type is restored before the service is started, so a service that was Automatic comes
    /// back Automatic rather than being started while still marked Disabled. Only services that
    /// were actually running are started — a stopped Manual service must stay stopped.
    /// </remarks>
    /// <returns>Null on a clean restore, or a description of what was left behind.</returns>
    private string? RestoreService(string service, ServiceSnapshot prior)
    {
        if (services is null || !prior.Present || prior.StartType is not { } startType)
        {
            return null;
        }

        services.SetStartType(service, startType, prior.DelayedAutostart);

        // The start result is REPORTED, not discarded. It used to be `out _`, which made this the one
        // undo path in the engine that could leave a service configured correctly and stopped while
        // saying nothing — the same shape as the residue that RollBackEntry was already fixed to
        // surface, and as the failed-throttle report immediately above in UndoApplied. The
        // journal-driven revert (RevertServiceStep) has always reported it; only this path, the one
        // that runs mid-apply when an entry is being unwound, stayed quiet.
        if (prior.RunState == ServiceRunState.Running
            && !services.TryStart(service, TimeSpan.FromSeconds(30), out var diagnosis))
        {
            return $"{service} was put back to {startType} but did not restart ({diagnosis}); " +
                   "it may start on demand, and a reboot will start it";
        }

        return null;
    }

    /// <summary>Entry-level atomicity: unwind every applied step of the failing entry, newest first.</summary>
    private void RollBackEntry(
        JournalWriter journal,
        string entryId,
        List<AppliedStep> appliedInEntry,
        AppliedStep? failedStep,
        string reason)
    {
        var toUndo = (failedStep is null ? appliedInEntry : appliedInEntry.Append(failedStep))
            .Reverse()
            .ToList();
        var undone = new List<int>();
        var residues = new List<string>();

        foreach (var undo in toUndo)
        {
            // Residue was previously discarded here, while the revert path reported it. Same fact, same
            // obligation to say it out loud: an unwind that left something behind and said nothing is how
            // a tool ends up having changed a machine it reported clean.
            if (UndoApplied(undo) is { } residue)
            {
                residues.Add($"step {undo.StepId}: {residue}");
            }

            undone.Add(undo.StepId);
        }

        journal.Append(new EntryRolledBackRecord
        {
            EntryId = entryId,
            Reason = residues.Count == 0 ? reason : $"{reason} [{string.Join("; ", residues)}]",
            RolledBackSteps = undone,
        });
    }

    // -------------------------------------------------------------- Revert

    /// <summary>
    /// Reverts a session from its journal. Never touches the catalog — the records are the truth.
    /// Idempotent: a crash mid-revert is survivable by running it again.
    /// </summary>
    /// <param name="onlyScope">
    /// When set, reverts ONLY the steps of that scope and leaves the rest applied.
    /// </param>
    /// <remarks>
    /// <para>
    /// <paramref name="onlyScope"/> exists for exactly one caller — <see cref="Recover"/> after a reboot —
    /// and it fixes a bug that made the Startup feature's central promise false. Recovery is supposed to
    /// auto-revert <see cref="TweakScope.Session"/> steps once the boot has passed and leave
    /// <see cref="TweakScope.Persistent"/> standing preferences alone; its own comment says so. But it
    /// implemented that by handing the WHOLE session to this method, which had no scope filter, so the
    /// distinction existed only when a session happened to be entirely one scope or entirely the other.
    /// </para>
    /// <para>
    /// The mixed session is the NORMAL case, not an edge case: <c>apps.close-browsers</c> is in
    /// <c>ProfileStore.BuiltInDefault</c> and a close journals <c>Scope = Session</c>, so any default
    /// profile plus one sign-in preference is mixed. Engage, reboot, and the standing preference the user
    /// set was silently put back — while <c>StartupPage</c> told them, in as many words, "This one stays
    /// in force across reboots."
    /// </para>
    /// <para>
    /// A FILTERED REVERT NEVER CLEARS <c>IsDirty</c>, and that is the part a naive fix gets wrong.
    /// <see cref="RevertResult.Clean"/> is <c>Deferred == 0 &amp;&amp; Failed == 0</c> — it says nothing
    /// about completeness — so a filtered run that reverted its half perfectly reports Clean while the
    /// other half is still on the machine. Clearing the flag on that would strand the persistent steps
    /// with no session marked dirty, which is the one state none of the four recovery nets look for.
    /// </para>
    /// <para>
    /// It also appends no <c>revertComplete</c> record, for the same reason and one more:
    /// <c>baseline-diff.ps1</c> detects an outstanding session as <c>applied &gt; 0 &amp;&amp;
    /// revertComplete == 0</c>, so writing one would make a half-reverted session look finished to the
    /// script whose whole job is to refuse to run over one.
    /// </para>
    /// </remarks>
    public RevertResult RevertSession(Guid sessionId, string initiator, TweakScope? onlyScope = null)
    {
        var sessionDir = paths.SessionDir(sessionId);
        var journalPath = Path.Combine(sessionDir, "journal.jsonl");

        // Same trap, same fix: File.Exists cannot tell "no such journal" from "not permitted to look", and
        // announcing "No journal for session X" about a journal that is sitting right there - holding the
        // only record of how to undo the machine - sends the user looking for the wrong problem.
        JournalReadResult read;
        try
        {
            read = JournalReader.Read(journalPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileNotFoundException($"No journal for session {sessionId:D}.", journalPath, ex);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new StateUnreadableException(journalPath, ex);
        }

        var messages = new List<string>();

        if (read.TornFinalLine)
        {
            messages.Add("Journal has a torn final line (crash mid-append). Proceeding with the intact records.");
        }

        var allPending = PendingSteps(read.Records);

        // Filtered here rather than inside PendingSteps: that method is the definition of "applied and not
        // yet reverted" and is read by the drift detector and by RevertAll's outstanding-session check,
        // both of which must keep seeing everything.
        var pending = onlyScope is { } scope
            ? allPending.Where(s => s.Scope == scope).ToList()
            : allPending;

        var leftApplied = allPending.Count - pending.Count;

        // Whether this revert is running in a different boot from the apply. It changes what
        // "restore the running state" can honestly mean for services.
        var rebootedSinceApply = read.Records.OfType<SessionStartRecord>().FirstOrDefault() is { } start
            && !QuiescePaths.IsSameBoot(start.BootId);

        using var journal = JournalWriter.Open(sessionDir);
        journal.Append(new RevertStartRecord { Initiator = initiator });

        var reverted = 0;
        var deferred = 0;
        var failed = 0;
        var broadcasts = new HashSet<ActivationKind>();
        var stateReplays = new List<ActivationState>();

        // Undoing a reboot-requiring change needs a reboot too, and only the steps that actually wrote
        // something count — a conflict-kept-current step changed nothing, so it owes nothing.
        var rebootEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Dependency order, not naive reverse: services and power first, then registry, then processes,
        // then activation broadcasts. Unwinding strictly newest-first would restore registry values that
        // a service reads at startup *after* restarting that service, so it would come back having
        // read the tweaked value. Within a kind, newest-first still holds.
        //
        // Processes last only because nothing depends on them in either direction — a priority class is
        // not read by anything else — and it puts the one kind of step that cannot fully round-trip at the
        // end of the report, where the caveats belong.
        var ordered = pending
            .OrderBy(s => s.Service is not null || s.PowerPrior is not null ? 0 : s.Process is not null ? 2 : 1)
            .ThenByDescending(s => s.StepId)
            .ToList();

        foreach (var step in ordered)
        {
            var revertedBefore = reverted;

            if (step.PowerPrior is { } powerPrior)
            {
                RevertPowerStep(journal, step, powerPrior, messages, ref reverted, ref failed);
                if (step.RequiresReboot && reverted > revertedBefore)
                {
                    rebootEntries.Add(step.EntryId);
                }

                continue;
            }

            if (step.Service is { } serviceName)
            {
                RevertServiceStep(journal, step, serviceName, rebootedSinceApply, messages, ref reverted, ref failed);
                if (step.RequiresReboot && reverted > revertedBefore)
                {
                    rebootEntries.Add(step.EntryId);
                }

                continue;
            }

            if (step.Process is { } processPrior)
            {
                RevertProcessStep(journal, step, processPrior, messages, ref reverted, ref failed);
                if (step.RequiresReboot && reverted > revertedBefore)
                {
                    rebootEntries.Add(step.EntryId);
                }

                continue;
            }

            var registryTarget = step.RegistryTarget;
            if (registryTarget is null || step.Prior is null)
            {
                messages.Add($"step {step.StepId}: journal record carries no registry, service, process or power target; skipped.");
                failed++;
                continue;
            }

            if (registryTarget.Hive == "HKU"
                && registryTarget.UserSid is { } sid
                && !registry.UserHiveLoaded(sid))
            {
                // Not loaded => defer, keep dirty. Writing via a probe that reports KeyAbsent
                // would corrupt a hive that materialises when that user next signs in.
                journal.Append(new RevertDeferredRecord
                {
                    StepId = step.StepId,
                    Reason = $"user hive {sid} is not loaded; sign in as that user and run recover",
                });
                messages.Add($"step {step.StepId}: deferred — hive {sid} not loaded.");
                deferred++;
                continue;
            }

            try
            {
                var live = registry.Probe(registryTarget);
                var liveMatchesIntended = live.Presence == RegPresence.ValuePresent
                    && live.Value is not null
                    && live.Value.DataEquals(step.IntendedNew!);

                string outcome;
                if (!liveMatchesIntended && live.Presence == RegPresence.ValuePresent)
                {
                    // Someone (Windows, the user, another tool) changed this since we applied it.
                    // Overwriting their change with our captured prior would destroy configuration
                    // we did not create. Keep current, say so loudly.
                    outcome = "conflict-kept-current";
                    messages.Add(
                        $"step {step.StepId} ({step.Target}): value changed since apply " +
                        $"(now {Describe(live)}); kept current. Use the journal to restore manually if intended.");
                }
                else
                {
                    var residue = RestorePrior(registryTarget, step.Prior);
                    outcome = step.Prior.Presence == RegPresence.ValuePresent ? "restored" : "deleted";

                    if (residue is not null)
                    {
                        // Counted as reverted, because it is: the value is back. Said out loud,
                        // because silent residue is how a tool ends up having changed a machine it
                        // reported clean.
                        outcome = "restored-with-residue";
                        messages.Add($"step {step.StepId} ({step.Target}): {residue}");
                    }

                    // An activation with captured state gets that state replayed; one without is a
                    // pure notification and only needs re-broadcasting.
                    var withState = step.ActivationPrior.Select(a => a.Kind).ToHashSet();
                    stateReplays.AddRange(step.ActivationPrior);

                    foreach (var kind in step.Activation.Where(k => k != ActivationKind.None && !withState.Contains(k)))
                    {
                        broadcasts.Add(kind);
                    }
                }

                journal.Append(new RevertedRecord { StepId = step.StepId, Outcome = outcome });
                reverted++;

                if (step.RequiresReboot && outcome != "conflict-kept-current")
                {
                    rebootEntries.Add(step.EntryId);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                messages.Add($"step {step.StepId} ({step.Target}): revert failed: {ex.Message}");
                failed++;
            }
        }

        // Activations after all registry writes, mirroring apply order. State replays first: they
        // are the ones that actually restore behaviour, and a later notification broadcast should
        // observe the already-corrected state.
        foreach (var state in stateReplays)
        {
            try
            {
                _capture?.Restore(state);
            }
            catch (InvalidOperationException ex)
            {
                // A failed replay leaves registry correct but session behaviour stale - report it
                // rather than let the run claim a clean revert.
                messages.Add($"activation {state.Kind}: replay failed: {ex.Message}");
                failed++;
            }
        }

        foreach (var kind in broadcasts)
        {
            broadcaster.Broadcast(kind);
        }

        // Not written for a filtered revert. The session is genuinely not complete - steps of the other
        // scope are still applied - and baseline-diff.ps1 keys "is a session outstanding" on
        // applied > 0 && revertComplete == 0, so claiming completion here would let it baseline over a
        // machine still holding changes.
        if (onlyScope is null)
        {
            journal.Append(new RevertCompleteRecord { Reverted = reverted, Deferred = deferred, Failed = failed });
        }

        if (leftApplied > 0)
        {
            messages.Add(
                $"{leftApplied} step(s) of the other scope were left applied on purpose: this was a " +
                $"{onlyScope}-only revert. The machine stays engaged until they are reverted too.");
        }

        var result = new RevertResult
        {
            Reverted = reverted,
            Deferred = deferred,
            Failed = failed,
            Messages = messages,
            RebootPendingEntries = [.. rebootEntries.OrderBy(x => x, StringComparer.Ordinal)],
        };

        // Written whether or not the revert came out clean. A step that was actually put back owes a
        // restart regardless of what happened to the steps beside it, and a revert that reports
        // "machine clean" while a reboot-requiring value is back in the registry but not yet in effect is
        // telling the truth about the registry and the wrong thing about the machine.
        if (result.Clean || rebootEntries.Count > 0)
        {
            var state = _state.Load();
            var updated = rebootEntries.Count > 0 ? state.WithRebootPending(rebootEntries) : state;

            // Only this session's own dirty flag, and only on a clean revert - unchanged from before.
            // `updated with` rather than a fresh QuiesceState so the marker just set (or one already
            // there from an earlier session) is not wiped by the clear.
            //
            // AND NEVER WHEN STEPS WERE LEFT APPLIED. Clean means "nothing deferred and nothing failed",
            // which says nothing about completeness: a scope-filtered revert that put its own half back
            // perfectly is Clean with the other half still on the machine. Clearing the flag there would
            // leave persistent steps applied and no session marked dirty - the one state none of the four
            // recovery nets goes looking for, because every one of them keys on isDirty.
            if (result.Clean
                && leftApplied == 0
                && (state.ActiveSessionId == sessionId || state.ActiveSessionId is null))
            {
                updated = updated with { IsDirty = false, ActiveSessionId = null };
            }

            _state.Save(updated);
        }

        return result;
    }

    /// <summary>Reverts every session on disk that still has unreverted steps, oldest journal last.</summary>
    public IReadOnlyList<(Guid SessionId, RevertResult Result)> RevertAll(string initiator)
    {
        var results = new List<(Guid, RevertResult)>();

        foreach (var sessionId in SessionsNewestFirst())
        {
            var journalPath = Path.Combine(paths.SessionDir(sessionId), "journal.jsonl");

            // The third instance of the same trap, and the worst place for it: a File.Exists probe returns
            // false for "not permitted to read", so this `continue` would silently SKIP a session holding
            // outstanding changes and the run would then report "reverted 0 - machine clean". The panic
            // button reporting success for work it never looked at is the single worst failure available
            // to this program.
            JournalReadResult read;
            try
            {
                read = JournalReader.Read(journalPath);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // A session directory with no journal in it. Nothing was ever recorded, so nothing is owed.
                continue;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new StateUnreadableException(journalPath, ex);
            }

            if (PendingSteps(read.Records).Count == 0)
            {
                continue;
            }

            results.Add((sessionId, RevertSession(sessionId, initiator)));
        }

        return results;
    }

    // ------------------------------------------------------------- Recover

    /// <summary>
    /// The boot/launch recovery pass. Predicate: <c>state.isDirty</c> — NEVER "journal lacks a
    /// committed record". An engaged machine's steady state is committed AND dirty; keying on
    /// committed makes the most common crash case (engaged, then BSOD) invisible.
    /// </summary>
    public RevertResult? Recover()
    {
        var state = _state.Load();
        if (!state.IsDirty)
        {
            return null;
        }

        var sessionId = state.ActiveSessionId ?? SessionsNewestFirst().FirstOrDefault();
        if (sessionId == Guid.Empty)
        {
            // Dirty flag with no journal on disk: nothing recoverable. Clear rather than wedge - but
            // `state with`, so clearing the flag does not also retract an outstanding reboot warning.
            _state.Save(state with { IsDirty = false, ActiveSessionId = null });
            return new RevertResult { Reverted = 0, Deferred = 0, Failed = 0, Messages = ["dirty flag set but no session journal found; cleared."] };
        }

        var read = JournalReader.Read(Path.Combine(paths.SessionDir(sessionId), "journal.jsonl"));
        var committed = read.Records.OfType<CommittedRecord>().Any();
        var sessionStart = read.Records.OfType<SessionStartRecord>().FirstOrDefault();

        if (!committed)
        {
            // Crash mid-apply. A half-applied state is invalid regardless of scope: unwind it all.
            return RevertSession(sessionId, "recover");
        }

        // Committed and dirty = engaged steady state. Session-scoped steps whose boot has passed
        // are auto-reverted (the gaming session is over, whatever it was); persistent-scoped
        // steps are standing preferences and are never auto-reverted by recovery.
        //
        // The scope filter is what makes that second clause true. It used to hand the WHOLE session to
        // RevertSession, which had no filter - so the sentence above described the intent and the code did
        // something else whenever a session held both scopes. Which is the normal case:
        // apps.close-browsers is in the built-in default profile and journals Scope = Session, so a default
        // profile plus one sign-in preference is mixed, and a reboot silently undid the preference.
        var rebootedSince = sessionStart is not null && !QuiescePaths.IsSameBoot(sessionStart.BootId);
        var pendingScopes = PendingSteps(read.Records).Select(s => s.Scope).Distinct().ToList();

        if (rebootedSince && pendingScopes.Contains(TweakScope.Session))
        {
            return RevertSession(sessionId, "recover", onlyScope: TweakScope.Session);
        }

        return new RevertResult
        {
            Reverted = 0,
            Deferred = 0,
            Failed = 0,
            Messages =
            [
                rebootedSince
                    ? "engaged session contains only persistent tweaks; recovery leaves standing preferences alone. Use restore to revert them."
                    : "machine is engaged in the current boot; nothing to recover. Use restore to disengage.",
            ],
        };
    }

    // ------------------------------------------------------------- Helpers

    /// <summary>
    /// Applied-but-not-reverted steps of a journal: applying records, minus entry rollbacks,
    /// minus already-reverted step ids. This recomputation is what makes revert idempotent.
    /// </summary>
    public static IReadOnlyList<ApplyingRecord> PendingSteps(IReadOnlyList<JournalRecord> records)
    {
        var rolledBack = records.OfType<EntryRolledBackRecord>()
            .SelectMany(r => r.RolledBackSteps)
            .ToHashSet();

        var reverted = records.OfType<RevertedRecord>()
            .Select(r => r.StepId)
            .ToHashSet();

        return records.OfType<ApplyingRecord>()
            .Where(a => !rolledBack.Contains(a.StepId) && !reverted.Contains(a.StepId))
            .ToList();
    }

    /// <summary>
    /// Puts one registry target back to its captured prior. Returns null on a clean restore, or a
    /// description of harmless residue left behind (see the created-key case below).
    /// </summary>
    private string? RestorePrior(RegistryTarget target, RegistryProbe prior)
    {
        // Check the end state BEFORE mutating. A restore whose outcome already holds must not issue
        // the write or the delete anyway: the operation can be refused even when it would change
        // nothing, and a refusal here fails the whole revert and leaves the session permanently
        // unclosable.
        //
        // Found on a real machine, not in theory. A write to
        // HKLM\SOFTWARE\Policies\Microsoft\Dsh!AllowNewsAndInterests is vetoed by a registry
        // callback keyed on that exact (key, value name) pair, so the value was never created. The
        // captured prior was ValueAbsent. Revert then tried to DELETE the value that was already
        // absent, was vetoed in turn, and reported "machine still DIRTY" over a value that had
        // never changed - a state no number of retries could clear, because the retry performs the
        // same forbidden no-op.
        //
        // This is also what makes revert genuinely idempotent, which the delete path already
        // claimed to be but only achieved when the delete was permitted.
        var current = registry.Probe(target);
        if (IsAlreadyRestored(current, prior))
        {
            return null;
        }

        switch (prior.Presence)
        {
            case RegPresence.ValuePresent:
                registry.SetValue(target, prior.Value!);
                break;

            case RegPresence.ValueAbsent:
                // The value did not exist. Deleting it — not writing 0 — is the entire point.
                registry.DeleteValue(target);
                break;

            case RegPresence.KeyAbsent:
                // Delete the value only if it is actually there. A KeyAbsent prior still needs the
                // created-key cleanup below even when the value never landed, so this branch cannot
                // short-circuit wholesale — but the delete itself must still be skipped, or a
                // vetoed value name traps the revert here exactly as it did for a ValueAbsent prior.
                if (current.Presence == RegPresence.ValuePresent)
                {
                    registry.DeleteValue(target);
                }

                if (prior.MissingKeyPath is { } created)
                {
                    try
                    {
                        registry.DeleteCreatedKeysIfEmpty(target, created);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        // The VALUE is what governs behaviour, and it is restored. An empty key we
                        // created and are not permitted to remove is cosmetic residue - reportable,
                        // but not grounds to declare the session permanently unrevertable.
                        //
                        // Observed: Quiesce created HKLM\SOFTWARE\Policies\Microsoft\Dsh on the way
                        // to a write that was then vetoed, and was subsequently refused permission
                        // to delete the empty key it had just created. Treating that as a failure
                        // wedged the session - it reported "machine still DIRTY" forever over a
                        // key holding nothing.
                        return $"restored, but could not remove the empty key it created at " +
                               $"{target.Hive}\\{target.Subkey} ({ex.Message})";
                    }
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(prior), prior.Presence, "Unknown presence.");
        }

        return null;
    }

    /// <summary>True when the live state already matches the captured prior, so restoring it is a no-op.</summary>
    private static bool IsAlreadyRestored(RegistryProbe current, RegistryProbe prior) => prior.Presence switch
    {
        // Absent either way. Notably KeyAbsent also satisfies this: the value is not there, and
        // recreating a whole key just to delete a value from it would be a strange way to restore.
        RegPresence.ValueAbsent => current.Presence is RegPresence.ValueAbsent or RegPresence.KeyAbsent,

        // Only short-circuit when the key is genuinely gone. A key that still exists may be one this
        // session created and still owes a DeleteCreatedKeysIfEmpty pass.
        RegPresence.KeyAbsent => current.Presence == RegPresence.KeyAbsent,

        RegPresence.ValuePresent => current.Presence == RegPresence.ValuePresent
            && current.Value is not null
            && prior.Value is not null
            && current.Value.DataEquals(prior.Value),

        _ => false,
    };

    private RegistryTarget ResolveTarget(RegistryOpSpec op) => op.Hive switch
    {
        // HKCU is resolved to the interactive user's HKU\<sid> at plan time. The HKCU alias is
        // banned: under elevation-as-another-admin or in a recovery task it silently points at a
        // different hive while every write "succeeds".
        CatalogHive.HKCU => new RegistryTarget
        {
            Hive = "HKU",
            UserSid = info.UserSid,
            Subkey = op.Subkey,
            ValueName = op.Value,
        },
        CatalogHive.HKLM => new RegistryTarget
        {
            Hive = "HKLM",
            Subkey = op.Subkey,
            ValueName = op.Value,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(op), op.Hive, "Unknown hive."),
    };

    private IEnumerable<Guid> SessionsNewestFirst()
    {
        // Enumerated rather than probed with Directory.Exists, for the reason StateStore.Load documents at
        // length: Exists returns false when the answer is "you are not allowed to look", and the journal
        // root is hardened to Administrators. That turned an unelevated `revert-all` into the cheerful
        // "No sessions with unreverted steps found" over a machine with outstanding changes on it.
        List<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(paths.JournalRoot).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing has ever been journalled here. The only benign reading.
            return [];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new StateUnreadableException(paths.JournalRoot, ex);
        }

        return directories
            .Select(d => Guid.TryParse(Path.GetFileName(d), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .OrderByDescending(g => Directory.GetCreationTimeUtc(paths.SessionDir(g)));
    }

    /// <summary>
    /// Names the active scheme for a message, degrading to the GUID and then to "unreadable".
    /// </summary>
    /// <remarks>
    /// "unreadable" rather than an empty string or "none": the difference between "the machine is on no
    /// power plan" (impossible) and "Quiesce could not find out" (routine, and the condition under which
    /// it refuses to act at all) is the whole point of the distinction.
    /// </remarks>
    private static string DescribeActive(PowerSchemeSnapshot snapshot) =>
        snapshot.Active is { } id
            ? snapshot.NameOf(id) ?? id.ToString("D")
            : "unreadable";

    private static string Describe(RegistryProbe probe) => probe.Presence switch
    {
        RegPresence.ValuePresent => JsonSerializer.Serialize(probe.Value),
        RegPresence.ValueAbsent => "<absent>",
        RegPresence.KeyAbsent => "<key absent>",
        _ => "<?>",
    };
}
