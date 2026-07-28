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

    private readonly GameLiveGuard _gameLive;

    /// <param name="services">
    /// The SCM, for the anti-cheat half of <see cref="GameLiveGuard"/>. Optional, and null means that
    /// half cannot fire.
    /// </param>
    public ProcessThrottler(IProcessControl processes, ProcessClassifier classifier, IServiceControl? services = null)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _gameLive = new GameLiveGuard(_processes, _classifier, services);
    }

    /// <summary>
    /// Lowers one process to <paramref name="target"/>, capturing what it was first.
    /// </summary>
    /// <param name="beforeWrite">
    /// Invoked with the captured prior class immediately before the write, and only when a write is
    /// actually going to happen.
    /// </param>
    /// <remarks>
    /// <paramref name="beforeWrite"/> is the write-ahead hook. The engine must make the prior durable
    /// before the mutation, and the prior is read here — so without a hook the engine would have to read
    /// the priority itself, journal that, and then let this method read it a second time. Two reads means
    /// two answers are possible, and the one the journal recorded would not be the one the write raced
    /// against. The registry and service paths avoid this only because the engine performs those writes
    /// itself; this gives the process path the same guarantee.
    /// </remarks>
    public ProcessThrottleOutcome Throttle(
        ProcessIdentity identity,
        ProcessPriorityClass target,
        Action<ProcessPriorityClass>? beforeWrite = null)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var live = _processes.Query(identity);
        if (!live.Present)
        {
            return Fail(identity, live, "no longer running");
        }

        if (WouldRefuse(live, target, out var reason))
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

        beforeWrite?.Invoke(prior);

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

        // The one thing restore will not do. Throttle refuses to lower a process from above the ceiling
        // for exactly this reason, so a journal written by this build cannot ask for it - but a journal
        // is a file on disk that outlives the build that wrote it, and "restore whatever the record says"
        // would make an edited record an arbitrary-priority primitive. Not a permanent wedge either: the
        // step reverts cleanly the moment the process exits, which also clears the throttle.
        if (!CanRestore(prior))
        {
            return Fail(
                identity,
                live,
                $"the recorded prior class {prior} is above the {Guardrails.MaxAssignablePriority} ceiling " +
                "Quiesce will never assign. Restart the process to clear the throttle.");
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

    /// <summary>
    /// Decides whether this process may be throttled to <paramref name="target"/>, and says why not.
    /// </summary>
    /// <remarks>
    /// Public so the plan can ask without writing anything, and so the reason the preflight list shows is
    /// produced by the code that will act rather than by a second copy of the rule that could drift from
    /// it. <see cref="Throttle"/> calls it again on a freshly re-read snapshot.
    /// </remarks>
    public bool WouldRefuse(ProcessSnapshot live, ProcessPriorityClass target, out string reason)
    {
        ArgumentNullException.ThrowIfNull(live);

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

        // Do not create an obligation that cannot be discharged. A process already above the ceiling -
        // realtime, in practice - could be lowered perfectly well, and then restore would have to write
        // that class back. Quiesce will not: BannedSymbols makes the realtime class unnameable in this
        // codebase precisely because assigning it starves the compositor and the audio graph, and a
        // restore is still an assignment. Refusing the throttle is the only answer that leaves the
        // machine recoverable, and it is the same rule Restore relies on - see the remarks there.
        if (!CanRestore(live.PriorityClass))
        {
            reason =
                $"{live.ImageName} is running at a priority class above the {Guardrails.MaxAssignablePriority} " +
                "ceiling. Quiesce could lower it but would then have to raise it back past its own ceiling " +
                "to restore it, so it leaves the process alone instead.";
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

        // One implementation shared with ProcessCloser. These two files each carried their own identical
        // copy of this rule and its sentence - the rule with the highest cost of being wrong, duplicated.
        if (_gameLive.IsLive(out var gameReason))
        {
            reason = gameReason;
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

    /// <summary>
    /// True when <paramref name="priority"/> is a class Quiesce is able to write back.
    /// </summary>
    /// <remarks>
    /// Exactly the classes the rank table names, which is what makes the rule self-maintaining: a class
    /// added by a future Windows release ranks highest and so is not restorable, without this needing to
    /// know what it was.
    /// </remarks>
    public static bool CanRestore(ProcessPriorityClass priority) => Rank(priority) != int.MaxValue;

    /// <summary>
    /// True when <paramref name="current"/> is already at or below <paramref name="target"/>, so a
    /// throttle to that target has nothing to do.
    /// </summary>
    /// <remarks>
    /// Public because the plan needs it, and it must be asked BEFORE the refusal check. "Already lower
    /// than asked" and "would be a raise" are the same condition — a process at Idle is both already
    /// throttled and impossible to move to BelowNormal without raising it — and only the first is a true
    /// description. Getting this backwards reported every already-throttled process as a guardrail
    /// refusal, which is precisely the mistake the registry path already documents in reverse.
    /// </remarks>
    public static bool IsAtOrBelow(ProcessPriorityClass current, ProcessPriorityClass target) =>
        Rank(current) <= Rank(target);

    /// <summary>Maps a catalog throttle level onto the priority class it names.</summary>
    public static ProcessPriorityClass ToPriorityClass(Catalog.ThrottleLevel level) => level switch
    {
        Catalog.ThrottleLevel.BelowNormal => ProcessPriorityClass.BelowNormal,
        Catalog.ThrottleLevel.Idle => ProcessPriorityClass.Idle,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown throttle level."),
    };

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
