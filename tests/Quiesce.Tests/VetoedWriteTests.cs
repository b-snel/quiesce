using Quiesce.Core.Engine;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

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
