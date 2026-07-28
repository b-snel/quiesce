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
    EngineInfo info)
{
    private readonly StateStore _state = new(paths.DataRoot);

    // ---------------------------------------------------------------- Plan

    public EngagePlan Plan(CatalogFile catalog, string profile)
    {
        var steps = new List<PlannedStep>();
        var stepId = 0;

        foreach (var entry in catalog.Entries)
        {
            foreach (var op in entry.Ops)
            {
                stepId++;

                var target = ResolveTarget(op);
                var prior = registry.Probe(target);
                var intended = new RegistryData { Kind = op.ExpectedKind, Data = op.LeanData };

                var noOp = prior.Presence == RegPresence.ValuePresent
                    && prior.Value is not null
                    && prior.Value.DataEquals(intended);

                steps.Add(new PlannedStep
                {
                    StepId = stepId,
                    EntryId = entry.Id,
                    Scope = entry.Scope,
                    Target = target,
                    Prior = prior,
                    IntendedNew = intended,
                    Activation = entry.Activation,
                    NoOp = noOp,
                });
            }
        }

        return new EngagePlan { Profile = profile, CatalogVersion = catalog.CatalogVersion, Steps = steps };
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

        foreach (var entryGroup in effective.GroupBy(s => s.EntryId))
        {
            var appliedInEntry = new List<(PlannedStep Step, RegistryProbe Prior)>();
            var entryFailed = false;

            foreach (var step in entryGroup)
            {
                // Re-probe at apply time: the plan-time prior may be stale, and the journalled
                // prior must be the machine's state at the moment of mutation.
                var prior = registry.Probe(step.Target);
                var live = prior.Presence == RegPresence.ValuePresent ? prior.Value : null;

                if (live is not null && live.DataEquals(step.IntendedNew))
                {
                    skipped++;
                    continue;
                }

                journal.Append(new ApplyingRecord
                {
                    StepId = step.StepId,
                    EntryId = step.EntryId,
                    Scope = step.Scope,
                    Target = step.Target,
                    Prior = prior,
                    IntendedNew = step.IntendedNew,
                    Activation = step.Activation,
                });

                registry.SetValue(step.Target, step.IntendedNew);

                // Verify by re-reading the authoritative source. A non-throwing API call is not
                // success: Tamper Protection and policy engines silently swallow writes.
                var check = registry.Probe(step.Target);
                var ok = check.Presence == RegPresence.ValuePresent
                    && check.Value is not null
                    && check.Value.DataEquals(step.IntendedNew);

                journal.Append(new AppliedRecord
                {
                    StepId = step.StepId,
                    Verify = ok ? "ok" : $"mismatch: live={Describe(check)}",
                });

                if (!ok)
                {
                    RollBackEntry(journal, entryGroup.Key, appliedInEntry, (step, prior));
                    rolledBackEntries.Add(entryGroup.Key);
                    entryFailed = true;
                    break;
                }

                appliedInEntry.Add((step, prior));
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
        };
    }

    /// <summary>Entry-level atomicity: unwind every applied step of the failing entry, newest first.</summary>
    private void RollBackEntry(
        JournalWriter journal,
        string entryId,
        List<(PlannedStep Step, RegistryProbe Prior)> appliedInEntry,
        (PlannedStep Step, RegistryProbe Prior) failedStep)
    {
        var toUndo = appliedInEntry.Append(failedStep).Reverse().ToList();
        var undone = new List<int>();

        foreach (var (step, prior) in toUndo)
        {
            RestorePrior(step.Target, prior);
            undone.Add(step.StepId);
        }

        journal.Append(new EntryRolledBackRecord
        {
            EntryId = entryId,
            Reason = "verify failed",
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

        using var journal = JournalWriter.Open(sessionDir);
        journal.Append(new RevertStartRecord { Initiator = initiator });

        var reverted = 0;
        var deferred = 0;
        var failed = 0;
        var broadcasts = new HashSet<ActivationKind>();

        // Registry steps unwind newest-first. (Dependency-ordered revert across op kinds —
        // services before registry before relaunch — arrives with those op kinds.)
        foreach (var step in pending.OrderByDescending(s => s.StepId))
        {
            if (step.Target.Hive == "HKU"
                && step.Target.UserSid is { } sid
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
                var live = registry.Probe(step.Target);
                var liveMatchesIntended = live.Presence == RegPresence.ValuePresent
                    && live.Value is not null
                    && live.Value.DataEquals(step.IntendedNew);

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
                    RestorePrior(step.Target, step.Prior);
                    outcome = step.Prior.Presence == RegPresence.ValuePresent ? "restored" : "deleted";

                    foreach (var kind in step.Activation.Where(k => k != ActivationKind.None))
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

        // Broadcasts after all registry writes, mirroring apply order.
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
        var rebootedSince = sessionStart is not null && sessionStart.BootId != QuiescePaths.CurrentBootId();
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
