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
        var state = new StateStore(env.Paths.DataRoot).Load();
        Console.WriteLine($"machine: {(state.IsDirty ? $"ENGAGED (session {state.ActiveSessionId:D})" : "clean")}");
        Console.WriteLine($"data:    {env.Paths.DataRoot}");

        if (env.CatalogPath is null)
        {
            // Still useful without a catalog: dirty state is what you need in a recovery situation.
            Console.WriteLine("catalog: <none found> — tweak status unavailable, but restore/revert-all still work.");
            return CommandRouter.ExitCode.Ok;
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

        return CommandRouter.ExitCode.Ok;
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
                Console.WriteLine($"  step {step.StepId}  SKIP (already lean)  {step.Target}");
                continue;
            }

            Console.WriteLine($"  step {step.StepId}  [{step.EntryId}]");
            Console.WriteLine($"    target: {step.Target}");
            Console.WriteLine($"    prior:  {DescribeProbe(step.Prior)}");
            Console.WriteLine($"    write:  {step.IntendedNew.Kind} {JsonSerializer.Serialize(step.IntendedNew.Data)}");
            if (step.Activation.Count > 0)
            {
                Console.WriteLine($"    then:   broadcast {string.Join(", ", step.Activation)}");
            }
        }

        if (plan.RequiresElevation && !CliEnvironment.IsElevated())
        {
            Console.WriteLine();
            Console.WriteLine("note: this plan writes HKLM and will need an elevated prompt to engage.");
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

        var plan = engine.Plan(catalog, "default", new ProfileStore(env.Paths.DataRoot).ActiveEnabled());
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
                var now = registry.Probe(step.Target);
                if (!ProbesEqual(step.Prior, now))
                {
                    mismatches.Add($"step {step.StepId} {step.Target}: prior={DescribeProbe(step.Prior)} now={DescribeProbe(now)}");
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
