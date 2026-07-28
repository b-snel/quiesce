using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quiesce.Core.Catalog;

/// <summary>
/// How much measured support a tweak has. Required on every entry and rendered in the UI —
/// this is the field that keeps Quiesce honest.
/// </summary>
/// <remarks>
/// Serialized as strings so that reordering the enum can never silently reinterpret an old
/// catalog or journal.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<Evidence>))]
public enum Evidence
{
    /// <summary>Backed by published measurements or verified locally.</summary>
    Measured,

    /// <summary>Real effect, but only under specific conditions (hardware, workload).</summary>
    Situational,

    /// <summary>A hardware-dependent coin flip. Presented as an experiment, never an optimization.</summary>
    AB,

    /// <summary>Changes something visible; no performance claim.</summary>
    Cosmetic,

    /// <summary>Circulates in tweak lists with no credible measurement behind it. Ships off.</summary>
    NoEvidence,

    /// <summary>Actively harmful or misleading; shipped only as a documented refusal.</summary>
    NotRecommended,
}

[JsonConverter(typeof(JsonStringEnumConverter<Impact>))]
public enum Impact
{
    High,
    Medium,
    Low,
    None,
}

/// <summary>Lifetime of an applied entry.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TweakScope>))]
public enum TweakScope
{
    /// <summary>Applied on Engage, reverted on Restore, auto-reverted by boot recovery.</summary>
    Session,

    /// <summary>
    /// A standing preference (debloat, telemetry). Reverted only by an explicit user action —
    /// boot recovery must never auto-revert these.
    /// </summary>
    Persistent,
}

/// <summary>
/// Post-write notifications required for a change to take effect in the running session.
/// </summary>
/// <remarks>
/// These are recorded into the journal alongside the prior state, NOT looked up from the catalog
/// at revert time: the panic-revert path owns no catalog, and a revert that restores registry
/// bytes without re-broadcasting leaves the session running on the tweaked behaviour.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ActivationKind>))]
public enum ActivationKind
{
    None,
    ShChangeNotify,
    SpiSetMouse,
    WmSettingChange,
}

/// <summary>Registry hives Quiesce is allowed to touch. Deliberately not the full set.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CatalogHive>))]
public enum CatalogHive
{
    HKLM,

    /// <summary>
    /// Resolved to <c>HKU\&lt;sid&gt;</c> of the interactive user at plan time — never the
    /// <c>HKCU</c> alias, which under elevation or a recovery task can silently point at another
    /// user's hive (or SYSTEM's).
    /// </summary>
    HKCU,
}

/// <summary>Reduced state a service is moved to. Never Disabled for trigger-started services.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServiceStartMode>))]
public enum ServiceStartMode
{
    Automatic,
    Manual,
    Disabled,
}

/// <summary>What a process op does. There is no terminate, and there never will be.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProcessAction>))]
public enum ProcessAction
{
    /// <summary>
    /// Ask the application to close, by posting <c>WM_CLOSE</c> to its windows.
    /// </summary>
    /// <remarks>
    /// The one action in Quiesce that does not round-trip: Restore lists what was closed and does not
    /// relaunch it. Recorded here rather than hidden, because an entry that cannot be undone must read
    /// differently from one that can.
    /// </remarks>
    Close,

    /// <summary>Lower the process's priority class, capturing the prior class for restore.</summary>
    Throttle,
}

/// <summary>
/// How far a throttle may lower a process. Deliberately has no value above Normal.
/// </summary>
/// <remarks>
/// The guardrail expressed as a type rather than as a check. <see cref="Guardrails.MaxAssignablePriority"/>
/// still bounds every write at runtime, but a catalog cannot even <em>ask</em> for a raise: there is no
/// spelling of "High" in this enum, so the JSON that would request it fails to parse.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ThrottleLevel>))]
public enum ThrottleLevel
{
    /// <summary>Runs less often than normal work, but still runs. The safe default.</summary>
    BelowNormal,

    /// <summary>Runs only when nothing else wants the CPU. Starves anything latency-sensitive.</summary>
    Idle,
}

/// <summary>Base for every kind of mutation a catalog entry can contain.</summary>
/// <remarks>
/// Polymorphic on the <c>kind</c> discriminator that catalog JSON already carried, so adding op
/// kinds does not fork the engine: plan, journal, verify and revert all operate on
/// <see cref="OpSpec"/> and dispatch once, at the point of the actual system call.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RegistryOpSpec), "registry")]
[JsonDerivedType(typeof(ServiceOpSpec), "service")]
[JsonDerivedType(typeof(ProcessOpSpec), "process")]
[JsonDerivedType(typeof(PowerOpSpec), "power")]
public abstract record OpSpec
{
    /// <summary>True when applying this op needs administrator rights, independent of the entry.</summary>
    [JsonIgnore]
    public abstract bool NeedsAdmin { get; }

    /// <summary>Short human identity for logs and the preflight list.</summary>
    [JsonIgnore]
    public abstract string TargetDescription { get; }
}

/// <summary>A service reconfiguration: reduce its start type, and optionally stop it now.</summary>
public sealed record ServiceOpSpec : OpSpec
{
    /// <summary>Service short name (the SCM key name, not the display name).</summary>
    [JsonPropertyName("service")]
    public required string Service { get; init; }

    /// <summary>
    /// Start type to move the service to. Trigger-started services are clamped to
    /// <see cref="ServiceStartMode.Manual"/> at plan time regardless of what the catalog asks for:
    /// disabling a trigger-started service makes activation fail silently, and the dependent
    /// feature breaks weeks later with no obvious cause.
    /// </summary>
    [JsonPropertyName("startMode")]
    public required ServiceStartMode StartMode { get; init; }

    /// <summary>Whether to stop it in this session as well as reconfiguring it.</summary>
    [JsonPropertyName("stopNow")]
    public bool StopNow { get; init; } = true;

    /// <summary>Service configuration is always machine-wide.</summary>
    public override bool NeedsAdmin => true;

    public override string TargetDescription => $"service {Service}";
}

/// <summary>
/// A group of running applications to close or throttle, identified by where they live on disk.
/// </summary>
/// <remarks>
/// <para>
/// TARGETING IS PATH-BASED, and that is the whole design of this op. An image name alone is not an
/// identity: anything can be called <c>chrome.exe</c>, and a copy sitting in a temp directory is not
/// the browser the user meant. So a match requires the image name <em>and</em> a directory the real
/// installation lives under, both from the catalog, plus a full image path Quiesce could actually
/// read — a process whose path is unreadable never matches anything.
/// </para>
/// <para>
/// One op describes a group and fans out to one plan step per live process, so the preflight list
/// names every process by PID before anything is asked to close, and each process carries its own
/// journal record and its own prior. Processes that appear <em>after</em> the plan is built are not
/// touched: what the user approved is what runs.
/// </para>
/// <para>
/// This op narrows; it never widens. Everything a process op selects is still put through
/// <see cref="ProcessClassifier"/> and refused if it is protected, hosts a service, belongs to a game
/// or launcher, or is part of what launched Quiesce. The catalog chooses among candidates the
/// guardrails already permit.
/// </para>
/// </remarks>
public sealed record ProcessOpSpec : OpSpec
{
    [JsonPropertyName("action")]
    public required ProcessAction Action { get; init; }

    /// <summary>Image name, with or without the <c>.exe</c> suffix. Matched case-insensitively.</summary>
    [JsonPropertyName("imageName")]
    public required string ImageName { get; init; }

    /// <summary>
    /// Directory fragments the real installation lives under, e.g. <c>\Google\Chrome\Application\</c>.
    /// A process matches when its full image path contains any one of them.
    /// </summary>
    /// <remarks>
    /// Each fragment is separator-delimited at both ends on purpose, so it names a <em>directory</em>
    /// rather than a prefix: <c>\Discord\</c> cannot match <c>\DiscordCanary\</c>, and
    /// <c>\Google\Chrome\Application\</c> cannot match a stray <c>chrome.exe</c> two directories up.
    /// Fragments rather than absolute roots because the same application legitimately installs under
    /// Program Files on one machine and LocalAppData on another, and an absolute list would silently
    /// match nothing on the machine it was not written for.
    /// </remarks>
    [JsonPropertyName("underDirectories")]
    public required IReadOnlyList<string> UnderDirectories { get; init; }

    /// <summary>Required for <see cref="ProcessAction.Throttle"/>, forbidden for a close.</summary>
    [JsonPropertyName("throttleTo")]
    public ThrottleLevel? ThrottleTo { get; init; }

    /// <summary>
    /// Closing or throttling a process in your own session needs no elevation.
    /// </summary>
    /// <remarks>
    /// The first op kind that does not. An unelevated Quiesce cannot read the image path of an
    /// elevated process, so those simply never match — refused for lack of an identity rather than
    /// acted on with a guess.
    /// </remarks>
    public override bool NeedsAdmin => false;

    public override string TargetDescription => Action == ProcessAction.Throttle
        ? $"throttle {ImageName} to {ThrottleTo}"
        : $"close {ImageName}";

    /// <summary>
    /// Whether one live process is a member of this group.
    /// </summary>
    /// <remarks>
    /// Takes primitives rather than a snapshot so the catalog layer stays free of platform types and
    /// the rule can be tested without a process.
    /// </remarks>
    public bool Matches(string imageName, string? imagePath)
    {
        ArgumentNullException.ThrowIfNull(imageName);

        // No readable path, no match. Never fall back to the name: "something called chrome.exe,
        // location unknown" is exactly the case name matching gets wrong, and the cost of being wrong
        // is closing a program the user did not ask Quiesce to close.
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return false;
        }

        if (!Bare(imageName).Equals(Bare(ImageName), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var directory in UnderDirectories)
        {
            if (imagePath.Contains(directory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static string Bare(string imageName) =>
        imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? imageName[..^4] : imageName;
}

/// <summary>Selects an existing Windows power scheme as the active one.</summary>
/// <remarks>
/// <para>
/// SELECTS. It does not create, duplicate, delete or rename a scheme, and it does not write a single
/// setting index inside one. That restraint is the entire reason this op is safe: the prior is one
/// GUID, so there is exactly one fact to put back, and the undo cannot be partially right. An op that
/// edited settings would have to capture 58 AC/DC pairs and would fail the project's own test — that
/// the undo is more trustworthy than the change.
/// </para>
/// <para>
/// A scheme the machine does not have is a NO-OP WITH A REASON, exactly like a service that is absent
/// on this build. Ultimate Performance in particular is genuinely missing on many machines. Quiesce
/// deliberately does not run <c>powercfg -duplicatescheme</c> to create it: a scheme Quiesce created
/// is a scheme Restore would then be obliged to delete, and that is a much larger risk than declining
/// to offer the row.
/// </para>
/// <para>
/// Identified by GUID rather than by name because friendly names are localized — "Ultimate
/// Performance" does not exist under that spelling on a non-English Windows, and a name-matched
/// catalog would silently find nothing there while reporting the row as available.
/// </para>
/// </remarks>
public sealed record PowerOpSpec : OpSpec
{
    /// <summary>The scheme to make active. Must already exist on the machine.</summary>
    [JsonPropertyName("scheme")]
    public required Guid Scheme { get; init; }

    /// <summary>
    /// Selecting a power scheme needs no elevation.
    /// </summary>
    /// <remarks>
    /// Not an assumption — measured. The active scheme lives in
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes</c>, whose ACL grants
    /// <c>BUILTIN\Users</c> read only, so the obvious inference is that this needs admin. It does not:
    /// <c>powercfg /setactive</c> succeeded from a standard, non-elevated interactive user on this
    /// machine, because the call goes through the Power service rather than writing the key. Declaring
    /// admin on the strength of the ACL would gate the row permanently for a user who can run it.
    /// </remarks>
    public override bool NeedsAdmin => false;

    public override string TargetDescription => $"power scheme {Scheme:D}";
}

/// <summary>A single registry mutation within a catalog entry.</summary>
public sealed record RegistryOpSpec : OpSpec
{
    [JsonPropertyName("hive")]
    public required CatalogHive Hive { get; init; }

    /// <summary>Always <c>Registry64</c>. Present in the data so a future exception is explicit.</summary>
    [JsonPropertyName("view")]
    public string View { get; init; } = "Registry64";

    [JsonPropertyName("subkey")]
    public required string Subkey { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }

    /// <summary>
    /// The registry value kind this op writes — and asserts on read. A DWord write to a REG_SZ
    /// target silently no-ops, so kind mismatches are treated as errors, not coincidences.
    /// </summary>
    [JsonPropertyName("expectedKind")]
    public required string ExpectedKind { get; init; }

    /// <summary>The "lean" data to write. Type must match <see cref="ExpectedKind"/>.</summary>
    [JsonPropertyName("leanData")]
    public required JsonElement LeanData { get; init; }

    /// <summary>
    /// HKLM always needs elevation — and so does the per-user policy subtree, which is owned by
    /// Administrators and grants the interactive user read-only. Deriving this from the hive alone
    /// is the bug that crashed an apply during M3.
    /// </summary>
    public override bool NeedsAdmin =>
        Hive == CatalogHive.HKLM || Subkey.Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase);

    public override string TargetDescription => $@"{Hive}\{Subkey} :: {Value}";
}

/// <summary>One toggleable feature. The unit of user consent and of transactional atomicity.</summary>
public sealed record CatalogEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("evidence")]
    public required Evidence Evidence { get; init; }

    [JsonPropertyName("impact")]
    public required Impact Impact { get; init; }

    /// <summary>1 = benign, higher = riskier. Tier 0 is reserved for guardrail-locked rows.</summary>
    [JsonPropertyName("riskTier")]
    public required int RiskTier { get; init; }

    [JsonPropertyName("scope")]
    public required TweakScope Scope { get; init; }

    [JsonPropertyName("requiresAdmin")]
    public required bool RequiresAdmin { get; init; }

    [JsonPropertyName("requiresReboot")]
    public required bool RequiresReboot { get; init; }

    [JsonPropertyName("conflictsWith")]
    public IReadOnlyList<string> ConflictsWith { get; init; } = [];

    /// <summary>Minimum Windows build. Entries below it report "not present on this build".</summary>
    [JsonPropertyName("minBuild")]
    public int MinBuild { get; init; }

    [JsonPropertyName("ops")]
    public required IReadOnlyList<OpSpec> Ops { get; init; }

    [JsonPropertyName("activation")]
    public IReadOnlyList<ActivationKind> Activation { get; init; } = [];

    /// <summary>Honest, user-facing: what stops working when this is applied.</summary>
    [JsonPropertyName("whatItBreaks")]
    public required string WhatItBreaks { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

/// <summary>Root of a catalog file.</summary>
public sealed record CatalogFile
{
    /// <summary>Must be first in the JSON so a cheap probe can refuse future versions.</summary>
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("catalogVersion")]
    public required string CatalogVersion { get; init; }

    [JsonPropertyName("entries")]
    public required IReadOnlyList<CatalogEntry> Entries { get; init; }
}
