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

/// <summary>
/// The state file could not be read, so whether the machine is modified is UNKNOWN.
/// </summary>
/// <remarks>
/// A distinct type because the callers must not treat it as "clean". Every one of them answers a question
/// where a wrong "no" is worse than an error: is this machine modified, is there a session to restore, is
/// there anything to recover. Reporting "I could not tell you, run elevated" is a useful answer; reporting
/// "you are fine" when the tool has no idea is the failure mode this whole project is organised against.
/// </remarks>
public sealed class StateUnreadableException(string path, Exception inner) : Exception(
    $"Cannot read {path}, so whether this machine is modified is unknown. The data root is restricted to " +
    "Administrators by design — run this command from an elevated prompt. Do NOT read this as 'clean'.",
    inner);

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

    /// <summary>
    /// Reads the state file. Throws <see cref="StateUnreadableException"/> rather than guessing.
    /// </summary>
    /// <remarks>
    /// OPENS THE FILE INSTEAD OF ASKING WHETHER IT EXISTS, and that is the whole point of this method.
    /// <para>
    /// <see cref="File.Exists(string)"/> returns <c>false</c> when the answer is actually "you are not
    /// allowed to look" — it swallows every exception by design. The data root is deliberately hardened to
    /// Administrators only, because an elevated Quiesce later executes the revert plan it finds there. So
    /// an unelevated reader got <c>false</c>, fell through to a default <see cref="QuiesceState"/>, and
    /// reported <c>isDirty: false</c>.
    /// </para>
    /// <para>
    /// Which is to say: on a machine that WAS engaged, with GameDVR and mouse acceleration actually
    /// turned off in the registry at that moment, <c>quiesce inventory</c> printed
    /// <c>machine: clean</c>. Found on real hardware, not in review. "I cannot tell" had been silently
    /// converted into "everything is fine" on the single question this tool exists to answer, and the
    /// same fall-through made <c>restore</c> say "No active session" and <c>recover</c> say "Machine is
    /// clean" over an engaged machine.
    /// </para>
    /// <para>
    /// Opening and letting the exception distinguish the cases is the only reliable way to tell absent
    /// from denied. There is no <c>File.Exists</c> anywhere in this class now, deliberately.
    /// </para>
    /// </remarks>
    public QuiesceState Load()
    {
        string json;
        try
        {
            json = File.ReadAllText(StatePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Genuinely absent: first run, nothing has ever been applied. The only case that may
            // legitimately be reported as clean.
            return new QuiesceState();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            throw new StateUnreadableException(StatePath, ex);
        }

        var state = JsonSerializer.Deserialize<QuiesceState>(json, Options)
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
