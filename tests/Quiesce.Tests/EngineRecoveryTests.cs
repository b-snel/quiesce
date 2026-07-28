using System.Text.Json;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

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
    public void Boot_id_sampling_jitter_is_not_mistaken_for_a_reboot()
    {
        // CurrentBootId() samples the clock and the uptime counter separately, so two calls in the
        // same boot can land on different seconds. Comparing for exact equality made recovery
        // intermittently decide the machine had rebooted and auto-revert a live session - pulling
        // tweaks out from under a running game. This pins the tolerance.
        var recorded = QuiescePaths.CurrentBootId();
        var shifted = (long.Parse(recorded, System.Globalization.CultureInfo.InvariantCulture) - 1)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(QuiescePaths.IsSameBoot(recorded), "the current boot id must match itself");
        Assert.True(QuiescePaths.IsSameBoot(shifted), "a one-second sampling shift is not a reboot");

        // A genuine reboot moves boot time by far more than the jitter window.
        var muchEarlier = (long.Parse(recorded, System.Globalization.CultureInfo.InvariantCulture) - 3600)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(QuiescePaths.IsSameBoot(muchEarlier), "an hour's difference is a different boot");
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
    public void Recovery_after_a_reboot_reverts_the_session_half_and_leaves_the_standing_preference()
    {
        // THE MIXED SESSION, which had never been tested and is the NORMAL case. Recover's own comment
        // says session-scoped steps are auto-reverted once the boot has passed while persistent-scoped
        // standing preferences are "never auto-reverted by recovery" - but it implemented that by handing
        // the whole session to RevertSession, which had no scope filter. So the distinction held only for
        // a session that happened to be entirely one scope.
        //
        // Every existing test was exactly that: Persistent_tweaks_survive_recovery uses a Persistent-only
        // session, and EngineTestHarness.DwordEntry defaults to Persistent. Meanwhile the real default
        // profile ships apps.close-browsers, whose close journals Scope = Session - so any real machine
        // with one sign-in preference is mixed, and a reboot silently put the preference back while
        // StartupPage said "This one stays in force across reboots."
        var session = EngineTestHarness.DwordEntry(
            id: "test.session", valueName: "SessionValue", scope: TweakScope.Session);
        var persistent = EngineTestHarness.DwordEntry(
            id: "test.persistent", valueName: "PersistentValue", scope: TweakScope.Persistent);

        var sessionTarget = EngineTestHarness.TargetOf(session);
        var persistentTarget = EngineTestHarness.TargetOf(persistent);
        _h.Registry.Seed(sessionTarget, EngineTestHarness.Dword(1));
        _h.Registry.Seed(persistentTarget, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(
            _h.Engine.Plan(EngineTestHarness.CatalogOf(session, persistent), "test"), FaultInjector.None);

        Assert.Equal(0u, _h.Registry.Peek(sessionTarget)!.Data.GetUInt32());
        Assert.Equal(0u, _h.Registry.Peek(persistentTarget)!.Data.GetUInt32());

        ForgeADifferentBoot(engage.SessionId);

        var recovery = _h.Engine.Recover();

        Assert.NotNull(recovery);
        Assert.Equal(1, recovery!.Reverted);

        // The session step is back...
        Assert.Equal(1u, _h.Registry.Peek(sessionTarget)!.Data.GetUInt32());

        // ...and the standing preference is STILL APPLIED. This is the assertion the bug failed.
        Assert.Equal(0u, _h.Registry.Peek(persistentTarget)!.Data.GetUInt32());

        // And the machine stays dirty, which is the half a naive fix gets wrong: Clean means "nothing
        // deferred and nothing failed", not "nothing left". Clearing the flag here would leave a
        // persistent step applied with no session marked dirty - the one state none of the four recovery
        // nets looks for, because every one of them keys on isDirty.
        Assert.True(recovery.Clean);
        Assert.True(_h.State.IsDirty);
        Assert.Equal(engage.SessionId, _h.State.ActiveSessionId);

        // Said out loud rather than left for the user to notice.
        Assert.Contains(recovery.Messages, m => m.Contains("left applied on purpose", StringComparison.Ordinal));

        // No revertComplete record: the session genuinely is not complete, and baseline-diff.ps1 keys
        // "is a session outstanding" on applied > 0 && revertComplete == 0.
        Assert.DoesNotContain(_h.Journal(engage.SessionId), r => r is RevertCompleteRecord);

        // And an explicit Restore afterwards still finishes the job and clears the flag.
        var restore = _h.Engine.RevertSession(engage.SessionId, "restore");

        Assert.True(restore.Clean);
        Assert.Equal(1u, _h.Registry.Peek(persistentTarget)!.Data.GetUInt32());
        Assert.False(_h.State.IsDirty);
    }

    [Fact]
    public void An_unfiltered_revert_still_writes_revertComplete_and_clears_the_flag()
    {
        // The control for the test above: the ordinary path must be untouched by the filter's existence.
        var entry = EngineTestHarness.DwordEntry(scope: TweakScope.Session);
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = _h.Engine.Engage(
            _h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        var restore = _h.Engine.RevertSession(engage.SessionId, "restore");

        Assert.True(restore.Clean);
        Assert.False(_h.State.IsDirty);
        Assert.Contains(_h.Journal(engage.SessionId), r => r is RevertCompleteRecord);
        Assert.DoesNotContain(restore.Messages, m => m.Contains("left applied on purpose", StringComparison.Ordinal));
    }

    /// <summary>
    /// Rewrites the session's <c>sessionStart</c> boot id so recovery believes a reboot happened.
    /// </summary>
    /// <remarks>
    /// Editing the journal rather than faking a clock, because <c>QuiescePaths.IsSameBoot</c> is what
    /// recovery actually consults and this keeps the test on the real code path. Same approach as
    /// <c>PowerSchemeTests</c>, which needed it first.
    /// </remarks>
    private void ForgeADifferentBoot(Guid sessionId)
    {
        var path = Path.Combine(_h.Paths.SessionDir(sessionId), "journal.jsonl");
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
        //
        // The sabotage must throw something the engine treats as a genuine CRASH, not as a refused
        // write. IOException and UnauthorizedAccessException are both handled outcomes now: the
        // entry rolls back and Engage returns normally, which is correct behaviour but does not
        // leave the dirty state this test needs. Using one of those made the test pass for the
        // wrong reason - it was pinning an escaping exception thrown by the ROLLBACK re-writing a
        // prior that had never changed, which is the bug RestorePrior's already-restored check
        // removed. See Refused_write_no_longer_escapes_engage below for the handled path.
        var dying = new SabotagedRegistry(_h.Registry, crashOnSet: true);
        var engine = new TransactionEngine(dying, _h.Broadcaster, _h.Paths, new EngineInfo
        {
            AppVersion = "0.0.0-test",
            OsBuild = "10.0.26200",
            UserSid = EngineTestHarness.Sid,
        });

        Assert.ThrowsAny<InvalidProgramException>(() =>
            engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None));

        Assert.True(_h.State.IsDirty);

        var result = _h.Engine.Recover();

        Assert.NotNull(result);
        Assert.True(result!.Clean);
        Assert.False(_h.State.IsDirty);
        Assert.Equal(1u, _h.Registry.Peek(target)!.Data.GetUInt32()); // untouched
    }

    [Fact]
    public void Refused_write_no_longer_escapes_engage()
    {
        // Before RestorePrior checked the end state first, a refused write took this path: apply
        // throws, the entry rolls back, the rollback re-writes a prior that had never changed, the
        // same registry refuses THAT too, and the exception escapes Engage - crashing the caller
        // mid-apply, which is exactly what the typed write-failure diagnosis was introduced to stop.
        // The forward write was guarded; the rollback was not.
        var entry = EngineTestHarness.DwordEntry();
        var target = EngineTestHarness.TargetOf(entry);
        _h.Registry.Seed(target, EngineTestHarness.Dword(1));

        var refusing = new SabotagedRegistry(_h.Registry, throwOnSet: true);
        var engine = new TransactionEngine(refusing, _h.Broadcaster, _h.Paths, new EngineInfo
        {
            AppVersion = "0.0.0-test",
            OsBuild = "10.0.26200",
            UserSid = EngineTestHarness.Sid,
        });

        var engage = engine.Engage(_h.Engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        Assert.Contains(entry.Id, engage.RolledBackEntries);
        Assert.Equal(1u, _h.Registry.Peek(target)!.Data.GetUInt32()); // untouched

        // Not asserting on IsDirty: a session that rolled every entry back still holds the flag
        // until it is reverted. That is conservative rather than wrong, and it is not what this
        // test is about - the point is that Engage RETURNED instead of throwing.
    }

    /// <summary>Wraps the fake to sabotage specific operations.</summary>
    private sealed class SabotagedRegistry(
        FakeRegistry inner,
        string? failVerifyOn = null,
        bool throwOnSet = false,
        bool crashOnSet = false)
        : Quiesce.Core.Platform.IRegistry
    {
        public Quiesce.Core.Platform.RegistryProbe Probe(Quiesce.Core.Platform.RegistryTarget target) =>
            inner.Probe(target);

        public string? SetValue(Quiesce.Core.Platform.RegistryTarget target, Quiesce.Core.Platform.RegistryData data)
        {
            // Not in the engine's handled set, so it escapes Engage and models a real crash.
            if (crashOnSet)
            {
                throw new InvalidProgramException("simulated process death mid-apply");
            }

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
