using Quiesce.Core;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// The case that prompted the whole feature: Engage closed an application and the user reopened it.
/// </summary>
/// <remarks>
/// Separate from <see cref="EngineDriftTests"/> because the process layer needs a wired classifier and a
/// pinned ancestry, and because the close arm is the one the journal could not previously notice at all —
/// a relaunched application has a new PID and a new creation time, so the identity check every other part
/// of revert relies on reports it absent forever.
/// <para>
/// Joins both static-guarding collections: <c>ProcessAncestry.OverrideForTests</c> because the fake PIDs
/// start at 1000 and can collide with the test host's real ancestry chain, and the session guard because
/// the engine's process paths consult it.
/// </para>
/// </remarks>
[Collection(SessionGuardCollection.Name)]
public class ProcessDriftTests : IDisposable
{
    private const string CometExe = @"C:\Users\t\AppData\Local\Perplexity\Comet\Application\comet.exe";
    private const string CometDir = @"\Perplexity\Comet\Application\";

    private readonly FakeRegistry _registry = new();
    private readonly FakeProcessControl _processes = new();
    private readonly RecordingActivation _activation = new();
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), "quiesce-tests", Guid.NewGuid().ToString("N"));

    public ProcessDriftTests()
    {
        ProcessAncestry.OverrideForTests = new HashSet<int>();
        SessionGuard.OverrideForTests = false;
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

    private TransactionEngine Engine() => new(
        _registry,
        _activation,
        new QuiescePaths(_dataRoot),
        new EngineInfo { AppVersion = "test", OsBuild = "10.0.26200", UserSid = EngineTestHarness.Sid },
        _activation,
        services: null,
        processes: _processes,
        processClassifier: new ProcessClassifier(
            gameDirectories: null,
            serviceHostPids: null,
            selfHostImagePaths: null));

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
                UnderDirectories = [CometDir],
                ThrottleTo = action == ProcessAction.Throttle ? ThrottleLevel.BelowNormal : null,
            },
        ],
        WhatItBreaks = "nothing (test)",
    };

    [Fact]
    public void A_closed_application_that_came_back_is_drift_and_is_resyncable()
    {
        // The user's own scenario, end to end: Engage closes Comet, the user reopens it, and the machine no
        // longer matches the session. Invisible to the journal's identity check by construction, because
        // the new instance has a different pid AND a different creation time.
        var engine = Engine();
        _processes.Add("comet", CometExe, pid: 4100);
        _processes.Add("comet", CometExe, pid: 4101);

        var engage = engine.Engage(
            engine.Plan(EngineTestHarness.CatalogOf(CometEntry()), "test"), FaultInjector.None);

        Assert.Empty(_processes.Enumerate());
        Assert.False(engine.DetectDrift(engage.SessionId).Any);

        // Reopened. New pids, new creation ticks - a different instance in every respect except which
        // program it is.
        _processes.Add("comet", CometExe, pid: 7200);
        _processes.Add("comet", CometExe, pid: 7201);
        _processes.Add("comet", CometExe, pid: 7202);

        var drift = engine.DetectDrift(engage.SessionId);
        var item = Assert.Single(drift.Items);

        Assert.Equal(DriftKind.ProcessReturned, item.Kind);
        Assert.True(item.Resyncable);
        Assert.Null(item.NotResyncableReason);
        Assert.Equal(3, item.LiveProcesses.Count);
        Assert.Contains("comet.exe is running again", item.Detail);
    }

    [Fact]
    public void Nineteen_closed_processes_coming_back_is_one_item_not_nineteen()
    {
        // The measured shape on this machine. A close journals one step per INSTANCE, so Comet produced
        // nineteen applying records; nineteen drift items for one reopened browser would be accurate and
        // useless. Grouped on the RECORDED image path, the same key IsSameProgram compares on.
        var engine = Engine();
        for (var i = 0; i < 19; i++)
        {
            _processes.Add("comet", CometExe);
        }

        var engage = engine.Engage(
            engine.Plan(EngineTestHarness.CatalogOf(CometEntry()), "test"), FaultInjector.None);

        // Nineteen separate applying records, read from the journal rather than assumed.
        Assert.Equal(19, Journal(engage.SessionId).OfType<ApplyingRecord>().Count(r => r.Process is not null));

        _processes.Add("comet", CometExe);
        _processes.Add("comet", CometExe);

        var item = Assert.Single(engine.DetectDrift(engage.SessionId).Items);

        // Two live now, nineteen closed then, and the sentence says both rather than implying the counts match.
        Assert.Equal(2, item.LiveProcesses.Count);
        Assert.Contains("2 processes now", item.Detail);
        Assert.Contains("closed 19 processes", item.Detail);
    }

    private IReadOnlyList<JournalRecord> Journal(Guid sessionId) =>
        JournalReader.Read(Path.Combine(new QuiescePaths(_dataRoot).SessionDir(sessionId), "journal.jsonl")).Records;

    [Fact]
    public void A_different_program_with_the_same_name_elsewhere_is_not_the_one_that_came_back()
    {
        // The safety property, restated for drift. IsSameProgram compares the FULL PATH, never the name -
        // "something called comet.exe somewhere unknown" is precisely the case name matching gets wrong,
        // and getting it wrong here would mean a resync closing a program Quiesce never touched.
        var engine = Engine();
        _processes.Add("comet", CometExe, pid: 4100);

        var engage = engine.Engage(
            engine.Plan(EngineTestHarness.CatalogOf(CometEntry()), "test"), FaultInjector.None);

        _processes.Add("comet", @"C:\Users\t\AppData\Local\Temp\comet.exe", pid: 8000);

        Assert.False(engine.DetectDrift(engage.SessionId).Any);
    }

    [Fact]
    public void A_process_whose_path_cannot_be_read_never_matches()
    {
        var engine = Engine();
        _processes.Add("comet", CometExe, pid: 4100);

        var engage = engine.Engage(
            engine.Plan(EngineTestHarness.CatalogOf(CometEntry()), "test"), FaultInjector.None);

        // Elevated processes and other users' processes deny the path query even to an elevated caller.
        // "Cannot say what this is" must resolve to no match, never to a name-based guess.
        _processes.Add("comet", imagePath: null, pid: 8100);

        Assert.False(engine.DetectDrift(engage.SessionId).Any);
    }

    [Fact]
    public void A_session_applied_before_the_last_restart_reports_drift_but_refuses_to_resync_it()
    {
        // What a session closed before a reboot is not what is running after one: these came back because
        // the user signed in, not because the machine drifted. Re-closing them would be Quiesce acting on a
        // comparison it has no business making.
        var engine = Engine();
        _processes.Add("comet", CometExe, pid: 4100);

        var engage = engine.Engage(
            engine.Plan(EngineTestHarness.CatalogOf(CometEntry()), "test"), FaultInjector.None);

        ForgeADifferentBoot(engage.SessionId);
        _processes.Add("comet", CometExe, pid: 7200);

        var drift = engine.DetectDrift(engage.SessionId);
        var item = Assert.Single(drift.Items);

        Assert.True(drift.AppliedBeforeLastRestart);
        Assert.Equal(DriftKind.ProcessReturned, item.Kind);
        Assert.False(item.Resyncable);
        Assert.Contains("because you signed in", item.NotResyncableReason);
    }

    [Fact]
    public void A_throttled_process_that_restarted_is_resyncable_because_nothing_has_captured_its_prior()
    {
        // The asymmetry worth understanding. The throttled instance is gone, so revert has nothing to put
        // back for the NEW one - there is no journalled prior covering it. Re-throttling therefore captures
        // a prior nothing has captured yet, which is why this is the one non-close kind that resyncs safely.
        var engine = Engine();
        _processes.Add("comet", CometExe, pid: 4100);

        var engage = engine.Engage(
            engine.Plan(EngineTestHarness.CatalogOf(CometEntry(ProcessAction.Throttle)), "test"),
            FaultInjector.None);

        _processes.Exit(new ProcessIdentity { Pid = 4100, CreatedUtcTicks = _processes.Enumerate()
            .First(p => p.Identity.Pid == 4100).Identity.CreatedUtcTicks });
        _processes.Add("comet", CometExe, pid: 7200);

        var item = Assert.Single(engine.DetectDrift(engage.SessionId).Items);

        Assert.Equal(DriftKind.ProcessRestarted, item.Kind);
        Assert.True(item.Resyncable);
        Assert.Contains("back at full priority", item.Detail);
    }

    [Fact]
    public void The_same_throttled_process_at_a_changed_priority_is_reported_but_refused()
    {
        // Same INSTANCE, different priority: something else changed it. Not resyncable, for the same reason
        // a changed registry value is not - a journalled prior already covers this identity, and a second
        // record for it would make Restore unable to say which prior is the real one.
        var engine = Engine();
        _processes.Add("comet", CometExe, pid: 4100);

        var engage = engine.Engage(
            engine.Plan(EngineTestHarness.CatalogOf(CometEntry(ProcessAction.Throttle)), "test"),
            FaultInjector.None);

        var identity = _processes.Enumerate().First(p => p.Identity.Pid == 4100).Identity;
        _processes.TrySetPriority(identity, System.Diagnostics.ProcessPriorityClass.High, out _);

        var item = Assert.Single(engine.DetectDrift(engage.SessionId).Items);

        Assert.Equal(DriftKind.ThrottleChanged, item.Kind);
        Assert.False(item.Resyncable);
        Assert.Contains("something changed it since", item.Detail);
    }

    /// <summary>Rewrites the session's boot id so the detector believes a restart happened.</summary>
    private void ForgeADifferentBoot(Guid sessionId)
    {
        var path = Path.Combine(new QuiescePaths(_dataRoot).SessionDir(sessionId), "journal.jsonl");
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
