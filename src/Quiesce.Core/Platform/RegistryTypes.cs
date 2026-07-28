using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Quiesce.Core.Platform;

/// <summary>Presence of a registry value at capture time. The tri-state that makes restore faithful.</summary>
/// <remarks>
/// "Value absent" and "value = 0" are different machine states, and conflating them is the
/// signature failure of every tool in this space: reverting by writing 0 where nothing existed is
/// a permanent behaviour change that also pins a Windows default Microsoft may later move.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<RegPresence>))]
public enum RegPresence
{
    /// <summary>The value existed; kind and data were captured.</summary>
    ValuePresent,

    /// <summary>The key existed but the value did not. Restore = delete the value.</summary>
    ValueAbsent,

    /// <summary>The key itself did not exist. Restore = delete the value and the keys we created.</summary>
    KeyAbsent,
}

/// <summary>
/// Fully-qualified identity of a registry value: hive + owning user SID (for per-user hives) +
/// view + subkey + value name.
/// </summary>
/// <remarks>
/// Per-user targets carry the SID and are opened as <c>HKU\&lt;sid&gt;</c>, never via the
/// <c>HKCU</c> alias: under elevation-as-another-admin or in a recovery task, HKCU silently points
/// at the wrong hive while every write "succeeds".
/// </remarks>
public sealed record RegistryTarget
{
    [JsonPropertyName("hive")]
    public required string Hive { get; init; } // "HKLM" | "HKU"

    /// <summary>Owning user SID. Required when <see cref="Hive"/> is HKU, forbidden for HKLM.</summary>
    [JsonPropertyName("userSid")]
    public string? UserSid { get; init; }

    [JsonPropertyName("subkey")]
    public required string Subkey { get; init; }

    [JsonPropertyName("valueName")]
    public required string ValueName { get; init; }

    public override string ToString() =>
        UserSid is null ? $@"{Hive}\{Subkey} :: {ValueName}" : $@"{Hive}\{UserSid}\{Subkey} :: {ValueName}";
}

/// <summary>
/// A registry value's kind and data in a form that survives JSON round-tripping byte-faithfully.
/// </summary>
public sealed record RegistryData
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>
    /// DWord/QWord as number, String/ExpandString as string, MultiString as string[],
    /// Binary as base64 string.
    /// </summary>
    [JsonPropertyName("data")]
    public required JsonElement Data { get; init; }

    public RegistryValueKind ValueKind =>
        Catalog.CatalogLoader.ParseKind(Kind) ?? throw new InvalidOperationException($"Unknown registry kind '{Kind}'.");

    /// <summary>Converts to the CLR object shape the Microsoft.Win32 API expects for a write.</summary>
    public object ToClrValue() => ValueKind switch
    {
        // Registry DWords are unsigned 32-bit; .NET's API surfaces them as int. Parse as long
        // first so 0x80000000..0xFFFFFFFF from JSON round-trips instead of overflowing.
        RegistryValueKind.DWord => unchecked((int)(uint)Data.GetInt64()),
        RegistryValueKind.QWord => unchecked((long)Data.GetUInt64()),
        RegistryValueKind.String or RegistryValueKind.ExpandString => Data.GetString()!,
        RegistryValueKind.MultiString => Data.EnumerateArray().Select(e => e.GetString()!).ToArray(),
        RegistryValueKind.Binary => Convert.FromBase64String(Data.GetString()!),
        _ => throw new InvalidOperationException($"Unwritable registry kind '{Kind}'."),
    };

    public static RegistryData FromClrValue(RegistryValueKind kind, object value)
    {
        var (kindName, element) = kind switch
        {
            RegistryValueKind.DWord => ("DWord", JsonSerializer.SerializeToElement((uint)unchecked((int)Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)))),
            RegistryValueKind.QWord => ("QWord", JsonSerializer.SerializeToElement(unchecked((ulong)Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)))),
            RegistryValueKind.String => ("String", JsonSerializer.SerializeToElement((string)value)),
            RegistryValueKind.ExpandString => ("ExpandString", JsonSerializer.SerializeToElement((string)value)),
            RegistryValueKind.MultiString => ("MultiString", JsonSerializer.SerializeToElement((string[])value)),
            RegistryValueKind.Binary => ("Binary", JsonSerializer.SerializeToElement(Convert.ToBase64String((byte[])value))),
            _ => throw new InvalidOperationException($"Uncapturable registry kind '{kind}'."),
        };

        return new RegistryData { Kind = kindName, Data = element };
    }

    public bool DataEquals(RegistryData other)
    {
        if (!string.Equals(Kind, other.Kind, StringComparison.Ordinal))
        {
            return false;
        }

        // JsonElement equality by raw text is fragile (1 vs 1.0); compare canonically.
        return JsonSerializer.Serialize(Data) == JsonSerializer.Serialize(other.Data);
    }
}

/// <summary>What a probe of a target found. This IS the captured prior state.</summary>
public sealed record RegistryProbe
{
    [JsonPropertyName("presence")]
    public required RegPresence Presence { get; init; }

    /// <summary>Captured kind+data. Present iff <see cref="Presence"/> is ValuePresent.</summary>
    [JsonPropertyName("value")]
    public RegistryData? Value { get; init; }

    /// <summary>
    /// When the key path had to be created to reach the target, the deepest pre-existing key and
    /// the relative path of keys that would be (or were) created below it. Restore deletes exactly
    /// these and nothing else.
    /// </summary>
    [JsonPropertyName("missingKeyPath")]
    public string? MissingKeyPath { get; init; }
}

/// <summary>
/// The mockable seam over the registry. The engine speaks only this; production code binds
/// <c>Win32Registry</c>, tests bind an in-memory fake.
/// </summary>
public interface IRegistry
{
    /// <summary>Reads presence, kind and data without modifying anything.</summary>
    RegistryProbe Probe(RegistryTarget target);

    /// <summary>
    /// Writes kind+data, creating intermediate keys as needed. Returns the keys created (relative
    /// to the deepest pre-existing key) so they can be journalled and deleted on restore.
    /// </summary>
    string? SetValue(RegistryTarget target, RegistryData data);

    /// <summary>Deletes the value. Missing value is not an error — restore must be idempotent.</summary>
    void DeleteValue(RegistryTarget target);

    /// <summary>
    /// Deletes <paramref name="relativeCreatedPath"/> under the target's subkey chain, but only if
    /// every key in it is empty (no values, no subkeys). Someone else writing into a key we created
    /// means it is no longer ours to delete.
    /// </summary>
    void DeleteCreatedKeysIfEmpty(RegistryTarget target, string relativeCreatedPath);

    /// <summary>
    /// True when <c>HKU\&lt;sid&gt;</c> is currently loaded. A revert of a per-user op whose hive
    /// is not loaded must be deferred — probing it would report KeyAbsent and a naive revert would
    /// then "restore" garbage into a hive that appears only when that user signs in.
    /// </summary>
    bool UserHiveLoaded(string sid);
}

/// <summary>
/// Seam for post-write system notifications. Recorded in the journal at apply time and re-issued
/// on revert — a restore that fixes registry bytes without re-broadcasting leaves the running
/// session on the tweaked behaviour until sign-out.
/// </summary>
public interface IActivationBroadcaster
{
    void Broadcast(Catalog.ActivationKind kind);
}
