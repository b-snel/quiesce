using Quiesce.Core;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// Resync: re-closing what came back, by appending to the session that is already open.
/// </summary>
/// <remarks>
/// The riskiest surface in the batch, so the tests are mostly about what it does NOT do. Every one of the
/// four properties the design rests on is asserted directly rather than argued for: no state write, no
/// truncated revert script, no new record type, and no reused step id.
/// </remarks>
[Collection(SessionGuardCollection.Name)]
public class ResyncTests : IDisposable
{
    private const string CometExe = @"C:\Users\t\AppData\Local\Perplexity\Comet\Application\comet.exe";

    private readonly FakeRegistry _registry = new();
    private readonly FakeProcessControl _processes = new();
    private readonly RecordingActivation _activation = new();
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), "quiesce-tests", Guid.NewGuid().ToString("N"));

    public ResyncTests()
    {
        ProcessAncestry.OverrideForTests = new HashSet<int>();
        SessionGuard.OverrideForTests = false;

        // The HKU hive has to be loaded for a registry step to be probed at all: DetectDrift skips an
        // unloaded one, correctly, because that user is simply not signed in and their absent value is not
        // drift. Without this the registry test below silently found no drift and passed for the wrong
        // reason - which is why it asserts Single(drift.Items) before asserting Empty(drift.Resyncable).
        _registry.LoadUserHive(EngineTestHarness.Sid);
    }

    public void Dispose()
    {
        ProcessAncestry.OverrideForTests = null;
        SessionGuard.OverrideForTests = null;

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private QuiescePaths Paths => new(_dataRoot);

    private TransactionEngine Engine() => new(
        _registry,
        _activation,
        Paths,
        new EngineInfo { AppVersion = "test", OsBuild = "10.0.26200", UserSid = EngineTestHarness.Sid },
        _activation,
        services: null,
        processes: _processes,
        processClassifier: new ProcessClassifier(null, null, null));

    private static CatalogEntry CometEntry(ProcessAction action = ProcessAction.Close) => new()
    {
        Id = "apps.close-comet",
        Category = "apps",
        Title = "Comet",
        Evidence = Evidence.Situational,
        Impact = Impact.Medium,
        RiskTier = 2,
        Scope = TweakScope.Session,
        RequiresAdmin = false,
        RequiresReboot = false,
        Ops =
        [
            new ProcessOpSpec
            {
                Action = action,
                ImageName = "comet",
                UnderDirectories = [@"\Perplexity\Comet\Application\"],
                ThrottleTo = action == ProcessAction.Throttle ? ThrottleLevel.Idle : null,
            },
        ],
        WhatItBreaks = "nothing (test)",
    };

    private IReadOnlyList<JournalRecord> Journal(Guid sessionId) =>
        JournalReader.Read(Path.Combine(Paths.SessionDir(sessionId), "journal.jsonl")).Records;

    /// <summary>Engage with Comet running, then reopen it. Returns the engaged session.</summary>
    private (TransactionEngine Engine, Guid SessionId) EngagedThenReopened(int reopenCount = 2)
    {
        var engine = Engine();
        _processes.Add("comet", CometExe);
        _processes.Add("comet", CometExe);

        var engage = engine.Engage(
            engine.Plan(EngineTestHarness.CatalogOf(CometEntry()), "test"), FaultInjector.None);

        for (var i = 0; i < reopenCount; i++)
        {
            _processes.Add("comet", CometExe);
        }

        return (engine, engage.SessionId);
    }

    [Fact]
    public void A_reopened_application_is_closed_again_in_the_same_session()
    {
        var (engine, sessionId) = EngagedThenReopened();

        var drift = engine.DetectDrift(sessionId);
        var result = engine.Resync(sessionId, engine.PlanResync(drift), "test");

        Assert.False(result.Refused);
        Assert.Equal(2, result.Acted);
        Assert.Equal(sessionId, result.SessionId);
        Assert.Empty(_processes.Enumerate());

        // And the machine matches again.
        Assert.False(engine.DetectDrift(sessionId).Any);

        // ONE session, not two. This is the property that makes the whole design safe: a second Engage
        // would have minted a new session id and overwritten ActiveSessionId, orphaning the first session
        // from both Restore and Recover, which read only that field.
        Assert.Single(Directory.GetDirectories(Paths.JournalRoot));
        Assert.Equal(sessionId, new StateStore(_dataRoot).Load().ActiveSessionId);
    }

    [Fact]
    public void Resync_never_writes_the_state_file()
    {
        // The property that makes a REFUSED resync provably harmless, and it holds for a successful one
        // too: IsDirty is already true and the active session is already this one, so there is nothing
        // state.json needs to learn. Asserted on bytes.
        var (engine, sessionId) = EngagedThenReopened();

        var statePath = Path.Combine(_dataRoot, "state.json");
        var before = File.ReadAllBytes(statePath);

        var result = engine.Resync(sessionId, engine.PlanResync(engine.DetectDrift(sessionId)), "test");

        Assert.False(result.Refused);
        Assert.Equal(before, File.ReadAllBytes(statePath));
    }

    [Fact]
    public void Resync_does_not_truncate_the_revert_script()
    {
        // RevertScriptWriter.Create opens with FileMode.Create, so reopening it for a live session would
        // destroy recovery net 4 - the one that needs no Quiesce binary at all. Resync opens none, and this
        // asserts the file is byte-identical afterwards.
        var (engine, sessionId) = EngagedThenReopened();

        var scriptPath = Directory
            .GetFiles(Paths.SessionDir(sessionId), "revert*.cmd")
            .Single();
        var before = File.ReadAllBytes(scriptPath);

        engine.Resync(sessionId, engine.PlanResync(engine.DetectDrift(sessionId)), "test");

        Assert.Equal(before, File.ReadAllBytes(scriptPath));
    }

    [Fact]
    public void Resync_adds_no_journal_record_type_an_older_build_cannot_read()
    {
        // JournalStore deserializes the discriminator OUTSIDE the JsonException guard, so an unknown
        // `record` value throws a raw JsonException that RevertSession's catch clauses do not cover - an
        // older or side-by-side staged build would then be unable to revert the machine at all.
        //
        // Asserted on the raw JSON rather than on types, because that is what another build parses.
        var (engine, sessionId) = EngagedThenReopened();

        engine.Resync(sessionId, engine.PlanResync(engine.DetectDrift(sessionId)), "test");

        var known = new[]
        {
            "sessionStart", "planned", "applying", "applied", "sideEffect", "entryRolledBack",
            "committed", "revertStart", "reverted", "revertDeferred", "revertComplete",
        };

        var lines = File.ReadAllLines(Path.Combine(Paths.SessionDir(sessionId), "journal.jsonl"));

        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var record = System.Text.Json.JsonDocument.Parse(line).RootElement
                .GetProperty("record").GetString();

            Assert.Contains(record, known);
        }
    }

    [Fact]
    public void Resync_step_ids_are_past_every_id_the_journal_already_used()
    {
        // PendingSteps filters reverted ids through a HashSet<int>, so one RevertedRecord{StepId=7} would
        // discharge TWO records numbered 7 - and the second would never be undone. Planned records count
        // too: every elided or refused step has one with no matching applying record.
        var (engine, sessionId) = EngagedThenReopened();

        var beforeIds = Journal(sessionId)
            .Select(r => r switch
            {
                PlannedRecord p => p.StepId,
                ApplyingRecord a => a.StepId,
                _ => 0,
            })
            .Where(id => id > 0)
            .ToHashSet();

        Assert.NotEmpty(beforeIds);

        engine.Resync(sessionId, engine.PlanResync(engine.DetectDrift(sessionId)), "test");

        var resyncIds = Journal(sessionId)
            .OfType<ApplyingRecord>()
            .Where(r => r.Target.Contains("resync", StringComparison.Ordinal))
            .Select(r => r.StepId)
            .ToList();

        Assert.NotEmpty(resyncIds);
        Assert.All(resyncIds, id => Assert.True(id > beforeIds.Max(), $"step {id} reuses an existing id"));
        Assert.Equal(resyncIds.Count, resyncIds.Distinct().Count());
    }

    [Fact]
    public void A_resync_record_carries_the_scope_of_the_step_it_puts_back()
    {
        // Recover decides whether to auto-revert a session by asking whether ANY pending step is
        // Session-scoped, so a resync record that guessed the scope would change what a reboot does to the
        // whole session.
        var (engine, sessionId) = EngagedThenReopened();

        engine.Resync(sessionId, engine.PlanResync(engine.DetectDrift(sessionId)), "test");

        var resynced = Journal(sessionId)
            .OfType<ApplyingRecord>()
            .Where(r => r.Target.Contains("resync", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(resynced);
        Assert.All(resynced, r => Assert.Equal(TweakScope.Session, r.Scope));
    }

    [Fact]
    public void Restore_after_a_resync_reports_both_closes_and_reopens_nothing()
    {
        var (engine, sessionId) = EngagedThenReopened();
        engine.Resync(sessionId, engine.PlanResync(engine.DetectDrift(sessionId)), "test");

        var restore = engine.RevertSession(sessionId, "restore");

        Assert.True(restore.Clean);
        Assert.False(new StateStore(_dataRoot).Load().IsDirty);
        Assert.Empty(_processes.Enumerate());

        // Four closed processes across two passes, and Restore says so about all of them rather than only
        // the ones the first pass closed.
        var closedNotes = restore.Messages
            .Count(m => m.Contains("does not relaunch", StringComparison.Ordinal));

        Assert.Equal(4, closedNotes);
    }

    // ------------------------------------------------------------- refusals

    [Fact]
    public void A_resync_on_a_clean_machine_is_refused_and_does_nothing()
    {
        var engine = Engine();
        _processes.Add("comet", CometExe);

        var engage = engine.Engage(
            engine.Plan(EngineTestHarness.CatalogOf(CometEntry()), "test"), FaultInjector.None);
        engine.RevertSession(engage.SessionId, "restore");

        _processes.Add("comet", CometExe);

        var result = engine.Resync(engage.SessionId, new EngagePlan
        {
            Profile = "resync",
            CatalogVersion = "test",
            Steps = [],
        }, "test");

        Assert.True(result.Refused);
        Assert.Contains("Nothing is engaged", result.RefusedReason);
        Assert.Single(_processes.Enumerate()); // untouched
    }

    [Fact]
    public void A_resync_against_a_session_that_is_not_active_is_refused()
    {
        var (engine, sessionId) = EngagedThenReopened();
        var plan = engine.PlanResync(engine.DetectDrift(sessionId));

        var result = engine.Resync(Guid.NewGuid(), plan, "test");

        Assert.True(result.Refused);
        Assert.Contains("not the active session", result.RefusedReason);
        Assert.Equal(2, _processes.Enumerate().Count); // untouched
    }

    [Fact]
    public void A_resync_of_a_session_from_before_the_last_restart_is_refused()
    {
        var (engine, sessionId) = EngagedThenReopened();
        var plan = engine.PlanResync(engine.DetectDrift(sessionId));

        ForgeADifferentBoot(sessionId);

        var result = engine.Resync(sessionId, plan, "test");

        Assert.True(result.Refused);
        Assert.Contains("before the last restart", result.RefusedReason);
        Assert.Equal(2, _processes.Enumerate().Count); // untouched
    }

    [Fact]
    public void A_refused_resync_leaves_the_journal_byte_identical()
    {
        // "Nothing was done" has to be literally true, not approximately true.
        var (engine, sessionId) = EngagedThenReopened();
        var plan = engine.PlanResync(engine.DetectDrift(sessionId));

        var journalPath = Path.Combine(Paths.SessionDir(sessionId), "journal.jsonl");
        var before = File.ReadAllBytes(journalPath);

        Assert.True(engine.Resync(Guid.NewGuid(), plan, "test").Refused);

        Assert.Equal(before, File.ReadAllBytes(journalPath));
    }

    [Fact]
    public void Only_process_drift_is_ever_planned_for_resync()
    {
        // The safety boundary, asserted at the planning layer. A registry value changed since engage is
        // reported by the detector and must never reach a plan: a second applying record for a target the
        // session already covers makes Restore end on the drifted value while reporting Clean.
        var engine = Engine();
        var entry = EngineTestHarness.DwordEntry(scope: TweakScope.Session);
        var target = EngineTestHarness.TargetOf(entry);
        _registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = engine.Engage(
            engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        _registry.Seed(target, EngineTestHarness.Dword(2));

        var drift = engine.DetectDrift(engage.SessionId);
        Assert.Single(drift.Items);
        Assert.Empty(drift.Resyncable);

        var plan = engine.PlanResync(drift);
        Assert.Empty(plan.Steps);

        var result = engine.Resync(engage.SessionId, plan, "test");
        Assert.True(result.Refused);
        Assert.Contains("Nothing to resync", result.RefusedReason);

        // The user's value is still theirs.
        Assert.Equal(2u, _registry.Peek(target)!.Data.GetUInt32());
    }

    [Fact]
    public void A_restarted_throttled_process_is_re_throttled_to_the_level_the_session_chose()
    {
        // Not to a default, and not to the PRIOR - re-applying the prior would restore full priority while
        // reporting a throttle. The entry throttles to Idle, so the resync must too.
        var engine = Engine();
        var original = _processes.Add("comet", CometExe);

        var engage = engine.Engage(
            engine.Plan(EngineTestHarness.CatalogOf(CometEntry(ProcessAction.Throttle)), "test"),
            FaultInjector.None);

        _processes.Exit(original.Identity);
        var restarted = _processes.Add("comet", CometExe);

        Assert.Equal(System.Diagnostics.ProcessPriorityClass.Normal, restarted.PriorityClass);

        var result = engine.Resync(engage.SessionId, engine.PlanResync(engine.DetectDrift(engage.SessionId)), "test");

        Assert.False(result.Refused);
        Assert.Equal(1, result.Acted);
        Assert.Equal(
            System.Diagnostics.ProcessPriorityClass.Idle,
            _processes.Query(restarted.Identity).PriorityClass);
    }

    [Fact]
    public void A_re_throttle_is_undone_by_restore_for_the_new_instance_too()
    {
        // The asymmetry that makes a throttle resync safe: the NEW instance has no journalled prior, so
        // re-throttling captures one, and Restore then puts that instance back. Without a captured prior it
        // would be left throttled forever.
        var engine = Engine();
        var original = _processes.Add("comet", CometExe);

        var engage = engine.Engage(
            engine.Plan(EngineTestHarness.CatalogOf(CometEntry(ProcessAction.Throttle)), "test"),
            FaultInjector.None);

        _processes.Exit(original.Identity);
        var restarted = _processes.Add("comet", CometExe);

        engine.Resync(engage.SessionId, engine.PlanResync(engine.DetectDrift(engage.SessionId)), "test");
        Assert.Equal(
            System.Diagnostics.ProcessPriorityClass.Idle,
            _processes.Query(restarted.Identity).PriorityClass);

        engine.RevertSession(engage.SessionId, "restore");

        Assert.Equal(
            System.Diagnostics.ProcessPriorityClass.Normal,
            _processes.Query(restarted.Identity).PriorityClass);
        Assert.False(new StateStore(_dataRoot).Load().IsDirty);
    }

    [Fact]
    public void A_close_that_declines_is_reported_and_is_not_a_failure()
    {
        // An application sitting on a save-your-work prompt. Nothing happened and nothing needs undoing, so
        // it is not a failure - but the user has to be told which of their applications is still open.
        var (engine, sessionId) = EngagedThenReopened(reopenCount: 1);

        var stubborn = _processes.Enumerate().Single();
        _processes.RefuseToExit.Add(stubborn.Identity.Pid);

        var result = engine.Resync(sessionId, engine.PlanResync(engine.DetectDrift(sessionId)), "test");

        Assert.False(result.Refused);
        Assert.Equal(0, result.Acted);
        Assert.Empty(result.Failures);
        Assert.Contains(result.Notes, n => n.Contains("unsaved work", StringComparison.Ordinal));
        Assert.Single(_processes.Enumerate());
    }

    [Fact]
    public void A_second_prior_for_one_registry_target_loses_the_original()
    {
        // THE REASON RESYNC IS PROCESSES ONLY, demonstrated rather than argued. This test does by hand what
        // the obvious resync design would have done automatically - append a second `applying` record for a
        // target the session already covers - and shows the machine ends on a value Quiesce wrote while
        // Restore reports itself clean.
        //
        // Prior X=1, lean Y=0, drift Z=2. PendingSteps has no per-target dedupe so it returns BOTH records;
        // revert orders ThenByDescending(StepId) so the SECOND one goes first, sees live == its intent, and
        // restores its prior (Z=2); the FIRST then sees a value matching neither its intent nor its prior
        // and takes conflict-kept-current. Both count as reverted, failed stays 0, Clean is true, IsDirty is
        // cleared - and the true original (X=1) is gone.
        //
        // If a future change ever makes a non-process resync look attractive, this is the test that says
        // what it costs.
        var engine = Engine();
        var entry = EngineTestHarness.DwordEntry(scope: TweakScope.Session);
        var target = EngineTestHarness.TargetOf(entry);
        _registry.Seed(target, EngineTestHarness.Dword(1)); // the true original

        var engage = engine.Engage(
            engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        Assert.Equal(0u, _registry.Peek(target)!.Data.GetUInt32());

        // Something changes it, and then a hypothetical resync re-writes the lean value and journals the
        // drifted value as a second prior.
        _registry.Seed(target, EngineTestHarness.Dword(2));

        var original = Journal(engage.SessionId).OfType<ApplyingRecord>().Single();
        AppendSecondPrior(engage.SessionId, original, driftedPrior: 2);
        _registry.Seed(target, EngineTestHarness.Dword(0)); // the "resync" write

        var restore = engine.RevertSession(engage.SessionId, "restore");

        // Restore is happy. The machine is not back.
        Assert.True(restore.Clean);
        Assert.False(new StateStore(_dataRoot).Load().IsDirty);
        Assert.Equal(2u, _registry.Peek(target)!.Data.GetUInt32());
        Assert.NotEqual(1u, _registry.Peek(target)!.Data.GetUInt32());
    }

    /// <summary>
    /// Appends a second <c>applying</c> record for the same target, as a naive resync would.
    /// </summary>
    /// <remarks>
    /// Written by hand precisely because the engine will not do it. Raw JSON rather than the writer, so the
    /// test cannot be quietly fixed by a change to the writer's API.
    /// </remarks>
    private void AppendSecondPrior(Guid sessionId, ApplyingRecord original, uint driftedPrior)
    {
        var path = Path.Combine(Paths.SessionDir(sessionId), "journal.jsonl");

        var second = new ApplyingRecord
        {
            StepId = original.StepId + 100,
            EntryId = original.EntryId,
            Scope = original.Scope,
            Target = original.Target + ", resync",
            RequiresReboot = original.RequiresReboot,
            RegistryTarget = original.RegistryTarget,
            Prior = new RegistryProbe
            {
                Presence = RegPresence.ValuePresent,
                Value = EngineTestHarness.Dword(driftedPrior),
            },
            IntendedNew = original.IntendedNew,
        };

        File.AppendAllText(
            path,
            System.Text.Json.JsonSerializer.Serialize<JournalRecord>(second) + Environment.NewLine);
    }

    private void ForgeADifferentBoot(Guid sessionId)
    {
        var path = Path.Combine(Paths.SessionDir(sessionId), "journal.jsonl");
        var lines = File.ReadAllLines(path);

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("\"sessionStart\"", StringComparison.Ordinal))
            {
                lines[i] = System.Text.RegularExpressions.Regex.Replace(
                    lines[i], "\"bootId\":\"[^\"]*\"", "\"bootId\":\"forged-earlier-boot\"");
            }
        }

        File.WriteAllLines(path, lines);
    }
}
