using System.Text.Json.Serialization;
using Quiesce.Core.Catalog;
using Quiesce.Core.Platform;

namespace Quiesce.Core.Journal;

/// <summary>
/// One line of <c>journal.jsonl</c>. Polymorphic on <c>record</c>; <c>schemaVersion</c> is
/// deliberately the first JSON property of every line so a reader can refuse a future version
/// with a cheap probe before attempting full deserialization.
/// </summary>
/// <remarks>
/// The journal is the single source of truth for revert. Every record must be fully
/// self-describing: the revert path (including the standalone panic binary) reads ONLY these
/// records — never the catalog — so anything revert needs (prior state, activation broadcasts,
/// the owning user SID) must be captured here at apply time.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "record")]
[JsonDerivedType(typeof(SessionStartRecord), "sessionStart")]
[JsonDerivedType(typeof(PlannedRecord), "planned")]
[JsonDerivedType(typeof(ApplyingRecord), "applying")]
[JsonDerivedType(typeof(AppliedRecord), "applied")]
[JsonDerivedType(typeof(EntryRolledBackRecord), "entryRolledBack")]
[JsonDerivedType(typeof(CommittedRecord), "committed")]
[JsonDerivedType(typeof(RevertStartRecord), "revertStart")]
[JsonDerivedType(typeof(RevertedRecord), "reverted")]
[JsonDerivedType(typeof(RevertDeferredRecord), "revertDeferred")]
[JsonDerivedType(typeof(RevertCompleteRecord), "revertComplete")]
public abstract record JournalRecord
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    [JsonPropertyOrder(-2)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("utcTs")]
    public DateTimeOffset UtcTs { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SessionStartRecord : JournalRecord
{
    [JsonPropertyName("sessionId")]
    public required Guid SessionId { get; init; }

    /// <summary>
    /// Identifies the boot this session started in, so recovery can distinguish "dirty in the
    /// current boot" from "dirty and we rebooted since".
    /// </summary>
    [JsonPropertyName("bootId")]
    public required string BootId { get; init; }

    [JsonPropertyName("osBuild")]
    public required string OsBuild { get; init; }

    [JsonPropertyName("appVersion")]
    public required string AppVersion { get; init; }

    [JsonPropertyName("catalogVersion")]
    public required string CatalogVersion { get; init; }

    [JsonPropertyName("profile")]
    public required string Profile { get; init; }
}

/// <summary>
/// One intended mutation, journalled BEFORE anything is touched. The preflight dialog renders
/// literally these records, so what the user approves is exactly what runs.
/// </summary>
public sealed record PlannedRecord : JournalRecord
{
    [JsonPropertyName("stepId")]
    public required int StepId { get; init; }

    [JsonPropertyName("entryId")]
    public required string EntryId { get; init; }

    [JsonPropertyName("scope")]
    public required TweakScope Scope { get; init; }

    [JsonPropertyName("target")]
    public required RegistryTarget Target { get; init; }

    [JsonPropertyName("intendedNew")]
    public required RegistryData IntendedNew { get; init; }

    [JsonPropertyName("activation")]
    public IReadOnlyList<ActivationKind> Activation { get; init; } = [];
}

/// <summary>
/// Written immediately before the mutation, carrying the captured prior. Once this record is
/// durable, the machine's original state is recoverable no matter what happens next.
/// </summary>
public sealed record ApplyingRecord : JournalRecord
{
    [JsonPropertyName("stepId")]
    public required int StepId { get; init; }

    [JsonPropertyName("entryId")]
    public required string EntryId { get; init; }

    [JsonPropertyName("scope")]
    public required TweakScope Scope { get; init; }

    [JsonPropertyName("target")]
    public required RegistryTarget Target { get; init; }

    [JsonPropertyName("prior")]
    public required RegistryProbe Prior { get; init; }

    [JsonPropertyName("intendedNew")]
    public required RegistryData IntendedNew { get; init; }

    /// <summary>Broadcasts revert must re-issue. In the journal, not the catalog, on purpose.</summary>
    [JsonPropertyName("activation")]
    public IReadOnlyList<ActivationKind> Activation { get; init; } = [];

    /// <summary>
    /// Live system state captured before the activation fired, for activations that overwrite
    /// state of their own (currently SPI_SETMOUSE). Restoring registry bytes without replaying
    /// this leaves the running session on the tweaked behaviour until sign-out.
    /// </summary>
    [JsonPropertyName("activationPrior")]
    public IReadOnlyList<ActivationState> ActivationPrior { get; init; } = [];
}

public sealed record AppliedRecord : JournalRecord
{
    [JsonPropertyName("stepId")]
    public required int StepId { get; init; }

    /// <summary>"ok" or a typed diagnosis. Driven by a re-read, never by the write's return code.</summary>
    [JsonPropertyName("verify")]
    public required string Verify { get; init; }
}

/// <summary>A multi-op entry failed partway and every step of it was rolled back.</summary>
public sealed record EntryRolledBackRecord : JournalRecord
{
    [JsonPropertyName("entryId")]
    public required string EntryId { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("rolledBackSteps")]
    public required IReadOnlyList<int> RolledBackSteps { get; init; }
}

/// <summary>
/// Apply finished. This means "apply completed", NOT "machine is clean" — an engaged machine in
/// its steady state has a committed session. Recovery must key on the state store's dirty flag,
/// never on the absence of this record.
/// </summary>
public sealed record CommittedRecord : JournalRecord
{
    [JsonPropertyName("appliedSteps")]
    public required int AppliedSteps { get; init; }

    [JsonPropertyName("skippedNoop")]
    public required int SkippedNoop { get; init; }
}

public sealed record RevertStartRecord : JournalRecord
{
    /// <summary>What initiated the revert: "restore" | "revert-all" | "recover".</summary>
    [JsonPropertyName("initiator")]
    public required string Initiator { get; init; }
}

public sealed record RevertedRecord : JournalRecord
{
    [JsonPropertyName("stepId")]
    public required int StepId { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; } // "restored" | "deleted" | "conflict-kept-current"
}

/// <summary>
/// A step revert was skipped because it cannot run in this context (e.g. the owning user's hive
/// is not loaded). The session stays dirty; this is never counted as reverted.
/// </summary>
public sealed record RevertDeferredRecord : JournalRecord
{
    [JsonPropertyName("stepId")]
    public required int StepId { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

public sealed record RevertCompleteRecord : JournalRecord
{
    [JsonPropertyName("reverted")]
    public required int Reverted { get; init; }

    [JsonPropertyName("deferred")]
    public required int Deferred { get; init; }

    [JsonPropertyName("failed")]
    public required int Failed { get; init; }
}
