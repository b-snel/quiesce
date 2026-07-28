using System.Text.Json;
using System.Text.Json.Serialization;
using Quiesce.Core.Platform;

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

    /// <summary>
    /// Uptime in milliseconds at the moment a change needing a restart was applied or reverted.
    /// Null means nothing is owed a reboot.
    /// </summary>
    /// <remarks>
    /// Uptime rather than a boot id, and this is the interesting decision. <see cref="QuiescePaths.IsSameBoot"/>
    /// derives boot time as <c>now - uptime</c>, which cannot distinguish a reboot from a resume: the two
    /// readings are taken from different clocks, and sleep advances one without the other. Deciding a
    /// reboot happened when the machine merely woke up would retract this warning without a restart, and
    /// a warning that disappears on its own is worse than no warning — the user concludes the change took
    /// effect.
    /// <para>
    /// <see cref="Environment.TickCount64"/> is monotonic within a boot and resets on one, so
    /// "current uptime is lower than the recorded uptime" is positive evidence of a restart and nothing
    /// else produces it. See <see cref="RebootPending"/> for the case this deliberately gets wrong.
    /// </para>
    /// </remarks>
    [JsonPropertyName("rebootPendingSinceUptimeMs")]
    public long? RebootPendingSinceUptimeMs { get; init; }

    /// <summary>Boot id when the marker was set. Recorded for legibility, not used for the decision.</summary>
    [JsonPropertyName("rebootPendingBootId")]
    public string? RebootPendingBootId { get; init; }

    /// <summary>Which entries are waiting on a restart, so the warning can name them.</summary>
    [JsonPropertyName("rebootPendingEntryIds")]
    public IReadOnlyList<string> RebootPendingEntryIds { get; init; } = [];

    /// <summary>
    /// A change needing a restart has been made and no restart has been observed since.
    /// </summary>
    /// <remarks>
    /// Errs toward over-warning, on purpose. The one case it gets wrong is a machine that reboots and
    /// then runs longer than its previous uptime before anything reads this file — the marker survives
    /// and the warning lingers after a restart that did happen. A stale "you should reboot" costs the
    /// user one unnecessary restart; the opposite error tells them a change is live when it is not, which
    /// is the class of lie this project is organised against. Every writer drops a stale marker, and the
    /// GUI reads at startup, so lingering needs an unusual sequence to happen at all.
    /// </remarks>
    [JsonIgnore]
    public bool RebootPending =>
        RebootPendingSinceUptimeMs is { } marked && Environment.TickCount64 >= marked;

    /// <summary>Adds entries to the reboot-pending set and stamps the current uptime.</summary>
    public QuiesceState WithRebootPending(IEnumerable<string> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);

        // Union rather than replace: two engages in one boot both owe a reboot, and the second must not
        // erase the first entry's claim on it.
        var union = RebootPending
            ? RebootPendingEntryIds.Concat(entryIds)
            : entryIds;

        var ids = union.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return this;
        }

        return this with
        {
            // Re-stamped to now, not left at the earlier value: the marker means "at least one restart is
            // owed since this point", and the newest change is the one that has to survive.
            RebootPendingSinceUptimeMs = Environment.TickCount64,
            RebootPendingBootId = QuiescePaths.CurrentBootId(),
            RebootPendingEntryIds = ids,
        };
    }

    /// <summary>Drops a marker the machine has demonstrably rebooted past. Applied on every write.</summary>
    public QuiesceState WithoutStaleRebootMarker() =>
        RebootPendingSinceUptimeMs is null || RebootPending
            ? this
            : this with
            {
                RebootPendingSinceUptimeMs = null,
                RebootPendingBootId = null,
                RebootPendingEntryIds = [],
            };
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

    /// <summary>
    /// Writes the state file atomically, dropping a reboot marker the machine has already rebooted past.
    /// </summary>
    /// <remarks>
    /// The stale-marker sweep is here rather than at each call site so that no writer can forget it, and
    /// because every writer holds the state anyway. It only ever removes a marker whose restart has been
    /// positively observed — see <see cref="QuiesceState.RebootPending"/>.
    /// </remarks>
    public void Save(QuiesceState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        state = state.WithoutStaleRebootMarker();

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
