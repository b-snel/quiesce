using System.Text;
using System.Text.Json;
using Quiesce.Core.Journal;

namespace Quiesce.Tests;

public class JournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "quiesce-tests", Guid.NewGuid().ToString("N"));

    public JournalTests()
    {
        // Tests that write journal files by hand (torn lines, future versions) need the directory
        // to exist; JournalWriter.Open creates it only on the writer path.
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string JournalPath => Path.Combine(_dir, "journal.jsonl");

    [Fact]
    public void SchemaVersion_is_the_first_property_of_every_record()
    {
        using (var writer = JournalWriter.Open(_dir))
        {
            writer.Append(new CommittedRecord { AppliedSteps = 1, SkippedNoop = 0 });
        }

        var line = File.ReadAllLines(JournalPath).Single();

        // The cheap-probe contract: a reader must find schemaVersion without parsing the whole
        // record shape. "record" (the discriminator) may precede it; nothing else may.
        using var doc = JsonDocument.Parse(line);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(
            names.IndexOf("schemaVersion") <= 1,
            $"schemaVersion must lead the record; property order was: {string.Join(", ", names)}");
    }

    [Fact]
    public void Torn_final_line_is_tolerated_and_reported()
    {
        using (var writer = JournalWriter.Open(_dir))
        {
            writer.Append(new CommittedRecord { AppliedSteps = 1, SkippedNoop = 0 });
        }

        // Simulate a crash mid-append: half a JSON object with no newline.
        File.AppendAllText(JournalPath, "{\"schemaVersion\":1,\"record\":\"applied\",\"st", Encoding.UTF8);

        var result = JournalReader.Read(JournalPath);

        Assert.True(result.TornFinalLine);
        Assert.Single(result.Records);
    }

    [Fact]
    public void Malformed_record_mid_file_is_refused_not_guessed()
    {
        using (var writer = JournalWriter.Open(_dir))
        {
            writer.Append(new CommittedRecord { AppliedSteps = 1, SkippedNoop = 0 });
        }

        File.AppendAllText(JournalPath, "{garbage\n", Encoding.UTF8);
        File.AppendAllText(
            JournalPath,
            "{\"schemaVersion\":1,\"record\":\"revertStart\",\"initiator\":\"x\",\"utcTs\":\"2026-01-01T00:00:00+00:00\"}\n",
            Encoding.UTF8);

        Assert.Throws<JournalFormatException>(() => JournalReader.Read(JournalPath));
    }

    [Fact]
    public void Future_schemaVersion_is_hard_refused()
    {
        // Best-effort parsing of a future format is how a revert mis-writes a machine.
        File.WriteAllText(
            JournalPath,
            "{\"schemaVersion\":999,\"record\":\"committed\",\"appliedSteps\":0,\"skippedNoop\":0}\n",
            Encoding.UTF8);

        Assert.Throws<JournalFormatException>(() => JournalReader.Read(JournalPath));
    }

    [Fact]
    public void Second_writer_on_the_same_session_is_refused()
    {
        using var first = JournalWriter.Open(_dir);

        // Interleaved writers are how prior state gets lost; the lock must refuse, not queue.
        Assert.Throws<JournalLockedException>(() => JournalWriter.Open(_dir));
    }

    [Fact]
    public void Reader_works_while_a_writer_holds_the_journal()
    {
        using var writer = JournalWriter.Open(_dir);
        writer.Append(new CommittedRecord { AppliedSteps = 2, SkippedNoop = 1 });

        var result = JournalReader.Read(JournalPath);

        var committed = Assert.IsType<CommittedRecord>(Assert.Single(result.Records));
        Assert.Equal(2, committed.AppliedSteps);
    }

    [Fact]
    public void Records_round_trip_through_polymorphic_serialization()
    {
        var target = new Quiesce.Core.Platform.RegistryTarget
        {
            Hive = "HKU",
            UserSid = EngineTestHarness.Sid,
            Subkey = @"SOFTWARE\Test",
            ValueName = "V",
        };

        using (var writer = JournalWriter.Open(_dir))
        {
            writer.Append(new ApplyingRecord
            {
                StepId = 3,
                EntryId = "e",
                Scope = Quiesce.Core.Catalog.TweakScope.Session,
                Target = target,
                Prior = new Quiesce.Core.Platform.RegistryProbe
                {
                    Presence = Quiesce.Core.Platform.RegPresence.ValueAbsent,
                },
                IntendedNew = EngineTestHarness.Dword(0),
                Activation = [Quiesce.Core.Catalog.ActivationKind.ShChangeNotify],
            });
        }

        var record = Assert.IsType<ApplyingRecord>(Assert.Single(JournalReader.Read(JournalPath).Records));

        Assert.Equal(3, record.StepId);
        Assert.Equal(target.UserSid, record.Target.UserSid); // the SID survives the round trip
        Assert.Equal(Quiesce.Core.Platform.RegPresence.ValueAbsent, record.Prior.Presence);
        Assert.Contains(Quiesce.Core.Catalog.ActivationKind.ShChangeNotify, record.Activation);
    }

    [Fact]
    public void Enums_serialize_as_strings_not_ordinals()
    {
        using (var writer = JournalWriter.Open(_dir))
        {
            writer.Append(new ApplyingRecord
            {
                StepId = 1,
                EntryId = "e",
                Scope = Quiesce.Core.Catalog.TweakScope.Persistent,
                Target = new Quiesce.Core.Platform.RegistryTarget { Hive = "HKLM", Subkey = "S", ValueName = "V" },
                Prior = new Quiesce.Core.Platform.RegistryProbe { Presence = Quiesce.Core.Platform.RegPresence.KeyAbsent },
                IntendedNew = EngineTestHarness.Dword(1),
            });
        }

        var line = File.ReadAllText(JournalPath);

        // Ordinal enums make a future reorder silently reinterpret every old journal.
        Assert.Contains("\"Persistent\"", line);
        Assert.Contains("\"KeyAbsent\"", line);
    }
}
