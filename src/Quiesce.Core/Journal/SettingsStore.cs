using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quiesce.Core.Journal;

/// <summary>
/// The app's own preferences. Deliberately NOT part of <see cref="QuiesceState"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>state.json</c> is the authority on whether this machine is dirty. It is read by the recovery path, by
/// the CLI and by the GUI, and every one of the four recovery nets keys on it. A UI toggle must not be a
/// reason to rewrite that file, so these live in their own document with their own
/// <see cref="SchemaVersion"/> — and a future version of one cannot refuse to load the other.
/// </para>
/// <para>
/// It lives in the same Administrators-only data root, which makes it machine-wide rather than per-user.
/// That is correct for a file the elevated app reads and mildly odd for a window preference; a second store
/// under the user profile would fix the oddity and introduce a second place for the two to disagree about
/// where anything is. The Settings page says which root it read.
/// </para>
/// </remarks>
public sealed record QuiesceSettings
{
    [JsonPropertyName("schemaVersion")]
    [JsonPropertyOrder(-2)]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Whether the window's X button hides to the notification area instead of exiting.
    /// </summary>
    /// <remarks>
    /// Defaults to true, because the tray's whole purpose is to keep the sync check reachable without a
    /// window — and because a machine that is still engaged should not become invisible when the window
    /// closes. Either way this changes nothing about the machine: only Restore un-engages one.
    /// </remarks>
    [JsonPropertyName("closeToNotificationArea")]
    public bool CloseToNotificationArea { get; init; } = true;

    /// <summary>
    /// Whether Quiesce registered a logon task to start itself at sign-in.
    /// </summary>
    /// <remarks>
    /// A RECORD OF INTENT, not the authority. The scheduled task is the authority, and
    /// <c>LogonTaskRegistration.IsRegistered</c> is what the page displays — this exists so the page can
    /// notice the two have diverged and say so, rather than silently re-registering something the user
    /// removed in Task Scheduler.
    /// </remarks>
    [JsonPropertyName("startAtSignIn")]
    public bool StartAtSignIn { get; init; }
}

/// <summary>Atomic load/save for <see cref="QuiesceSettings"/>.</summary>
public sealed class SettingsStore(string dataRoot)
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private string SettingsPath => Path.Combine(dataRoot, FileName);

    /// <summary>
    /// Reads the settings, or the documented defaults when the file is absent.
    /// </summary>
    /// <remarks>
    /// OPENS THE FILE RATHER THAN PROBING FOR IT, for the reason <see cref="StateStore.Load"/> documents at
    /// length: <c>File.Exists</c> returns false when the real answer is "not permitted to look", and this
    /// file lives in a data root hardened to Administrators. Absent means defaults; DENIED throws.
    /// <para>
    /// It throws <see cref="StateUnreadableException"/> — the same type, deliberately. The message is about
    /// the data root being restricted, which is exactly the situation, and inventing a second exception for
    /// one file would mean two catch clauses at every call site for one cause.
    /// </para>
    /// </remarks>
    public QuiesceSettings Load()
    {
        try
        {
            using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var loaded = JsonSerializer.Deserialize<QuiesceSettings>(stream, Options) ?? new QuiesceSettings();

            // Refused rather than guessed, the same as the journal and the state file. A newer document may
            // carry a preference whose absence this build would silently read as "off".
            if (loaded.SchemaVersion > 1)
            {
                throw new JournalFormatException(
                    $"{FileName} is schema version {loaded.SchemaVersion}, which this build does not " +
                    "understand. A newer Quiesce wrote it.");
            }

            return loaded;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new QuiesceSettings();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new StateUnreadableException(SettingsPath, ex);
        }
        catch (JsonException ex)
        {
            throw new JournalFormatException($"{FileName} is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>Write-temp then replace, the same as the state file and for the same reason.</summary>
    public void Save(QuiesceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(dataRoot);

        var tmp = SettingsPath + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings, Options);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(SettingsPath))
        {
            File.Replace(tmp, SettingsPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmp, SettingsPath);
        }
    }
}
