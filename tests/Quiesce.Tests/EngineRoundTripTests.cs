using Quiesce.Core.Engine;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// The core promise: after engage + revert, the machine is byte-identical to before — including
/// the states most tools get wrong (absent values, missing keys, pre-existing data).
/// </summary>
public class EngineRoundTripTests : IDisposable
{
    private readonly EngineTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Absent_value_is_restored_to_absent_not_zero()
    {
        // The signature failure of every tool in this space: the value does not exist on a clean
        // machine, and "restore" writes 0 — a permanent behaviour change. Quiesce must delete.
        var entry = EngineTestHarness.DwordEntry(leanData: 0);
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target with { ValueName = "Sibling" }, EngineTestHarness.Dword(7)); // key exists, value doesn't

        var catalog = EngineTestHarness.CatalogOf(entry);
        var engage = _h.Engine.Engage(_h.Engine.Plan(catalog, "test"), FaultInjector.None);

        Assert.Equal(0u, _h.Registry.Peek(target)!.Data.GetUInt32());

        var revert = _h.Engine.RevertSession(engage.SessionId, "test");

        Assert.True(revert.Clean);
        Assert.Null(_h.Registry.Peek(target)); // absent again - NOT zero
        Assert.False(_h.State.IsDirty);
    }

    [Fact]
    public void Present_value_is_restored_to_its_exact_prior_data()
    {
        var entry = EngineTestHarness.DwordEntry(leanData: 0);
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);
        Assert.Equal(0u, _h.Registry.Peek(target)!.Data.GetUInt32());

        var revert = _h.Engine.RevertSession(engage.SessionId, "test");

        Assert.True(revert.Clean);
        Assert.Equal(1u, _h.Registry.Peek(target)!.Data.GetUInt32());
    }

    [Fact]
    public void Missing_key_chain_is_created_then_fully_removed_on_revert()
    {
        var entry = EngineTestHarness.DwordEntry(subkey: @"SOFTWARE\QuiesceTest\Did\Not\Exist");
        var target = EngineTestHarness.TargetOf(entry);
        Assert.False(_h.Registry.KeyExists(target));

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);
        Assert.NotNull(_h.Registry.Peek(target));

        _h.Engine.RevertSession(engage.SessionId, "test");

        Assert.False(_h.Registry.KeyExists(target)); // created keys removed, not left as litter
    }

    [Fact]
    public void Created_key_is_kept_if_someone_else_wrote_into_it()
    {
        var entry = EngineTestHarness.DwordEntry(subkey: @"SOFTWARE\QuiesceTest\Fresh");
        var target = EngineTestHarness.TargetOf(entry);

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        // A third party writes into the key Quiesce created.
        _h.Registry.Seed(target with { ValueName = "SomeoneElses" }, EngineTestHarness.Dword(42));

        _h.Engine.RevertSession(engage.SessionId, "test");

        Assert.Null(_h.Registry.Peek(target));                                        // our value gone
        Assert.True(_h.Registry.KeyExists(target));                                   // key preserved
        Assert.NotNull(_h.Registry.Peek(target with { ValueName = "SomeoneElses" })); // their value intact
    }

    [Fact]
    public void Value_already_lean_is_elided_and_revert_never_touches_it()
    {
        // No-op elision: the user had already set this themselves. Restore must not "restore" it.
        var entry = EngineTestHarness.DwordEntry(leanData: 0);
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(0));

        var plan = _h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test");
        Assert.True(plan.Steps.Single().NoOp);

        var engage = _h.Engine.Engage(plan, FaultInjector.None);

        Assert.Equal(0, engage.Applied);
        Assert.False(_h.State.IsDirty); // nothing was touched, machine is not dirty
        Assert.Empty(TransactionEngine.PendingSteps(_h.Journal(engage.SessionId)));
        Assert.Equal(0u, _h.Registry.Peek(target)!.Data.GetUInt32());
    }

    [Fact]
    public void Value_changed_by_someone_else_after_apply_is_kept_not_clobbered()
    {
        var entry = EngineTestHarness.DwordEntry(leanData: 0);
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        // Windows Update / the user / another tool changes the value after our apply.
        _h.Registry.Seed(target, EngineTestHarness.Dword(5));

        var revert = _h.Engine.RevertSession(engage.SessionId, "test");

        // Overwriting their 5 with our captured 1 would destroy config we did not create.
        Assert.Equal(5u, _h.Registry.Peek(target)!.Data.GetUInt32());
        Assert.Contains(revert.Messages, m => m.Contains("kept current"));
        Assert.True(revert.Clean); // conflict-kept-current is a resolved outcome, not a failure
    }

    [Fact]
    public void Activation_broadcasts_are_reissued_on_revert_from_journal_alone()
    {
        // Revert must re-broadcast without the catalog: registry bytes back + no broadcast means
        // the session keeps running on the tweaked behaviour until sign-out.
        var entry = EngineTestHarness.DwordEntry(activation: [Quiesce.Core.Catalog.ActivationKind.ShChangeNotify]);
        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        _h.Broadcaster.Broadcasts.Clear();
        _h.Engine.RevertSession(engage.SessionId, "test");

        Assert.Contains(Quiesce.Core.Catalog.ActivationKind.ShChangeNotify, _h.Broadcaster.Broadcasts);
    }

    [Fact]
    public void Revert_is_idempotent()
    {
        var entry = EngineTestHarness.DwordEntry();
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        var first = _h.Engine.RevertSession(engage.SessionId, "test");
        var second = _h.Engine.RevertSession(engage.SessionId, "test");

        Assert.Equal(1, first.Reverted);
        Assert.Equal(0, second.Reverted); // nothing left to do, no error
        Assert.Equal(1u, _h.Registry.Peek(target)!.Data.GetUInt32());
    }

    [Fact]
    public void Engaging_while_dirty_is_refused()
    {
        // Engaging twice would capture session 1's tweaks as session 2's "original" state.
        var entry = EngineTestHarness.DwordEntry();
        _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        var again = _h.Engine.Plan(EngineTestHarness.CatalogOf(EngineTestHarness.DwordEntry(id: "test.other", valueName: "Other")), "test");

        Assert.Throws<InvalidOperationException>(() => _h.Engine.Engage(again, FaultInjector.None));
    }

    [Fact]
    public void Journal_is_self_sufficient_revert_needs_no_catalog()
    {
        // RevertSession's signature takes no catalog — this test locks in that the records alone
        // fully describe the undo, which is what keeps the panic binary catalog-free.
        var entry = EngineTestHarness.DwordEntry();
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        // No catalog object in scope from here on.
        var freshEngine = new TransactionEngine(_h.Registry, _h.Broadcaster, _h.Paths, new EngineInfo
        {
            AppVersion = "0.0.0-test",
            OsBuild = "10.0.26200",
            UserSid = EngineTestHarness.Sid,
        });

        var revert = freshEngine.RevertSession(engage.SessionId, "panic");

        Assert.True(revert.Clean);
        Assert.Equal(1u, _h.Registry.Peek(target)!.Data.GetUInt32());
    }
}
