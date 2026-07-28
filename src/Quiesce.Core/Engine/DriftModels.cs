using Quiesce.Core.Platform;

namespace Quiesce.Core.Engine;

/// <summary>
/// What kind of divergence was found between what a session applied and what the machine now holds.
/// </summary>
/// <remarks>
/// Split by whether Quiesce is willing to put it back, and that split is not a UI convenience — it is the
/// safety boundary. Only the two <c>Process</c> kinds are resyncable, because only they can be re-applied
/// without a second journal record for a target the session already covers. See
/// <see cref="DriftItem.Resyncable"/>.
/// </remarks>
public enum DriftKind
{
    /// <summary>An application Quiesce closed is running again. A NEW instance, not the closed one.</summary>
    ProcessReturned,

    /// <summary>An application Quiesce throttled restarted, so the new instance is at full priority.</summary>
    ProcessRestarted,

    /// <summary>The process Quiesce throttled is still the same instance, at a priority it did not set.</summary>
    ThrottleChanged,

    /// <summary>A service Quiesce stopped is running again. Its start type is usually still correct.</summary>
    ServiceRunning,

    /// <summary>A service's start type is no longer what Quiesce set.</summary>
    ServiceReconfigured,

    /// <summary>A registry value is no longer what Quiesce wrote.</summary>
    RegistryChanged,

    /// <summary>The active power plan is not the one Quiesce selected.</summary>
    PowerSchemeChanged,
}

/// <summary>One thing the machine no longer holds the way this session left it.</summary>
public sealed record DriftItem
{
    /// <summary>
    /// The journal step this describes. For a grouped process item, the lowest of the group.
    /// </summary>
    /// <remarks>
    /// A close journals one step per process INSTANCE, so a browser running nineteen processes produced
    /// nineteen steps. Reporting nineteen drift items for one reopened browser would be technically
    /// accurate and useless, so the group collapses to one item and this carries the lowest step id of it —
    /// enough to find the group in the journal, and never used as a key.
    /// </remarks>
    public required int StepId { get; init; }

    public required string EntryId { get; init; }

    /// <summary>The journal's own <c>Target</c> string, so the report and the journal read alike.</summary>
    public required string Target { get; init; }

    public required DriftKind Kind { get; init; }

    /// <summary>Shown to the user verbatim. Names what changed and what it was.</summary>
    public required string Detail { get; init; }

    /// <summary>Whether Resync will act on this.</summary>
    public required bool Resyncable { get; init; }

    /// <summary>
    /// Why not, when it is not. Never null in that case — a refusal always carries its reason.
    /// </summary>
    public string? NotResyncableReason { get; init; }

    /// <summary>The journalled process this is about, for the process kinds.</summary>
    public ProcessPrior? RecordedProcess { get; init; }

    /// <summary>
    /// The priority the session throttled to, as the journal spells it. Null for a close.
    /// </summary>
    /// <remarks>
    /// Carried so a resync re-throttles to what the SESSION chose rather than to a default. Reconstructing
    /// it from <see cref="ProcessPrior.PriorityClass"/> would be reading the prior — the value the throttle
    /// moved away from — and re-applying that would restore full priority while reporting a throttle.
    /// </remarks>
    public string? RecordedIntendedPriority { get; init; }

    /// <summary>
    /// The live processes matching a returned or restarted program. Empty for every other kind.
    /// </summary>
    /// <remarks>
    /// Carried on the item so the resync plan is built from what the DETECTOR saw, not from a second
    /// enumeration taken later. The two would differ, and the difference would be silent.
    /// </remarks>
    public IReadOnlyList<ProcessSnapshot> LiveProcesses { get; init; } = [];
}

/// <summary>
/// Whether an engaged session still matches the machine, and where it does not.
/// </summary>
/// <remarks>
/// <para>
/// Derived from the JOURNAL, never from the catalog or from a fresh <c>Plan</c>. Three reasons, all of
/// them cases where a plan-derived answer is wrong:
/// </para>
/// <para>
/// A plan is catalog-driven, so adding an app through <c>UserCatalogStore</c> while engaged would make it
/// report drift when nothing about the machine moved. An already-lean registry step journals NOTHING at
/// engage — the apply path elides before it appends — so a plan-derived resync would journal a fresh
/// record for a step the session never touched, and if that step's scope differed from the session's
/// pending set it would change what boot recovery does to the whole session. And <c>PlannedStep.NoOp</c>
/// is actively misleading for a close: "nothing matching X is running" makes a REOPENED application look
/// identical to one that was never closed.
/// </para>
/// <para>
/// Read-only. Nothing here writes to the machine, the journal, or <c>state.json</c>.
/// </para>
/// </remarks>
public sealed record DriftReport
{
    public required Guid SessionId { get; init; }

    public required IReadOnlyList<DriftItem> Items { get; init; }

    /// <summary>
    /// The journal could not be read, so drift is UNKNOWN — which is not "in sync".
    /// </summary>
    /// <remarks>
    /// The same distinction <see cref="Journal.StateStore"/> draws and for the same reason: the data root is
    /// hardened to Administrators, so every unelevated probe of it fails, and reporting "no drift" for
    /// "could not look" is how a tool ends up reassuring the user about something it never checked.
    /// </remarks>
    public required bool Unknown { get; init; }

    public string? UnknownReason { get; init; }

    /// <summary>
    /// The session was applied before the last restart, so nothing here is resyncable.
    /// </summary>
    /// <remarks>
    /// What a session closed before a reboot is not what is running after one — the user signed in again
    /// and their startup applications came back for ordinary reasons, not because the machine drifted.
    /// Re-closing them would be Quiesce acting on a comparison it has no business making.
    /// </remarks>
    public required bool AppliedBeforeLastRestart { get; init; }

    public required DateTimeOffset CheckedUtc { get; init; }

    public IReadOnlyList<DriftItem> Resyncable => [.. Items.Where(i => i.Resyncable)];

    public IReadOnlyList<DriftItem> ReportedOnly => [.. Items.Where(i => !i.Resyncable)];

    public bool Any => Items.Count > 0;

    /// <summary>An empty report for a machine that is not engaged. Not the same as "checked and clean".</summary>
    public static DriftReport NotEngaged(DateTimeOffset checkedUtc) => new()
    {
        SessionId = Guid.Empty,
        Items = [],
        Unknown = false,
        AppliedBeforeLastRestart = false,
        CheckedUtc = checkedUtc,
    };
}

/// <summary>
/// What a resync did. Deliberately not a <see cref="EngageResult"/>.
/// </summary>
/// <remarks>
/// A resync has no session id to report — it appends to the session that already exists — and no rolled-back
/// entries, because the entries it touches were already applied and still are. Reusing
/// <see cref="EngageResult"/> would have meant a <c>SessionId</c> field that looks new and is not, which is
/// exactly the confusion the whole design is built to avoid.
/// </remarks>
public sealed record ResyncResult
{
    /// <summary>The session that was added to. Never a new one.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>How many processes were actually closed or throttled.</summary>
    public required int Acted { get; init; }

    /// <summary>
    /// Non-null when the resync was refused before doing anything at all.
    /// </summary>
    /// <remarks>
    /// When this is set, NOTHING happened: no journal record, no mutation, no state write. That is the
    /// property the refusals are built around, and it is why the message can say "nothing was done"
    /// without qualification.
    /// </remarks>
    public string? RefusedReason { get; init; }

    /// <summary>
    /// Everything the user has to be told: what closed and will not reopen, what declined to close.
    /// </summary>
    /// <remarks>
    /// Carries the same content <see cref="EngageResult.Notes"/> does, and for the same reason — a closed
    /// application is the one thing this product does that its undo does not cover, so it is said at the
    /// moment it happens.
    /// </remarks>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Steps that failed, with the reason. A resync failure never rolls back an entry.</summary>
    public IReadOnlyList<string> Failures { get; init; } = [];

    public bool Refused => RefusedReason is not null;
}
