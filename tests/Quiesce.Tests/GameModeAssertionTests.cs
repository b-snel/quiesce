using System.Text.Json;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Platform;
using Xunit;

namespace Quiesce.Tests;

/// <summary>
/// The one entry that asserts a setting ON, and the one way it could do real harm.
/// </summary>
/// <remarks>
/// Game Mode is enabled by default, and Windows encodes "default" as the value being absent. So the
/// dangerous mistake is restoring by writing 0 instead of deleting: that would leave Game Mode switched
/// OFF on a machine where it started on, and Quiesce would report a clean restore while having made the
/// machine worse than it found it. The tri-state prior exists for exactly this, and this suite pins that it
/// is actually relied on here.
/// </remarks>
public sealed class GameModeAssertionTests
{
    private const string Subkey = @"Software\Microsoft\GameBar";
    private const string ValueName = "AutoGameModeEnabled";

    private static CatalogEntry Entry() => new()
    {
        Id = "gaming.game-mode-on",
        Category = "gaming",
        Title = "Assert Windows Game Mode is on",
        Evidence = Evidence.Situational,
        Impact = Impact.None,
        RiskTier = 1,
        Scope = TweakScope.Persistent,
        RequiresAdmin = false,
        RequiresReboot = false,
        Ops =
        [
            new RegistryOpSpec
            {
                Hive = CatalogHive.HKCU,
                Subkey = Subkey,
                Value = ValueName,
                ExpectedKind = "DWord",
                LeanData = JsonSerializer.SerializeToElement(1),
            },
        ],
        WhatItBreaks = "Nothing (test).",
    };

    private static RegistryTarget Target() => EngineTestHarness.TargetOf(Entry());

    /// <summary>
    /// THE ONE THAT MATTERS. Absent is the default-on state, so restore must delete, never write 0.
    /// </summary>
    [Fact]
    public void RestoringAnAbsentValueDeletesItRatherThanWritingZero()
    {
        using var harness = new EngineTestHarness();
        var target = Target();

        // The measured starting state: the GameBar key exists with an unrelated sibling value, and
        // AutoGameModeEnabled is absent.
        harness.Registry.SetValue(
            target with { ValueName = "GamepadDoublePressIntervalMs" },
            EngineTestHarness.Dword(1));

        Assert.Equal(RegPresence.ValueAbsent, harness.Registry.Probe(target).Presence);

        var engage = harness.Engine.Engage(
            harness.Engine.Plan(EngineTestHarness.CatalogOf(Entry()), "test"), FaultInjector.None);
        Assert.True(engage.Success);

        var applied = harness.Registry.Probe(target);
        Assert.Equal(RegPresence.ValuePresent, applied.Presence);
        Assert.True(applied.Value!.DataEquals(EngineTestHarness.Dword(1)));

        Assert.True(harness.Engine.RevertSession(engage.SessionId, "restore").Clean);

        // Absent again, NOT zero. A zero here would mean Game Mode off on a machine that started with it on.
        var back = harness.Registry.Probe(target);
        Assert.Equal(RegPresence.ValueAbsent, back.Presence);

        // And the unrelated sibling in the same key is untouched.
        Assert.Equal(
            RegPresence.ValuePresent,
            harness.Registry.Probe(target with { ValueName = "GamepadDoublePressIntervalMs" }).Presence);
    }

    /// <summary>
    /// The case the entry actually earns its place on: something had switched Game Mode off. Here the
    /// assertion changes real behaviour, and restore puts the explicit 0 back rather than deleting it.
    /// </summary>
    [Fact]
    public void OnAMachineWhereGameModeWasTurnedOffTheZeroIsPutBack()
    {
        using var harness = new EngineTestHarness();
        var target = Target();

        harness.Registry.SetValue(target, EngineTestHarness.Dword(0));

        var engage = harness.Engine.Engage(
            harness.Engine.Plan(EngineTestHarness.CatalogOf(Entry()), "test"), FaultInjector.None);
        Assert.True(engage.Success);
        Assert.True(harness.Registry.Probe(target).Value!.DataEquals(EngineTestHarness.Dword(1)));

        Assert.True(harness.Engine.RevertSession(engage.SessionId, "restore").Clean);

        // Restored to the user's explicit 0 - not deleted. Deleting would silently turn Game Mode back ON,
        // which is a change the user never asked Quiesce to make permanent.
        var back = harness.Registry.Probe(target);
        Assert.Equal(RegPresence.ValuePresent, back.Presence);
        Assert.True(back.Value!.DataEquals(EngineTestHarness.Dword(0)));
    }

    [Fact]
    public void AlreadyAssertedIsElidedRatherThanRewritten()
    {
        using var harness = new EngineTestHarness();
        harness.Registry.SetValue(Target(), EngineTestHarness.Dword(1));

        var plan = harness.Engine.Plan(EngineTestHarness.CatalogOf(Entry()), "test");

        Assert.True(Assert.Single(plan.Steps).NoOp);
    }

    /// <summary>
    /// Persistent, because a Session-scoped assertion would be auto-reverted by boot recovery — undoing
    /// the very guarantee the entry exists to make.
    /// </summary>
    [Fact]
    public void TheShippedEntryIsPersistentAndNeedsNoElevation()
    {
        var entry = ShippedGameModeEntry();

        Assert.Equal(TweakScope.Persistent, entry.Scope);
        Assert.False(entry.RequiresAdmin);
        Assert.False(entry.RequiresReboot);
    }

    /// <summary>
    /// It makes no performance claim, and that is deliberate: the evidence that Game Mode helps is mixed,
    /// and this entry is a consistency guarantee. Impact None is how the catalog says that.
    /// </summary>
    [Fact]
    public void TheShippedEntryClaimsNoPerformanceImpact()
    {
        var entry = ShippedGameModeEntry();

        Assert.Equal(Impact.None, entry.Impact);
        Assert.Equal(Evidence.Situational, entry.Evidence);
    }

    /// <summary>
    /// The unverified-kind caveat must stay in the notes. The value is absent on every machine that has
    /// never toggled it, so the loader's kind check cannot protect this write, and a silent no-op reported
    /// as success is the failure mode. If someone removes the warning, this fails.
    /// </summary>
    [Fact]
    public void TheShippedEntryAdmitsItsKindIsUnverified() =>
        Assert.Contains("NOT from observation", ShippedGameModeEntry().Notes!, StringComparison.Ordinal);

    /// <summary>
    /// AllowAutoGameMode is deliberately not written: semantics and kind both unverified. Doubling the
    /// unverified surface for no measured benefit is how a catalog starts lying.
    /// </summary>
    [Fact]
    public void TheShippedEntryWritesOnlyTheValueTheSettingsUiWrites()
    {
        var op = Assert.IsType<RegistryOpSpec>(Assert.Single(ShippedGameModeEntry().Ops));

        Assert.Equal(ValueName, op.Value);
        Assert.Equal(CatalogHive.HKCU, op.Hive);
        Assert.Equal(Subkey, op.Subkey);
    }

    /// <summary>Asserting Game Mode ships ON, on an explicit product decision.</summary>
    [Fact]
    public void AssertingGameModeIsInTheDefaultProfile() =>
        Assert.Contains("gaming.game-mode-on", ProfileStore.BuiltInDefault);

    private static CatalogEntry ShippedGameModeEntry()
    {
        var path = CatalogLocator.TryLocate(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("No catalog found next to the test assembly.");

        return CatalogLoader.LoadFile(path).Entries
            .Single(e => e.Id == "gaming.game-mode-on");
    }
}
