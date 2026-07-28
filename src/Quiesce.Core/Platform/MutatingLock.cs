namespace Quiesce.Core.Platform;

/// <summary>
/// The single-writer lock every mutating path takes, across processes and sessions.
/// </summary>
/// <remarks>
/// <para>
/// <c>Global\</c>, not <c>Local\</c>: the boot-recovery task lives in session 0, and a session-local
/// mutex would be invisible to it — exactly the race that lets a recovery revert interleave with an
/// interactive apply.
/// </para>
/// <para>
/// Promoted out of the CLI, where it was a private const and a private helper, because until now the
/// GUI held NOTHING. It called the engine bare from two click handlers, so "another Quiesce process is
/// mutating the machine, refusing to run concurrently" was enforced against a second CLI invocation and
/// not against the window the user was actually looking at. That was survivable while the only way to
/// start a mutation was to click a button on a window that disables its own buttons; it stops being
/// survivable the moment a tray menu can start one, because a WPF modal dialog disables only its owner
/// window and the tray lives on its own message-only HWND.
/// </para>
/// <para>
/// This is the OUTER guard and not the only one. The journal's <c>.lock</c> file is the real
/// last-resort mutual exclusion — it is an exclusive <c>FileStream</c> with
/// <c>FileOptions.DeleteOnClose</c>, so it holds even against a process that never took this mutex, and
/// it is what makes an abandoned mutex safe to treat as acquired.
/// </para>
/// </remarks>
public static class MutatingLock
{
    /// <summary>The mutex name. Public so a diagnostic can look for it, never to be opened by hand.</summary>
    public const string Name = @"Global\Quiesce.Mutating";

    /// <summary>
    /// Runs <paramref name="body"/> holding the lock. Returns false, having done nothing, if it is held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WaitOne(TimeSpan.Zero)</c> — refuse immediately rather than queue. A caller that blocked would
    /// be a GUI that appears hung behind a dialog it cannot show, or a boot-recovery task that waits on
    /// an interactive session that may never finish.
    /// </para>
    /// <para>
    /// <see cref="AbandonedMutexException"/> counts as ACQUIRED. It means the previous holder died
    /// without releasing, which is precisely the crash the recovery path exists for; treating it as
    /// "still locked" would wedge the machine dirty with no way to run the thing that fixes it.
    /// </para>
    /// <para>
    /// CALLER THREAD AFFINITY IS LOAD-BEARING. A <see cref="Mutex"/> is owned by the thread that
    /// acquired it and <see cref="Mutex.ReleaseMutex"/> throws
    /// <see cref="ApplicationException"/> from any other. So a GUI caller must invoke this INSIDE its
    /// <c>Task.Run</c> body — not around the <c>await</c> — or the release lands on whatever thread the
    /// continuation resumed on, which for an <c>async void</c> handler is the UI thread.
    /// </para>
    /// </remarks>
    /// <returns>
    /// True when the lock was taken and <paramref name="body"/> ran; false when it was already held, in
    /// which case <paramref name="result"/> is unset and NOTHING happened. A bool rather than a sentinel
    /// value, because every sentinel is a value some future body could legitimately return.
    /// </returns>
    public static bool TryRun<T>(Func<T> body, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T result)
    {
        ArgumentNullException.ThrowIfNull(body);

        using var mutex = new Mutex(initiallyOwned: false, Name);

        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                result = default;
                return false;
            }

            result = body();
            return true;
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// The sentence every caller shows when the lock is held. One wording, not three.
    /// </summary>
    public const string BusyMessage =
        "Another Quiesce process is changing this machine right now. Nothing was done — " +
        "wait for it to finish and try again.";
}
