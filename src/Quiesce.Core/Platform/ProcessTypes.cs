using System.Diagnostics;

namespace Quiesce.Core.Platform;

/// <summary>
/// Identifies a specific running process instance, not merely a PID.
/// </summary>
/// <remarks>
/// Windows recycles PIDs aggressively, and the window Quiesce cares about is exactly the one where
/// recycling bites: engage closes or throttles a process, the user plays for three hours, and restore
/// then looks the PID up again. By then that number can belong to something else entirely — and
/// restoring "the prior priority class" onto an unrelated process is a silent, invisible mutation of
/// a program Quiesce was never asked to touch.
/// <para>
/// Creation time is the disambiguator because it is immutable for the life of the process and cheap
/// to read. Two processes can share a PID across time but not a (PID, creation time) pair.
/// </para>
/// </remarks>
public sealed record ProcessIdentity
{
    public required int Pid { get; init; }

    /// <summary>Process creation time in UTC ticks. Immutable for the process's lifetime.</summary>
    public required long CreatedUtcTicks { get; init; }

    public override string ToString() => $"pid {Pid} (started {new DateTime(CreatedUtcTicks, DateTimeKind.Utc):HH:mm:ss})";
}

/// <summary>What Quiesce is permitted to do with a process, decided before anything is attempted.</summary>
public enum ProcessClass
{
    /// <summary>Nothing. Shell, system-critical, compositor, audio graph, UI hosts.</summary>
    NeverTouch,

    /// <summary>Nothing. A launcher, its overlay, or an anti-cheat component.</summary>
    LauncherOrAntiCheat,

    /// <summary>
    /// Nothing, at the process layer. Hosts one or more Windows services.
    /// </summary>
    /// <remarks>
    /// Managed through the service layer or not at all. Closing or throttling a service's host
    /// process walks straight past every M4 guardrail — the tier-0 list, the co-tenancy check and the
    /// remote-session lock are all keyed on service names, and none of them can see a request aimed
    /// at a PID. <c>svchost.exe</c> is the sharp case: it hosts <c>DcomLaunch</c>, which is tier-0 as
    /// a service, while the process itself would otherwise look entirely ordinary. On the development
    /// machine this was masked unelevated, because those processes deny their image path and were
    /// already refused for that reason — elevated, they would have looked like fair game.
    /// </remarks>
    ServiceHost,

    /// <summary>A game. Never touched, and its presence changes what Quiesce will do elsewhere.</summary>
    Game,

    /// <summary>A browser. Closed by default, keep-alive available as a toggle.</summary>
    Browser,

    /// <summary>Everything else. Eligible for the close ladder or a throttle if a profile says so.</summary>
    Ordinary,
}

/// <summary>
/// One process as observed, with the facts the later stages need and nothing more.
/// </summary>
public sealed record ProcessSnapshot
{
    public required ProcessIdentity Identity { get; init; }

    /// <summary>Image name without the <c>.exe</c> extension, as the guardrail lists spell it.</summary>
    public required string ImageName { get; init; }

    /// <summary>
    /// Full path to the executable, or null when it could not be read.
    /// </summary>
    /// <remarks>
    /// Null is common and not an error: protected processes and processes owned by other users deny
    /// the query even to an elevated caller. Targeting is path-based, so a null path means Quiesce
    /// cannot confirm what this program is — which must resolve to leaving it alone, never to
    /// falling back on the image name. "A process called chrome.exe somewhere unknown" is precisely
    /// the case name-based matching gets wrong.
    /// </remarks>
    public string? ImagePath { get; init; }

    public required int SessionId { get; init; }

    public required ProcessPriorityClass PriorityClass { get; init; }

    /// <summary>
    /// True when the process owns at least one top-level window.
    /// </summary>
    /// <remarks>
    /// Governs whether the close ladder has anything to talk to: WM_CLOSE needs a window. A
    /// windowless background process cannot be asked politely to exit, and Quiesce does not have a
    /// less polite option.
    /// </remarks>
    public required bool HasVisibleWindow { get; init; }

    /// <summary>
    /// False when the process's creation time could not be read, so it has no stable identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The most protected processes — <c>csrss</c>, <c>lsass</c>, <c>winlogon</c>, <c>services</c>,
    /// <c>smss</c> — deny <c>StartTime</c> to an unelevated caller. An earlier draft dropped those
    /// from the inventory entirely, which was safe (nothing can act on a process it cannot see) but
    /// dishonest in two ways: the reported process count was short by about ten with nothing saying
    /// so, and the inventory silently differed between an elevated and an unelevated run.
    /// </para>
    /// <para>
    /// They are now listed and flagged instead. Without a creation time there is no recycling-proof
    /// identity, so nothing can be journalled against them and the classifier must treat them as
    /// untouchable — which they already are by name. Visible and refused beats absent.
    /// </para>
    /// </remarks>
    public bool CreationTimeKnown { get; init; } = true;

    /// <summary>False when the process has exited since it was enumerated.</summary>
    public bool Present { get; init; } = true;
}

/// <summary>
/// Read access to live processes. The mutation surface arrives with the close ladder and throttle.
/// </summary>
/// <remarks>
/// A seam rather than direct <see cref="Process"/> calls, for the same reason
/// <see cref="IServiceControl"/> is: the interesting cases — a process that exits mid-operation, a
/// path that cannot be read, a recycled PID — are miserable to arrange against the real OS and
/// trivial against a fake.
/// </remarks>
public interface IProcessControl
{
    /// <summary>
    /// Every process visible to this caller. Processes that vanish mid-enumeration are omitted
    /// rather than reported as errors — that is normal, not exceptional.
    /// </summary>
    IReadOnlyList<ProcessSnapshot> Enumerate();

    /// <summary>
    /// Re-reads one process by identity. Returns a snapshot with <see cref="ProcessSnapshot.Present"/>
    /// false when the PID is gone <em>or</em> when it now belongs to a different process.
    /// </summary>
    ProcessSnapshot Query(ProcessIdentity identity);
}
