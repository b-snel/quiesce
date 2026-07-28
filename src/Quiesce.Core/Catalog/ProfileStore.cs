using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quiesce.Core.Catalog;

/// <summary>A named set of enabled catalog entry ids.</summary>
public sealed record Profile
{
    [JsonPropertyName("enabled")]
    public required IReadOnlyList<string> Enabled { get; init; }
}

public sealed record ProfileFile
{
    [JsonPropertyName("schemaVersion")]
    [JsonPropertyOrder(-2)]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("active")]
    public required string Active { get; init; }

    [JsonPropertyName("profiles")]
    public required IReadOnlyDictionary<string, Profile> Profiles { get; init; }
}

/// <summary>
/// Which catalog entries are switched on. Every entry is off unless listed.
/// </summary>
/// <remarks>
/// Opt-in rather than opt-out, deliberately: shipping a new catalog version must never silently
/// start applying tweaks the user never chose. An entry added in a later release stays off until
/// it is explicitly enabled, so upgrading the catalog can widen what is <em>available</em> but
/// never what is <em>applied</em>.
/// <para>
/// The built-in default matches the approved plan: GameDVR capture off, mouse acceleration off,
/// Widgets off, consumer features off, and the ContentDeliveryManager set. Everything else — every
/// A/B experiment, every no-evidence row, every cosmetic shell tweak — ships visible and off.
/// </para>
/// </remarks>
public sealed class ProfileStore(string dataRoot)
{
    public const string DefaultProfileName = "default";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>The plan's default profile. Small, defensible, and honest about what it excludes.</summary>
    public static readonly IReadOnlyList<string> BuiltInDefault =
    [
        "gaming.gamedvr-capture-off",
        "gaming.mouse-acceleration-off",
        "shell.disable-widgets-policy",
        "bloat.consumer-features-off",
        "bloat.contentdelivery-off",
    ];

    private string Path => System.IO.Path.Combine(dataRoot, "profiles.json");

    public ProfileFile Load()
    {
        if (!File.Exists(Path))
        {
            return new ProfileFile
            {
                Active = DefaultProfileName,
                Profiles = new Dictionary<string, Profile>
                {
                    [DefaultProfileName] = new() { Enabled = BuiltInDefault },
                },
            };
        }

        var file = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(Path), Options)
            ?? throw new CatalogException($"{Path}: deserialized to null.");

        if (file.SchemaVersion > 1)
        {
            throw new CatalogException(
                $"{Path}: schemaVersion {file.SchemaVersion} is newer than this build understands.");
        }

        return file;
    }

    public void Save(ProfileFile file)
    {
        Directory.CreateDirectory(dataRoot);

        var tmp = Path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, file, Options);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(Path))
        {
            File.Replace(tmp, Path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmp, Path);
        }
    }

    /// <summary>Ids enabled in the active profile.</summary>
    public IReadOnlySet<string> ActiveEnabled()
    {
        var file = Load();
        return file.Profiles.TryGetValue(file.Active, out var profile)
            ? profile.Enabled.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Turns one entry on or off in the active profile and persists it.</summary>
    public void SetEnabled(string entryId, bool enabled)
    {
        var file = Load();
        var active = file.Active;

        var current = file.Profiles.TryGetValue(active, out var profile)
            ? profile.Enabled.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (enabled)
        {
            current.Add(entryId);
        }
        else
        {
            current.Remove(entryId);
        }

        var profiles = file.Profiles.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        profiles[active] = new Profile { Enabled = [.. current.OrderBy(x => x, StringComparer.Ordinal)] };

        Save(file with { Profiles = profiles });
    }
}
