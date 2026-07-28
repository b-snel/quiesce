using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quiesce.Core.Journal;

/// <summary>The app-wide state file. Small, atomic, and the authority on "is this machine dirty".</summary>
public sealed record QuiesceState
{
    [JsonPropertyName("schemaVersion")]
    [JsonPropertyOrder(-2)]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// True from the moment an Engage journals its first mutation until a revert completes with
    /// nothing deferred. THIS is the recovery predicate. "The journal has a committed record" is
    /// not — committed means apply finished, which is the steady state of every engaged machine,
    /// and keying recovery on it makes the most common dirty state (engaged, then power loss)
    /// invisible to every automatic net.
    /// </summary>
    [JsonPropertyName("isDirty")]
    public bool IsDirty { get; init; }

    [JsonPropertyName("activeSessionId")]
    public Guid? ActiveSessionId { get; init; }
}

/// <summary>Atomic load/save for <see cref="QuiesceState"/>.</summary>
/// <remarks>
/// Write-temp + <see cref="File.Replace(string, string, string?)"/> is correct here — single
/// small document, last-writer-wins is acceptable, atomicity is what matters. The same technique
/// is banned for the journal, where a full-file replace under concurrency destroys history.
/// </remarks>
public sealed class StateStore(string dataRoot)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private string StatePath => Path.Combine(dataRoot, "state.json");

    public QuiesceState Load()
    {
        if (!File.Exists(StatePath))
        {
            return new QuiesceState();
        }

        var state = JsonSerializer.Deserialize<QuiesceState>(File.ReadAllText(StatePath), Options)
            ?? new QuiesceState();

        if (state.SchemaVersion > 1)
        {
            throw new JournalFormatException(
                $"{StatePath}: schemaVersion {state.SchemaVersion} is newer than this build understands.");
        }

        return state;
    }

    public void Save(QuiesceState state)
    {
        Directory.CreateDirectory(dataRoot);

        var tmp = StatePath + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state, Options);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(StatePath))
        {
            File.Replace(tmp, StatePath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmp, StatePath);
        }
    }
}
