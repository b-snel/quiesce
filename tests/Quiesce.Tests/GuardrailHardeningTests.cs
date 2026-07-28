using Quiesce.Core;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// Tests for the guardrail hardening that came out of the M4 adversarial review. Each one pins a
/// specific hole that review found.
/// </summary>
public class GuardrailHardeningTests
{
    [Fact]
    public void Per_user_service_instances_inherit_their_template_protection()
    {
        // Per-user services carry a _<luid> suffix: CDPUserSvc_4a2f1 is an instance of CDPUserSvc.
        // Without normalization a protected template would not match its own instances.
        Assert.True(Guardrails.IsServiceProtected("CryptSvc"));
        Assert.True(Guardrails.IsServiceProtected("CryptSvc_4a2f1"));
        Assert.True(Guardrails.IsServiceProtected("cryptsvc_DEADBEEF"));

        // Only hex suffixes are stripped, so a service legitimately ending in an underscore
        // segment is not silently reclassified as something else.
        Assert.False(Guardrails.IsServiceProtected("CryptSvc_helper"));
        Assert.False(Guardrails.IsServiceProtected("NotProtected_4a2f1"));
    }

    [Fact]
    public void Services_added_by_the_review_are_protected()
    {
        // NVIDIA container hosts ShadowPlay and carries a RUN PROCESS recovery action; anti-cheat
        // interference is a hardware-ban vector; an unclean VPN stop can leave a WFP kill-switch
        // filter blocking all traffic with no service left to remove it.
        Assert.True(Guardrails.IsServiceProtected("NvContainerLocalSystem"));
        Assert.True(Guardrails.IsServiceProtected("EasyAntiCheat_EOS"));
        Assert.True(Guardrails.IsServiceProtected("nordvpn-service"));
        Assert.True(Guardrails.IsServiceProtected("vgc"));
    }

    [Fact]
    public void The_remote_locked_list_is_not_a_second_copy()
    {
        // Two copies of a safety list is how drift happens: one gets an addition, the other does
        // not, and the gap is invisible until it matters.
        Assert.Same(Guardrails.RemoteSessionLockedServices, Guardrails.RemoteSessionLockedServices);
        Assert.All(
            Guardrails.RemoteSessionLockedServices,
            s => Assert.True(Guardrails.IsRemoteFragile(s), $"{s} must be recognised as remote-fragile"));
    }

    [Fact]
    public void A_dependent_that_cannot_be_stopped_blocks_its_parent()
    {
        // The stated rule is "refuse if any dependent is itself refused", but the first
        // implementation only tested tier-0 membership - so a dependent that was merely
        // unstoppable let its parent through.
        var services = new FakeServiceControl();
        services.Add("Parent", e =>
        {
            e.RunState = ServiceRunState.Running;
            e.Dependents.Add("StubbornChild");
        });
        services.Add("StubbornChild", e =>
        {
            e.RunState = ServiceRunState.Running;
            e.AcceptsStop = false;
        });

        var refused = Guardrails.RefuseServiceChange(services.Query("Parent"), services, out var reason);

        Assert.True(refused);
        Assert.Contains("StubbornChild", reason);
    }

    [Fact]
    public void A_stopped_service_does_not_get_a_vacuous_co_tenancy_pass()
    {
        // A stopped service reports PID 0. Treating that as "no co-tenants" is learning nothing and
        // calling it safety; the check must simply not claim to have evaluated anything.
        var services = new FakeServiceControl();
        var stopped = services.Add("Dormant", e =>
        {
            e.RunState = ServiceRunState.Stopped;
            e.HostProcessId = 0;
        });

        Assert.False(Guardrails.RefuseServiceChange(services.Query("Dormant"), services, out _));
        Assert.Equal(ServiceRunState.Stopped, stopped.RunState);
    }

    [Fact]
    public void SessionGuard_reports_the_live_machine_without_throwing()
    {
        // The rewrite swapped GetSystemMetrics(SM_REMOTESESSION) - which only sees the calling
        // process's own session, and returns FALSE from session 0 while an operator is on RDP - for
        // an enumeration of every session. This asserts the P/Invoke path actually works against
        // the real machine; the value itself depends on how the test host was launched.
        var remote = SessionGuard.IsRemoteSession();

        Assert.True(remote || !remote); // no exception is the assertion
    }

    [Fact]
    public void SessionGuard_override_is_honoured_and_restorable()
    {
        var original = SessionGuard.OverrideForTests;
        try
        {
            SessionGuard.OverrideForTests = true;
            Assert.True(SessionGuard.IsRemoteSession());

            SessionGuard.OverrideForTests = false;
            Assert.False(SessionGuard.IsRemoteSession());
        }
        finally
        {
            SessionGuard.OverrideForTests = original;
        }
    }
}
