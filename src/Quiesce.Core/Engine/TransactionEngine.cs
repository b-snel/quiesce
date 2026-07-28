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
    IServiceControl? services = null)
{
    private readonly StateStore _state = new(paths.DataRoot);

    /// <summary>
    /// Optional because most activations carry no state. When the broadcaster also implements
    /// capture (the Win32 one does), use it automatically.
    /// </summary>
    private readonly IActivationCapture? _capture = activationCapture ?? broadcaster as IActivationCapture;

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

        foreach (var entry in entries)
        {
            foreach (var op in entry.Ops)
            {
                stepId++;
                steps.Add(op switch
                {
                    RegistryOpSpec r => PlanRegistry(stepId, entry, r),
                    ServiceOpSpec s => PlanService(stepId, entry, s),
                    _ => throw new NotSupportedException($"Unsupported op kind '{op.GetType().Name}'."),
                });
            }
        }

        return new EngagePlan { Profile = profile, CatalogVersion = catalog.CatalogVersion, Steps = steps };
    }

    private PlannedStep PlanRegistry(int stepId, CatalogEntry entry, RegistryOpSpec op)
    {
        var target = ResolveTarget(op);
        var prior = registry.Probe(target);
        var intended = new RegistryData { Kind = op.ExpectedKind, Data = op.LeanData };

        var noOp = prior.Presence == RegPresence.ValuePresent
            && prior.Value is not null
            && prior.Value.DataEquals(intended);

        return new PlannedStep
        {
            StepId = stepId,
            EntryId = entry.Id,
            Scope = entry.Scope,
            Op = op,
            Target = target.ToString(),
            RegistryTarget = target,
            Prior = prior,
            IntendedNew = intended,
            Activation = entry.Activation,
            NoOp = noOp,
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
                IntendedNew = step.IntendedNew,
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

        foreach (var entryGroup in effective.GroupBy(s => s.EntryId))
        {
            var appliedInEntry = new List<AppliedStep>();
            var entryFailed = false;

            foreach (var step in entryGroup)
            {
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
            }
        }

        revertScript.Finish();
        journal.Append(new CommittedRecord { AppliedSteps = applied, SkippedNoop = skipped });

        // The machine is now engaged: committed AND dirty is the steady state. isDirty clears only
        // when a revert completes cleanly.
        if (applied == 0 && rolledBackEntries.Count == 0)
        {
            _state.Save(new QuiesceState { IsDirty = false, ActiveSessionId = null });
        }

        return new EngageResult
        {
            SessionId = sessionId,
            Applied = applied,
            SkippedNoop = skipped,
            RolledBackEntries = rolledBackEntries,
            Diagnoses = diagnoses,
        };
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
    private void UndoApplied(AppliedStep applied)
    {
        if (applied.RegistryTarget is { } target && applied.RegistryPrior is { } prior)
        {
            RestorePrior(target, prior);
            return;
        }

        if (applied.Service is { } service && applied.ServicePrior is { } servicePrior && services is not null)
        {
            RestoreService(service, servicePrior);
        }
    }

    /// <summary>
    /// Restores a service's three captured facts: start type, delayed-auto flag, and run state.
    /// </summary>
    /// <remarks>
    /// Start type is restored before the service is started, so a service that was Automatic comes
    /// back Automatic rather than being started while still marked Disabled. Only services that
    /// were actually running are started — a stopped Manual service must stay stopped.
    /// </remarks>
    private void RestoreService(string service, ServiceSnapshot prior)
    {
        if (services is null || !prior.Present || prior.StartType is not { } startType)
        {
            return;
        }

        services.SetStartType(service, startType, prior.DelayedAutostart);

        if (prior.RunState == ServiceRunState.Running)
        {
            services.TryStart(service, TimeSpan.FromSeconds(30), out _);
        }
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

        foreach (var undo in toUndo)
        {
            UndoApplied(undo);
            undone.Add(undo.StepId);
        }

        journal.Append(new EntryRolledBackRecord
        {
            EntryId = entryId,
            Reason = reason,
            RolledBackSteps = undone,
        });
    }

    // -------------------------------------------------------------- Revert

    /// <summary>
    /// Reverts a session from its journal. Never touches the catalog — the records are the truth.
    /// Idempotent: a crash mid-revert is survivable by running it again.
    /// </summary>
    public RevertResult RevertSession(Guid sessionId, string initiator)
    {
        var sessionDir = paths.SessionDir(sessionId);
        var journalPath = Path.Combine(sessionDir, "journal.jsonl");

        if (!File.Exists(journalPath))
        {
            throw new FileNotFoundException($"No journal for session {sessionId:D}.", journalPath);
        }

        var read = JournalReader.Read(journalPath);
        var messages = new List<string>();

        if (read.TornFinalLine)
        {
            messages.Add("Journal has a torn final line (crash mid-append). Proceeding with the intact records.");
        }

        var pending = PendingSteps(read.Records);

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

        // Dependency order, not naive reverse: services and power first, then registry, then
        // activation broadcasts. Unwinding strictly newest-first would restore registry values that
        // a service reads at startup *after* restarting that service, so it would come back having
        // read the tweaked value. Within a kind, newest-first still holds.
        var ordered = pending
            .OrderBy(s => s.Service is not null ? 0 : 1)
            .ThenByDescending(s => s.StepId)
            .ToList();

        foreach (var step in ordered)
        {
            if (step.Service is { } serviceName)
            {
                RevertServiceStep(journal, step, serviceName, rebootedSinceApply, messages, ref reverted, ref failed);
                continue;
            }

            var registryTarget = step.RegistryTarget;
            if (registryTarget is null || step.Prior is null)
            {
                messages.Add($"step {step.StepId}: journal record carries neither a registry nor a service target; skipped.");
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
                    RestorePrior(registryTarget, step.Prior);
                    outcome = step.Prior.Presence == RegPresence.ValuePresent ? "restored" : "deleted";

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

        journal.Append(new RevertCompleteRecord { Reverted = reverted, Deferred = deferred, Failed = failed });

        var result = new RevertResult { Reverted = reverted, Deferred = deferred, Failed = failed, Messages = messages };

        if (result.Clean)
        {
            var state = _state.Load();
            if (state.ActiveSessionId == sessionId || state.ActiveSessionId is null)
            {
                _state.Save(new QuiesceState { IsDirty = false, ActiveSessionId = null });
            }
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
            if (!File.Exists(journalPath))
            {
                continue;
            }

            var read = JournalReader.Read(journalPath);
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
            // Dirty flag with no journal on disk: nothing recoverable. Clear rather than wedge.
            _state.Save(new QuiesceState { IsDirty = false, ActiveSessionId = null });
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
        var rebootedSince = sessionStart is not null && !QuiescePaths.IsSameBoot(sessionStart.BootId);
        var pendingScopes = PendingSteps(read.Records).Select(s => s.Scope).Distinct().ToList();

        if (rebootedSince && pendingScopes.Contains(TweakScope.Session))
        {
            return RevertSession(sessionId, "recover");
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

    private void RestorePrior(RegistryTarget target, RegistryProbe prior)
    {
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
                registry.DeleteValue(target);
                if (prior.MissingKeyPath is { } created)
                {
                    registry.DeleteCreatedKeysIfEmpty(target, created);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(prior), prior.Presence, "Unknown presence.");
        }
    }

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
        if (!Directory.Exists(paths.JournalRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(paths.JournalRoot)
            .Select(d => Guid.TryParse(Path.GetFileName(d), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .OrderByDescending(g => Directory.GetCreationTimeUtc(paths.SessionDir(g)));
    }

    private static string Describe(RegistryProbe probe) => probe.Presence switch
    {
        RegPresence.ValuePresent => JsonSerializer.Serialize(probe.Value),
        RegPresence.ValueAbsent => "<absent>",
        RegPresence.KeyAbsent => "<key absent>",
        _ => "<?>",
    };
}
