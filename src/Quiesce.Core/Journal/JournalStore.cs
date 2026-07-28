using System.Text;
using System.Text.Json;

namespace Quiesce.Core.Journal;

/// <summary>
/// Append-only writer and torn-line-tolerant reader for a session's <c>journal.jsonl</c>.
/// </summary>
/// <remarks>
/// Durability rules, each of which exists because a review found the bug it prevents:
/// <list type="bullet">
/// <item>True append-only <see cref="FileStream"/> with <c>Flush(flushToDisk: true)</c> per
/// record. Never a rewrite-and-<c>File.Replace</c> scheme: a full-file replace under two writers
/// loses the other writer's records wholesale — vanished prior state, not a torn line.
/// (<c>File.Replace</c> is the right tool for <c>state.json</c>, and the wrong one here.)</item>
/// <item>An exclusive <c>.lock</c> file held for the writer's lifetime, so a second process
/// (a concurrent CLI, the recovery task in session 0) refuses instead of interleaving. A named
/// mutex alone is insufficient: <c>Local\</c> is per-session and invisible across sessions.</item>
/// <item>The reader tolerates a torn final line (crash mid-append) and reports it, but hard-refuses
/// any record whose <c>schemaVersion</c> is newer than this build — best-effort parsing of an
/// unknown future format is how a revert mis-writes a machine.</item>
/// </list>
/// </remarks>
public sealed class JournalWriter : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly FileStream _lock;
    private readonly FileStream _stream;

    private JournalWriter(FileStream lockStream, FileStream journalStream)
    {
        _lock = lockStream;
        _stream = journalStream;
    }

    /// <summary>Opens (creating if needed) the journal for a session directory, taking the lock.</summary>
    /// <exception cref="JournalLockedException">Another process holds the journal lock.</exception>
    public static JournalWriter Open(string sessionDir)
    {
        Directory.CreateDirectory(sessionDir);

        FileStream? lockStream = null;
        try
        {
            lockStream = new FileStream(
                Path.Combine(sessionDir, ".lock"),
                FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new JournalLockedException(
                $"Journal in '{sessionDir}' is locked by another process. " +
                "Refusing to interleave writes — that is how prior state gets lost.", ex);
        }

        try
        {
            var journalStream = new FileStream(
                Path.Combine(sessionDir, "journal.jsonl"),
                FileMode.Append, FileAccess.Write, FileShare.Read);

            return new JournalWriter(lockStream, journalStream);
        }
        catch
        {
            lockStream.Dispose();
            throw;
        }
    }

    /// <summary>Appends one record and does not return until it is on disk.</summary>
    public void Append(JournalRecord record)
    {
        var line = JsonSerializer.Serialize(record, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        _stream.Write(bytes);
        _stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        _stream.Dispose();
        _lock.Dispose();
    }
}

public sealed record JournalReadResult
{
    public required IReadOnlyList<JournalRecord> Records { get; init; }

    /// <summary>True when the final line was torn (crash mid-append). Reported, never swallowed.</summary>
    public required bool TornFinalLine { get; init; }
}

public static class JournalReader
{
    public static JournalReadResult Read(string journalPath)
    {
        var records = new List<JournalRecord>();
        var torn = false;

        // FileShare.ReadWrite: the reader must work while a writer holds the file open.
        using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        var lineNo = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Cheap schemaVersion probe before typed deserialization.
            int version;
            try
            {
                using var doc = JsonDocument.Parse(line);
                version = doc.RootElement.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number
                    ? sv.GetInt32()
                    : throw new JournalFormatException($"{journalPath}:{lineNo}: record has no numeric schemaVersion.");
            }
            catch (JsonException)
            {
                // Only the FINAL line may be malformed - that is the crash-mid-append case.
                // A torn line in the middle means corruption, and we refuse to guess.
                if (reader.EndOfStream)
                {
                    torn = true;
                    break;
                }

                throw new JournalFormatException($"{journalPath}:{lineNo}: malformed record mid-file. Refusing to guess.");
            }

            if (version > JournalRecord.CurrentSchemaVersion)
            {
                throw new JournalFormatException(
                    $"{journalPath}:{lineNo}: schemaVersion {version} is newer than this build understands " +
                    $"({JournalRecord.CurrentSchemaVersion}). Update Quiesce before touching this journal.");
            }

            var record = JsonSerializer.Deserialize<JournalRecord>(line, JournalWriter.JsonOptions)
                ?? throw new JournalFormatException($"{journalPath}:{lineNo}: record deserialized to null.");

            records.Add(record);
        }

        return new JournalReadResult { Records = records, TornFinalLine = torn };
    }
}

public sealed class JournalLockedException(string message, Exception inner) : IOException(message, inner);

public sealed class JournalFormatException(string message) : Exception(message);
