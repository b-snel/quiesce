using System.Text.Json.Serialization;

namespace Quiesce.Core.Platform;

/// <summary>One power scheme installed on the machine.</summary>
public sealed record PowerScheme
{
    public required Guid Id { get; init; }

    /// <summary>
    /// The localized display name, or null when it could not be read.
    /// </summary>
    /// <remarks>
    /// Nullable because the friendly name is cosmetic and the GUID is the identity. A scheme whose
    /// name cannot be read is still perfectly selectable and restorable, so failing over a missing
    /// string would refuse work for no safety gain.
    /// </remarks>
    public string? FriendlyName { get; init; }

    /// <summary>
    /// This scheme's "sleep after" timeout on AC power, in seconds. Zero means never; null means it
    /// could not be read.
    /// </summary>
    /// <remarks>
    /// Read for one reason: a scheme that sleeps sooner than the current one would disconnect a remote
    /// session with no way back in, and the RDP guardrails elsewhere in this project are all keyed on
    /// service names — they cannot see a hazard that arrives through a power setting. So this is the
    /// fact <see cref="Guardrails.RefusePowerSchemeChange"/> needs, and it is captured here rather than
    /// probed at guardrail time so the rule stays pure and testable.
    /// <para>
    /// AC only. This machine has no battery, so the DC column is inert — and on a laptop the machine
    /// sleeping on battery is the user's deliberate setting rather than something a gaming tweak should
    /// have an opinion about.
    /// </para>
    /// </remarks>
    public uint? SleepAfterAcSeconds { get; init; }

    /// <summary>True when this scheme will not put the machine to sleep on AC power.</summary>
    public bool NeverSleepsOnAc => SleepAfterAcSeconds == 0;

    public override string ToString() => FriendlyName is null ? $"{Id:D}" : $"{FriendlyName} ({Id:D})";
}

/// <summary>Live power scheme state: which one is active, and which ones exist.</summary>
public sealed record PowerSchemeSnapshot
{
    /// <summary>Null when the active scheme could not be read at all.</summary>
    public Guid? Active { get; init; }

    public string? ActiveFriendlyName { get; init; }

    /// <summary>
    /// Every scheme the machine has. Used to refuse a target that is not installed, rather than
    /// discovering it as an error code from the set call.
    /// </summary>
    public IReadOnlyList<PowerScheme> Installed { get; init; } = [];

    public bool Contains(Guid scheme) => Installed.Any(s => s.Id == scheme);

    public string? NameOf(Guid scheme) => Installed.FirstOrDefault(s => s.Id == scheme)?.FriendlyName;

    public PowerScheme? SchemeOf(Guid scheme) => Installed.FirstOrDefault(s => s.Id == scheme);
}

/// <summary>
/// The active power scheme before Quiesce changed it — the whole undo, in one GUID.
/// </summary>
/// <remarks>
/// <para>
/// The smallest prior in the project, and deliberately so. Quiesce selects among schemes that
/// already exist; it never creates, deletes, duplicates or edits one, and it never writes an
/// individual setting index. That restraint is what keeps the prior to a single GUID instead of the
/// 58 AC/DC setting pairs a scheme actually contains — and it is what makes the undo trustworthy,
/// because there is exactly one fact to put back.
/// </para>
/// <para>
/// The friendly name is captured alongside it purely so a journal, and the emergency
/// <c>revert.cmd</c>, are legible months later without a lookup table. Restore keys on the GUID.
/// </para>
/// </remarks>
public sealed record PowerPrior
{
    [JsonPropertyName("scheme")]
    public required Guid Scheme { get; init; }

    [JsonPropertyName("friendlyName")]
    public string? FriendlyName { get; init; }

    /// <summary>
    /// False when the active scheme could not be read, in which case there is no prior to restore
    /// and the change must not be attempted.
    /// </summary>
    [JsonPropertyName("readable")]
    public bool Readable { get; init; } = true;

    public override string ToString() =>
        FriendlyName is null ? $"{Scheme:D}" : $"{FriendlyName} ({Scheme:D})";
}

/// <summary>Well-known scheme GUIDs, as Windows ships them.</summary>
/// <remarks>
/// Named constants rather than magic GUIDs at the point of use, because the two that matter are
/// distinguished only by their first byte at a glance and confusing them would be a silent
/// behavioural inversion: <see cref="PowerSaver"/> makes the machine slower.
/// </remarks>
public static class WellKnownPowerSchemes
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    /// <summary>
    /// Ultimate Performance. Present on this machine, but genuinely absent on many.
    /// </summary>
    /// <remarks>
    /// Microsoft hides it from the Control Panel list on battery-powered systems and it is not
    /// created at all on some SKUs. Quiesce treats "not installed" as a no-op with a reason, exactly
    /// like a service that does not exist on this build — it does NOT run
    /// <c>powercfg -duplicatescheme</c> to conjure one, because a scheme Quiesce created is a scheme
    /// Restore would then be obliged to delete, and deleting power schemes is a much larger risk
    /// than declining to offer one.
    /// </remarks>
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
}

/// <summary>The mockable seam over the power scheme APIs.</summary>
/// <remarks>
/// Narrow on purpose. There is a read and there is a scheme selection, and nothing else: no create,
/// no delete, no per-setting write. The interface is the guardrail — a catalog cannot ask for a
/// capability that has no method behind it.
/// </remarks>
public interface IPowerControl
{
    /// <summary>Reads the active scheme and the installed set. Never throws for "cannot read".</summary>
    PowerSchemeSnapshot Query();

    /// <summary>
    /// Makes <paramref name="scheme"/> the active power scheme.
    /// </summary>
    /// <remarks>
    /// Verification is the caller's job, by re-reading through <see cref="Query"/>. This mirrors
    /// every other platform seam in the project: a non-throwing call is not evidence that anything
    /// changed.
    /// </remarks>
    void SetActiveScheme(Guid scheme);
}
