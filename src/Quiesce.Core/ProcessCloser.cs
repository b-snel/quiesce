using Quiesce.Core.Platform;

namespace Quiesce.Core;

/// <summary>
/// Closes processes gracefully, or declines and says why. There is no force path.
/// </summary>
/// <remarks>
/// <para>
/// Closing is the one thing Quiesce does that it cannot undo, and that asymmetry is deliberate
/// rather than an oversight. Relaunching an application afterwards would mean guessing its working
/// directory, its command line and whether a second instance is even legal — and for a browser it
/// would restore the process without the tabs, which looks more like data loss than a restore.
/// Restore therefore <em>lists</em> what was closed and leaves reopening to the user.
/// </para>
/// <para>
/// Because of that, the bar for closing anything is high: the class must permit it, the identity must
/// still match, and no protected game may be running.
/// </para>
/// </remarks>
public sealed class ProcessCloser
{
    private readonly IProcessControl _processes;
    private readonly ProcessClassifier _classifier;

    public ProcessCloser(IProcessControl processes, ProcessClassifier classifier)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    /// <summary>The default grace period. A save prompt will consume all of it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Asks one process to close.
    /// </summary>
    /// <remarks>
    /// Every guardrail is re-evaluated here rather than trusted from plan time. That is the M4 lesson
    /// restated for processes: the machine is live, and the gap between planning and acting is exactly
    /// where a game gets launched or a PID gets recycled. The check that protects the machine is the
    /// second one.
    /// </remarks>
    public ProcessCloseOutcome Close(ProcessIdentity identity, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var live = _processes.Query(identity);

        if (!live.Present)
        {
            return new ProcessCloseOutcome
            {
                Identity = identity,
                ImageName = live.ImageName,
                Result = ProcessCloseResult.AlreadyGone,
                Detail = "already exited before Quiesce asked",
            };
        }

        if (WouldRefuse(live, out var reason))
        {
            return new ProcessCloseOutcome
            {
                Identity = identity,
                ImageName = live.ImageName,
                ImagePath = live.ImagePath,
                Result = ProcessCloseResult.Refused,
                Detail = reason,
            };
        }

        var result = _processes.TryClose(identity, timeout ?? DefaultTimeout, out var diagnosis);

        return new ProcessCloseOutcome
        {
            Identity = identity,
            ImageName = live.ImageName,
            ImagePath = live.ImagePath,
            Result = result,
            Detail = diagnosis,
        };
    }

    /// <summary>
    /// Decides whether this process may be closed at all, and says why not.
    /// </summary>
    /// <remarks>
    /// Public so the plan can ask the question without mutating anything. Closing has no dry run — asking
    /// <em>is</em> the action — so a plan that wanted to show the user "this one will be declined, and
    /// here is the reason" would otherwise have to reimplement this rule, and the copy would drift.
    /// <see cref="Close"/> calls it again on a freshly re-read snapshot, because the gap between planning
    /// and acting is exactly where a game gets launched.
    /// </remarks>
    public bool WouldRefuse(ProcessSnapshot live, out string reason)
    {
        ArgumentNullException.ThrowIfNull(live);

        var cls = _classifier.Classify(live);

        if (cls is not (ProcessClass.Ordinary or ProcessClass.Browser))
        {
            reason = cls switch
            {
                // Added when a process group first made this reachable from the catalog: the class existed
                // and the throttler explained it properly, but this switch had no arm for it, so the
                // refusal fell through to the generic "not in a class Quiesce will close". The whole point
                // of giving self-protection its own class is that the user gets the true reason, and a
                // generic one here reads like the app being arbitrary rather than the app protecting the
                // process driving the change.
                ProcessClass.SelfOrLauncherOfSelf =>
                    $"{live.ImageName} is Quiesce itself or part of what launched it. Closing it would kill " +
                    "the process performing this change and strand the journal mid-apply.",
                ProcessClass.NeverTouch =>
                    $"{live.ImageName} is on the never-touch list, or its identity could not be " +
                    "established well enough to act on safely.",
                ProcessClass.ServiceHost =>
                    $"{live.ImageName} hosts one or more Windows services. Services are managed through " +
                    "the service layer, which has its own guardrails; closing the host process would " +
                    "bypass all of them.",
                ProcessClass.LauncherOrAntiCheat =>
                    $"{live.ImageName} is part of a game launcher or an anti-cheat system.",
                ProcessClass.Game =>
                    $"{live.ImageName} is a game.",
                _ => $"{live.ImageName} is not in a class Quiesce will close.",
            };

            return true;
        }

        // Deferred from the M4 review and closed here, because this is the first code that mutates
        // process state. A protected game can start at any moment - including during a long apply, if
        // the user alt-tabs while the UI says "applying" - and interfering with anything near a
        // running kernel anti-cheat is a hardware-ban vector, not merely a stability risk. An EAC ban
        // propagates to every EAC title tied to the hardware, so this is the one refusal where the
        // downside of being wrong is unrecoverable.
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
    /// True when a game is actually running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately keyed on <see cref="ProcessClass.Game"/> ALONE, not on
    /// <see cref="ProcessClass.LauncherOrAntiCheat"/>. Blocking on launchers looks more cautious and
    /// would make the entire feature inert: Steam, the Epic helper and Riot Vanguard run essentially
    /// permanently on a gaming machine — Vanguard starts at boot by design — so any of them present
    /// would refuse every close forever, on every machine this tool is aimed at. Those components are
    /// never touched themselves; their mere presence is not a hazard.
    /// </para>
    /// <para>
    /// Known limitation, worth stating rather than hiding: a game that is not under any discovered
    /// game directory classifies as Ordinary and will not trip this check. The guard is only as good
    /// as the allowlist feeding the classifier.
    /// </para>
    /// <para>
    /// Re-enumerated on every call rather than cached. Caching would defeat the point — the scenario
    /// is a game launching <em>during</em> a long apply — and the cost is a few hundred milliseconds
    /// across an entire engage, against a per-process close timeout measured in seconds.
    /// </para>
    /// </remarks>
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
}
