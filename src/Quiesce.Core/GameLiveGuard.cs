using Quiesce.Core.Platform;

namespace Quiesce.Core;

/// <summary>
/// Answers one question for both the closer and the throttler: is a protected game live right now?
/// </summary>
/// <remarks>
/// <para>
/// This is the refusal where being wrong is unrecoverable. Interfering with anything near a running
/// kernel anti-cheat is a hardware-ban vector, not merely a stability risk, and an EAC ban propagates to
/// every EAC title tied to the hardware. Every other guardrail in this product protects against a machine
/// that misbehaves until the next reboot; this one protects against an outcome with no remedy at all.
/// </para>
/// <para>
/// ONE COPY, and that is why the class exists. <c>ProcessCloser</c> and <c>ProcessThrottler</c> each held
/// their own identical <c>AnyGameRunning</c> and their own identical refusal sentence. Two copies of the
/// rule with the highest cost of being wrong is the shape this codebase already learned to distrust —
/// <c>ServicesPage</c> cross-checks itself against <c>Guardrails</c> at construction for exactly this
/// reason — and adding the anti-cheat signal would have made it three.
/// </para>
/// <para>
/// TWO INDEPENDENT SIGNALS, because the first one cannot fire. <see cref="ProcessClass.Game"/> is derived
/// from the game-directory allowlist, and every production call site constructs the classifier with
/// <c>gameDirectories: null</c> — documented at <c>CliEnvironment</c> and true of the GUI as well — so
/// until game discovery lands, the class-based half is dead code that reads as though it is not. The
/// service-based half is observable today. Both are kept: the first becomes real when discovery lands,
/// and the second catches the case the first never will, since an anti-cheat protects games installed
/// anywhere.
/// </para>
/// </remarks>
internal sealed class GameLiveGuard(
    IProcessControl processes,
    ProcessClassifier classifier,
    IServiceControl? services)
{
    /// <summary>
    /// True when something says a game is live, with the sentence to show the user.
    /// </summary>
    /// <remarks>
    /// Re-evaluated on every call and never cached. The scenario is a game starting DURING a long apply —
    /// the user alt-tabs while the UI says "applying" — so a cached answer is the answer to a question
    /// nobody asked. The cost is a process enumeration and up to three SCM queries per acted-on process,
    /// against a close timeout measured in seconds.
    /// </remarks>
    public bool IsLive(out string reason)
    {
        foreach (var process in processes.Enumerate())
        {
            if (classifier.Classify(process) == ProcessClass.Game)
            {
                reason =
                    $"{process.ImageName} is running. Quiesce will not close or throttle anything while a " +
                    "game is live: kernel anti-cheats treat activity around a protected process as " +
                    "suspicious, and a ban cannot be undone. Close the game first.";
                return true;
            }
        }

        if (AntiCheatStartedByAGame() is { } antiCheat)
        {
            reason =
                $"the {antiCheat} service is running, which means a protected game started it. Quiesce " +
                "will not close or throttle anything while a kernel anti-cheat is live: it treats activity " +
                "around a protected process as suspicious, and a ban cannot be undone. Close the game first.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// The name of a demand-started anti-cheat service that is running, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Queries a fixed three-name list rather than enumerating the SCM: the list is short,
    /// <see cref="IServiceControl.Query"/> already answers "not installed" without throwing, and this runs
    /// before every acted-on process rather than once per engage.
    /// </para>
    /// <para>
    /// A failure to read the SCM yields NO SIGNAL rather than a refusal, and that is the less safe
    /// direction, so it is said out loud: failing closed would let a transient SCM error block a Restore,
    /// and refusing to undo is never the safer error. The class-based sweep above is unaffected.
    /// </para>
    /// <para>
    /// Null when no service layer was wired at all. That is honest about the cost rather than pretending:
    /// with no SCM this half of the guard cannot fire, which is the state the whole product was in.
    /// </para>
    /// </remarks>
    private string? AntiCheatStartedByAGame()
    {
        if (services is null)
        {
            return null;
        }

        foreach (var candidate in Guardrails.AntiCheatGameSignalServices)
        {
            ServiceSnapshot snapshot;
            try
            {
                snapshot = services.Query(candidate);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            {
                continue;
            }

            if (Guardrails.IsAntiCheatGameSignal(snapshot))
            {
                return snapshot.Service;
            }
        }

        return null;
    }
}
