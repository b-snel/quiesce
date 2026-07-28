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
/// Widgets off, consumer features off, the ContentDeliveryManager set, and browsers closed. Everything
/// else — every A/B experiment, every no-evidence row, every cosmetic shell tweak, every service, and
/// the other two app groups — ships visible and off.
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
    /// <remarks>
    /// <c>apps.close-browsers</c> is the only irreversible thing Quiesce enables by default, and it is
    /// here because the product decision was explicit: browsers are closed by default, with keeping them
    /// alive available as a toggle. What makes that defensible is that nothing happens without the
    /// preflight dialog, which names every browser process by PID and states that Restore will not reopen
    /// them before the user approves anything. The two other app groups — throttling Discord, closing the
    /// Claude desktop app — are off, so the standing rule holds everywhere it can: a group ships visible
    /// and off unless there is a stated decision to the contrary.
    /// </remarks>
    public static readonly IReadOnlyList<string> BuiltInDefault =
    [
        "gaming.gamedvr-capture-off",
        "gaming.mouse-acceleration-off",
        "shell.disable-widgets-policy",
        "bloat.consumer-features-off",
        "bloat.contentdelivery-off",
        "apps.close-browsers",
    ];

    private string Path => System.IO.Path.Combine(dataRoot, "profiles.json");

    /// <summary>
    /// Reads the profile file, falling back to <see cref="BuiltInDefault"/> only when there genuinely
    /// isn't one.
    /// </summary>
    /// <remarks>
    /// Opened rather than probed with <c>File.Exists</c>, which returns <c>false</c> for "not permitted to
    /// look" — and this file lives in the Administrators-only data root. The fall-through was milder than
    /// the one in <see cref="Journal.StateStore"/> but the same lie in the same place: an unelevated
    /// <c>print-plan</c> would silently compute the plan from the shipped defaults while telling the user
    /// it was showing them theirs.
    /// </remarks>
    public ProfileFile Load()
    {
        string json;
        try
        {
            json = File.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
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
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new Journal.StateUnreadableException(Path, ex);
        }

        var file = JsonSerializer.Deserialize<ProfileFile>(json, Options)
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
