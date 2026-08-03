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
/// The built-in default is things that plausibly affect a game: GameDVR capture off, mouse
/// acceleration off, Game Mode asserted, Widgets off, and browsers closed. Everything else — every
/// A/B experiment, every no-evidence row, every cosmetic shell tweak, every service, the debloat and
/// privacy rows, and the other two app groups — ships visible and off.
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
    /// <para>
    /// THE DEBLOAT AND PRIVACY ROWS ARE NOT HERE, and that is a correction rather than an omission.
    /// <c>bloat.consumer-features-off</c> and <c>bloat.contentdelivery-off</c> shipped enabled, and every
    /// row in both of them says in its own <c>whatItBreaks</c> that it has no effect on frame rate. What
    /// they do have is a round trip: Engage turned the lock screen's suggestion surfaces off and Restore
    /// faithfully turned them back on, because on this machine 1 is genuinely what they were before. So
    /// finishing a gaming session put the advertising back, and the user reasonably read that as Quiesce
    /// switching things on. Nothing was broken — Restore means restore — but a profile called "the things
    /// that help a game" had no business holding settings whose own text says they help no game.
    /// </para>
    /// <para>
    /// They stay in the catalog, visible and off, because they are still worth applying — just once and
    /// on purpose, not on every Engage. Enabling one is now a decision that comes with the knowledge that
    /// Restore undoes it. <c>shell.disable-widgets-policy</c> is kept on a different argument: roughly
    /// 87 MB and two resident processes is a resource claim, not a clutter one.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> BuiltInDefault =
    [
        "gaming.game-mode-on",
        "gaming.gamedvr-capture-off",
        "gaming.mouse-acceleration-off",
        "shell.disable-widgets-policy",
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
    public void SetEnabled(string entryId, bool enabled) => SetEnabled([entryId], enabled);

    /// <summary>
    /// Turns a set of entries on or off in one read-modify-write.
    /// </summary>
    /// <remarks>
    /// Bulk rather than a loop over the single-entry form, which would be 36 load/save round trips for a
    /// "select all" — and 36 chances for a partially-written profile if one of them threw halfway. One
    /// atomic replace either takes the whole selection or leaves the file as it was.
    /// </remarks>
    public void SetEnabled(IEnumerable<string> entryIds, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(entryIds);

        var file = Load();
        var active = file.Active;

        var current = file.Profiles.TryGetValue(active, out var profile)
            ? profile.Enabled.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entryId in entryIds)
        {
            if (enabled)
            {
                current.Add(entryId);
            }
            else
            {
                current.Remove(entryId);
            }
        }

        Replace(file, active, current);
    }

    /// <summary>
    /// Replaces the active profile's enabled set outright.
    /// </summary>
    /// <remarks>
    /// The set form rather than a union, because that is what the bulk buttons mean: "select all" is a
    /// claim about the whole set, not an instruction to add to it. It also prunes ids the catalog no longer
    /// ships — an entry renamed between catalog versions otherwise leaves a dead id in the profile that no
    /// per-entry toggle can ever reach, since there is no row to switch off.
    /// </remarks>
    public void SetEnabledExactly(IEnumerable<string> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);

        var file = Load();
        Replace(file, file.Active, entryIds.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private void Replace(ProfileFile file, string active, HashSet<string> enabled)
    {
        var profiles = file.Profiles.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        profiles[active] = new Profile { Enabled = [.. enabled.OrderBy(x => x, StringComparer.Ordinal)] };

        Save(file with { Profiles = profiles });
    }
}
