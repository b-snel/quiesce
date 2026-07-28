using System.Diagnostics;
using Quiesce.Core;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// Process groups end to end through the engine: plan, journal, apply, revert.
/// </summary>
/// <remarks>
/// Deliberately driven through <see cref="TransactionEngine"/> rather than through
/// <see cref="ProcessCloser"/> and <see cref="ProcessThrottler"/> directly, which have their own tests.
/// What is under test here is the claim that made this op kind worth adding: that a process op travels
/// the same plan → journal → apply → revert path as a registry value, with the same write-ahead
/// ordering and the same conflict rules, and that the two places it genuinely cannot — a close has no
/// undo — are visible rather than papered over.
/// </remarks>
[Collection(SessionGuardCollection.Name)]
public class ProcessOpTests : IDisposable
{
    private const string ChromeDir = @"C:\Program Files\Google\Chrome\Application\";
    private const string ChromeExe = ChromeDir + "chrome.exe";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "quiesce-proc", Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _registry = new();
    private readonly FakeProcessControl _processes = new();
    private readonly RecordingActivation _activation = new();

    public ProcessOpTests()
    {
        _registry.LoadUserHive(EngineTestHarness.Sid);
        SessionGuard.OverrideForTests = false;

        // Pinned empty: the classifier reads live ancestry, and the fake hands out PIDs from 1000 up,
        // which can collide with a real PID in the test host's own chain and fail an unrelated test.
        ProcessAncestry.OverrideForTests = new HashSet<int>();
    }

    public void Dispose()
    {
        SessionGuard.OverrideForTests = null;
        ProcessAncestry.OverrideForTests = null;

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private IReadOnlyList<JournalRecord> Journal(Guid sessionId) => JournalReader
        .Read(Path.Combine(new QuiescePaths(_dataRoot).SessionDir(sessionId), "journal.jsonl"))
        .Records;

    /// <summary>
    /// The journal as it exists on disk right now, readable while the writer still holds the file.
    /// </summary>
    /// <remarks>
    /// Shared read access is required, not incidental: these tests inspect the journal from inside a
    /// mutation, which is the only moment at which write-ahead ordering can actually be observed.
    /// </remarks>
    private string JournalTextNow()
    {
        var root = new QuiescePaths(_dataRoot).JournalRoot;
        if (!Directory.Exists(root))
        {
            return string.Empty;
        }

        var text = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(root, "journal.jsonl", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
    }

    private TransactionEngine EngineWith(IReadOnlySet<string>? hostImages = null) => new(
        _registry,
        _activation,
        new QuiescePaths(_dataRoot),
        new EngineInfo { AppVersion = "test", OsBuild = "10.0.26200", UserSid = EngineTestHarness.Sid },
        _activation,
        services: null,
        processes: _processes,
        processClassifier: new ProcessClassifier(
            gameDirectories: [@"D:\Games\Elden Ring"],
            serviceHostPids: null,
            selfHostImagePaths: hostImages));

    private static CatalogEntry ProcessEntry(
        ProcessAction action = ProcessAction.Close,
        string imageName = "chrome",
        string? directory = null,
        ThrottleLevel? throttleTo = null,
        string id = "apps.test") => new()
    {
        Id = id,
        Category = "apps",
        Title = id,
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
                ImageName = imageName,
                UnderDirectories = [directory ?? @"\Google\Chrome\Application\"],
                ThrottleTo = throttleTo ?? (action == ProcessAction.Throttle ? ThrottleLevel.BelowNormal : null),
            },
        ],
        WhatItBreaks = "nothing (test)",
    };

    // ------------------------------------------------------------- targeting

    [Fact]
    public void Targeting_is_by_path_so_a_same_named_program_elsewhere_is_not_a_member()
    {
        var real = _processes.Add("chrome", ChromeExe);
        var impostor = _processes.Add("chrome", @"C:\Users\someone\AppData\Local\Temp\chrome.exe");

        var plan = EngineWith().Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test");

        var acted = plan.EffectiveSteps.Select(s => s.ProcessBefore!.Identity.Pid).ToList();
        Assert.Equal([real.Identity.Pid], acted);
        Assert.DoesNotContain(impostor.Identity.Pid, acted);
    }

    [Fact]
    public void A_directory_fragment_cannot_match_a_longer_name_that_starts_the_same_way()
    {
        // The reason fragments are delimited at both ends. Without the trailing separator, a group
        // targeting Discord would also collect Discord Canary - a different application the user did not
        // choose, whose close is just as irreversible.
        var discord = _processes.Add("Discord", @"C:\Users\me\AppData\Local\Discord\app-1.0\Discord.exe");
        _processes.Add("Discord", @"C:\Users\me\AppData\Local\DiscordCanary\app-1.0\Discord.exe");

        var entry = ProcessEntry(imageName: "Discord", directory: @"\Discord\");
        var plan = EngineWith().Plan(EngineTestHarness.CatalogOf(entry), "test");

        Assert.Equal(
            [discord.Identity.Pid],
            plan.EffectiveSteps.Select(s => s.ProcessBefore!.Identity.Pid).ToList());
    }

    [Fact]
    public void A_process_whose_path_cannot_be_read_is_never_a_member()
    {
        // Protected and other-user processes deny the path query routinely. Falling back to the name is
        // exactly the mistake path-based targeting exists to prevent.
        _processes.Add("chrome", imagePath: null);

        var plan = EngineWith().Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test");

        Assert.Empty(plan.EffectiveSteps);
        Assert.All(plan.Steps, s => Assert.True(s.NoOp));
    }

    [Fact]
    public void One_step_per_live_process_each_named_by_pid()
    {
        var first = _processes.Add("chrome", ChromeExe);
        var second = _processes.Add("chrome", ChromeExe);

        var plan = EngineWith().Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test");

        Assert.Equal(2, plan.EffectiveSteps.Count());
        Assert.Contains($"pid {first.Identity.Pid}", string.Join("|", plan.Steps.Select(s => s.Target)));
        Assert.Contains($"pid {second.Identity.Pid}", string.Join("|", plan.Steps.Select(s => s.Target)));

        // Distinct step ids, because each one gets its own journal record and its own prior.
        Assert.Equal(2, plan.Steps.Select(s => s.StepId).Distinct().Count());
    }

    [Fact]
    public void Nothing_running_is_a_no_op_that_says_why_rather_than_claiming_already_lean()
    {
        var plan = EngineWith().Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test");

        var step = Assert.Single(plan.Steps);
        Assert.True(step.NoOp);
        Assert.Contains("nothing matching chrome is running", step.NoOpDetail);
    }

    [Fact]
    public void A_process_that_appears_after_the_plan_is_not_touched()
    {
        // The preflight dialog renders the plan, so the plan is what the user approved. Collecting extra
        // processes at apply time would close applications that were never shown to anyone.
        var planned = _processes.Add("chrome", ChromeExe);
        var engine = EngineWith();
        var plan = engine.Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test");

        var latecomer = _processes.Add("chrome", ChromeExe);
        engine.Engage(plan, FaultInjector.None);

        Assert.Equal([$"close chrome ({planned.Identity.Pid})"], _processes.CloseLog);
        Assert.True(_processes.Query(latecomer.Identity).Present);
    }

    // ---------------------------------------------------------------- close

    [Fact]
    public void A_close_is_journalled_before_it_happens()
    {
        var chrome = _processes.Add("chrome", ChromeExe);
        var engine = EngineWith();
        var plan = engine.Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test");

        // Inspected at the instant the close request is delivered: the record has to be on disk already,
        // because the request may succeed and the machine may then lose power.
        var journalledFirst = false;
        _processes.BeforeClose = () => journalledFirst = JournalTextNow().Contains("\"record\":\"applying\"");

        engine.Engage(plan, FaultInjector.None);

        Assert.True(journalledFirst, "the applying record must be durable before the close is requested");
        Assert.False(_processes.Query(chrome.Identity).Present);
    }

    [Fact]
    public void Closing_is_counted_as_applied_and_reported_as_not_reopened()
    {
        _processes.Add("chrome", ChromeExe);
        var engine = EngineWith();

        var result = engine.Engage(engine.Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test"), FaultInjector.None);

        Assert.Equal(1, result.Applied);
        Assert.Equal(0, result.SkippedNoop);
        Assert.Contains(result.Notes, n => n.Contains("closed chrome") && n.Contains("will not reopen"));
    }

    [Fact]
    public void An_application_that_declines_to_close_is_not_a_failure_and_does_not_roll_its_entry_back()
    {
        // The overwhelmingly common cause is a "save your work?" prompt, which the graceful ladder exists
        // to respect. Failing the entry would be worse than useless: the sibling that DID close cannot be
        // reopened, so there is nothing to roll back to.
        var stubborn = _processes.Add("chrome", ChromeExe);
        var willing = _processes.Add("chrome", ChromeExe);
        _processes.RefuseToExit.Add(stubborn.Identity.Pid);

        var engine = EngineWith();
        var result = engine.Engage(engine.Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test"), FaultInjector.None);

        Assert.True(result.Success);
        Assert.Empty(result.RolledBackEntries);
        Assert.Equal(1, result.Applied);
        Assert.Contains(result.Notes, n => n.Contains("still running") && n.Contains("unsaved work"));
        Assert.True(_processes.Query(stubborn.Identity).Present);
        Assert.False(_processes.Query(willing.Identity).Present);
    }

    [Fact]
    public void Restore_does_not_relaunch_a_closed_application_but_says_so_and_still_goes_clean()
    {
        _processes.Add("chrome", ChromeExe);
        var engine = EngineWith();
        var engage = engine.Engage(engine.Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test"), FaultInjector.None);

        var revert = engine.RevertSession(engage.SessionId, "restore");

        // Clean, deliberately. Refusing to close the session over an application the user can reopen in
        // one click would leave the machine permanently dirty - the wedge this project has already fixed
        // twice - and the machine genuinely has nothing of Quiesce's left on it.
        Assert.True(revert.Clean);
        Assert.False(new StateStore(_dataRoot).Load().IsDirty);
        Assert.Contains(revert.Messages, m => m.Contains("does not relaunch"));

        var outcomes = Journal(engage.SessionId).OfType<RevertedRecord>().Select(r => r.Outcome);
        Assert.Contains("closed-not-relaunched", outcomes);
    }

    [Fact]
    public void Reverting_a_close_that_never_happened_does_nothing_to_the_still_running_process()
    {
        var stubborn = _processes.Add("chrome", ChromeExe);
        _processes.RefuseToExit.Add(stubborn.Identity.Pid);

        var engine = EngineWith();
        var engage = engine.Engage(engine.Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test"), FaultInjector.None);
        var revert = engine.RevertSession(engage.SessionId, "restore");

        Assert.True(revert.Clean);
        Assert.True(_processes.Query(stubborn.Identity).Present);
        Assert.Contains(
            "not-closed-nothing-to-do",
            Journal(engage.SessionId).OfType<RevertedRecord>().Select(r => r.Outcome));
    }

    // ------------------------------------------------------------- throttle

    [Fact]
    public void A_throttle_round_trips_exactly_through_the_journal()
    {
        var chrome = _processes.Add("chrome", ChromeExe, priority: ProcessPriorityClass.AboveNormal);
        var engine = EngineWith();
        var entry = ProcessEntry(ProcessAction.Throttle, throttleTo: ThrottleLevel.Idle);

        var engage = engine.Engage(engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);
        Assert.Equal(ProcessPriorityClass.Idle, _processes.Query(chrome.Identity).PriorityClass);

        var revert = engine.RevertSession(engage.SessionId, "restore");

        Assert.True(revert.Clean);
        Assert.Equal(ProcessPriorityClass.AboveNormal, _processes.Query(chrome.Identity).PriorityClass);
    }

    [Fact]
    public void The_prior_priority_reaches_disk_before_the_write_that_changes_it()
    {
        // Same rule as the registry path, and the reason the throttler takes a beforeWrite hook at all:
        // the prior is read inside the throttler, so without the hook the engine would have to read it a
        // second time and journal an answer the write never raced against.
        var chrome = _processes.Add("chrome", ChromeExe, priority: ProcessPriorityClass.Normal);
        var engine = EngineWith();
        var plan = engine.Plan(
            EngineTestHarness.CatalogOf(ProcessEntry(ProcessAction.Throttle, throttleTo: ThrottleLevel.Idle)),
            "test");

        string? journalAtWriteTime = null;
        _processes.BeforePriorityWrite = () => journalAtWriteTime = JournalTextNow();

        engine.Engage(plan, FaultInjector.None);

        Assert.NotNull(journalAtWriteTime);
        Assert.Contains("\"record\":\"applying\"", journalAtWriteTime);
        Assert.Contains("\"priorityClass\":\"Normal\"", journalAtWriteTime);
        Assert.Equal(ProcessPriorityClass.Idle, _processes.Query(chrome.Identity).PriorityClass);
    }

    [Fact]
    public void Each_process_gets_its_own_prior_rather_than_one_assumed_for_the_group()
    {
        // Measured on the development machine: one application's processes sat at three different classes
        // at once. Restoring the group to a single value would have promoted the idle ones and demoted the
        // busy one, and a byte-level check would have called that clean.
        var busy = _processes.Add("chrome", ChromeExe, priority: ProcessPriorityClass.AboveNormal);
        var ordinary = _processes.Add("chrome", ChromeExe, priority: ProcessPriorityClass.Normal);
        var engine = EngineWith();
        var entry = ProcessEntry(ProcessAction.Throttle, throttleTo: ThrottleLevel.Idle);

        var engage = engine.Engage(engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);
        engine.RevertSession(engage.SessionId, "restore");

        Assert.Equal(ProcessPriorityClass.AboveNormal, _processes.Query(busy.Identity).PriorityClass);
        Assert.Equal(ProcessPriorityClass.Normal, _processes.Query(ordinary.Identity).PriorityClass);
    }

    [Fact]
    public void An_already_throttled_process_is_elided_at_plan_time()
    {
        _processes.Add("chrome", ChromeExe, priority: ProcessPriorityClass.Idle);
        var entry = ProcessEntry(ProcessAction.Throttle, throttleTo: ThrottleLevel.BelowNormal);

        var plan = EngineWith().Plan(EngineTestHarness.CatalogOf(entry), "test");

        var step = Assert.Single(plan.Steps);
        Assert.True(step.NoOp);
        Assert.Contains("already at Idle", step.NoOpDetail);
        Assert.Empty(_processes.PriorityLog);
    }

    [Fact]
    public void A_priority_changed_since_apply_is_kept_rather_than_overwritten()
    {
        var chrome = _processes.Add("chrome", ChromeExe, priority: ProcessPriorityClass.Normal);
        var engine = EngineWith();
        var entry = ProcessEntry(ProcessAction.Throttle, throttleTo: ThrottleLevel.Idle);
        var engage = engine.Engage(engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        // Someone else moved it after Quiesce did. Overwriting that with a stale capture would destroy a
        // choice Quiesce did not make - the same rule the registry and service paths apply.
        _processes.TrySetPriority(chrome.Identity, ProcessPriorityClass.High, out _);

        var revert = engine.RevertSession(engage.SessionId, "restore");

        Assert.Equal(ProcessPriorityClass.High, _processes.Query(chrome.Identity).PriorityClass);
        Assert.Contains(revert.Messages, m => m.Contains("kept current"));
    }

    [Fact]
    public void A_throttle_that_does_not_stick_fails_its_entry_and_is_unwound()
    {
        var chrome = _processes.Add("chrome", ChromeExe, priority: ProcessPriorityClass.Normal);
        _processes.IgnorePriorityWrites.Add(chrome.Identity.Pid);
        var engine = EngineWith();
        var entry = ProcessEntry(ProcessAction.Throttle, throttleTo: ThrottleLevel.Idle);

        var result = engine.Engage(engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        Assert.False(result.Success);
        Assert.Contains("apps.test", result.RolledBackEntries);
        Assert.Equal(ProcessPriorityClass.Normal, _processes.Query(chrome.Identity).PriorityClass);
    }

    // ------------------------------------------------------------- refusals

    [Fact]
    public void A_member_the_guardrails_refuse_is_shown_with_the_reason_not_quietly_dropped()
    {
        // During development every process of the application hosting Quiesce matches and every one is
        // refused. Seeing that is the point: a group that silently shrank to nothing would be
        // indistinguishable from a group with nothing to do.
        var host = _processes.Add("chrome", ChromeExe);
        var engine = EngineWith(hostImages: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ChromeExe });

        var plan = engine.Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test");

        var step = Assert.Single(plan.Steps);
        Assert.Equal(host.Identity.Pid, step.ProcessBefore!.Identity.Pid);
        Assert.Contains("launched it", step.RefusedReason);
        Assert.Empty(plan.EffectiveSteps);

        engine.Engage(plan, FaultInjector.None);
        Assert.Empty(_processes.CloseLog);
        Assert.True(_processes.Query(host.Identity).Present);
    }

    [Fact]
    public void Nothing_is_closed_while_a_game_is_running()
    {
        var chrome = _processes.Add("chrome", ChromeExe);
        _processes.Add("eldenring", @"D:\Games\Elden Ring\Game\eldenring.exe");

        var plan = EngineWith().Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test");

        Assert.Contains("cannot be undone", Assert.Single(plan.Steps).RefusedReason);
        Assert.True(_processes.Query(chrome.Identity).Present);
    }

    [Fact]
    public void A_recycled_pid_between_plan_and_apply_is_not_acted_on()
    {
        var chrome = _processes.Add("chrome", ChromeExe);
        var engine = EngineWith();
        var plan = engine.Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test");

        var replacement = _processes.Recycle(chrome.Identity, "notepad", @"C:\Windows\System32\notepad.exe");
        var result = engine.Engage(plan, FaultInjector.None);

        Assert.Empty(_processes.CloseLog);
        Assert.True(_processes.Query(replacement.Identity).Present);
        Assert.Contains(result.Notes, n => n.Contains("exited before Quiesce asked"));
    }

    [Fact]
    public void An_engine_wired_without_a_classifier_refuses_process_ops()
    {
        // A classifier built with no arguments knows no game directories, no service host PIDs and nothing
        // about what launched Quiesce. Defaulting one would produce an engine that acts on processes with
        // its safety checks switched off, which is worse than one that declines.
        var chrome = _processes.Add("chrome", ChromeExe);
        var engine = new TransactionEngine(
            _registry,
            _activation,
            new QuiescePaths(_dataRoot),
            new EngineInfo { AppVersion = "test", OsBuild = "10.0.26200", UserSid = EngineTestHarness.Sid },
            _activation,
            services: null,
            processes: _processes,
            processClassifier: null);

        var plan = engine.Plan(EngineTestHarness.CatalogOf(ProcessEntry()), "test");

        Assert.Contains("without its guardrails", Assert.Single(plan.Steps).RefusedReason);
        Assert.True(_processes.Query(chrome.Identity).Present);
    }
}
