using Quiesce.Core;
using Quiesce.Core.Catalog;
using Quiesce.Core.Startup;

namespace Quiesce.Tests;

/// <summary>
/// Authoring a close entry and a sign-in entry in one write. Both, or neither.
/// </summary>
public class CombinedAuthoringTests : IDisposable
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

    private UserCatalogStore Store => new(_dataRoot);

    private static AppCandidate Comet() => new()
    {
        InstallDirectory = @"C:\Users\t\AppData\Local\Perplexity\Comet\Application",
        DirectoryFragment = @"\Perplexity\Comet\Application\",
        ImageNames = ["comet"],
        ProcessCount = 19,
        WindowedCount = 1,
        WindowedButProtectedCount = 0,
        DisplayName = "Comet",
        CoveredBy = [],
    };

    private static StartupItem CometShortcut() => new()
    {
        Name = "Comet.lnk",
        Command = @"C:\Users\t\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\Comet.lnk",
        Location = StartupLocation.UserStartupFolder,
        ApprovalBlob = null,
    };

    private static StartupItem LogonTask() => new()
    {
        Name = "CometUpdaterTaskUser145.2.7632.4583",
        Command = @"C:\Users\t\AppData\Local\Perplexity\CometUpdater\145.2.7632.4583\updater.exe --wake",
        Location = StartupLocation.LogonTask,
        ApprovalBlob = null,
    };

    [Fact]
    public void Both_halves_are_authored_in_one_write()
    {
        var result = Store.AddAppAndStartup(Comet(), ProcessAction.Close, null, CometShortcut(), shipped: null);

        Assert.NotNull(result.CloseEntryId);
        Assert.NotNull(result.StartupEntryId);
        Assert.Equal(2, result.WrittenIds.Count);

        var stored = Store.Load();
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Entries.Count);

        // Two different promises, and the SCOPES are what make them different. A close is Session-scoped and
        // has no undo; a sign-in preference is Persistent and Restore puts it back exactly.
        var close = stored.Entries.Single(e => e.Id == result.CloseEntryId);
        var startup = stored.Entries.Single(e => e.Id == result.StartupEntryId);

        Assert.Equal(TweakScope.Session, close.Scope);
        Assert.Equal(TweakScope.Persistent, startup.Scope);

        // And both ship switched OFF - authoring is not targeting. Features plus the preflight remain the gate.
        //
        // Asserted as "these two ids are not enabled" rather than "nothing is enabled": a never-saved profile
        // returns ProfileStore.BuiltInDefault, so an Empty() assertion here would be testing the default
        // profile rather than what this method wrote.
        var enabled = new ProfileStore(_dataRoot).ActiveEnabled();
        Assert.DoesNotContain(result.CloseEntryId!, enabled);
        Assert.DoesNotContain(result.StartupEntryId!, enabled);
    }

    [Fact]
    public void A_logon_task_refuses_the_whole_gesture_and_writes_nothing()
    {
        // THE REACHABLE HALF-WRITTEN CASE, which is the entire reason this method exists. Two separate calls
        // would have written the close entry, then thrown on the sign-in half, and reported failure over a
        // file that had already changed.
        //
        // Quiesce switches sign-in entries off by writing Explorer's approval value, and a scheduled task has
        // none - StartupItemDiscovery.EntryFor says so and throws rather than pretending.
        Assert.Throws<CatalogException>(() =>
            Store.AddAppAndStartup(Comet(), ProcessAction.Close, null, LogonTask(), shipped: null));

        // Nothing at all on disk. Not "the close entry".
        Assert.Null(Store.Load());
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public void Either_half_alone_is_allowed()
    {
        // A running application with no sign-in entry, and a sign-in entry for something not running, are both
        // ordinary - most rows on the merged page are one or the other.
        var closeOnly = Store.AddAppAndStartup(Comet(), ProcessAction.Close, null, null, shipped: null);
        Assert.NotNull(closeOnly.CloseEntryId);
        Assert.Null(closeOnly.StartupEntryId);

        var startupOnly = Store.AddAppAndStartup(null, ProcessAction.Close, null, CometShortcut(), shipped: null);
        Assert.Null(startupOnly.CloseEntryId);
        Assert.NotNull(startupOnly.StartupEntryId);

        Assert.Equal(2, Store.Load()!.Entries.Count);
    }

    [Fact]
    public void Neither_half_being_needed_writes_nothing_and_says_so()
    {
        Store.AddAppAndStartup(Comet(), ProcessAction.Close, null, CometShortcut(), shipped: null);

        var again = Store.AddAppAndStartup(Comet(), ProcessAction.Close, null, CometShortcut(), shipped: null);

        Assert.True(again.WroteNothing);
        Assert.Empty(again.WrittenIds);

        // And it did not duplicate. This is the four-presses-four-entries bug, which is prevented by reusing
        // the same equivalence checks the single-purpose methods use rather than reimplementing them.
        Assert.Equal(2, Store.Load()!.Entries.Count);
    }

    [Fact]
    public void Pressing_it_four_times_produces_two_entries()
    {
        for (var i = 0; i < 4; i++)
        {
            Store.AddAppAndStartup(Comet(), ProcessAction.Close, null, CometShortcut(), shipped: null);
        }

        Assert.Equal(2, Store.Load()!.Entries.Count);
    }

    [Fact]
    public void Authoring_nothing_at_all_is_a_programming_error_not_a_silent_no_op()
    {
        Assert.Throws<ArgumentException>(() =>
            Store.AddAppAndStartup(null, ProcessAction.Close, null, null, shipped: null));
    }

    [Fact]
    public void The_written_entries_load_through_the_same_validator_as_the_shipped_catalog()
    {
        // The guardrails are in the LOADER, so an authored entry is only safe if it survives being loaded.
        // Asserted by round-tripping through the real merge rather than trusting the writer.
        Store.AddAppAndStartup(Comet(), ProcessAction.Close, null, CometShortcut(), shipped: null);

        var shipped = EngineTestHarness.CatalogOf(EngineTestHarness.DwordEntry());
        var merged = UserCatalogStore.Merge(shipped, Store.Load());

        Assert.Equal(3, merged.Entries.Count);
    }
}
