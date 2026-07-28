using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// The fake itself, because one of its behaviours made a real test fail for an invented reason.
/// </summary>
/// <remarks>
/// Testing a test double is normally waste. This one earns it: the suite has exactly one source of
/// process identity, every process-layer test depends on it, and a silent collision there makes an
/// assertion pass or fail on the runner's own process id.
/// </remarks>
public class FakeProcessControlTests
{
    [Fact]
    public void An_auto_assigned_pid_never_collides_with_an_explicitly_set_one()
    {
        // The exact shape of the observed flake, forced rather than waited for: add at the explicit pid
        // that the OLD assignment would have handed to the next auto-assigned process. Before the fix,
        // "second" overwrote "first" - the count did not grow, so "third" then overwrote "second", and
        // three Adds left one entry.
        //
        // 1001 is not arbitrary: with one process present, _byPid.Count + 1000 == 1001.
        var processes = new FakeProcessControl();

        processes.Add("first", @"C:\A\first.exe", pid: 1001);
        processes.Add("second", @"C:\B\second.exe");
        processes.Add("third", @"C:\C\third.exe");

        var names = processes.Enumerate().Select(p => p.ImageName).OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(["first", "second", "third"], names);
    }

    [Fact]
    public void Every_auto_assigned_pid_is_distinct_across_many_adds()
    {
        var processes = new FakeProcessControl();

        for (var i = 0; i < 50; i++)
        {
            processes.Add($"p{i}", $@"C:\P\p{i}.exe");
        }

        var pids = processes.Enumerate().Select(p => p.Identity.Pid).ToList();

        Assert.Equal(50, pids.Count);
        Assert.Equal(50, pids.Distinct().Count());
    }

    [Fact]
    public void Recycling_a_pid_still_reuses_it_deliberately()
    {
        // The one case where reusing a pid IS the point: Windows recycles them, and Query must report
        // Present = false for the old identity because the creation time no longer matches. The fix to
        // auto-assignment must not have broken this.
        var processes = new FakeProcessControl();
        var original = processes.Add("gone", @"C:\A\gone.exe", pid: 4242);

        var replacement = processes.Recycle(original.Identity, "different", @"C:\B\different.exe");

        Assert.Equal(4242, replacement.Identity.Pid);
        Assert.NotEqual(original.Identity.CreatedUtcTicks, replacement.Identity.CreatedUtcTicks);
        Assert.False(processes.Query(original.Identity).Present);
    }
}
