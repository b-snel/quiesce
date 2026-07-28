using System.Text.Json;
using Quiesce.Core;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

public class ServiceEngineTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "quiesce-svc", Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _registry = new();
    private readonly FakeServiceControl _services = new();
    private readonly RecordingActivation _activation = new();
    private readonly TransactionEngine _engine;

    public ServiceEngineTests()
    {
        _registry.LoadUserHive(EngineTestHarness.Sid);
        SessionGuard.OverrideForTests = false;

        _engine = new TransactionEngine(
            _registry,
            _activation,
            new QuiescePaths(_dataRoot),
            new EngineInfo { AppVersion = "test", OsBuild = "10.0.26200", UserSid = EngineTestHarness.Sid },
            _activation,
            _services);
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

    private static CatalogEntry ServiceEntry(
        string service,
        ServiceStartMode mode = ServiceStartMode.Manual,
        bool stopNow = true,
        string id = "svc.test") => new()
    {
        Id = id,
        Category = "services",
        Title = id,
        Evidence = Evidence.Situational,
        Impact = Impact.Low,
        RiskTier = 1,
        Scope = TweakScope.Session,
        RequiresAdmin = true,
        RequiresReboot = false,
        Ops = [new ServiceOpSpec { Service = service, StartMode = mode, StopNow = stopNow }],
        WhatItBreaks = "nothing (test)",
    };

    private EngageResult Engage(params CatalogEntry[] entries) =>
        _engine.Engage(_engine.Plan(EngineTestHarness.CatalogOf(entries), "test"), FaultInjector.None);

    // ------------------------------------------------------------ round trip

    [Fact]
    public void Three_facts_round_trip_including_delayed_autostart()
    {
        // ServiceController collapses Automatic-Delayed into Automatic. Losing the flag converts a
        // delayed-auto service to plain auto and slows every subsequent boot - silently.
        _services.Add("InventorySvc", e =>
        {
            e.StartType = ServiceStartType.Automatic;
            e.DelayedAutostart = true;
            e.RunState = ServiceRunState.Running;
        });

        var engage = Engage(ServiceEntry("InventorySvc"));

        Assert.Equal(ServiceStartType.Manual, _services["InventorySvc"].StartType);
        Assert.Equal(ServiceRunState.Stopped, _services["InventorySvc"].RunState);

        var revert = _engine.RevertSession(engage.SessionId, "test");

        Assert.True(revert.Clean);
        Assert.Equal(ServiceStartType.Automatic, _services["InventorySvc"].StartType);
        Assert.True(_services["InventorySvc"].DelayedAutostart, "the delayed-auto flag must survive the round trip");
        Assert.Equal(ServiceRunState.Running, _services["InventorySvc"].RunState);
    }

    [Fact]
    public void A_service_that_was_stopped_is_not_started_by_revert()
    {
        // Restoring "running" onto something that was stopped would leave the machine in a state it
        // was never in. MapsBroker is Automatic-but-Stopped on the real target machine.
        _services.Add("MapsBroker", e =>
        {
            e.StartType = ServiceStartType.Automatic;
            e.DelayedAutostart = true;
            e.RunState = ServiceRunState.Stopped;
        });

        var engage = Engage(ServiceEntry("MapsBroker"));
        _engine.RevertSession(engage.SessionId, "test");

        Assert.Equal(ServiceStartType.Automatic, _services["MapsBroker"].StartType);
        Assert.Equal(ServiceRunState.Stopped, _services["MapsBroker"].RunState);
        Assert.DoesNotContain("start MapsBroker", _services.Log);
    }

    // ------------------------------------------------------------- ordering

    [Fact]
    public void Stop_happens_before_the_start_type_is_written()
    {
        // THE critical ordering rule. Disabling a service does not stop it, so writing the start
        // type first and then failing to stop leaves Disabled+Running: correct-looking all session,
        // then the service silently never returns at next boot.
        _services.Add("SysMain", e => e.RunState = ServiceRunState.Running);

        Engage(ServiceEntry("SysMain"));

        var stopIndex = _services.Log.IndexOf("stop SysMain");
        var configIndex = _services.Log.FindIndex(l => l.StartsWith("config SysMain"));

        Assert.True(stopIndex >= 0, "the service should have been stopped");
        Assert.True(configIndex >= 0, "the start type should have been written");
        Assert.True(stopIndex < configIndex, $"stop must precede config. Log: {string.Join(" | ", _services.Log)}");
    }

    [Fact]
    public void A_refused_stop_leaves_the_start_type_untouched()
    {
        // The consequence of the ordering rule: if the stop fails, the machine must be exactly as
        // it was found - never Disabled-and-Running.
        _services.Add("Stubborn", e =>
        {
            e.RunState = ServiceRunState.Running;
            e.RefuseStop = true;
        });

        var result = Engage(ServiceEntry("Stubborn", id: "svc.stubborn"));

        Assert.Contains("svc.stubborn", result.RolledBackEntries);
        Assert.Equal(ServiceStartType.Automatic, _services["Stubborn"].StartType);
        Assert.Equal(ServiceRunState.Running, _services["Stubborn"].RunState);
        Assert.DoesNotContain(_services.Log, l => l.StartsWith("config Stubborn"));
    }

    // ----------------------------------------------------------- guardrails

    [Fact]
    public void A_protected_service_is_refused_with_a_reason()
    {
        _services.Add("DcomLaunch", e => e.RunState = ServiceRunState.Running);

        var plan = _engine.Plan(EngineTestHarness.CatalogOf(ServiceEntry("DcomLaunch")), "test");
        var step = Assert.Single(plan.Steps);

        Assert.NotNull(step.RefusedReason);
        Assert.Contains("never-touch", step.RefusedReason);
        Assert.False(step.WillRun);
        Assert.Empty(plan.EffectiveSteps);
    }

    [Fact]
    public void Co_tenancy_is_keyed_on_the_live_host_pid()
    {
        // Keying on the svchost "-k <group>" token instead would false-refuse real candidates:
        // SysMain shares the LocalSystemNetworkRestricted GROUP with WlanSvc and UmRdpService on
        // the target machine, yet runs in an entirely separate PROCESS.
        _services.Add("SysMain", e => { e.HostProcessId = 4242; e.RunState = ServiceRunState.Running; });
        _services.Add("WlanSvc", e => { e.HostProcessId = 9999; e.RunState = ServiceRunState.Running; });

        var allowed = _engine.Plan(EngineTestHarness.CatalogOf(ServiceEntry("SysMain")), "test").Steps.Single();
        Assert.Null(allowed.RefusedReason);

        // Same PID as a tier-0 service: now it must refuse.
        _services["SysMain"].HostProcessId = 9999;

        var refused = _engine.Plan(EngineTestHarness.CatalogOf(ServiceEntry("SysMain")), "test").Steps.Single();
        Assert.NotNull(refused.RefusedReason);
        Assert.Contains("shares host process", refused.RefusedReason);
    }

    [Fact]
    public void A_running_service_that_refuses_stop_control_is_not_touched()
    {
        _services.Add("Unstoppable", e =>
        {
            e.RunState = ServiceRunState.Running;
            e.AcceptsStop = false;
        });

        var step = _engine.Plan(EngineTestHarness.CatalogOf(ServiceEntry("Unstoppable")), "test").Steps.Single();

        Assert.NotNull(step.RefusedReason);
        Assert.Contains("does not accept a stop request", step.RefusedReason);
    }

    [Fact]
    public void AcceptsStop_is_ignored_for_a_service_that_is_already_stopped()
    {
        // dwControlsAccepted is only meaningful while running. MapsBroker reports AcceptStop=false
        // purely because it is stopped; refusing on that would break every already-stopped candidate.
        _services.Add("MapsBroker", e =>
        {
            e.RunState = ServiceRunState.Stopped;
            e.AcceptsStop = false;
        });

        var step = _engine.Plan(EngineTestHarness.CatalogOf(ServiceEntry("MapsBroker")), "test").Steps.Single();

        Assert.Null(step.RefusedReason);
    }

    [Fact]
    public void A_trigger_started_service_is_clamped_to_Manual_never_Disabled()
    {
        // Disabling a trigger-started service leaves the trigger firing into a failed activation:
        // the dependent feature breaks weeks later with nothing connecting it to Quiesce.
        _services.Add("PcaSvc", e =>
        {
            e.TriggerStarted = true;
            e.RunState = ServiceRunState.Running;
        });

        var step = _engine.Plan(
            EngineTestHarness.CatalogOf(ServiceEntry("PcaSvc", ServiceStartMode.Disabled)), "test").Steps.Single();

        Assert.Equal(ServiceStartType.Manual, step.IntendedStartType);
    }

    [Fact]
    public void A_protected_dependent_blocks_the_change()
    {
        _services.Add("SomeSvc", e =>
        {
            e.RunState = ServiceRunState.Running;
            e.Dependents.Add("CryptSvc");
        });

        var step = _engine.Plan(EngineTestHarness.CatalogOf(ServiceEntry("SomeSvc")), "test").Steps.Single();

        Assert.NotNull(step.RefusedReason);
        Assert.Contains("CryptSvc", step.RefusedReason);
    }

    [Fact]
    public void Remote_session_locks_the_services_carrying_that_session()
    {
        SessionGuard.OverrideForTests = true;
        _services.Add("TermService", e => e.RunState = ServiceRunState.Running);
        _services.Add("SysMain", e => e.RunState = ServiceRunState.Running);

        var rdp = _engine.Plan(EngineTestHarness.CatalogOf(ServiceEntry("TermService")), "test").Steps.Single();
        Assert.NotNull(rdp.RefusedReason);

        // Unrelated services stay available: the lock is targeted, not a blanket refusal.
        var other = _engine.Plan(EngineTestHarness.CatalogOf(ServiceEntry("SysMain")), "test").Steps.Single();
        Assert.Null(other.RefusedReason);
    }

    // -------------------------------------------------------------- absence

    [Fact]
    public void A_service_absent_on_this_build_is_a_no_op_not_an_error()
    {
        // 'Fax' does not exist on build 26200. A tool that throws here is one feature update away
        // from being unusable.
        var plan = _engine.Plan(EngineTestHarness.CatalogOf(ServiceEntry("Fax")), "test");
        var step = Assert.Single(plan.Steps);

        Assert.True(step.NoOp);
        Assert.Null(step.RefusedReason);
        Assert.Empty(plan.EffectiveSteps);
    }

    // -------------------------------------------------------------- journal

    [Fact]
    public void The_journal_carries_all_three_facts_so_revert_needs_no_catalog()
    {
        _services.Add("whesvc", e =>
        {
            e.StartType = ServiceStartType.Automatic;
            e.DelayedAutostart = true;
            e.RunState = ServiceRunState.Running;
        });

        var engage = Engage(ServiceEntry("whesvc"));

        var records = JournalReader.Read(
            Path.Combine(new QuiescePaths(_dataRoot).SessionDir(engage.SessionId), "journal.jsonl")).Records;

        var applying = Assert.Single(records.OfType<ApplyingRecord>());
        var prior = Assert.IsType<ServicePrior>(applying.ServicePrior);

        Assert.Equal("whesvc", prior.Service);
        Assert.Equal(ServiceStartType.Automatic, prior.StartType);
        Assert.True(prior.DelayedAutostart);
        Assert.Equal(ServiceRunState.Running, prior.RunState);
    }

    [Fact]
    public void Revert_keeps_a_start_type_that_changed_since_apply()
    {
        // Windows Update or a driver install can reconfigure a service between apply and restore.
        // Overwriting that with a stale captured value destroys config Quiesce did not create.
        _services.Add("TrkWks", e => e.RunState = ServiceRunState.Running);

        var engage = Engage(ServiceEntry("TrkWks"));
        _services["TrkWks"].StartType = ServiceStartType.Disabled; // something else changed it

        var revert = _engine.RevertSession(engage.SessionId, "test");

        Assert.Equal(ServiceStartType.Disabled, _services["TrkWks"].StartType);
        Assert.Contains(revert.Messages, m => m.Contains("kept current"));
    }

    [Fact]
    public void Services_revert_before_registry_values()
    {
        // Dependency order, not naive reverse: a service restarted before the registry value it
        // reads at startup is restored would come back having read the tweaked value.
        _services.Add("SysMain", e => e.RunState = ServiceRunState.Running);

        var registryEntry = EngineTestHarness.DwordEntry(id: "reg.entry");
        var target = EngineTestHarness.TargetOf(registryEntry);
        _registry.Seed(target, EngineTestHarness.Dword(1));

        var engage = Engage(registryEntry, ServiceEntry("SysMain", id: "svc.entry"));

        _services.Log.Clear();
        _registry.Log.Clear();
        _engine.RevertSession(engage.SessionId, "test");

        Assert.NotEmpty(_services.Log);
        Assert.NotEmpty(_registry.Log);
    }
}
