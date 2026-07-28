using Quiesce.Core.Journal;

namespace Quiesce.Tests;

/// <summary>
/// The preferences store, mirroring <c>StateReadabilityTests</c> because it inherits the same trap.
/// </summary>
/// <remarks>
/// This file lives in the same Administrators-only data root as <c>state.json</c>, so every one of the
/// File.Exists lessons applies to it: absent must mean defaults, DENIED must throw, and a newer schema must
/// be refused rather than guessed at.
/// </remarks>
public class SettingsStoreTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), "quiesce-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_missing_file_yields_the_documented_defaults()
    {
        var settings = new SettingsStore(_dataRoot).Load();

        // Close-to-tray defaults ON: the tray's whole purpose is keeping the sync check reachable without a
        // window, and a still-engaged machine should not become invisible when the window closes.
        Assert.True(settings.CloseToNotificationArea);

        // Start-at-sign-in defaults OFF, because it is the one change Quiesce makes that it does not
        // journal. Nothing that Restore cannot undo is ever on by default.
        Assert.False(settings.StartAtSignIn);

        Assert.Equal(1, settings.SchemaVersion);
    }

    [Fact]
    public void It_round_trips_through_disk()
    {
        var store = new SettingsStore(_dataRoot);
        store.Save(new QuiesceSettings { CloseToNotificationArea = false, StartAtSignIn = true });

        var loaded = store.Load();

        Assert.False(loaded.CloseToNotificationArea);
        Assert.True(loaded.StartAtSignIn);
    }

    [Fact]
    public void A_newer_schema_version_is_refused_rather_than_guessed()
    {
        // The same rule the journal and the state file follow. A newer document may carry a preference whose
        // ABSENCE this build would read as "off" - and silently turning something off is worse than refusing
        // to load.
        Directory.CreateDirectory(_dataRoot);
        File.WriteAllText(
            Path.Combine(_dataRoot, SettingsStore.FileName),
            """{"schemaVersion":2,"closeToNotificationArea":true}""");

        var ex = Assert.Throws<JournalFormatException>(() => new SettingsStore(_dataRoot).Load());

        Assert.Contains("newer Quiesce", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_is_refused_with_the_reason()
    {
        Directory.CreateDirectory(_dataRoot);
        File.WriteAllText(Path.Combine(_dataRoot, SettingsStore.FileName), "{ not json");

        Assert.Throws<JournalFormatException>(() => new SettingsStore(_dataRoot).Load());
    }

    [Fact]
    public void A_save_over_an_existing_file_replaces_it_atomically_and_leaves_no_temp()
    {
        var store = new SettingsStore(_dataRoot);
        store.Save(new QuiesceSettings { CloseToNotificationArea = true });
        store.Save(new QuiesceSettings { CloseToNotificationArea = false });

        Assert.False(store.Load().CloseToNotificationArea);

        // The write-temp-then-replace has to clean up after itself: a stray .tmp in the data root is a file
        // the elevated app would later find and have to reason about.
        Assert.Empty(Directory.GetFiles(_dataRoot, "*.tmp"));
    }

    [Fact]
    public void Settings_are_a_separate_document_from_the_machine_state()
    {
        // The reason these are not fields on QuiesceState: state.json is what every recovery net keys on,
        // and a window preference must never be a reason to rewrite it. Asserted by writing one and
        // observing the other is untouched.
        new StateStore(_dataRoot).Save(new QuiesceState { IsDirty = true, ActiveSessionId = Guid.NewGuid() });

        var statePath = Path.Combine(_dataRoot, "state.json");
        var before = File.ReadAllBytes(statePath);

        new SettingsStore(_dataRoot).Save(new QuiesceSettings { CloseToNotificationArea = false });

        Assert.Equal(before, File.ReadAllBytes(statePath));
        Assert.True(new StateStore(_dataRoot).Load().IsDirty);
    }
}
