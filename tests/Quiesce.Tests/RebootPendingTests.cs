using System.Text.Json;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;
using Xunit;

namespace Quiesce.Tests;

/// <summary>
/// The reboot-pending marker: what sets it, what keeps it, and the one thing that clears it.
/// </summary>
/// <remarks>
/// The failure this pins is the placebo: a tweak that is written to the registry, reported as applied,
/// and doing nothing at all until the machine restarts. The second half is the symmetric one — Restore
/// reports the machine clean while the running system is still on the tweaked behaviour — which is why
/// the marker has to survive a clean revert rather than be cleared by it.
/// </remarks>
public sealed class RebootPendingTests
{
    private static CatalogEntry RebootEntry(string id = "test.reboot", string value = "RebootValue") =>
        EngineTestHarness.DwordEntry(id, valueName: value) with { RequiresReboot = true };

    [Fact]
    public void FreshStateOwesNoReboot()
    {
        var state = new QuiesceState();

        Assert.False(state.RebootPending);
        Assert.Empty(state.RebootPendingEntryIds);
    }

    [Fact]
    public void MarkerSetNowIsPending()
    {
        var state = new QuiesceState().WithRebootPending(["a.b"]);

        Assert.True(state.RebootPending);
        Assert.Equal(["a.b"], state.RebootPendingEntryIds);
        Assert.NotNull(state.RebootPendingSinceUptimeMs);
    }

    [Fact]
    public void MarkingNothingChangesNothing()
    {
        var state = new QuiesceState().WithRebootPending([]);

        Assert.False(state.RebootPending);
        Assert.Null(state.RebootPendingSinceUptimeMs);
    }

    /// <summary>
    /// The clearing condition, and the only one: uptime went backwards, which nothing but a restart does.
    /// </summary>
    [Fact]
    public void MarkerFromAHigherUptimeReadsAsRebooted()
    {
        // A marker stamped at an uptime this boot has not reached is a marker from a previous boot.
        var state = new QuiesceState
        {
            RebootPendingSinceUptimeMs = Environment.TickCount64 + (long)TimeSpan.FromDays(30).TotalMilliseconds,
            RebootPendingEntryIds = ["a.b"],
        };

        Assert.False(state.RebootPending);
    }

    [Fact]
    public void MarkingTwiceUnionsRatherThanReplaces()
    {
        var state = new QuiesceState()
            .WithRebootPending(["first.entry"])
            .WithRebootPending(["second.entry"]);

        Assert.Equal(["first.entry", "second.entry"], state.RebootPendingEntryIds);
    }

    /// <summary>
    /// After a reboot the set starts over. Carrying the old ids forward would attribute an outstanding
    /// restart to entries whose restart already happened.
    /// </summary>
    [Fact]
    public void MarkingAfterARebootDoesNotCarryTheOldSetForward()
    {
        var stale = new QuiesceState
        {
            RebootPendingSinceUptimeMs = Environment.TickCount64 + (long)TimeSpan.FromDays(30).TotalMilliseconds,
            RebootPendingEntryIds = ["from.previous.boot"],
        };

        var marked = stale.WithRebootPending(["fresh.entry"]);

        Assert.Equal(["fresh.entry"], marked.RebootPendingEntryIds);
    }

    [Fact]
    public void SaveDropsAStaleMarkerAndKeepsALiveOne()
    {
        using var harness = new EngineTestHarness();
        var store = new StateStore(harness.DataRoot);

        store.Save(new QuiesceState
        {
            RebootPendingSinceUptimeMs = Environment.TickCount64 + (long)TimeSpan.FromDays(30).TotalMilliseconds,
            RebootPendingEntryIds = ["gone"],
        });
        Assert.Null(store.Load().RebootPendingSinceUptimeMs);

        store.Save(new QuiesceState().WithRebootPending(["still.owed"]));
        Assert.True(store.Load().RebootPending);
        Assert.Equal(["still.owed"], store.Load().RebootPendingEntryIds);
    }

    [Fact]
    public void EngagingARebootRequiringEntryMarksTheState()
    {
        using var harness = new EngineTestHarness();
        var entry = RebootEntry();
        var catalog = EngineTestHarness.CatalogOf(entry);

        var result = harness.Engine.Engage(harness.Engine.Plan(catalog, "test"), FaultInjector.None);

        Assert.Equal(["test.reboot"], result.RebootPendingEntries);
        Assert.True(harness.State.RebootPending);
        Assert.Equal(["test.reboot"], harness.State.RebootPendingEntryIds);
    }

    [Fact]
    public void EngagingAnOrdinaryEntryDoesNotMarkTheState()
    {
        using var harness = new EngineTestHarness();
        var catalog = EngineTestHarness.CatalogOf(EngineTestHarness.DwordEntry());

        var result = harness.Engine.Engage(harness.Engine.Plan(catalog, "test"), FaultInjector.None);

        Assert.Empty(result.RebootPendingEntries);
        Assert.False(harness.State.RebootPending);
    }

    /// <summary>
    /// An entry that changed nothing owes nothing. Warning about an already-lean value sends the user to
    /// restart for a machine that is already in the state they asked for, which makes the warning noise.
    /// </summary>
    [Fact]
    public void AnAlreadyLeanRebootEntryDoesNotMarkTheState()
    {
        using var harness = new EngineTestHarness();
        var entry = RebootEntry();
        var catalog = EngineTestHarness.CatalogOf(entry);

        harness.Registry.SetValue(EngineTestHarness.TargetOf(entry), EngineTestHarness.Dword(0));

        var result = harness.Engine.Engage(harness.Engine.Plan(catalog, "test"), FaultInjector.None);

        Assert.Empty(result.RebootPendingEntries);
        Assert.False(harness.State.RebootPending);
    }

    [Fact]
    public void PlannedAndApplyingRecordsCarryTheRebootFlag()
    {
        using var harness = new EngineTestHarness();
        var catalog = EngineTestHarness.CatalogOf(RebootEntry());

        var result = harness.Engine.Engage(harness.Engine.Plan(catalog, "test"), FaultInjector.None);
        var records = harness.Journal(result.SessionId);

        Assert.True(records.OfType<PlannedRecord>().Single().RequiresReboot);
        Assert.True(records.OfType<ApplyingRecord>().Single().RequiresReboot);
    }

    /// <summary>
    /// The one that matters most. Putting a reboot-requiring value back does not put the machine back, so
    /// a clean restore still owes a restart — and clearing the dirty flag must not take the marker with it.
    /// </summary>
    [Fact]
    public void RestoringARebootRequiringEntryStillOwesARestart()
    {
        using var harness = new EngineTestHarness();
        var catalog = EngineTestHarness.CatalogOf(RebootEntry());

        var engage = harness.Engine.Engage(harness.Engine.Plan(catalog, "test"), FaultInjector.None);
        var revert = harness.Engine.RevertSession(engage.SessionId, "restore");

        Assert.True(revert.Clean);
        Assert.Equal(["test.reboot"], revert.RebootPendingEntries);

        var state = harness.State;
        Assert.False(state.IsDirty);
        Assert.True(state.RebootPending);
        Assert.Equal(["test.reboot"], state.RebootPendingEntryIds);
    }

    /// <summary>
    /// A revert that kept the current value because something else changed it wrote nothing, so it owes
    /// nothing — the same rule as an already-lean apply, seen from the other end.
    /// </summary>
    [Fact]
    public void AConflictKeptCurrentRevertOwesNoRestart()
    {
        using var harness = new EngineTestHarness();
        var entry = RebootEntry();
        var catalog = EngineTestHarness.CatalogOf(entry);

        var engage = harness.Engine.Engage(harness.Engine.Plan(catalog, "test"), FaultInjector.None);

        // Someone else moved the value after Quiesce applied it.
        harness.Registry.SetValue(EngineTestHarness.TargetOf(entry), EngineTestHarness.Dword(7));

        var revert = harness.Engine.RevertSession(engage.SessionId, "restore");

        Assert.Empty(revert.RebootPendingEntries);
    }

    [Fact]
    public void AnUnmarkedEngageDoesNotWipeAMarkerFromAnEarlierSession()
    {
        using var harness = new EngineTestHarness();
        var rebootCatalog = EngineTestHarness.CatalogOf(RebootEntry());

        var first = harness.Engine.Engage(harness.Engine.Plan(rebootCatalog, "test"), FaultInjector.None);
        harness.Engine.RevertSession(first.SessionId, "restore");
        Assert.True(harness.State.RebootPending);

        // A second, entirely ordinary engage-and-restore cycle. The outstanding restart is still outstanding.
        var ordinary = EngineTestHarness.CatalogOf(EngineTestHarness.DwordEntry("test.ordinary", valueName: "Other"));
        var second = harness.Engine.Engage(harness.Engine.Plan(ordinary, "test"), FaultInjector.None);
        harness.Engine.RevertSession(second.SessionId, "restore");

        Assert.True(harness.State.RebootPending);
        Assert.Equal(["test.reboot"], harness.State.RebootPendingEntryIds);
    }

    [Fact]
    public void PlanReportsWhichEntriesWillNeedARestart()
    {
        using var harness = new EngineTestHarness();
        var catalog = EngineTestHarness.CatalogOf(
            RebootEntry(),
            EngineTestHarness.DwordEntry("test.ordinary", valueName: "Other"));

        var plan = harness.Engine.Plan(catalog, "test");

        Assert.Equal(["test.reboot"], plan.RebootRequiringEntries);
    }

    /// <summary>An already-lean entry is not in the plan's warning either — same reason, plan-time.</summary>
    [Fact]
    public void PlanExcludesAnAlreadyLeanRebootEntry()
    {
        using var harness = new EngineTestHarness();
        var entry = RebootEntry();
        harness.Registry.SetValue(EngineTestHarness.TargetOf(entry), EngineTestHarness.Dword(0));

        var plan = harness.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test");

        Assert.Empty(plan.RebootRequiringEntries);
    }

    [Fact]
    public void RebootFlagSurvivesAJournalRoundTripAsJson()
    {
        var record = new ApplyingRecord
        {
            StepId = 1,
            EntryId = "e",
            Scope = TweakScope.Persistent,
            Target = "t",
            RequiresReboot = true,
        };

        var json = JsonSerializer.Serialize<JournalRecord>(record, JournalWriter.JsonOptions);
        Assert.Contains("\"requiresReboot\":true", json, StringComparison.Ordinal);

        var back = Assert.IsType<ApplyingRecord>(
            JsonSerializer.Deserialize<JournalRecord>(json, JournalWriter.JsonOptions));
        Assert.True(back.RequiresReboot);
    }

    /// <summary>
    /// A journal written before the field existed reads as false rather than failing. Understating a
    /// reboot need on an old journal is the accepted cost of not refusing to revert one at all.
    /// </summary>
    [Fact]
    public void AJournalWithoutTheFieldStillDeserializes()
    {
        const string json =
            """{"record":"applying","schemaVersion":1,"stepId":1,"entryId":"e","scope":"Persistent","target":"t"}""";

        var back = Assert.IsType<ApplyingRecord>(
            JsonSerializer.Deserialize<JournalRecord>(json, JournalWriter.JsonOptions));
        Assert.False(back.RequiresReboot);
    }
}
