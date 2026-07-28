using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Startup;
using Xunit;

namespace Quiesce.Tests;

/// <summary>
/// Switching off a sign-in entry: the blob format, and the catalog entry it becomes.
/// </summary>
/// <remarks>
/// The whole feature rests on one claim — that disabling an auto-start entry is an ordinary, fully
/// reversible registry write — so these tests pin the two things that could make it false: a blob that
/// does not mean what Quiesce thinks it means, and an entry whose prior cannot be put back.
/// </remarks>
public sealed class StartupItemTests
{
    // The exact bytes measured on real hardware. Enabled entries carry a zeroed tail; disabled ones
    // usually carry a FILETIME, and Docker Desktop proved the tail is optional.
    private static readonly byte[] Enabled = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DisabledTimestamped = [3, 0, 0, 0, 0x2C, 0xBC, 0x5E, 0xB8, 0xD2, 0xC2, 0xDC, 0x01];
    private static readonly byte[] DisabledNoTimestamp = [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void TheMeasuredBlobsReadAsTheStateTheyWereMeasuredIn()
    {
        Assert.False(StartupApproval.IsDisabled(Enabled));
        Assert.True(StartupApproval.IsDisabled(DisabledTimestamped));
        Assert.True(StartupApproval.IsDisabled(DisabledNoTimestamp));
    }

    /// <summary>
    /// Absent means enabled, and that asymmetry is load-bearing for revert: an entry that never had an
    /// approval value must end with no value again, which the tri-state prior does by deleting.
    /// </summary>
    [Fact]
    public void NoApprovalValueMeansEnabled()
    {
        Assert.False(StartupApproval.IsDisabled(null));
        Assert.False(StartupApproval.IsDisabled([]));
    }

    /// <summary>
    /// Bit 0 is the flag, not equality with 3. Folder entries are reported in the wild carrying 6 and 7;
    /// an equality test would call a 7 enabled, which is the dangerous direction — claiming to have
    /// switched something off that still runs.
    /// </summary>
    [Theory]
    [InlineData(2u, false)]
    [InlineData(3u, true)]
    [InlineData(6u, false)]
    [InlineData(7u, true)]
    public void BitZeroDecidesRatherThanEqualityWithThree(uint state, bool expected)
    {
        var blob = new byte[12];
        BitConverter.GetBytes(state).CopyTo(blob, 0);

        Assert.Equal(expected, StartupApproval.IsDisabled(blob));
    }

    [Fact]
    public void DisablingPreservesTheTailSoAnAlreadyOffEntryElides()
    {
        // Derived, not canonical: the bytes for an entry the user disabled by hand come back identical, so
        // the engine's already-lean check elides the write instead of rewriting a cosmetic timestamp.
        Assert.Equal(DisabledTimestamped, StartupApproval.Disable(DisabledTimestamped));
    }

    [Fact]
    public void DisablingAnEnabledEntrySetsOnlyTheFlag()
    {
        var disabled = StartupApproval.Disable(Enabled);

        Assert.True(StartupApproval.IsDisabled(disabled));
        Assert.Equal(DisabledNoTimestamp, disabled);
    }

    [Fact]
    public void DisablingWithNoPriorValueProducesTheShapeDockerDesktopProvedIsAccepted()
    {
        Assert.Equal(DisabledNoTimestamp, StartupApproval.Disable(null));
        Assert.Equal(12, StartupApproval.Disable(null).Length);
    }

    [Fact]
    public void EnableIsTheInverseOfDisable()
    {
        Assert.False(StartupApproval.IsDisabled(StartupApproval.Enable(DisabledTimestamped)));
        Assert.True(StartupApproval.IsDisabled(StartupApproval.Disable(StartupApproval.Enable(DisabledTimestamped))));
    }

    [Fact]
    public void DisableDoesNotMutateItsInput()
    {
        var original = DisabledTimestamped.ToArray();
        StartupApproval.Disable(original);

        Assert.Equal(DisabledTimestamped, original);
    }

    // ------------------------------------------------------- the catalog entry

    [Fact]
    public void TheGeneratedEntryPassesTheCatalogValidator()
    {
        var inventory = new FakeStartupInventory();
        var item = inventory.AddEnabled("Comet.lnk", StartupLocation.UserStartupFolder);

        var entry = StartupItemDiscovery.EntryFor(item);

        CatalogLoader.Validate(EngineTestHarness.CatalogOf(entry), "test");

        var op = Assert.IsType<RegistryOpSpec>(Assert.Single(entry.Ops));
        Assert.Equal(CatalogHive.HKCU, op.Hive);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", op.Subkey);
        Assert.Equal("Comet.lnk", op.Value);
        Assert.Equal("Binary", op.ExpectedKind);
        Assert.Equal(Convert.ToBase64String(DisabledNoTimestamp), op.LeanData.GetString());
    }

    /// <summary>
    /// Persistent, and this is the decision the whole feature turns on. Session scope would be auto-reverted
    /// by boot recovery once the boot has passed — precisely the moment the change needed to still hold.
    /// </summary>
    [Fact]
    public void TheGeneratedEntryIsPersistentScope() =>
        Assert.Equal(
            TweakScope.Persistent,
            StartupItemDiscovery.EntryFor(new FakeStartupInventory().AddEnabled("Thing")).Scope);

    /// <summary>
    /// Writing the approval value takes effect immediately; what it governs is a future sign-in. Flagging a
    /// reboot would raise the restart banner over a session that is in no way stale, diluting the banner.
    /// </summary>
    [Fact]
    public void TheGeneratedEntryDoesNotClaimToNeedAReboot() =>
        Assert.False(StartupItemDiscovery.EntryFor(new FakeStartupInventory().AddEnabled("Thing")).RequiresReboot);

    [Theory]
    [InlineData(StartupLocation.UserRun, false)]
    [InlineData(StartupLocation.UserStartupFolder, false)]
    [InlineData(StartupLocation.MachineRun, true)]
    [InlineData(StartupLocation.MachineRun32, true)]
    [InlineData(StartupLocation.MachineStartupFolder, true)]
    public void AdminIsClaimedExactlyWhenTheApprovalValueLivesInHklm(StartupLocation location, bool needsAdmin)
    {
        var item = new FakeStartupInventory().AddEnabled("Thing", location);

        Assert.Equal(needsAdmin, item.NeedsAdmin);
        Assert.Equal(needsAdmin, StartupItemDiscovery.EntryFor(item).RequiresAdmin);
    }

    /// <summary>
    /// A WOW6432Node Run value is governed by StartupApproved\Run32 — not by a WOW6432Node copy of the
    /// approval key. Getting that mapping wrong would write a value Explorer never reads, and Quiesce would
    /// verify its own write and report success for a change with no effect.
    /// </summary>
    [Fact]
    public void ARun32EntryIsGovernedByTheRun32ApprovalKeyInTheSixtyFourBitView()
    {
        var item = new FakeStartupInventory().AddEnabled("Discord", StartupLocation.MachineRun32);
        var op = (RegistryOpSpec)StartupItemDiscovery.EntryFor(item).Ops[0];

        Assert.Equal(CatalogHive.HKLM, op.Hive);
        Assert.EndsWith(@"StartupApproved\Run32", op.Subkey, StringComparison.Ordinal);
        Assert.DoesNotContain("WOW6432Node", op.Subkey, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Registry64", op.View);
    }

    /// <summary>
    /// A logon task has no approval value, so no registry op can reach it. Refused loudly rather than
    /// producing an entry that would verify its own write and change nothing — which matters concretely:
    /// Comet's updater has BOTH a Run value and a logon task on the measured machine.
    /// </summary>
    [Fact]
    public void ALogonTaskIsRefusedRatherThanTurnedIntoAnEntryThatDoesNothing()
    {
        var item = new FakeStartupInventory().Add("SomeTask", StartupLocation.LogonTask);

        Assert.False(item.CanDisable);

        var ex = Assert.Throws<CatalogException>(() => StartupItemDiscovery.EntryFor(item));
        Assert.Contains("cannot be switched off", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACometStyleRunValueWarnsThatALogonTaskMaySurviveIt()
    {
        var entry = StartupItemDiscovery.EntryFor(
            new FakeStartupInventory().AddEnabled("CometUpdaterTaskUser145.2.7632.4583"));

        Assert.Contains("logon scheduled task", entry.Notes!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEntrySaysTheSignInEffectIsUnverified() =>
        Assert.Contains(
            "UNVERIFIED ACROSS A SIGN-IN",
            StartupItemDiscovery.EntryFor(new FakeStartupInventory().AddEnabled("Thing")).Notes!,
            StringComparison.Ordinal);

    // ------------------------------------------------- the round trip that matters

    /// <summary>
    /// The claim the whole feature rests on: switching off a sign-in entry is an ordinary registry write
    /// that Restore puts back exactly. Driven through the real engine, not asserted about the entry.
    /// </summary>
    [Fact]
    public void SwitchingOffASignInEntryRoundTripsThroughTheEngine()
    {
        using var harness = new EngineTestHarness();

        var item = new FakeStartupInventory().AddEnabled("Comet.lnk", StartupLocation.UserStartupFolder);
        var entry = StartupItemDiscovery.EntryFor(item);
        var target = EngineTestHarness.TargetOf(entry);

        // The machine as measured: an approval value that says "enabled".
        harness.Registry.SetValue(target, new Core.Platform.RegistryData
        {
            Kind = "Binary",
            Data = System.Text.Json.JsonSerializer.SerializeToElement(Convert.ToBase64String(Enabled)),
        });

        var catalog = EngineTestHarness.CatalogOf(entry);
        var engage = harness.Engine.Engage(harness.Engine.Plan(catalog, "test"), FaultInjector.None);
        Assert.True(engage.Success);

        var applied = harness.Registry.Probe(target);
        Assert.Equal(Core.Platform.RegPresence.ValuePresent, applied.Presence);
        Assert.True(StartupApproval.IsDisabled(Convert.FromBase64String(applied.Value!.Data.GetString()!)));

        var revert = harness.Engine.RevertSession(engage.SessionId, "restore");
        Assert.True(revert.Clean);

        var back = harness.Registry.Probe(target);
        Assert.Equal(Core.Platform.RegPresence.ValuePresent, back.Presence);
        Assert.Equal(Enabled, Convert.FromBase64String(back.Value!.Data.GetString()!));
    }

    /// <summary>
    /// The absent case, which is the one a naive implementation gets wrong: an entry that never had an
    /// approval value must end with NO value, not with an explicit "enabled" one. "Absent is not zero" is
    /// the same rule the whole registry layer is built around.
    /// </summary>
    [Fact]
    public void AnEntryWithNoPriorApprovalValueIsRestoredToHavingNone()
    {
        using var harness = new EngineTestHarness();

        var item = new FakeStartupInventory().Add("Thing", approval: null);
        var entry = StartupItemDiscovery.EntryFor(item);
        var target = EngineTestHarness.TargetOf(entry);

        Assert.Equal(Core.Platform.RegPresence.KeyAbsent, harness.Registry.Probe(target).Presence);

        var engage = harness.Engine.Engage(harness.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);
        Assert.True(engage.Success);
        Assert.Equal(Core.Platform.RegPresence.ValuePresent, harness.Registry.Probe(target).Presence);

        Assert.True(harness.Engine.RevertSession(engage.SessionId, "restore").Clean);

        // Back to nothing at all - and the key Quiesce created on the way in is gone too.
        Assert.Equal(Core.Platform.RegPresence.KeyAbsent, harness.Registry.Probe(target).Presence);
    }

    /// <summary>
    /// An entry the user already switched off by hand is elided, not rewritten. This is what preserving the
    /// blob's tail buys: a canonical "disabled" constant would differ only in the timestamp, and Quiesce
    /// would report changing something that was already in the state asked for.
    /// </summary>
    [Fact]
    public void AnAlreadyDisabledEntryIsElidedRatherThanRewritten()
    {
        using var harness = new EngineTestHarness();

        var item = new FakeStartupInventory().AddDisabled("Steam");
        var entry = StartupItemDiscovery.EntryFor(item);
        var target = EngineTestHarness.TargetOf(entry);

        harness.Registry.SetValue(target, new Core.Platform.RegistryData
        {
            Kind = "Binary",
            Data = System.Text.Json.JsonSerializer.SerializeToElement(Convert.ToBase64String(DisabledTimestamped)),
        });

        var plan = harness.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test");

        Assert.True(Assert.Single(plan.Steps).NoOp);
        Assert.Empty(plan.EffectiveSteps);
    }

    // ------------------------------------------------------------- discovery

    [Fact]
    public void StillOnEntriesSortAboveAlreadyOffOnes()
    {
        var inventory = new FakeStartupInventory();
        inventory.AddDisabled("Steam");
        inventory.AddEnabled("Discord");
        inventory.AddDisabled("OneDrive");
        inventory.AddEnabled("Comet.lnk", StartupLocation.UserStartupFolder);

        var found = new StartupItemDiscovery(inventory).Discover();

        Assert.Equal(["Discord", "Comet.lnk", "OneDrive", "Steam"], found.Select(i => i.Name));
        Assert.Equal(2, found.Count(i => !i.AlreadyDisabled));
    }

    [Fact]
    public void EveryLocationIsSwept()
    {
        var inventory = new FakeStartupInventory();
        inventory.AddEnabled("a", StartupLocation.UserRun);
        inventory.AddEnabled("b.lnk", StartupLocation.UserStartupFolder);
        inventory.AddEnabled("c", StartupLocation.MachineRun);
        inventory.AddEnabled("d", StartupLocation.MachineRun32);
        inventory.AddEnabled("e.lnk", StartupLocation.MachineStartupFolder);

        Assert.Equal(5, new StartupItemDiscovery(inventory).Discover().Count);
    }
}
