using System.Diagnostics;
using Quiesce.Core;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// The refusal where being wrong is unrecoverable, and the only one that could not previously fire.
/// </summary>
/// <remarks>
/// Driven through <see cref="ProcessCloser.WouldRefuse"/> rather than against the guard directly: the
/// guard is internal, and what matters is that the two public callers actually consult it. The throttler
/// is covered too, because these two files each used to carry their own copy of the rule.
/// <para>
/// Joins <see cref="ProcessAncestryCollection"/> because <see cref="ProcessClassifier.ForMachine"/> is not
/// used here but <c>ProcessAncestry.OverrideForTests</c> still has to be pinned: the fake PIDs start at
/// 1000 and can collide with the real ancestry chain of the test host, which would classify a fake process
/// as <c>SelfOrLauncherOfSelf</c> and refuse it for the wrong reason.
/// </para>
/// </remarks>
[Collection(ProcessAncestryCollection.Name)]
public class GameLiveGuardTests : IDisposable
{
    private readonly FakeProcessControl _processes = new();
    private readonly FakeServiceControl _services = new();

    public GameLiveGuardTests() => ProcessAncestry.OverrideForTests = new HashSet<int>();

    public void Dispose()
    {
        ProcessAncestry.OverrideForTests = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>A closer wired the way the engine wires it, with a real allowlist so both halves can run.</summary>
    private ProcessCloser Closer(bool withServices = true) => new(
        _processes,
        new ProcessClassifier(
            gameDirectories: [@"D:\Games\Elden Ring"],
            serviceHostPids: null,
            selfHostImagePaths: null),
        withServices ? _services : null);

    private ProcessSnapshot AddOrdinary(string name = "comet", int pid = 4100) =>
        _processes.Add(
            name,
            $@"C:\Users\t\AppData\Local\Perplexity\Comet\Application\{name}.exe",
            pid,
            hasWindow: true);

    [Fact]
    public void A_demand_started_anti_cheat_that_is_running_refuses_the_close()
    {
        // The case that could never fire before. EasyAntiCheat_EOS is DEMAND_START on this machine
        // (measured), so a game launcher started it - which is the fact being inferred.
        _services.Add("EasyAntiCheat_EOS", e =>
        {
            e.StartType = ServiceStartType.Manual;
            e.RunState = ServiceRunState.Running;
        });

        var refused = Closer().WouldRefuse(AddOrdinary(), out var reason);

        Assert.True(refused);
        Assert.Contains("EasyAntiCheat_EOS", reason, StringComparison.Ordinal);
        Assert.Contains("ban cannot be undone", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_installed_but_stopped_anti_cheat_permits_the_close()
    {
        // Which is the state it is in for most of a machine's life. Refusing here would make the product
        // permanently inert on any machine with a protected game installed - not one that is running.
        _services.Add("EasyAntiCheat_EOS", e =>
        {
            e.StartType = ServiceStartType.Manual;
            e.RunState = ServiceRunState.Stopped;
        });

        Assert.False(Closer().WouldRefuse(AddOrdinary(), out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void An_auto_start_anti_cheat_is_not_treated_as_evidence()
    {
        // The Vanguard-shaped trap, generalised. A service Windows started at boot is running for reasons
        // that have nothing to do with a game, so its running state carries no information. Including it
        // would refuse every close forever on exactly the machines this tool is aimed at.
        //
        // This is a deliberate FALSE NEGATIVE and it is the documented trade-off: if an anti-cheat is
        // auto-start and a game really is live, the close proceeds.
        _services.Add("EasyAntiCheat_EOS", e =>
        {
            e.StartType = ServiceStartType.Automatic;
            e.RunState = ServiceRunState.Running;
        });

        Assert.False(Closer().WouldRefuse(AddOrdinary(), out _));
    }

    [Theory]
    [InlineData(ServiceStartType.Boot)]
    [InlineData(ServiceStartType.System)]
    public void A_kernel_mode_anti_cheat_driver_is_not_treated_as_evidence(ServiceStartType startType)
    {
        // Belt and braces for the Vanguard exclusion. vgk is a boot-start driver, so even if someone
        // added it to AntiCheatGameSignalServices it could not produce a false signal - the predicate
        // requires Manual positively rather than merely "not Automatic", which was the first draft and
        // would have let both of these through.
        _services.Add("EasyAntiCheat_EOS", e =>
        {
            e.StartType = startType;
            e.RunState = ServiceRunState.Running;
        });

        Assert.False(Closer().WouldRefuse(AddOrdinary(), out _));
    }

    [Fact]
    public void Riot_Vanguard_is_not_in_the_signal_list_at_all()
    {
        // Asserted on the list, not on behaviour, because the reason is a fact about Vanguard rather than
        // about Quiesce: vgc and vgk start at boot by design and run permanently wherever Valorant is
        // installed. ProcessCloser's own remark already warns against exactly this mistake for launcher
        // PROCESSES; this is the same mistake one layer down. Vanguard is still never touched - it is just
        // not evidence that a game is on screen.
        Assert.DoesNotContain("vgc", Guardrails.AntiCheatGameSignalServices);
        Assert.DoesNotContain("vgk", Guardrails.AntiCheatGameSignalServices);
    }

    [Fact]
    public void Every_signal_service_is_also_on_the_never_touch_list()
    {
        // Two lists, one machine. A service Quiesce reads as "a game is live" must also be one Quiesce
        // refuses to stop - otherwise there is a configuration in which it would stop the very thing it
        // uses as its evidence, and then permit every close.
        Assert.All(
            Guardrails.AntiCheatGameSignalServices,
            service => Assert.True(
                Guardrails.IsServiceProtected(service),
                $"{service} is read as evidence of a live game but is not protected from being stopped"));
    }

    [Fact]
    public void With_no_service_layer_the_anti_cheat_half_cannot_fire()
    {
        // Documented rather than treated as a bug: the engine passes services through, but the tests that
        // only exercise the process layer pass null, and so would any future caller that forgot. The guard
        // degrades to the class-based half, which is the state the whole product was in.
        _services.Add("EasyAntiCheat_EOS", e =>
        {
            e.StartType = ServiceStartType.Manual;
            e.RunState = ServiceRunState.Running;
        });

        Assert.False(Closer(withServices: false).WouldRefuse(AddOrdinary(), out _));
    }

    [Fact]
    public void A_running_game_still_refuses_by_class_when_the_allowlist_knows_it()
    {
        // The original check, kept and still exercised. It is inert in production only because every
        // production call site passes gameDirectories: null - the rule itself works, and it becomes the
        // real answer when game discovery lands.
        _processes.Add("eldenring", @"D:\Games\Elden Ring\Game\eldenring.exe", 9001, hasWindow: true);

        var refused = Closer().WouldRefuse(AddOrdinary(), out var reason);

        Assert.True(refused);
        Assert.Contains("eldenring", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_throttler_refuses_for_the_same_reason_in_the_same_words()
    {
        // These two files each held their own copy of the rule AND its sentence. This asserts they cannot
        // drift apart again: same input, byte-identical refusal.
        _services.Add("BEService", e =>
        {
            e.StartType = ServiceStartType.Manual;
            e.RunState = ServiceRunState.Running;
        });

        var live = AddOrdinary();
        var classifier = new ProcessClassifier(
            gameDirectories: [@"D:\Games\Elden Ring"],
            serviceHostPids: null,
            selfHostImagePaths: null);

        var closerRefused = new ProcessCloser(_processes, classifier, _services)
            .WouldRefuse(live, out var closerReason);
        var throttlerRefused = new ProcessThrottler(_processes, classifier, _services)
            .WouldRefuse(live, ProcessPriorityClass.BelowNormal, out var throttlerReason);

        Assert.True(closerRefused);
        Assert.True(throttlerRefused);
        Assert.Equal(closerReason, throttlerReason);
    }
}
