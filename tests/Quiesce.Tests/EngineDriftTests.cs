using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// Drift detection: does the machine still hold what this session applied, and where does it not?
/// </summary>
/// <remarks>
/// Read-only throughout. Nothing here writes to a machine, a journal or <c>state.json</c>, which is why
/// this whole capability can ship before anything that acts on it.
/// </remarks>
public class EngineDriftTests : IDisposable
{
    private readonly EngineTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void An_engaged_session_that_still_holds_its_values_reports_no_drift()
    {
        var entry = EngineTestHarness.DwordEntry();
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(
            _h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        var drift = _h.Engine.DetectDrift(engage.SessionId);

        Assert.False(drift.Unknown);
        Assert.False(drift.Any);
        Assert.Empty(drift.Resyncable);
    }

    [Fact]
    public void A_registry_value_changed_since_engage_is_reported_and_refused()
    {
        var entry = EngineTestHarness.DwordEntry();
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(
            _h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        // Something else sets it to 2 - neither the prior (1) nor what Quiesce wrote (0).
        _h.Registry.Seed(target, EngineTestHarness.Dword(2));

        var item = Assert.Single(_h.Engine.DetectDrift(engage.SessionId).Items);

        Assert.Equal(DriftKind.RegistryChanged, item.Kind);
        Assert.False(item.Resyncable);
        Assert.NotNull(item.NotResyncableReason);
        Assert.Contains("Restore keeps your value here too", item.NotResyncableReason);
    }

    [Fact]
    public void Drift_and_revert_agree_about_a_changed_value()
    {
        // THE TEST THAT STOPS THE TWO DIVERGING, and the reason it is worth its own name. The drift
        // detector shares RegistryHoldsIntended with RevertSession's conflict test, but the service, power
        // and throttle conflict tests ask a deliberately different question - "did a third party change
        // it", which also compares against the prior - and were left alone rather than bent into a shape
        // they do not have. So the guarantee cannot be structural for those, and has to be asserted:
        // whenever drift says a value changed, revert must decline to overwrite it, and say so.
        var entry = EngineTestHarness.DwordEntry();
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(
            _h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        _h.Registry.Seed(target, EngineTestHarness.Dword(2));

        var drift = _h.Engine.DetectDrift(engage.SessionId);
        Assert.Single(drift.Items);
        Assert.Equal(DriftKind.RegistryChanged, drift.Items[0].Kind);

        var revert = _h.Engine.RevertSession(engage.SessionId, "restore");

        // Revert kept the user's value, exactly as the drift report promised it would.
        Assert.Equal(2u, _h.Registry.Peek(target)!.Data.GetUInt32());
        Assert.Contains(
            _h.Journal(engage.SessionId).OfType<RevertedRecord>(),
            r => r.Outcome == "conflict-kept-current");
        Assert.Contains(revert.Messages, m => m.Contains("kept current", StringComparison.Ordinal));
    }

    [Fact]
    public void A_value_put_back_to_its_prior_by_hand_is_still_drift()
    {
        // Subtle and worth pinning: the user setting the value back to what it was before Engage is drift
        // too - the machine no longer holds what the session applied. Revert treats it differently (it can
        // restore the prior over a value that already equals the prior, harmlessly), which is why drift is
        // defined as "does it hold INTENDED" and not as "is there a conflict".
        var entry = EngineTestHarness.DwordEntry();
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(
            _h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var item = Assert.Single(_h.Engine.DetectDrift(engage.SessionId).Items);
        Assert.Equal(DriftKind.RegistryChanged, item.Kind);
    }

    [Fact]
    public void A_reverted_step_is_no_longer_compared()
    {
        // Drift is computed over PendingSteps, so a step that has been reverted drops out. Otherwise every
        // restored machine would report drift on everything it just put back.
        var entry = EngineTestHarness.DwordEntry();
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(
            _h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);
        _h.Engine.RevertSession(engage.SessionId, "restore");

        Assert.False(_h.Engine.DetectDrift(engage.SessionId).Any);
    }

    [Fact]
    public void An_unreadable_journal_reports_UNKNOWN_and_never_in_sync()
    {
        // The File.Exists trap in a new place. The data root is hardened to Administrators, so an
        // unelevated caller gets an exception rather than an answer - and "no drift" for "could not look"
        // is the reassuring lie this codebase has already fixed in five other places.
        var drift = _h.Engine.DetectDrift(Guid.NewGuid());

        Assert.True(drift.Unknown);
        Assert.False(drift.Any);
        Assert.NotNull(drift.UnknownReason);
        Assert.Contains("cannot tell", drift.UnknownReason);
    }

    [Fact]
    public void Nothing_in_a_drift_check_writes_anything()
    {
        // The property the whole read-only claim rests on, asserted rather than assumed: journal bytes and
        // state.json unchanged across a detect that FINDS drift. If this ever fails, a tray menu opening
        // has become a mutation.
        var entry = EngineTestHarness.DwordEntry();
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(
            _h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);
        _h.Registry.Seed(target, EngineTestHarness.Dword(2));

        var journalPath = Path.Combine(_h.Paths.SessionDir(engage.SessionId), "journal.jsonl");
        var journalBefore = File.ReadAllBytes(journalPath);
        var stateBefore = File.ReadAllBytes(Path.Combine(_h.DataRoot, "state.json"));

        Assert.True(_h.Engine.DetectDrift(engage.SessionId).Any);

        Assert.Equal(journalBefore, File.ReadAllBytes(journalPath));
        Assert.Equal(stateBefore, File.ReadAllBytes(Path.Combine(_h.DataRoot, "state.json")));
    }

    [Fact]
    public void A_registry_step_that_was_already_lean_is_invisible_to_drift()
    {
        // Journal-derived, not plan-derived, and this is the case that proves it. An already-lean step
        // journals NOTHING at engage - the apply path elides before it appends - so there is no record to
        // compare against and drift must not invent one. A plan-derived detector would report this value as
        // drifted the moment anything changed it, and a plan-derived RESYNC would then journal a fresh
        // record for a step this session never touched, changing what boot recovery does to the whole
        // session by changing its pending scope mix.
        var entry = EngineTestHarness.DwordEntry();
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(0)); // already lean

        var engage = _h.Engine.Engage(
            _h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        Assert.DoesNotContain(_h.Journal(engage.SessionId).OfType<ApplyingRecord>(), r => r.EntryId == entry.Id);

        _h.Registry.Seed(target, EngineTestHarness.Dword(9));

        Assert.False(_h.Engine.DetectDrift(engage.SessionId).Any);
    }
}
