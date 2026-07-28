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

/// <summary>A single registry mutation within a catalog entry.</summary>
public sealed record RegistryOpSpec
{
    /// <summary>Discriminator. Only <c>registry</c> exists until M4.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

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
    public required IReadOnlyList<RegistryOpSpec> Ops { get; init; }

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
