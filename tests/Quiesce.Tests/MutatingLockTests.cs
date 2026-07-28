using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// The cross-process single-writer lock every mutating path now takes.
/// </summary>
/// <remarks>
/// <para>
/// These tests take the REAL <c>Global\Quiesce.Mutating</c> mutex, because the name is the contract:
/// the whole point of promoting it out of the CLI is that the GUI and the CLI open the same one, and a
/// test against an injected name would pass just as happily if they opened two different mutexes.
/// </para>
/// <para>
/// Consequences, both handled. They are serialised into their own collection, because one of them holds
/// the mutex on a background thread for the duration of an assertion and xUnit parallelises test
/// classes — a concurrent <see cref="MutatingLock.TryRun{T}"/> anywhere else would observe "busy" and
/// fail for a reason unrelated to what it was testing. And every acquisition is released in a
/// <c>finally</c> on the thread that took it, since <see cref="Mutex.ReleaseMutex"/> throws from any
/// other: a leaked hold here would not fail this class, it would fail whatever ran next.
/// </para>
/// <para>
/// Do NOT run these while a real Quiesce is mid-apply on the same machine.
/// </para>
/// </remarks>
[Collection(MutatingLockCollection.Name)]
public class MutatingLockTests
{
    [Fact]
    public void The_body_runs_and_its_value_comes_back_when_the_lock_is_free()
    {
        var ran = 0;

        var acquired = MutatingLock.TryRun(
            () =>
            {
                ran++;
                return 42;
            },
            out var result);

        Assert.True(acquired);
        Assert.Equal(1, ran);
        Assert.Equal(42, result);
    }

    [Fact]
    public void A_lock_held_by_another_thread_refuses_and_the_body_never_runs()
    {
        // "Never runs" is the property that matters, not the return value. The GUI reports "nothing was
        // done" on this path, and that sentence has to be literally true - a body that ran and had its
        // result discarded would have already journaled, already closed the browser, already written
        // state.json.
        //
        // The holder MUST be another thread. A Win32 mutex is recursive for its owning thread, so a
        // holder on this thread would let TryRun straight through - see
        // The_lock_does_not_guard_against_the_same_thread_reentering.
        var ran = 0;
        using var taken = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        Exception? holderFailure = null;

        var holder = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(initiallyOwned: false, MutatingLock.Name);
                if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                {
                    holderFailure = new InvalidOperationException(
                        "precondition: could not take the lock, so something outside this test holds it");
                    return;
                }

                try
                {
                    taken.Set();
                    release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception ex)
            {
                holderFailure = ex;
                taken.Set();
            }
        });

        holder.Start();

        try
        {
            Assert.True(taken.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the holder thread never acquired the lock");
            Assert.Null(holderFailure);

            var acquired = MutatingLock.TryRun(
                () =>
                {
                    ran++;
                    return 42;
                },
                out var result);

            Assert.False(acquired);
            Assert.Equal(0, ran);
            Assert.Equal(0, result);
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(10));
        }

        // And the lock is usable again once the other thread let go.
        Assert.True(MutatingLock.TryRun(() => 7, out var afterRelease));
        Assert.Equal(7, afterRelease);
    }

    [Fact]
    public void The_lock_does_not_guard_against_the_same_thread_reentering()
    {
        // Documented rather than fixed, because it is a property of Win32 mutexes and not a defect: a
        // mutex is owned by a THREAD and is recursive for that thread, so a nested TryRun on one thread
        // succeeds twice. This is not the protection it looks like.
        //
        // Safe as the code stands, and the reason is worth writing down: the GUI takes this inside
        // Task.Run - a pool thread - exactly once per mutation, and the CLI takes it once per verb. There
        // is no nesting anywhere. What guards against the app starting a SECOND mutation while one is in
        // flight is App.Mutating, which is a different mechanism for a different problem. If a future
        // caller ever wraps one mutating operation inside another, this test is where to find out that
        // the mutex will not stop it.
        var inner = false;

        Assert.True(MutatingLock.TryRun(
            () => MutatingLock.TryRun(
                () =>
                {
                    inner = true;
                    return 1;
                },
                out _),
            out var nestedAcquired));

        Assert.True(nestedAcquired);
        Assert.True(inner);
    }

    [Fact]
    public void The_lock_is_released_after_the_body_throws()
    {
        // Without the finally in TryRun this is the shape of the bug: one failed apply, and the machine
        // refuses every subsequent mutation - including the Restore that would put it back - until the
        // process exits. The recovery paths would be locked out by the failure they exist to recover from.
        Assert.Throws<InvalidOperationException>(() =>
            MutatingLock.TryRun<int>(() => throw new InvalidOperationException("boom"), out _));

        Assert.True(MutatingLock.TryRun(() => 1, out var after));
        Assert.Equal(1, after);
    }

    [Fact]
    public void A_reference_type_body_returns_null_only_when_the_lock_was_held()
    {
        // The GUI distinguishes "busy" from "done" by null-checking the result, so this pins that a
        // successful run of a reference-returning body cannot itself produce null and be misread as busy.
        Assert.True(MutatingLock.TryRun(() => "done", out var value));
        Assert.Equal("done", value);
    }

    [Fact]
    public void The_lock_is_global_so_the_boot_recovery_task_in_session_zero_can_see_it()
    {
        // Asserted on the NAME because it is unobservable from a single session: a Local\ mutex would
        // pass every other test in this class and still leave the session-0 recovery task free to revert
        // while an interactive apply is running, which is the exact race the prefix exists to prevent.
        Assert.StartsWith(@"Global\", MutatingLock.Name, StringComparison.Ordinal);
    }
}
