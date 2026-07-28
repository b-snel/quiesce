using Quiesce.Core;
using Quiesce.Core.Catalog;
using Xunit;

namespace Quiesce.Tests;

/// <summary>
/// Entries the user adds themselves: what they are allowed to contain, and what happens when they collide
/// with the shipped catalog.
/// </summary>
/// <remarks>
/// The invariant is that a user file is data like any other, so it narrows and never widens. Every entry
/// it produces goes through <see cref="CatalogLoader"/>, which means the guardrails apply to an entry the
/// user wrote exactly as they apply to a shipped one — a file that could talk Quiesce into closing the
/// shell would be an arbitrary-close primitive with a friendly button on it.
/// </remarks>
public sealed class UserCatalogStoreTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), "quiesce-tests", Guid.NewGuid().ToString("N"));

    private UserCatalogStore Store => new(_dataRoot);

    private static AppCandidate Candidate(
        string display = "thing",
        string? directory = null,
        params string[] imageNames) => new()
    {
        InstallDirectory = directory ?? @"C:\Program Files\Thing",
        DirectoryFragment = (directory ?? @"C:\Program Files\Thing") + '\\',
        ImageNames = imageNames.Length == 0 ? [display] : imageNames,
        ProcessCount = 1,
        WindowedCount = 1,
        DisplayName = display,
        CoveredBy = [],
    };

    [Fact]
    public void NoFileMeansNoUserEntries() => Assert.Null(Store.Load());

    [Fact]
    public void AnAddedCloseEntryPassesTheCatalogValidator()
    {
        var id = Store.Add(Candidate("comet", @"C:\Users\x\AppData\Local\Perplexity\Comet\Application"),
            ProcessAction.Close, throttleTo: null, shipped: null);

        var loaded = Store.Load();
        Assert.NotNull(loaded);

        var entry = Assert.Single(loaded.Entries);
        Assert.Equal(id, entry.Id);
        Assert.Equal("apps.user.close-comet", entry.Id);

        var op = Assert.IsType<ProcessOpSpec>(Assert.Single(entry.Ops));
        Assert.Equal(ProcessAction.Close, op.Action);
        Assert.Equal("comet", op.ImageName);
        Assert.Equal([@"C:\Users\x\AppData\Local\Perplexity\Comet\Application\"], op.UnderDirectories);
        Assert.Null(op.ThrottleTo);

        // Load() ran the loader, so reaching here at all is the validation assertion. Stated anyway,
        // because that is the property this test exists for.
        CatalogLoader.Validate(loaded, "test");
    }

    /// <summary>
    /// The generated op must match exactly the directory that was observed and nothing above it — a
    /// fragment one level up would pull in every sibling application.
    /// </summary>
    [Fact]
    public void TheGeneratedOpMatchesTheObservedDirectoryAndNotItsParent()
    {
        var entry = UserCatalogStore.EntryFor(
            Candidate("thing", @"C:\Program Files\Vendor\Thing"), ProcessAction.Close, null);

        var op = Assert.IsType<ProcessOpSpec>(entry.Ops[0]);

        Assert.True(op.Matches("thing", @"C:\Program Files\Vendor\Thing\thing.exe"));
        Assert.True(op.Matches("thing.exe", @"C:\Program Files\Vendor\Thing\sub\thing.exe"));
        Assert.False(op.Matches("thing", @"C:\Program Files\Vendor\Other\thing.exe"));
        Assert.False(op.Matches("thing", @"C:\Program Files\Vendor\thing.exe"));
        Assert.False(op.Matches("thing", @"D:\Program Files\Vendor\Thing\thing.exe"));
    }

    /// <summary>A copy of the program somewhere else is not the program the user pointed at.</summary>
    [Fact]
    public void TheGeneratedOpDoesNotMatchTheSameNameElsewhere()
    {
        var entry = UserCatalogStore.EntryFor(Candidate("thing"), ProcessAction.Close, null);
        var op = Assert.IsType<ProcessOpSpec>(entry.Ops[0]);

        Assert.False(op.Matches("thing", @"C:\Users\x\AppData\Local\Temp\thing.exe"));
    }

    [Fact]
    public void EveryImageNameInTheDirectoryGetsAnOp()
    {
        var entry = UserCatalogStore.EntryFor(
            Candidate("thing", @"C:\Program Files\Thing", "thing", "thing-helper", "thing-gpu"),
            ProcessAction.Close,
            null);

        Assert.Equal(3, entry.Ops.Count);
        Assert.Equal(
            ["thing", "thing-helper", "thing-gpu"],
            entry.Ops.Cast<ProcessOpSpec>().Select(o => o.ImageName));
    }

    [Fact]
    public void AThrottleEntryCarriesTheLevelAndACloseEntryDoesNot()
    {
        var throttle = UserCatalogStore.EntryFor(Candidate(), ProcessAction.Throttle, ThrottleLevel.Idle);
        Assert.Equal(ThrottleLevel.Idle, Assert.IsType<ProcessOpSpec>(throttle.Ops[0]).ThrottleTo);

        var close = UserCatalogStore.EntryFor(Candidate(), ProcessAction.Close, ThrottleLevel.Idle);
        Assert.Null(Assert.IsType<ProcessOpSpec>(close.Ops[0]).ThrottleTo);
    }

    /// <summary>
    /// A throttle with no level asked for would fail the validator. Defaulted rather than left null so a
    /// UI that forgot to pass one cannot write a file this build refuses to load.
    /// </summary>
    [Fact]
    public void AThrottleWithNoLevelDefaultsRatherThanFailingValidation()
    {
        var entry = UserCatalogStore.EntryFor(Candidate(), ProcessAction.Throttle, throttleTo: null);

        Assert.Equal(ThrottleLevel.BelowNormal, Assert.IsType<ProcessOpSpec>(entry.Ops[0]).ThrottleTo);
        CatalogLoader.Validate(EngineTestHarness.CatalogOf(entry), "test");
    }

    /// <summary>
    /// The guardrail holds against a user-written entry exactly as it holds against a shipped one. Nothing
    /// in the discovery flow can produce this — the classifier filters protected processes out before the
    /// user ever sees them — which is precisely why it is worth pinning that the second gate is real.
    /// </summary>
    [Fact]
    public void AnEntryNamingAProtectedProcessIsRefused()
    {
        var entry = UserCatalogStore.EntryFor(
            Candidate("explorer", @"C:\Windows", "explorer"), ProcessAction.Close, null);

        var ex = Assert.Throws<CatalogException>(() =>
            CatalogLoader.Validate(EngineTestHarness.CatalogOf(entry), "test"));

        Assert.Contains("never-touch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SavingAnEntryTheLoaderWouldRefuseThrowsBeforeItReachesDisk()
    {
        var bad = EngineTestHarness.CatalogOf(UserCatalogStore.EntryFor(
            Candidate("explorer", @"C:\Windows", "explorer"), ProcessAction.Close, null));

        Assert.Throws<CatalogException>(() => Store.Save(bad));
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public void AddingTwoAppsWithTheSameNameProducesDistinctIds()
    {
        var first = Store.Add(Candidate("thing", @"C:\Program Files\A"), ProcessAction.Close, null, null);
        var second = Store.Add(Candidate("thing", @"C:\Program Files\B"), ProcessAction.Close, null, null);

        Assert.NotEqual(first, second);
        Assert.Equal(2, Store.Load()!.Entries.Count);
    }

    [Fact]
    public void AnIdCollidingWithAShippedEntryIsAvoided()
    {
        var shipped = EngineTestHarness.CatalogOf(UserCatalogStore.EntryFor(
            Candidate("thing"), ProcessAction.Close, null));

        var id = Store.Add(Candidate("thing"), ProcessAction.Close, null, shipped);

        Assert.NotEqual(shipped.Entries[0].Id, id);
    }

    /// <summary>
    /// Neither file can see the other, so the duplicate-id check has to happen on the merged result. A
    /// duplicate would make the profile ambiguous about which entry it had enabled.
    /// </summary>
    [Fact]
    public void MergingADuplicateIdIsRefused()
    {
        var entry = UserCatalogStore.EntryFor(Candidate("thing"), ProcessAction.Close, null);
        var shipped = EngineTestHarness.CatalogOf(entry);
        var user = EngineTestHarness.CatalogOf(entry);

        var ex = Assert.Throws<CatalogException>(() => UserCatalogStore.Merge(shipped, user));
        Assert.Contains("duplicate id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MergeAppendsUserEntriesAndRecordsBothVersions()
    {
        var shipped = EngineTestHarness.CatalogOf(EngineTestHarness.DwordEntry());
        var user = EngineTestHarness.CatalogOf(
            UserCatalogStore.EntryFor(Candidate("thing"), ProcessAction.Close, null));

        var merged = UserCatalogStore.Merge(shipped, user);

        Assert.Equal(2, merged.Entries.Count);
        Assert.Equal("test+user.1", merged.CatalogVersion);
    }

    [Fact]
    public void MergeWithNoUserFileIsTheShippedCatalogUnchanged()
    {
        var shipped = EngineTestHarness.CatalogOf(EngineTestHarness.DwordEntry());

        Assert.Same(shipped, UserCatalogStore.Merge(shipped, null));
    }

    [Fact]
    public void RemoveDeletesOnlyTheNamedEntry()
    {
        var first = Store.Add(Candidate("a", @"C:\Program Files\A"), ProcessAction.Close, null, null);
        Store.Add(Candidate("b", @"C:\Program Files\B"), ProcessAction.Close, null, null);

        Assert.True(Store.Remove(first));
        Assert.False(Store.Remove(first));

        var remaining = Assert.Single(Store.Load()!.Entries);
        Assert.Equal("apps.user.close-b", remaining.Id);
    }

    /// <summary>
    /// A user entry must be Session scope and must not claim admin, both enforced by the loader for
    /// process ops. Asserted directly so a later change to the generator cannot drift past them.
    /// </summary>
    [Fact]
    public void GeneratedEntriesAreSessionScopedAndNeedNoElevation()
    {
        var entry = UserCatalogStore.EntryFor(Candidate(), ProcessAction.Close, null);

        Assert.Equal(TweakScope.Session, entry.Scope);
        Assert.False(entry.RequiresAdmin);
        Assert.False(entry.RequiresReboot);
    }

    /// <summary>
    /// Situational, not Measured. Nothing has been measured about this application — the user asserted it
    /// helps them, which is a different claim, and the evidence field exists so that stays visible.
    /// </summary>
    [Fact]
    public void GeneratedEntriesClaimSituationalEvidence() =>
        Assert.Equal(Evidence.Situational, UserCatalogStore.EntryFor(Candidate(), ProcessAction.Close, null).Evidence);

    [Fact]
    public void ACloseEntrySaysRestoreWillNotReopenIt()
    {
        var entry = UserCatalogStore.EntryFor(Candidate("thing"), ProcessAction.Close, null);

        Assert.Contains("does NOT reopen", entry.WhatItBreaks, StringComparison.Ordinal);
    }

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
    }
}
