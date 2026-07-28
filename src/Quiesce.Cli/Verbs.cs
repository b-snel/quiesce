using System.Text.Json;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

namespace Quiesce.Cli;

/// <summary>Implementations of the M1 verbs. Thin: parse options, call the engine, print, exit-code.</summary>
internal static class Verbs
{
    /// <summary>Serializes the single-writer verbs across processes and sessions.</summary>
    /// <remarks>
    /// <c>Global\</c>, not <c>Local\</c>: the boot-recovery task lives in session 0, and a
    /// session-local mutex would be invisible to it — exactly the race that lets a recovery revert
    /// interleave with an interactive apply.
    /// </remarks>
    private const string MutexName = @"Global\Quiesce.Mutating";

    // ------------------------------------------------------------ read-only

    public static int Inventory(CliEnvironment env)
    {
        // Reported as UNKNOWN, never as clean. The data root is Administrators-only by design, so an
        // unelevated inventory genuinely cannot answer this - and the answer it used to give was "clean",
        // on a machine that was engaged at the time. Everything below still works unelevated, so the rest
        // of the report is printed rather than abandoned.
        var unknownState = false;
        QuiesceState? state = null;
        try
        {
            state = new StateStore(env.Paths.DataRoot).Load();
        }
        catch (StateUnreadableException ex)
        {
            unknownState = true;
            Console.WriteLine("machine: UNKNOWN - Quiesce cannot tell whether this machine is modified.");
            Console.WriteLine($"         {ex.Message}");
        }

        if (state is not null)
        {
            Console.WriteLine($"machine: {(state.IsDirty ? $"ENGAGED (session {state.ActiveSessionId:D})" : "clean")}");
        }

        Console.WriteLine($"data:    {env.Paths.DataRoot}");

        // Surfaced because it changes which guardrails are active, and because a support bundle
        // that does not say whether the machine was remote cannot explain a refusal.
        var remote = SessionGuard.IsRemoteSession();
        Console.WriteLine(
            $"session: {(remote ? "REMOTE — network, shell and Remote Desktop services are locked" : "local")}");

        if (env.CatalogPath is null)
        {
            // Still useful without a catalog: dirty state is what you need in a recovery situation.
            Console.WriteLine("catalog: <none found> — tweak status unavailable, but restore/revert-all still work.");
            return unknownState ? CommandRouter.ExitCode.NotElevated : CommandRouter.ExitCode.Ok;
        }

        var catalog = env.LoadCatalog();
        var plan = env.CreateEngine().Plan(catalog, "default", new ProfileStore(env.Paths.DataRoot).ActiveEnabled());

        Console.WriteLine($"catalog: {env.CatalogPath} (v{catalog.CatalogVersion}, {catalog.Entries.Count} entries)");
        Console.WriteLine();

        foreach (var entry in catalog.Entries)
        {
            var steps = plan.Steps.Where(s => s.EntryId == entry.Id).ToList();
            var status = steps.All(s => s.NoOp) ? "already lean"
                : steps.Any(s => s.NoOp) ? "partially applied"
                : "not applied";

            Console.WriteLine($"[{status,-17}] {entry.Id}  ({entry.Evidence}, {entry.Impact} impact, tier {entry.RiskTier})");
            Console.WriteLine($"                    {entry.Title}");
            Console.WriteLine($"                    breaks: {entry.WhatItBreaks}");
        }

        // Non-zero when the machine's state could not be read, so a script cannot mistake an
        // "I don't know" report for a clean bill of health.
        return unknownState ? CommandRouter.ExitCode.NotElevated : CommandRouter.ExitCode.Ok;
    }

    public static int PrintPlan(CliEnvironment env)
    {
        var catalog = env.LoadCatalog();
        var plan = env.CreateEngine().Plan(catalog, "default", new ProfileStore(env.Paths.DataRoot).ActiveEnabled());

        Console.WriteLine($"plan for profile 'default' — {plan.EffectiveSteps.Count()} mutation(s), " +
                          $"{plan.Steps.Count(s => s.NoOp)} already-lean elision(s). Nothing has been changed.");
        Console.WriteLine();

        foreach (var step in plan.Steps)
        {
            if (step.NoOp)
            {
                // The reason, when there is one. "Already lean" is right for a registry value that
                // already holds the target; it is nonsense for a process group with nothing running.
                Console.WriteLine($"  step {step.StepId}  SKIP ({step.NoOpDetail ?? "already lean"})  {step.Target}");
                continue;
            }

            // Refused steps get their own section below, with the reason.
            if (step.RefusedReason is not null)
            {
                continue;
            }

            Console.WriteLine($"  step {step.StepId}  [{step.EntryId}]");
            Console.WriteLine($"    target: {step.Target}");

            if (step.ServiceBefore is { } svc)
            {
                Console.WriteLine(
                    $"    prior:  startType={svc.StartType}" +
                    (svc.DelayedAutostart ? " (delayed)" : string.Empty) +
                    $", {svc.RunState}");
                Console.WriteLine(
                    $"    change: startType={step.IntendedStartType}" +
                    (step.IntendedStop ? ", stop now" : ", leave running"));
            }
            else if (step.ProcessBefore is { } process)
            {
                Console.WriteLine($"    prior:  priority {process.PriorityClass}, {process.ImagePath ?? "<path unreadable>"}");
                Console.WriteLine(step.ProcessAction == Core.Catalog.ProcessAction.Throttle
                    ? $"    change: priority {step.IntendedPriority} (restored on Restore)"
                    : "    change: asked to close - AND NOT REOPENED BY RESTORE");
            }
            else
            {
                Console.WriteLine($"    prior:  {DescribeProbe(step.Prior!)}");
                Console.WriteLine($"    write:  {step.IntendedNew!.Kind} {JsonSerializer.Serialize(step.IntendedNew.Data)}");
            }

            if (step.Activation.Count > 0)
            {
                Console.WriteLine($"    then:   broadcast {string.Join(", ", step.Activation)}");
            }
        }

        // Refusals are shown, never silently dropped. A guardrail the user cannot see is
        // indistinguishable from a tweak that quietly did nothing.
        var refused = plan.RefusedSteps.ToList();
        if (refused.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"REFUSED by guardrails ({refused.Count}) — these will not be attempted:");

            // Grouped by reason, because a process group refuses per process and the counts get large:
            // the application hosting a development run has fourteen processes, all refused for the same
            // reason, and fourteen identical paragraphs bury the one line that matters.
            foreach (var group in refused.GroupBy(s => (s.EntryId, s.RefusedReason)))
            {
                var targets = group.Select(s => s.Target).ToList();
                Console.WriteLine($"  [{group.Key.EntryId}] {targets.Count} step(s)");
                foreach (var target in targets.Take(3))
                {
                    Console.WriteLine($"    {target}");
                }

                if (targets.Count > 3)
                {
                    Console.WriteLine($"    ... and {targets.Count - 3} more, same reason");
                }

                Console.WriteLine($"    {group.Key.RefusedReason}");
            }
        }

        if (plan.RequiresElevation && !CliEnvironment.IsElevated())
        {
            Console.WriteLine();
            Console.WriteLine("note: this plan needs administrator rights to engage.");
        }

        return CommandRouter.ExitCode.Ok;
    }

    // ------------------------------------------------------------- mutating

    public static int Engage(CliEnvironment env, string? faultSpec)
    {
        var preflight = env.RunAclPreflight(Console.Error);
        if (preflight != 0)
        {
            return preflight;
        }

        var elevation = RequireElevationToEngage(env);
        if (elevation != 0)
        {
            return elevation;
        }

        var catalog = env.LoadCatalog();
        var engine = env.CreateEngine();
        var plan = engine.Plan(catalog, "default", new ProfileStore(env.Paths.DataRoot).ActiveEnabled());

        if (plan.RequiresElevation && !CliEnvironment.IsElevated())
        {
            Console.Error.WriteLine("quiesce: this plan writes HKLM; run from an elevated prompt.");
            return CommandRouter.ExitCode.NotElevated;
        }

        if (!plan.EffectiveSteps.Any())
        {
            Console.WriteLine("Nothing to do — every enabled tweak is already at its lean value.");
            return CommandRouter.ExitCode.Ok;
        }

        return WithMutex(() =>
        {
            // FaultInjectedException deliberately escapes: the process must die the way a real
            // crash would, leaving the journal and dirty flag for `recover` to deal with.
            var result = engine.Engage(plan, FaultInjector.Parse(faultSpec));

            Console.WriteLine($"engaged: session {result.SessionId:D}");
            Console.WriteLine($"  applied {result.Applied}, skipped {result.SkippedNoop} already-lean");

            // Printed before the rollbacks, because this is where "Quiesce closed something and will not
            // bring it back" is said. It is the one consequence the undo does not cover, so it does not
            // get to be a footnote.
            foreach (var note in result.Notes)
            {
                Console.WriteLine($"  note: {note}");
            }

            foreach (var entry in result.RolledBackEntries)
            {
                var why = result.Diagnoses.TryGetValue(entry, out var d) ? d : "verification failed";
                Console.WriteLine($"  ROLLED BACK: {entry}");
                Console.WriteLine($"               {why}");
            }

            return result.Success ? CommandRouter.ExitCode.Ok : 1;
        });
    }

    public static int Restore(CliEnvironment env)
    {
        var state = new StateStore(env.Paths.DataRoot).Load();
        if (state.ActiveSessionId is not { } sessionId)
        {
            Console.WriteLine("No active session. Nothing to restore.");
            return CommandRouter.ExitCode.Ok;
        }

        return WithMutex(() => PrintRevert(env.CreateEngine().RevertSession(sessionId, "restore")));
    }

    public static int RevertAll(CliEnvironment env)
    {
        return WithMutex(() =>
        {
            var results = env.CreateEngine().RevertAll("revert-all");
            if (results.Count == 0)
            {
                Console.WriteLine("No sessions with unreverted steps found.");
                return CommandRouter.ExitCode.Ok;
            }

            var worst = CommandRouter.ExitCode.Ok;
            foreach (var (sessionId, result) in results)
            {
                Console.WriteLine($"session {sessionId:D}:");
                var code = PrintRevert(result);
                worst = Math.Max(worst, code);
            }

            return worst;
        });
    }

    public static int Recover(CliEnvironment env)
    {
        return WithMutex(() =>
        {
            var result = env.CreateEngine().Recover();
            if (result is null)
            {
                Console.WriteLine("Machine is clean; nothing to recover.");
                return CommandRouter.ExitCode.Ok;
            }

            return PrintRevert(result);
        });
    }

    /// <summary>
    /// The M1 regression net: engage, immediately revert, and assert every touched target is
    /// byte-identical to its captured prior. Exit 0 only on a perfect round trip.
    /// </summary>
    public static int VerifyRevert(CliEnvironment env)
    {
        var catalog = env.LoadCatalog();
        var engine = env.CreateEngine();
        var registry = new Win32Registry();

        // Entries that close applications are excluded from this verb outright, and the exclusion is
        // reported. This is a round-trip assertion: it engages, immediately reverts, and demands the
        // machine be byte-identical afterwards. A close cannot satisfy that by construction - there is no
        // undo - so including one would mean closing the operator's browser in order to assert something
        // meaningless about it. Enabled entries are otherwise untouched, so the run still covers
        // everything that CAN round-trip, and says what it left out.
        var enabled = new ProfileStore(env.Paths.DataRoot).ActiveEnabled();
        var irreversible = catalog.Entries
            .Where(e => enabled.Contains(e.Id)
                && e.Ops.OfType<Core.Catalog.ProcessOpSpec>().Any(op => op.Action == Core.Catalog.ProcessAction.Close))
            .Select(e => e.Id)
            .ToList();

        if (irreversible.Count > 0)
        {
            Console.WriteLine(
                $"verify-revert: excluding {irreversible.Count} entr{(irreversible.Count == 1 ? "y" : "ies")} " +
                "that close applications — a closed application cannot be reopened, so a round-trip " +
                "assertion over it would be meaningless (and would cost you the application):");
            foreach (var id in irreversible)
            {
                Console.WriteLine($"  {id}");
            }
        }

        var tested = enabled.Where(id => !irreversible.Contains(id, StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var plan = engine.Plan(catalog, "default", tested);
        var mutations = plan.EffectiveSteps.ToList();

        if (mutations.Count == 0)
        {
            Console.WriteLine("verify-revert: nothing to test — all targets already lean. (Round trip vacuously passes.)");
            return CommandRouter.ExitCode.Ok;
        }

        if (plan.RequiresElevation && !CliEnvironment.IsElevated())
        {
            Console.Error.WriteLine("quiesce: plan writes HKLM; run verify-revert elevated.");
            return CommandRouter.ExitCode.NotElevated;
        }

        return WithMutex(() =>
        {
            var engage = engine.Engage(plan, FaultInjector.None);
            var revert = engine.RevertSession(engage.SessionId, "verify-revert");

            var mismatches = new List<string>();
            foreach (var step in mutations)
            {
                // Only registry targets are byte-comparable here; service round-trips are asserted
                // by the baseline-diff script, which can read the SCM's three facts back.
                if (step.RegistryTarget is not { } target || step.Prior is not { } priorProbe)
                {
                    continue;
                }

                var now = registry.Probe(target);
                if (!ProbesEqual(priorProbe, now))
                {
                    mismatches.Add($"step {step.StepId} {step.Target}: prior={DescribeProbe(priorProbe)} now={DescribeProbe(now)}");
                }
            }

            var state = new StateStore(env.Paths.DataRoot).Load();

            Console.WriteLine($"verify-revert: {mutations.Count} target(s) round-tripped, " +
                              $"{mismatches.Count} mismatch(es), dirty={state.IsDirty}, " +
                              $"reverted={revert.Reverted} deferred={revert.Deferred} failed={revert.Failed}");

            foreach (var m in mismatches)
            {
                Console.Error.WriteLine($"  DRIFT: {m}");
            }

            var ok = mismatches.Count == 0 && revert.Clean && !state.IsDirty;
            return ok ? CommandRouter.ExitCode.Ok : 1;
        });
    }

    // -------------------------------------------------------------- helpers

    /// <summary>
    /// Gate for verbs that create new obligations (i.e. <c>engage</c>).
    /// </summary>
    /// <remarks>
    /// The journal directory must be admin-only, because an elevated Quiesce later executes the
    /// revert plan it finds there. Establishing that requires elevation, so engaging without it is
    /// refused on the real data root.
    /// <para>
    /// Applied ONLY to engage. The revert verbs deliberately have no elevation gate: refusing to
    /// undo is a worse outcome than the ACL weakness it would be protecting against, and a tool
    /// that can engage but then declines to disengage has stranded its user. Revert attempts the
    /// work and reports honestly if a write is denied.
    /// </para>
    /// </remarks>
    private static int RequireElevationToEngage(CliEnvironment env)
    {
        if (!env.Paths.IsDefaultRoot || CliEnvironment.IsElevated())
        {
            return 0;
        }

        Console.Error.WriteLine(
            $"quiesce: administrator rights are required to engage, because the journal in {env.Paths.DataRoot}");
        Console.Error.WriteLine(
            "  must be writable only by Administrators - Quiesce executes the revert plan it finds");
        Console.Error.WriteLine(
            "  there while elevated. Run from an elevated prompt.");
        Console.Error.WriteLine(
            "  (restore, revert-all and recover never require elevation: undo must always be possible.)");
        return CommandRouter.ExitCode.NotElevated;
    }

    private static int WithMutex(Func<int> body)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);

        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // previous holder died; the journal lock is the real guard
            }

            if (!acquired)
            {
                Console.Error.WriteLine("quiesce: another Quiesce process is mutating the machine. Refusing to run concurrently.");
                return CommandRouter.ExitCode.UsageError;
            }

            return body();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static int PrintRevert(RevertResult result)
    {
        Console.WriteLine($"  reverted {result.Reverted}, deferred {result.Deferred}, failed {result.Failed}" +
                          (result.Clean ? " — machine clean" : " — machine still DIRTY"));
        foreach (var message in result.Messages)
        {
            Console.WriteLine($"  {message}");
        }

        return result.Clean ? CommandRouter.ExitCode.Ok : 1;
    }

    private static bool ProbesEqual(RegistryProbe a, RegistryProbe b)
    {
        if (a.Presence != b.Presence)
        {
            return false;
        }

        return a.Presence != RegPresence.ValuePresent
            || (a.Value is not null && b.Value is not null && a.Value.DataEquals(b.Value));
    }

    private static string DescribeProbe(RegistryProbe probe) => probe.Presence switch
    {
        RegPresence.ValuePresent => $"{probe.Value!.Kind} {JsonSerializer.Serialize(probe.Value.Data)}",
        RegPresence.ValueAbsent => "<value absent>",
        RegPresence.KeyAbsent => $"<key absent{(probe.MissingKeyPath is { } p ? $"; would create {p}" : string.Empty)}>",
        _ => "<?>",
    };
}
