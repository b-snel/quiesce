using System.Diagnostics;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// Nothing. Quiesce itself, or a process in the chain that launched it.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NeverTouch"/> so the reason shown to the user is the true one rather
    /// than a generic refusal. Closing the launcher kills the process driving the apply and strands
    /// the journal; throttling it starves the apply with the apply.
    /// </remarks>
    SelfOrLauncherOfSelf,

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

    public ProcessPrior ToPrior() => new()
    {
        Pid = Identity.Pid,
        CreatedUtcTicks = Identity.CreatedUtcTicks,
        ImageName = ImageName,
        ImagePath = ImagePath,
        PriorityClass = PriorityClass.ToString(),
    };
}

/// <summary>
/// Everything about a process that a revert needs, captured before it is touched.
/// </summary>
/// <remarks>
/// Journalled, so it is read by the revert path — including the standalone one — and must stand alone
/// without the catalog. The image path is here because a closed process cannot be queried afterwards
/// and "which program did Quiesce close" is the whole value of the record.
/// </remarks>
public sealed record ProcessPrior
{
    [JsonPropertyName("pid")]
    public required int Pid { get; init; }

    /// <summary>
    /// Creation time, which together with the PID identifies the instance rather than the number.
    /// </summary>
    /// <remarks>
    /// Without it a restore three hours later could write a captured priority onto whatever process
    /// inherited the PID — a silent mutation of a program Quiesce was never asked to touch.
    /// </remarks>
    [JsonPropertyName("createdUtcTicks")]
    public required long CreatedUtcTicks { get; init; }

    [JsonPropertyName("imageName")]
    public required string ImageName { get; init; }

    [JsonPropertyName("imagePath")]
    public string? ImagePath { get; init; }

    /// <summary>
    /// The priority class as .NET spells it, stored as a string.
    /// </summary>
    /// <remarks>
    /// A string rather than the enum because the enum's values are Win32 flag bits — legible as names,
    /// meaningless as numbers in a journal someone has to read during a recovery. It also lets revert
    /// refuse a class it does not recognise instead of coercing it into a number that would silently
    /// change the process's priority.
    /// </remarks>
    [JsonPropertyName("priorityClass")]
    public string? PriorityClass { get; init; }

    public ProcessIdentity ToIdentity() => new() { Pid = Pid, CreatedUtcTicks = CreatedUtcTicks };

    /// <summary>
    /// Whether <paramref name="live"/> is the same program this record describes, started again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOT the same instance — deliberately the opposite question from <see cref="ToIdentity"/>. A
    /// relaunched application has a new PID and a new creation time, so
    /// <c>Query(prior.ToIdentity()).Present</c> is false for it forever. That is correct for a revert,
    /// which must never write a captured priority onto a process that merely inherited the number, and it
    /// is exactly why an application coming back after Quiesce closed it is invisible to the journal: there
    /// was no question the journal could ask that would notice.
    /// </para>
    /// <para>
    /// FULL IMAGE PATH, case-insensitive, and never the image name alone. The recorded path is the
    /// strongest fact the journal holds about what was closed, and a program with the same name somewhere
    /// else on disk is not the one Quiesce closed — the same rule
    /// <see cref="Catalog.ProcessOpSpec.Matches"/> enforces, for the same reason, and the reason this
    /// stays a journal-only comparison rather than reaching for the catalog: a revert must work from the
    /// records alone, and so must anything that decides what a revert will later have to undo.
    /// </para>
    /// <para>
    /// An unreadable path on either side is never a match. A close whose target could not be pathed should
    /// not have happened, and a live process whose path cannot be read is one Quiesce cannot identify — in
    /// both directions the answer to "is this the same program" is "cannot say", which must resolve to no.
    /// </para>
    /// </remarks>
    public bool IsSameProgram(ProcessSnapshot live)
    {
        ArgumentNullException.ThrowIfNull(live);

        return !string.IsNullOrWhiteSpace(ImagePath)
            && !string.IsNullOrWhiteSpace(live.ImagePath)
            && ImagePath.Equals(live.ImagePath, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>How a close attempt ended.</summary>
public enum ProcessCloseResult
{
    /// <summary>It exited within the timeout.</summary>
    Closed,

    /// <summary>It had already exited before Quiesce asked. Success, not failure.</summary>
    AlreadyGone,

    /// <summary>A guardrail refused before anything was attempted.</summary>
    Refused,

    /// <summary>
    /// It has no top-level window, so there is nothing to send a polite request to.
    /// </summary>
    /// <remarks>
    /// Not an error and not something to escalate against. Quiesce has no impolite option — a
    /// windowless background process simply cannot be asked to leave, and 263 of the 272 processes on
    /// the development machine are in this category.
    /// </remarks>
    NoWindow,

    /// <summary>
    /// It was asked and did not exit in time.
    /// </summary>
    /// <remarks>
    /// The overwhelmingly common cause is a modal "save your work?" prompt, which is the exact
    /// situation the graceful ladder exists to respect. The prompt is left on screen and the process
    /// is left running.
    /// </remarks>
    DeclinedToClose,
}

/// <summary>
/// The result of one close attempt, with enough detail to report it without re-reading the machine.
/// </summary>
/// <remarks>
/// Carries the image path because a closed process cannot be queried afterwards, and telling the user
/// <em>which program</em> was closed is the whole value of the record. Restore lists these; it never
/// relaunches them.
/// </remarks>
public sealed record ProcessCloseOutcome
{
    public required ProcessIdentity Identity { get; init; }

    public required string ImageName { get; init; }

    public string? ImagePath { get; init; }

    public required ProcessCloseResult Result { get; init; }

    /// <summary>Plain-language explanation. Always populated for anything other than a clean close.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>True when the machine ended up in the state Quiesce asked for.</summary>
    public bool Succeeded => Result is ProcessCloseResult.Closed or ProcessCloseResult.AlreadyGone;
}

/// <summary>
/// Read access to live processes, plus the graceful close request.
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

    /// <summary>
    /// Asks a process to close by posting <c>WM_CLOSE</c> to each of its top-level windows, then
    /// waits for it to exit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never escalates. There is no force path here and no force path anywhere in Quiesce — a process
    /// that declines is left running and reported, exactly as <see cref="IServiceControl.TryStop"/>
    /// leaves a stubborn service alone. Terminating a program discards unsaved work with no prompt,
    /// which is a worse outcome than a slightly less lean machine.
    /// </para>
    /// <para>
    /// Returns false with a diagnosis rather than throwing, including when the process has no window
    /// to talk to.
    /// </para>
    /// </remarks>
    /// <returns>
    /// What happened, as a value rather than a bool. An earlier draft returned bool and left the
    /// caller to distinguish "no window" from "declined" by string-matching the diagnosis, which
    /// meant rewording a message here silently reclassified outcomes there.
    /// <see cref="ProcessCloseResult.Refused"/> is never returned — guardrails are the caller's job.
    /// </returns>
    ProcessCloseResult TryClose(ProcessIdentity identity, TimeSpan timeout, out string diagnosis);

    /// <summary>
    /// Sets a process's priority class, then verifies by re-reading it.
    /// </summary>
    /// <remarks>
    /// Verification is not paranoia. <c>SetPriorityClass</c> returns success for a request the kernel
    /// then adjusts or ignores, and a throttle that silently did nothing would still be journalled as
    /// applied and "restored" later — writing a priority the process never had.
    /// </remarks>
    /// <returns>True only when a re-read confirms the new class.</returns>
    bool TrySetPriority(ProcessIdentity identity, ProcessPriorityClass priority, out string diagnosis);
}
