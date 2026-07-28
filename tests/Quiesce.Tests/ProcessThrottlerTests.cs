using System.Diagnostics;
using Quiesce.Core;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

public class ProcessThrottlerTests : IDisposable
{
    public ProcessThrottlerTests() => ProcessAncestry.OverrideForTests = new HashSet<int>();

    public void Dispose() => ProcessAncestry.OverrideForTests = null;

    private static readonly string[] GameDirs = [@"D:\SteamLibrary\steamapps\common\Elden Ring"];

    private readonly FakeProcessControl _processes = new();

    private ProcessThrottler ThrottlerWith(
        IReadOnlySet<uint>? hostPids = null,
        IReadOnlySet<string>? hostImages = null) =>
        new(_processes, new ProcessClassifier(GameDirs, hostPids, hostImages));

    [Fact]
    public void Throttling_captures_the_prior_class_rather_than_assuming_normal()
    {
        // Measured on the development machine: one application's 14 processes sat at Normal, Idle AND
        // AboveNormal simultaneously. Restoring everything to Normal would have promoted the idle ones
        // and demoted the busy one, and a byte-level check would have called that a clean restore.
        var busy = _processes.Add("app", @"C:\Apps\App\app.exe", priority: ProcessPriorityClass.AboveNormal);
        var idle = _processes.Add("app", @"C:\Apps\App\app.exe", priority: ProcessPriorityClass.Idle);
        var throttler = ThrottlerWith();

        var busyOutcome = throttler.Throttle(busy.Identity, ProcessPriorityClass.BelowNormal);
        var idleOutcome = throttler.Throttle(idle.Identity, ProcessPriorityClass.BelowNormal);

        Assert.Equal(ProcessPriorityClass.AboveNormal, busyOutcome.Prior);

        // The already-lower one is refused rather than raised to BelowNormal.
        Assert.False(idleOutcome.Succeeded);
        Assert.Contains("never raises", idleOutcome.Detail);
        Assert.Equal(ProcessPriorityClass.Idle, _processes.Query(idle.Identity).PriorityClass);
    }

    [Fact]
    public void A_throttle_round_trips_exactly()
    {
        var app = _processes.Add("app", @"C:\Apps\App\app.exe", priority: ProcessPriorityClass.AboveNormal);
        var throttler = ThrottlerWith();

        var applied = throttler.Throttle(app.Identity, ProcessPriorityClass.Idle);
        Assert.True(applied.Succeeded);
        Assert.Equal(ProcessPriorityClass.Idle, _processes.Query(app.Identity).PriorityClass);

        var restored = throttler.Restore(app.Identity, applied.Prior!.Value);

        Assert.True(restored.Succeeded);
        Assert.Equal(ProcessPriorityClass.AboveNormal, _processes.Query(app.Identity).PriorityClass);
    }

    [Fact]
    public void Quiesce_never_raises_priority_even_within_the_ceiling()
    {
        var app = _processes.Add("app", @"C:\Apps\App\app.exe", priority: ProcessPriorityClass.Normal);

        var outcome = ThrottlerWith().Throttle(app.Identity, ProcessPriorityClass.AboveNormal);

        Assert.False(outcome.Succeeded);
        Assert.Contains("never raises", outcome.Detail);
        Assert.Empty(_processes.PriorityLog);
    }

    [Fact]
    public void The_priority_ceiling_is_enforced()
    {
        // The enum's values are Win32 flag bits and are not in priority order - Idle is 64, Normal 32,
        // High 128 - so a naive comparison would rank Idle above Normal and let a throttle promote
        // things. This pins the ordering, not just the ceiling.
        var app = _processes.Add("app", @"C:\Apps\App\app.exe", priority: ProcessPriorityClass.High);

        var outcome = ThrottlerWith().Throttle(app.Identity, ProcessPriorityClass.High);

        Assert.False(outcome.Succeeded);
        Assert.Contains("ceiling", outcome.Detail);
    }

    [Fact]
    public void An_already_throttled_process_is_a_no_op_and_is_not_journalled_as_applied()
    {
        // Same rule as the registry path: eliding a value the user had already set means restore can
        // never "restore" something Quiesce did not change.
        var app = _processes.Add("app", @"C:\Apps\App\app.exe", priority: ProcessPriorityClass.Idle);

        var outcome = ThrottlerWith().Throttle(app.Identity, ProcessPriorityClass.Idle);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.NoOp);
        Assert.Empty(_processes.PriorityLog);
    }

    [Fact]
    public void A_write_that_does_not_stick_is_a_failure_not_a_success()
    {
        // SetPriorityClass can report success while the kernel declines or adjusts the request. Without
        // the verify re-read this would be journalled as applied, and restore would later write a
        // priority the process never actually had.
        var app = _processes.Add("app", @"C:\Apps\App\app.exe", priority: ProcessPriorityClass.Normal);
        _processes.IgnorePriorityWrites.Add(app.Identity.Pid);

        var outcome = ThrottlerWith().Throttle(app.Identity, ProcessPriorityClass.Idle);

        Assert.False(outcome.Succeeded);
        Assert.Contains("reads Normal", outcome.Detail);
    }

    [Fact]
    public void The_host_application_and_all_its_processes_are_refused()
    {
        const string hostImage = @"C:\Users\someone\AppData\Local\HostApp\host.exe";
        var sibling = _processes.Add("host", hostImage, priority: ProcessPriorityClass.Normal);
        var throttler = ThrottlerWith(
            hostImages: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hostImage });

        var outcome = throttler.Throttle(sibling.Identity, ProcessPriorityClass.Idle);

        Assert.False(outcome.Succeeded);
        Assert.Contains("launched it", outcome.Detail);
        Assert.Empty(_processes.PriorityLog);
    }

    [Fact]
    public void Nothing_is_throttled_while_a_game_is_running()
    {
        var app = _processes.Add("app", @"C:\Apps\App\app.exe");
        _processes.Add("eldenring", @"D:\SteamLibrary\steamapps\common\Elden Ring\Game\eldenring.exe");

        var outcome = ThrottlerWith().Throttle(app.Identity, ProcessPriorityClass.Idle);

        Assert.False(outcome.Succeeded);
        Assert.Contains("cannot be undone", outcome.Detail);
    }

    [Fact]
    public void A_recycled_pid_is_not_throttled_in_place_of_the_original()
    {
        var original = _processes.Add("app", @"C:\Apps\App\app.exe", priority: ProcessPriorityClass.Normal);
        var replacement = _processes.Recycle(original.Identity, "notepad", @"C:\Windows\System32\notepad.exe");

        var outcome = ThrottlerWith().Throttle(original.Identity, ProcessPriorityClass.Idle);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ProcessPriorityClass.Normal, _processes.Query(replacement.Identity).PriorityClass);
    }

    [Fact]
    public void Restoring_a_process_that_exited_is_a_success_with_nothing_to_do()
    {
        var app = _processes.Add("app", @"C:\Apps\App\app.exe", priority: ProcessPriorityClass.Normal);
        var applied = ThrottlerWith().Throttle(app.Identity, ProcessPriorityClass.Idle);
        _processes.Exit(app.Identity);

        var restored = ThrottlerWith().Restore(app.Identity, applied.Prior!.Value);

        Assert.True(restored.Succeeded);
        Assert.True(restored.NoOp);
    }

    [Fact]
    public void A_process_above_the_ceiling_is_not_throttled_at_all()
    {
        // Do not create an obligation that cannot be discharged. Lowering a realtime process would work
        // perfectly well, and then restore would have to raise it back past the ceiling - which Quiesce
        // will not do, because assigning that class starves the compositor and the audio graph.
        // BannedSymbols makes the class unnameable here, so the value is constructed by number.
        const ProcessPriorityClass aboveTheCeiling = (ProcessPriorityClass)0x00000100;
        var app = _processes.Add("app", @"C:\Apps\App\app.exe", priority: aboveTheCeiling);

        var outcome = ThrottlerWith().Throttle(app.Identity, ProcessPriorityClass.Idle);

        Assert.False(outcome.Succeeded);
        Assert.Contains("above the AboveNormal ceiling", outcome.Detail);
        Assert.Empty(_processes.PriorityLog);
    }

    [Fact]
    public void Restore_refuses_a_recorded_prior_above_the_ceiling()
    {
        // Throttle refuses to create this obligation, so no journal this build writes can ask for it - but
        // a journal is a file that outlives the build that wrote it, and "restore whatever the record says"
        // would turn an edited record into an arbitrary-priority primitive.
        const ProcessPriorityClass aboveTheCeiling = (ProcessPriorityClass)0x00000100;
        var app = _processes.Add("app", @"C:\Apps\App\app.exe", priority: ProcessPriorityClass.Idle);

        var outcome = ThrottlerWith().Restore(app.Identity, aboveTheCeiling);

        Assert.False(outcome.Succeeded);
        Assert.Contains("Restart the process", outcome.Detail);
        Assert.Equal(ProcessPriorityClass.Idle, _processes.Query(app.Identity).PriorityClass);
    }

    [Fact]
    public void The_prior_is_handed_to_the_caller_before_the_write_happens()
    {
        // The write-ahead hook. The prior is read inside Throttle, so a caller that has to make it durable
        // first would otherwise read the priority a second time - and journal an answer the write never
        // raced against.
        var app = _processes.Add("app", @"C:\Apps\App\app.exe", priority: ProcessPriorityClass.Normal);
        var observed = new List<string>();
        _processes.BeforePriorityWrite = () => observed.Add("write");

        ThrottlerWith().Throttle(
            app.Identity,
            ProcessPriorityClass.Idle,
            beforeWrite: prior => observed.Add($"hook:{prior}"));

        Assert.Equal(["hook:Normal", "write"], observed);
    }

    [Fact]
    public void The_write_ahead_hook_does_not_fire_when_nothing_will_be_written()
    {
        // A refusal or a no-op must not make a caller journal a change that never happens: the journal
        // would then owe a restore for a process Quiesce never touched.
        var refused = _processes.Add("host", @"C:\Apps\Host\host.exe", priority: ProcessPriorityClass.Normal);
        var already = _processes.Add("app", @"C:\Apps\App\app.exe", priority: ProcessPriorityClass.Idle);
        var fired = new List<string>();

        var throttler = ThrottlerWith(
            hostImages: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Apps\Host\host.exe" });

        throttler.Throttle(refused.Identity, ProcessPriorityClass.Idle, beforeWrite: _ => fired.Add("refused"));
        throttler.Throttle(already.Identity, ProcessPriorityClass.Idle, beforeWrite: _ => fired.Add("noop"));

        Assert.Empty(fired);
    }

    [Fact]
    public void Restore_does_not_re_run_the_class_guardrails()
    {
        // Quiesce is strict about creating obligations and never refuses to discharge one. If a game
        // launches between apply and restore, refusing to put a priority back would leave the process
        // throttled with no path to recovery. The identity check is the guard that matters on this path.
        var app = _processes.Add("app", @"C:\Apps\App\app.exe", priority: ProcessPriorityClass.Normal);
        var throttler = ThrottlerWith();
        var applied = throttler.Throttle(app.Identity, ProcessPriorityClass.Idle);

        _processes.Add("eldenring", @"D:\SteamLibrary\steamapps\common\Elden Ring\Game\eldenring.exe");

        var restored = throttler.Restore(app.Identity, applied.Prior!.Value);

        Assert.True(restored.Succeeded);
        Assert.Equal(ProcessPriorityClass.Normal, _processes.Query(app.Identity).PriorityClass);
    }
}
