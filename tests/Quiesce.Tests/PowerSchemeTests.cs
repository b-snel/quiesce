using Quiesce.Core;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;
using Xunit;

namespace Quiesce.Tests;

/// <summary>
/// The power scheme op: one GUID in, one GUID out.
/// </summary>
/// <remarks>
/// The smallest undo in the project, so the tests concentrate on the places where "smallest" could
/// quietly become "wrong": a target that is not installed, an active scheme that cannot be read, a set
/// call that reports success without changing anything, and the guardrail asymmetries — Power saver may
/// be restored but never selected, and a scheme that sleeps sooner is refused over RDP while one that
/// never sleeps must not be.
/// </remarks>
[Collection(SessionGuardCollection.Name)]
public class PowerSchemeTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "quiesce-power", Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _registry = new();
    private readonly RecordingActivation _activation = new();
    private readonly FakePowerControl _power = FakePowerControl.LikeTheDevelopmentMachine();
    private readonly TransactionEngine _engine;

    public PowerSchemeTests()
    {
        _registry.LoadUserHive(EngineTestHarness.Sid);
        SessionGuard.OverrideForTests = false;

        _engine = new TransactionEngine(
            _registry,
            _activation,
            new QuiescePaths(_dataRoot),
            new EngineInfo { AppVersion = "test", OsBuild = "10.0.26200", UserSid = EngineTestHarness.Sid },
            _activation,
            power: _power);
    }

    public void Dispose()
    {
        SessionGuard.OverrideForTests = null;
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static CatalogEntry PowerEntry(
        Guid? scheme = null,
        TweakScope scope = TweakScope.Session,
        string id = "power.test") => new()
    {
        Id = id,
        Category = "power",
        Title = id,
        Evidence = Evidence.Situational,
        Impact = Impact.Low,
        RiskTier = 1,
        Scope = scope,
        RequiresAdmin = false,
        RequiresReboot = false,
        Ops = [new PowerOpSpec { Scheme = scheme ?? WellKnownPowerSchemes.UltimatePerformance }],
        WhatItBreaks = "nothing (test)",
    };

    private EngagePlan Plan(params CatalogEntry[] entries) =>
        _engine.Plan(EngineTestHarness.CatalogOf(entries), "test");

    private EngageResult Engage(params CatalogEntry[] entries) =>
        _engine.Engage(Plan(entries), FaultInjector.None);

    // ------------------------------------------------------------ round trip

    [Fact]
    public void TheActiveSchemeRoundTripsExactly()
    {
        var engage = Engage(PowerEntry());

        Assert.True(engage.Success);
        Assert.Equal(1, engage.Applied);
        Assert.Equal(WellKnownPowerSchemes.UltimatePerformance, _power.Active);

        Assert.True(_engine.RevertSession(engage.SessionId, "restore").Clean);
        Assert.Equal(WellKnownPowerSchemes.Balanced, _power.Active);
    }

    [Fact]
    public void ThePriorSchemeIsJournalledBeforeTheSwitch()
    {
        var engage = Engage(PowerEntry());

        var applying = Assert.Single(Journal(engage.SessionId).OfType<ApplyingRecord>());

        Assert.NotNull(applying.PowerPrior);
        Assert.Equal(WellKnownPowerSchemes.Balanced, applying.PowerPrior!.Scheme);
        Assert.Equal("Balanced", applying.PowerPrior.FriendlyName);
        Assert.Equal(WellKnownPowerSchemes.UltimatePerformance, applying.IntendedScheme);
        Assert.Equal("Ultimate Performance", applying.IntendedSchemeName);
    }

    /// <summary>
    /// A power step must survive the journal as a POWER step. The revert dispatch recognises one by
    /// <c>powerPrior</c> being populated, so a serialization slip would send it down the registry branch
    /// and report "carries no target" over a machine left on the lean plan.
    /// </summary>
    [Fact]
    public void APowerStepIsStillAPowerStepAfterAJournalRoundTrip()
    {
        var engage = Engage(PowerEntry());

        var reread = Assert.Single(Journal(engage.SessionId).OfType<ApplyingRecord>());

        Assert.NotNull(reread.PowerPrior);
        Assert.Null(reread.Service);
        Assert.Null(reread.RegistryTarget);
        Assert.Null(reread.Process);
    }

    // -------------------------------------------------------------- no-ops

    [Fact]
    public void ASchemeThatIsNotInstalledIsANoOpWithAReasonRatherThanAFailure()
    {
        _power.Remove(WellKnownPowerSchemes.UltimatePerformance);

        var step = Assert.Single(Plan(PowerEntry()).Steps);

        Assert.True(step.NoOp);
        Assert.Null(step.RefusedReason);
        Assert.Contains("not installed", step.NoOpDetail!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Quiesce never creates a scheme. A machine without Ultimate Performance must come out of an
    /// Engage with exactly the schemes it went in with.
    /// </summary>
    [Fact]
    public void AMissingSchemeIsNeverConjuredIntoExistence()
    {
        _power.Remove(WellKnownPowerSchemes.UltimatePerformance);

        var engage = Engage(PowerEntry());

        Assert.True(engage.Success);
        Assert.Equal(0, engage.Applied);
        Assert.Empty(_power.SetCalls);
        Assert.False(_power.Query().Contains(WellKnownPowerSchemes.UltimatePerformance));
    }

    [Fact]
    public void AlreadyActiveIsElidedRatherThanRewritten()
    {
        _power.Active = WellKnownPowerSchemes.UltimatePerformance;

        var step = Assert.Single(Plan(PowerEntry()).Steps);

        Assert.True(step.NoOp);
        Assert.Contains("already on", step.NoOpDetail!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Already-active must beat refused, exactly as already-lean beats refused for a registry value.
    /// Power saver is on the never-select list, so a machine sitting on Power saver must read as
    /// "already there", not as "Quiesce refuses" — the refusal would be a statement about a switch that
    /// was never going to happen.
    /// </summary>
    [Fact]
    public void AlreadyActiveBeatsRefusedEvenForASchemeQuiesceWouldNeverSelect()
    {
        _power.Active = WellKnownPowerSchemes.PowerSaver;

        var step = Assert.Single(Plan(PowerEntry(WellKnownPowerSchemes.PowerSaver)).Steps);

        Assert.True(step.NoOp);
        Assert.Null(step.RefusedReason);
    }

    // ------------------------------------------------------- no prior, no change

    /// <summary>
    /// THE ONE THAT MATTERS MOST. With no readable prior there is nothing to restore, so the switch must
    /// be refused rather than made — an unrevertable change is the single thing this project will not do.
    /// </summary>
    [Fact]
    public void AnUnreadableActiveSchemeRefusesTheSwitchRatherThanMakingAnUnrevertableChange()
    {
        _power.ActiveUnreadable = true;

        var step = Assert.Single(Plan(PowerEntry()).Steps);

        Assert.False(step.NoOp);
        Assert.NotNull(step.RefusedReason);
        Assert.Contains("cannot put back", step.RefusedReason!, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, Engage(PowerEntry()).Applied);
        Assert.Empty(_power.SetCalls);
    }

    // -------------------------------------------------------------- verify

    /// <summary>
    /// PowerSetActiveScheme returns a Win32 error code where every sibling API returns a BOOL, so a
    /// call that "succeeds" while changing nothing is a realistic failure. It must fail the entry, not
    /// be reported as applied.
    /// </summary>
    [Fact]
    public void ASwitchThatSilentlyDoesNothingFailsItsEntry()
    {
        _power.SilentlyIgnoreWrites = true;

        var engage = Engage(PowerEntry());

        Assert.False(engage.Success);
        Assert.Equal("power.test", Assert.Single(engage.RolledBackEntries));
        Assert.Contains("Verification failed", engage.Diagnoses["power.test"], StringComparison.Ordinal);
        Assert.Equal(WellKnownPowerSchemes.Balanced, _power.Active);
    }

    [Fact]
    public void AThrowingSwitchFailsItsEntryRatherThanEscaping()
    {
        _power.ThrowOnSet = new System.ComponentModel.Win32Exception(5, "Access is denied");

        var engage = Engage(PowerEntry());

        Assert.False(engage.Success);
        Assert.Contains("PowerFailed", engage.Diagnoses["power.test"], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- conflict

    /// <summary>
    /// The user picked a plan themselves after engaging. Restore must keep their choice rather than
    /// overwrite it with the stale capture — the same rule the registry, service and process paths apply.
    /// </summary>
    [Fact]
    public void APlanChangedSinceApplyIsKeptRatherThanOverwritten()
    {
        var engage = Engage(PowerEntry());

        _power.Active = WellKnownPowerSchemes.HighPerformance;

        var revert = _engine.RevertSession(engage.SessionId, "restore");

        Assert.True(revert.Clean);
        Assert.Equal(WellKnownPowerSchemes.HighPerformance, _power.Active);
        Assert.Contains(revert.Messages, m => m.Contains("kept it", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Restore is idempotent: already back on the prior scheme means nothing to do, not a second write.
    /// </summary>
    [Fact]
    public void ARestoreThatIsAlreadyDoneIssuesNoFurtherSwitch()
    {
        var engage = Engage(PowerEntry());
        _power.Active = WellKnownPowerSchemes.Balanced;
        var callsBefore = _power.SetCalls.Count;

        Assert.True(_engine.RevertSession(engage.SessionId, "restore").Clean);
        Assert.Equal(callsBefore, _power.SetCalls.Count);
    }

    /// <summary>
    /// The prior scheme was deleted between apply and restore. Quiesce cannot select it and must say so
    /// rather than substitute something plausible.
    /// </summary>
    [Fact]
    public void APriorSchemeThatNoLongerExistsFailsLoudlyRatherThanPickingASubstitute()
    {
        var engage = Engage(PowerEntry());

        _power.Remove(WellKnownPowerSchemes.Balanced);

        var revert = _engine.RevertSession(engage.SessionId, "restore");

        Assert.False(revert.Clean);
        Assert.Contains(revert.Messages, m => m.Contains("no longer exists", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(WellKnownPowerSchemes.UltimatePerformance, _power.Active);
    }

    // ------------------------------------------------------------ guardrails

    [Fact]
    public void PowerSaverIsNeverSelected()
    {
        var target = new PowerScheme { Id = WellKnownPowerSchemes.PowerSaver, FriendlyName = "Power saver", SleepAfterAcSeconds = 0 };

        Assert.True(Guardrails.RefusePowerSchemeChange(target, null, isRemoteSession: false, out var reason));
        Assert.Contains("slower", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The asymmetry, stated as a test. Power saver may never be SELECTED, but a user who was on it must
    /// get it back — a guardrail applied to restore would strand them on whatever Quiesce switched to.
    /// </summary>
    [Fact]
    public void PowerSaverIsStillRestoredWhenItIsWhatTheUserHad()
    {
        _power.Active = WellKnownPowerSchemes.PowerSaver;

        var engage = Engage(PowerEntry());
        Assert.True(engage.Success);
        Assert.Equal(WellKnownPowerSchemes.UltimatePerformance, _power.Active);

        Assert.True(_engine.RevertSession(engage.SessionId, "restore").Clean);
        Assert.Equal(WellKnownPowerSchemes.PowerSaver, _power.Active);
    }

    [Fact]
    public void ACatalogCannotAskForASchemeQuiesceWillNeverSelect()
    {
        var ex = Assert.Throws<CatalogException>(() => CatalogLoader.Validate(
            EngineTestHarness.CatalogOf(PowerEntry(WellKnownPowerSchemes.PowerSaver)), "test"));

        Assert.Contains("never select", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------- the RDP sleep guardrail

    /// <summary>
    /// THE ZERO-MEANS-NEVER TRAP. Sleep-after is zero for "never", which as an integer is smaller than
    /// every real timeout — so a naive numeric comparison refuses precisely the scheme that removes the
    /// hazard. This test exists so that bug cannot be reintroduced.
    /// </summary>
    [Fact]
    public void ASchemeThatNeverSleepsIsAllowedOverRdpEvenThoughZeroIsTheSmallestNumber()
    {
        var target = new PowerScheme { Id = WellKnownPowerSchemes.UltimatePerformance, FriendlyName = "Ultimate Performance", SleepAfterAcSeconds = 0 };
        var current = new PowerScheme { Id = WellKnownPowerSchemes.Balanced, FriendlyName = "Balanced", SleepAfterAcSeconds = 18000 };

        Assert.False(Guardrails.RefusePowerSchemeChange(target, current, isRemoteSession: true, out _));
    }

    [Fact]
    public void ASchemeThatSleepsSoonerIsRefusedOverRdp()
    {
        var target = new PowerScheme { Id = Guid.NewGuid(), FriendlyName = "Naps A Lot", SleepAfterAcSeconds = 600 };
        var current = new PowerScheme { Id = WellKnownPowerSchemes.Balanced, FriendlyName = "Balanced", SleepAfterAcSeconds = 18000 };

        Assert.True(Guardrails.RefusePowerSchemeChange(target, current, isRemoteSession: true, out var reason));
        Assert.Contains("disconnects you", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASchemeThatSleepsLaterIsAllowedOverRdp()
    {
        var target = new PowerScheme { Id = Guid.NewGuid(), FriendlyName = "Naps Less", SleepAfterAcSeconds = 36000 };
        var current = new PowerScheme { Id = WellKnownPowerSchemes.Balanced, FriendlyName = "Balanced", SleepAfterAcSeconds = 18000 };

        Assert.False(Guardrails.RefusePowerSchemeChange(target, current, isRemoteSession: true, out _));
    }

    /// <summary>
    /// Moving from never-sleeps to sleeps-at-all is a regression in disconnection risk even though the
    /// number went up from zero.
    /// </summary>
    [Fact]
    public void MovingFromNeverSleepsToAnySleepAtAllIsRefusedOverRdp()
    {
        var target = new PowerScheme { Id = Guid.NewGuid(), FriendlyName = "Naps Eventually", SleepAfterAcSeconds = 36000 };
        var current = new PowerScheme { Id = WellKnownPowerSchemes.UltimatePerformance, FriendlyName = "Ultimate Performance", SleepAfterAcSeconds = 0 };

        Assert.True(Guardrails.RefusePowerSchemeChange(target, current, isRemoteSession: true, out var reason));
        Assert.Contains("does not sleep at all", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unreadable sleep timeout counts as refused while remote, not as permission.</summary>
    [Fact]
    public void AnUnknownSleepTimeoutIsRefusedOverRdp()
    {
        var target = new PowerScheme { Id = Guid.NewGuid(), FriendlyName = "Unknown", SleepAfterAcSeconds = null };

        Assert.True(Guardrails.RefusePowerSchemeChange(target, null, isRemoteSession: true, out var reason));
        Assert.Contains("could not read", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same scheme locally is fine. Nothing is at stake: the screen sleeps and the user moves the
    /// mouse. A guardrail that fired here would refuse work for no safety gain.
    /// </summary>
    [Fact]
    public void ASchemeThatSleepsSoonerIsAllowedWhenTheSessionIsLocal()
    {
        var target = new PowerScheme { Id = Guid.NewGuid(), FriendlyName = "Naps A Lot", SleepAfterAcSeconds = 600 };
        var current = new PowerScheme { Id = WellKnownPowerSchemes.Balanced, FriendlyName = "Balanced", SleepAfterAcSeconds = 18000 };

        Assert.False(Guardrails.RefusePowerSchemeChange(target, current, isRemoteSession: false, out _));
    }

    /// <summary>
    /// The guardrail is re-evaluated at apply time, not just at plan time: a user can connect over RDP
    /// while the preflight dialog is open. Same discipline as the service path.
    /// </summary>
    [Fact]
    public void BecomingRemoteBetweenPlanAndApplyStillRefusesTheSwitch()
    {
        _power.Install(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Naps A Lot", 600);
        var entry = PowerEntry(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        var plan = Plan(entry);
        Assert.True(Assert.Single(plan.Steps).WillRun);

        SessionGuard.OverrideForTests = true;

        var engage = _engine.Engage(plan, FaultInjector.None);

        Assert.False(engage.Success);
        Assert.Equal(WellKnownPowerSchemes.Balanced, _power.Active);
    }

    // -------------------------------------------------------- recovery + scope

    /// <summary>
    /// Unlike a throttle, an active power scheme is machine-wide state under HKLM that SURVIVES A
    /// REBOOT. So Session scope plus boot recovery is not ceremony here — without it, a machine that
    /// crashed while engaged would stay on the lean plan indefinitely.
    /// </summary>
    [Fact]
    public void BootRecoveryPutsTheSchemeBackAfterACrashWhileEngaged()
    {
        var engage = Engage(PowerEntry(scope: TweakScope.Session));
        Assert.Equal(WellKnownPowerSchemes.UltimatePerformance, _power.Active);

        // Rewrite the session's boot id so recovery sees a different boot, as it would after a restart.
        ForgeADifferentBoot(engage.SessionId);

        var recovered = _engine.Recover();

        Assert.NotNull(recovered);
        Assert.Equal(WellKnownPowerSchemes.Balanced, _power.Active);
    }

    /// <summary>A Persistent power entry is a standing preference and recovery must leave it alone.</summary>
    [Fact]
    public void BootRecoveryLeavesAPersistentPowerEntryAlone()
    {
        var engage = Engage(PowerEntry(scope: TweakScope.Persistent));
        ForgeADifferentBoot(engage.SessionId);

        _engine.Recover();

        Assert.Equal(WellKnownPowerSchemes.UltimatePerformance, _power.Active);
    }

    // ------------------------------------------------------------- revert.cmd

    [Fact]
    public void TheEmergencyScriptRestoresTheSchemeWithPowercfg()
    {
        var engage = Engage(PowerEntry());

        var script = File.ReadAllText(Path.Combine(new QuiescePaths(_dataRoot).SessionDir(engage.SessionId), "revert.cmd"));

        Assert.Contains($"powercfg /setactive {WellKnownPowerSchemes.Balanced:D}", script, StringComparison.OrdinalIgnoreCase);

        // The GUID, never the localized name: powercfg's aliases only cover Microsoft's own schemes.
        Assert.DoesNotContain("SCHEME_BALANCED", script, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------ catalog validation

    [Fact]
    public void APowerOnlyEntryCannotClaimItNeedsAdmin()
    {
        var entry = PowerEntry() with { RequiresAdmin = true };

        var ex = Assert.Throws<CatalogException>(() =>
            CatalogLoader.Validate(EngineTestHarness.CatalogOf(entry), "test"));

        Assert.Contains("does not need elevation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APowerOnlyEntryCannotClaimItNeedsAReboot()
    {
        var entry = PowerEntry() with { RequiresReboot = true };

        var ex = Assert.Throws<CatalogException>(() =>
            CatalogLoader.Validate(EngineTestHarness.CatalogOf(entry), "test"));

        Assert.Contains("takes effect the moment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEntryCannotSelectTwoSchemesAtOnce()
    {
        var entry = PowerEntry() with
        {
            Ops =
            [
                new PowerOpSpec { Scheme = WellKnownPowerSchemes.UltimatePerformance },
                new PowerOpSpec { Scheme = WellKnownPowerSchemes.HighPerformance },
            ],
        };

        var ex = Assert.Throws<CatalogException>(() =>
            CatalogLoader.Validate(EngineTestHarness.CatalogOf(entry), "test"));

        Assert.Contains("more than one power op", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptySchemeGuidIsRefused()
    {
        var ex = Assert.Throws<CatalogException>(() =>
            CatalogLoader.Validate(EngineTestHarness.CatalogOf(PowerEntry(Guid.Empty)), "test"));

        Assert.Contains("no scheme", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------- the engine without power

    /// <summary>
    /// A build wired without power control refuses with a reason rather than throwing or silently
    /// skipping — the same shape as the process layer's refusal when it has no classifier.
    /// </summary>
    [Fact]
    public void AnEngineWithNoPowerControlRefusesWithAReason()
    {
        var engine = new TransactionEngine(
            _registry,
            _activation,
            new QuiescePaths(Path.Combine(_dataRoot, "nopower")),
            new EngineInfo { AppVersion = "test", OsBuild = "10.0.26200", UserSid = EngineTestHarness.Sid });

        var step = Assert.Single(engine.Plan(EngineTestHarness.CatalogOf(PowerEntry()), "test").Steps);

        Assert.NotNull(step.RefusedReason);
        Assert.Contains("unavailable", step.RefusedReason!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- helpers

    private IReadOnlyList<JournalRecord> Journal(Guid sessionId) =>
        JournalReader.Read(Path.Combine(new QuiescePaths(_dataRoot).SessionDir(sessionId), "journal.jsonl")).Records;

    /// <summary>
    /// Rewrites the session's <c>sessionStart</c> boot id so recovery believes a reboot happened.
    /// </summary>
    /// <remarks>
    /// Editing the journal rather than faking a clock, because <c>QuiescePaths.IsSameBoot</c> is what
    /// recovery actually consults and this keeps the test on the real code path.
    /// </remarks>
    private void ForgeADifferentBoot(Guid sessionId)
    {
        var path = Path.Combine(new QuiescePaths(_dataRoot).SessionDir(sessionId), "journal.jsonl");
        var lines = File.ReadAllLines(path);

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("\"sessionStart\"", StringComparison.Ordinal))
            {
                lines[i] = System.Text.RegularExpressions.Regex.Replace(
                    lines[i], "\"bootId\":\"[^\"]*\"", "\"bootId\":\"forged-earlier-boot\"");
            }
        }

        File.WriteAllLines(path, lines);
    }
}
