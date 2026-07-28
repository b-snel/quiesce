using System.Text.Json;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;

namespace Quiesce.Tests;

/// <summary>Crash injection, the recovery predicate, entry atomicity, and hive deferral.</summary>
public class EngineRecoveryTests : IDisposable
{
    private readonly EngineTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Crash_mid_apply_is_recovered_by_the_next_recover_pass()
    {
        // Two entries; the fault fires after the first entry's step is applied, simulating a
        // crash between entries. No committed record is written; state stays dirty.
        var e1 = EngineTestHarness.DwordEntry(id: "test.first", valueName: "First");
        var e2 = EngineTestHarness.DwordEntry(id: "test.second", valueName: "Second");
        var t1 = EngineTestHarness.TargetOf(e1);
        _h.Registry.Seed(t1, EngineTestHarness.Dword(1));

        var plan = _h.Engine.Plan(EngineTestHarness.CatalogOf(e1, e2), "test");
        Assert.Throws<FaultInjectedException>(() => _h.Engine.Engage(plan, FaultInjector.Parse("afterStep1")));

        Assert.True(_h.State.IsDirty);
        Assert.Equal(0u, _h.Registry.Peek(t1)!.Data.GetUInt32()); // half-applied

        var result = _h.Engine.Recover();

        Assert.NotNull(result);
        Assert.True(result!.Clean);
        Assert.Equal(1u, _h.Registry.Peek(t1)!.Data.GetUInt32()); // unwound
        Assert.False(_h.State.IsDirty);
    }

    [Fact]
    public void Recovery_predicate_is_isDirty_not_absence_of_committed()
    {
        // THE critical fix from review: a successfully engaged machine has a committed journal AND
        // is dirty. A recovery keyed on "no committed record" would skip it — making the most
        // common dirty state (engaged, then power loss) invisible to every automatic net.
        var entry = EngineTestHarness.DwordEntry(scope: TweakScope.Session);
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        var journal = _h.Journal(engage.SessionId);
        Assert.Contains(journal, r => r is CommittedRecord); // apply DID finish
        Assert.True(_h.State.IsDirty);                       // and the machine IS dirty

        var result = _h.Engine.Recover();

        // Same boot: recovery must NOT auto-revert a live gaming session out from under the user.
        Assert.NotNull(result);
        Assert.Equal(0, result!.Reverted);
        Assert.Equal(0u, _h.Registry.Peek(target)!.Data.GetUInt32());
        Assert.True(_h.State.IsDirty);
    }

    [Fact]
    public void Persistent_tweaks_survive_recovery_but_are_reverted_by_explicit_restore()
    {
        // Persistent scope = standing preference (debloat, telemetry). Recovery must never
        // auto-revert those, or every reboot undoes the user's chosen configuration.
        var entry = EngineTestHarness.DwordEntry(scope: TweakScope.Persistent);
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        var recovery = _h.Engine.Recover();
        Assert.Equal(0, recovery!.Reverted);
        Assert.Equal(0u, _h.Registry.Peek(target)!.Data.GetUInt32()); // still applied

        var restore = _h.Engine.RevertSession(engage.SessionId, "restore");
        Assert.True(restore.Clean);
        Assert.Equal(1u, _h.Registry.Peek(target)!.Data.GetUInt32()); // explicit revert works
    }

    [Fact]
    public void Multi_op_entry_failing_verification_rolls_back_the_whole_entry()
    {
        // Entry-level atomicity: op 2 of 2 fails verification -> op 1 must be unwound too, not
        // left half-applied with the UI showing a binary state for a non-binary machine.
        var entry = EngineTestHarness.DwordEntry(id: "test.multi") with
        {
            Ops =
            [
                EngineTestHarness.DwordEntry(valueName: "OpA").Ops[0],
                EngineTestHarness.DwordEntry(valueName: "OpB").Ops[0],
            ],
        };

        var tA = EngineTestHarness.TargetOf(entry, 0);
        var tB = EngineTestHarness.TargetOf(entry, 1);
        _h.Registry.Seed(tA, EngineTestHarness.Dword(1));
        _h.Registry.Seed(tB, EngineTestHarness.Dword(1));

        var plan = _h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test");
        var sabotage = new SabotagedRegistry(_h.Registry, failVerifyOn: tB.ValueName);
        var engine = new TransactionEngine(sabotage, _h.Broadcaster, _h.Paths, new EngineInfo
        {
            AppVersion = "0.0.0-test",
            OsBuild = "10.0.26200",
            UserSid = EngineTestHarness.Sid,
        });

        var result = engine.Engage(plan, FaultInjector.None);

        Assert.Contains("test.multi", result.RolledBackEntries);
        Assert.Equal(1u, _h.Registry.Peek(tA)!.Data.GetUInt32()); // op A unwound
        Assert.Equal(1u, _h.Registry.Peek(tB)!.Data.GetUInt32()); // op B never stuck
    }

    [Fact]
    public void Unloaded_user_hive_defers_revert_and_keeps_the_machine_dirty()
    {
        // Deferral, not skip-as-done: writing revertComplete for steps that could not run is how a
        // tool permanently orphans per-user changes while claiming success.
        var entry = EngineTestHarness.DwordEntry();
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        _h.Registry.UnloadUserHive(EngineTestHarness.Sid); // user signed out; hive gone

        var revert = _h.Engine.RevertSession(engage.SessionId, "recover");

        Assert.Equal(0, revert.Reverted);
        Assert.Equal(1, revert.Deferred);
        Assert.False(revert.Clean);
        Assert.True(_h.State.IsDirty); // still dirty - the deferred step is owed

        _h.Registry.LoadUserHive(EngineTestHarness.Sid);
        _h.Registry.Seed(target, EngineTestHarness.Dword(0)); // hive returns with the tweak applied

        var second = _h.Engine.RevertSession(engage.SessionId, "recover");

        Assert.Equal(1, second.Reverted);
        Assert.True(second.Clean);
        Assert.Equal(1u, _h.Registry.Peek(target)!.Data.GetUInt32());
        Assert.False(_h.State.IsDirty);
    }

    [Fact]
    public void Dirty_before_first_mutation_crash_between_planned_and_apply_recovers_to_clean()
    {
        // The fault fires "after step 0", i.e. before anything is applied but after the dirty
        // flag is set. Recovery must complete cleanly with zero steps.
        var entry = EngineTestHarness.DwordEntry();
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        // Simulate by engaging with a registry that dies on first write.
        var dying = new SabotagedRegistry(_h.Registry, throwOnSet: true);
        var engine = new TransactionEngine(dying, _h.Broadcaster, _h.Paths, new EngineInfo
        {
            AppVersion = "0.0.0-test",
            OsBuild = "10.0.26200",
            UserSid = EngineTestHarness.Sid,
        });

        Assert.ThrowsAny<IOException>(() =>
            engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None));

        Assert.True(_h.State.IsDirty);

        var result = _h.Engine.Recover();

        Assert.NotNull(result);
        Assert.True(result!.Clean);
        Assert.False(_h.State.IsDirty);
        Assert.Equal(1u, _h.Registry.Peek(target)!.Data.GetUInt32()); // untouched
    }

    /// <summary>Wraps the fake to sabotage specific operations.</summary>
    private sealed class SabotagedRegistry(FakeRegistry inner, string? failVerifyOn = null, bool throwOnSet = false)
        : Quiesce.Core.Platform.IRegistry
    {
        public Quiesce.Core.Platform.RegistryProbe Probe(Quiesce.Core.Platform.RegistryTarget target) =>
            inner.Probe(target);

        public string? SetValue(Quiesce.Core.Platform.RegistryTarget target, Quiesce.Core.Platform.RegistryData data)
        {
            if (throwOnSet)
            {
                throw new IOException("simulated hardware/ACL failure on write");
            }

            // Simulate a silently-swallowed write (Tamper Protection style): the API "succeeds"
            // but the value never changes, so the post-apply verification probe sees the original.
            if (failVerifyOn is not null
                && target.ValueName.Equals(failVerifyOn, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return inner.SetValue(target, data);
        }

        public void DeleteValue(Quiesce.Core.Platform.RegistryTarget target) => inner.DeleteValue(target);

        public void DeleteCreatedKeysIfEmpty(Quiesce.Core.Platform.RegistryTarget target, string relativeCreatedPath) =>
            inner.DeleteCreatedKeysIfEmpty(target, relativeCreatedPath);

        public bool UserHiveLoaded(string sid) => inner.UserHiveLoaded(sid);
    }
}
