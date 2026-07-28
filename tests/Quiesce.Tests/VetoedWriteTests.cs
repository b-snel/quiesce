using Quiesce.Core;
using Quiesce.Core.Engine;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// Plan-time refusal of writes the OS vetoes in the kernel, so they are never attempted.
/// </summary>
public class OsVetoPlanTests : IDisposable
{
    private readonly EngineTestHarness _h = new();

    public OsVetoPlanTests() => KernelRegistryFilter.OverrideForTests = true;

    public void Dispose()
    {
        KernelRegistryFilter.OverrideForTests = null;
        KernelRegistryFilter.ResetCache();
        _h.Dispose();
    }

    // The real vetoed pair, as the catalog spells it.
    private const string VetoedSubkey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private static Quiesce.Core.Catalog.CatalogEntry WidgetsButtonEntry(int leanData = 0) =>
        EngineTestHarness.DwordEntry(
            id: "shell.hide-widgets-button", subkey: VetoedSubkey, valueName: "TaskbarDa", leanData: leanData);

    [Fact]
    public void The_vetoed_pair_is_refused_at_plan_time_and_never_runs()
    {
        var entry = WidgetsButtonEntry();
        _h.Registry.Seed(EngineTestHarness.TargetOf(entry), EngineTestHarness.Dword(1)); // not yet lean

        var plan = _h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test");
        var step = Assert.Single(plan.Steps);

        Assert.False(step.WillRun);
        Assert.NotNull(step.RefusedReason);
        Assert.Contains("UCPD", step.RefusedReason);
        Assert.Empty(plan.EffectiveSteps);
        Assert.Single(plan.RefusedSteps);
    }

    [Fact]
    public void Already_lean_beats_refused_so_a_healthy_entry_is_not_made_scary()
    {
        // TaskbarDa is BOTH vetoed and already lean on the development machine. If refusal were
        // evaluated first, that entry would announce "Windows blocks this" about a write that was
        // never going to happen — alarming, and false.
        var entry = WidgetsButtonEntry();
        _h.Registry.Seed(EngineTestHarness.TargetOf(entry), EngineTestHarness.Dword(0)); // already lean

        var step = Assert.Single(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test").Steps);

        Assert.True(step.NoOp);
        Assert.Null(step.RefusedReason);
        Assert.False(step.WillRun);
    }

    [Fact]
    public void The_veto_is_scoped_to_the_exact_key_and_value_pair()
    {
        // Mirrors the probe matrix measured on the machine: same name elsewhere is fine, and a
        // different name in the same key is fine. A guardrail broader than the evidence would
        // silently disable working tweaks.
        var sameNameOtherKey = EngineTestHarness.DwordEntry(
            id: "other.key", subkey: @"Software\QuiesceTest\Elsewhere", valueName: "TaskbarDa", leanData: 0);
        var otherNameSameKey = EngineTestHarness.DwordEntry(
            id: "other.value", subkey: VetoedSubkey, valueName: "TaskbarDaZ", leanData: 0);

        foreach (var entry in new[] { sameNameOtherKey, otherNameSameKey })
        {
            _h.Registry.Seed(EngineTestHarness.TargetOf(entry), EngineTestHarness.Dword(1));
            var step = Assert.Single(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test").Steps);
            Assert.True(step.WillRun, $"{entry.Id} must plan normally");
        }
    }

    [Fact]
    public void Hive_is_part_of_the_match_so_an_hklm_write_is_not_refused_by_an_hkcu_observation()
    {
        // Explorer\Advanced exists under both hives. TaskbarDa was measured refused under HKCU
        // only, and the guardrail must not extrapolate to HKLM from that.
        Assert.True(Guardrails.RefuseRegistryWrite("HKCU", VetoedSubkey, "TaskbarDa", out _));
        Assert.False(Guardrails.RefuseRegistryWrite("HKLM", VetoedSubkey, "TaskbarDa", out _));
    }

    [Fact]
    public void Nothing_is_refused_when_the_driver_is_not_running()
    {
        // The gate is "is the driver active", never "this never works". If Microsoft drops the pair
        // or the driver stops loading, the tweak must come back on its own.
        KernelRegistryFilter.OverrideForTests = false;

        Assert.False(Guardrails.RefuseRegistryWrite("HKCU", VetoedSubkey, "TaskbarDa", out _));

        var entry = WidgetsButtonEntry();
        _h.Registry.Seed(EngineTestHarness.TargetOf(entry), EngineTestHarness.Dword(1));
        Assert.True(Assert.Single(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test").Steps).WillRun);
    }

    [Fact]
    public void A_refused_step_leaves_the_machine_completely_untouched()
    {
        // The whole point of moving this to plan time. Attempting the write would create the key
        // (SetValue calls CreateSubKey first), get vetoed, roll the entry back, and leave an empty
        // key Quiesce is then refused permission to delete.
        var entry = WidgetsButtonEntry();
        var target = EngineTestHarness.TargetOf(entry);

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        Assert.Equal(0, engage.Applied);
        Assert.Empty(engage.RolledBackEntries);
        Assert.Empty(_h.Registry.Log);
        Assert.False(_h.Registry.KeyExists(target));
    }
}

/// <summary>
/// A value that Windows refuses to let anyone write, on a key that is otherwise fully writable.
/// </summary>
/// <remarks>
/// Observed on a real Windows 11 machine: writes to
/// <c>HKLM\SOFTWARE\Policies\Microsoft\Dsh!AllowNewsAndInterests</c> are vetoed on that exact
/// (key, value name) pair, from an elevated process, with BUILTIN\Administrators holding
/// FullControl. The same key accepts every other value name; the same value name is accepted in
/// other keys. It is a kernel registry callback, not a permission.
///
/// The veto covers the DELETE as well as the write, and that is what made it dangerous rather than
/// merely annoying.
/// </remarks>
public class VetoedWriteTests : IDisposable
{
    private readonly EngineTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void A_vetoed_write_rolls_back_and_leaves_the_session_revertible()
    {
        // The failure that motivated this: apply is refused, so the value is never created; the
        // captured prior is ValueAbsent; and revert then tries to DELETE the already-absent value
        // and is vetoed in turn. The session reported "machine still DIRTY" over a value that had
        // never changed, and retrying could never clear it because the retry performs the same
        // forbidden no-op.
        var entry = EngineTestHarness.DwordEntry(id: "shell.vetoed", valueName: "Vetoed");
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.VetoWritesTo(target);

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        // Apply refused, entry rolled back, nothing written.
        Assert.Null(_h.Registry.Peek(target));

        var revert = _h.Engine.RevertSession(engage.SessionId, "test");

        Assert.True(revert.Clean, $"revert must close cleanly; failures: {string.Join(" | ", revert.Messages)}");
        Assert.Null(_h.Registry.Peek(target));
    }

    [Fact]
    public void Restoring_a_prior_that_already_holds_performs_no_write_at_all()
    {
        // The general rule the fix encodes, independent of any veto: if the end state is already
        // correct, do not issue the operation. Asserted on the registry Log so it cannot be
        // satisfied by an exception that happens to be swallowed somewhere.
        //
        // The key must already exist so the captured prior is ValueAbsent rather than KeyAbsent. A
        // KeyAbsent prior legitimately still owes a created-key cleanup, so it is not a no-op even
        // when the value itself is already gone - which is what the first version of this test got
        // wrong.
        var entry = EngineTestHarness.DwordEntry(id: "shell.noop-restore", valueName: "AlreadyRight");
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target with { ValueName = "SomethingElse" }, EngineTestHarness.Dword(7));

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);
        Assert.NotNull(_h.Registry.Peek(target));

        // Put the machine back by hand, exactly as the prior recorded it: absent.
        _h.Registry.DeleteValue(target);
        _h.Registry.Log.Clear();

        var revert = _h.Engine.RevertSession(engage.SessionId, "test");

        Assert.True(revert.Clean);
        Assert.DoesNotContain(_h.Registry.Log, l => l.StartsWith("del ", StringComparison.Ordinal));
        Assert.DoesNotContain(_h.Registry.Log, l => l.StartsWith("set ", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unremovable_created_key_is_reported_as_residue_not_a_failed_revert()
    {
        // Observed end state of the real incident: Quiesce created
        // HKLM\SOFTWARE\Policies\Microsoft\Dsh on the way to a write that was then vetoed, and was
        // afterwards refused permission to delete the empty key it had just created. Treating that
        // as a revert failure wedged the session - "machine still DIRTY", forever, over a key
        // holding nothing, with every retry hitting the same refusal.
        //
        // The value is what governs behaviour and it is restored. The leftover empty key is
        // residue: reported, never silent, but not a reason to refuse to close the session.
        var entry = EngineTestHarness.DwordEntry(id: "shell.created-key", valueName: "Vetoed");
        var target = EngineTestHarness.TargetOf(entry);

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);
        _h.Registry.RefuseCreatedKeyCleanup = true;

        var revert = _h.Engine.RevertSession(engage.SessionId, "test");

        Assert.True(revert.Clean, $"session must close; failures: {string.Join(" | ", revert.Messages)}");
        Assert.Null(_h.Registry.Peek(target));
        Assert.Contains(revert.Messages, m => m.Contains("could not remove the empty key", StringComparison.Ordinal));
    }

    [Fact]
    public void A_value_present_prior_that_already_matches_is_not_rewritten()
    {
        var entry = EngineTestHarness.DwordEntry(id: "shell.prior-present", valueName: "HadAValue", leanData: 0);
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        // Restore the prior by hand, then revert: the engine must notice and do nothing.
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));
        _h.Registry.Log.Clear();

        var revert = _h.Engine.RevertSession(engage.SessionId, "test");

        Assert.True(revert.Clean);
        Assert.Empty(_h.Registry.Log);
        Assert.True(EngineTestHarness.Dword(1).DataEquals(_h.Registry.Peek(target)!));
    }
}
