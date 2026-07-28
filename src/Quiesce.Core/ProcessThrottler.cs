using System.Diagnostics;
using Quiesce.Core.Platform;

namespace Quiesce.Core;

/// <summary>The result of one throttle or one restore.</summary>
public sealed record ProcessThrottleOutcome
{
    public required ProcessIdentity Identity { get; init; }

    public required string ImageName { get; init; }

    public string? ImagePath { get; init; }

    /// <summary>
    /// The class the process had before Quiesce touched it. This is what restore writes back.
    /// </summary>
    /// <remarks>
    /// Captured per process rather than assumed. On the development machine the host application's 14
    /// processes sat at three different classes at once — Normal, Idle and AboveNormal — so restoring
    /// everything to Normal would have quietly promoted the idle ones and demoted the busy one, and a
    /// byte-level check would have called that a clean restore.
    /// </remarks>
    public ProcessPriorityClass? Prior { get; init; }

    public ProcessPriorityClass? Applied { get; init; }

    public required bool Succeeded { get; init; }

    /// <summary>True when the live state already matched the target, so nothing was written.</summary>
    public bool NoOp { get; init; }

    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Lowers process priority reversibly, capturing the prior class first.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="ProcessCloser"/>, and unlike closing this one genuinely round-trips:
/// priority is a single value, reading it is cheap, and writing it back is exact. That is why the plan
/// pairs "graceful close" with "reversible throttle" and rules suspension out entirely — a suspended
/// process is neither closed nor running, and nothing in Windows guarantees it resumes cleanly.
/// </remarks>
public sealed class ProcessThrottler
{
    private readonly IProcessControl _processes;
    private readonly ProcessClassifier _classifier;

    public ProcessThrottler(IProcessControl processes, ProcessClassifier classifier)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    /// <summary>
    /// Lowers one process to <paramref name="target"/>, capturing what it was first.
    /// </summary>
    public ProcessThrottleOutcome Throttle(ProcessIdentity identity, ProcessPriorityClass target)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var live = _processes.Query(identity);
        if (!live.Present)
        {
            return Fail(identity, live, "no longer running");
        }

        if (Refuse(live, target, out var reason))
        {
            return Fail(identity, live, reason);
        }

        // Already there. Reported as a no-op and NOT journalled as applied, so restore cannot later
        // "restore" a priority the user had set themselves.
        if (live.PriorityClass == target)
        {
            return new ProcessThrottleOutcome
            {
                Identity = identity,
                ImageName = live.ImageName,
                ImagePath = live.ImagePath,
                Prior = live.PriorityClass,
                Applied = target,
                Succeeded = true,
                NoOp = true,
                Detail = $"already at {target}",
            };
        }

        var prior = live.PriorityClass;

        if (!_processes.TrySetPriority(identity, target, out var diagnosis))
        {
            return Fail(identity, live, diagnosis);
        }

        return new ProcessThrottleOutcome
        {
            Identity = identity,
            ImageName = live.ImageName,
            ImagePath = live.ImagePath,
            Prior = prior,
            Applied = target,
            Succeeded = true,
        };
    }

    /// <summary>
    /// Writes a captured prior class back.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT re-run the class guardrails. Restore must always be able to discharge an
    /// obligation it created: if a process changed class between apply and restore — a game launched,
    /// say — refusing to put its priority back would leave it throttled with no path to recovery. The
    /// identity check is the guard that matters here, and it is unconditional.
    /// </remarks>
    public ProcessThrottleOutcome Restore(ProcessIdentity identity, ProcessPriorityClass prior)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var live = _processes.Query(identity);

        if (!live.Present)
        {
            // The process exited, or its PID was recycled. Either way there is nothing to restore and
            // nothing was left behind — a success, not a failure.
            return new ProcessThrottleOutcome
            {
                Identity = identity,
                ImageName = live.ImageName,
                Prior = prior,
                Succeeded = true,
                NoOp = true,
                Detail = "exited before restore; nothing to put back",
            };
        }

        if (live.PriorityClass == prior)
        {
            return new ProcessThrottleOutcome
            {
                Identity = identity,
                ImageName = live.ImageName,
                ImagePath = live.ImagePath,
                Prior = prior,
                Succeeded = true,
                NoOp = true,
                Detail = $"already back at {prior}",
            };
        }

        if (!_processes.TrySetPriority(identity, prior, out var diagnosis))
        {
            return Fail(identity, live, diagnosis);
        }

        return new ProcessThrottleOutcome
        {
            Identity = identity,
            ImageName = live.ImageName,
            ImagePath = live.ImagePath,
            Prior = prior,
            Applied = prior,
            Succeeded = true,
        };
    }

    private bool Refuse(ProcessSnapshot live, ProcessPriorityClass target, out string reason)
    {
        var cls = _classifier.Classify(live);

        if (cls is not (ProcessClass.Ordinary or ProcessClass.Browser))
        {
            reason = cls switch
            {
                ProcessClass.SelfOrLauncherOfSelf =>
                    $"{live.ImageName} is Quiesce itself or part of what launched it. Throttling it would " +
                    "starve the process driving this change.",
                ProcessClass.ServiceHost =>
                    $"{live.ImageName} hosts Windows services; throttling it would slow every service in " +
                    "it while bypassing the service guardrails entirely.",
                ProcessClass.NeverTouch => $"{live.ImageName} is on the never-touch list.",
                ProcessClass.LauncherOrAntiCheat => $"{live.ImageName} is launcher or anti-cheat software.",
                ProcessClass.Game => $"{live.ImageName} is a game.",
                _ => $"{live.ImageName} is not in a class Quiesce will throttle.",
            };

            return true;
        }

        // Quiesce only ever lowers. The ceiling in Guardrails makes RealTime unreachable, but the
        // stronger rule is the useful one: a "throttle" that raised anything would be doing the exact
        // thing the project says it will not do, and raising one process starves the compositor, the
        // audio graph and the input stack while an average-FPS counter goes up and hides it.
        if (Rank(target) > Rank(Guardrails.MaxAssignablePriority))
        {
            reason = $"{target} is above the {Guardrails.MaxAssignablePriority} ceiling Quiesce will never exceed.";
            return true;
        }

        if (Rank(target) > Rank(live.PriorityClass))
        {
            reason =
                $"{live.ImageName} is already at {live.PriorityClass}, which is lower than the requested " +
                $"{target}. Quiesce lowers priority and never raises it.";
            return true;
        }

        if (AnyGameRunning(out var game))
        {
            reason =
                $"{game} is running. Quiesce will not close or throttle anything while a game is live: " +
                "kernel anti-cheats treat activity around a protected process as suspicious, and a ban " +
                "cannot be undone. Close the game first.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Orders priority classes so "lower" and "higher" are comparable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The enum's numeric values are Win32 flag bits and are NOT in priority order — Idle is 64,
    /// Normal is 32, High is 128 — so comparing the enum directly would rank Idle above Normal and let
    /// a "throttle" quietly promote things.
    /// </para>
    /// <para>
    /// The top of the scale is deliberately unnamed. BannedSymbols makes referencing the realtime class
    /// a compile error, and the analyzer duly rejected an earlier version of this table that spelled it
    /// out. Ranking anything unrecognised as the highest possible value is the better answer anyway:
    /// the ceiling check then refuses it without this code needing to know what it was, and a class
    /// added by a future Windows release is refused by default rather than silently ranked as Normal.
    /// </para>
    /// </remarks>
    private static int Rank(ProcessPriorityClass priority) => priority switch
    {
        ProcessPriorityClass.Idle => 0,
        ProcessPriorityClass.BelowNormal => 1,
        ProcessPriorityClass.Normal => 2,
        ProcessPriorityClass.AboveNormal => 3,
        ProcessPriorityClass.High => 4,
        _ => int.MaxValue,
    };

    private bool AnyGameRunning(out string gameName)
    {
        foreach (var process in _processes.Enumerate())
        {
            if (_classifier.Classify(process) == ProcessClass.Game)
            {
                gameName = process.ImageName;
                return true;
            }
        }

        gameName = string.Empty;
        return false;
    }

    private static ProcessThrottleOutcome Fail(ProcessIdentity identity, ProcessSnapshot live, string detail) => new()
    {
        Identity = identity,
        ImageName = live.ImageName,
        ImagePath = live.ImagePath,
        Prior = live.Present ? live.PriorityClass : null,
        Succeeded = false,
        Detail = detail,
    };
}
