using Quiesce.Core;

namespace Quiesce.Tests;

/// <summary>
/// The guardrails are the one part of Quiesce that must never regress, so they are tested by name
/// against the specific failure modes that put each entry on the list.
/// </summary>
public class GuardrailsTests
{
    [Theory]
    // Terminating this svchost group is CRITICAL_PROCESS_DIED (0xEF) and a possible boot loop.
    // These four share the `svchost -k DcomLaunch -p` process on the target machine.
    [InlineData("DcomLaunch")]
    [InlineData("BrokerInfrastructure")]
    [InlineData("Power")]
    [InlineData("SystemEventsBroker")]
    // Stopping these is the classic "the booster broke my sound" bug report.
    [InlineData("Audiosrv")]
    [InlineData("AudioEndpointBuilder")]
    // The operator may be on RDP over the only up NIC. Stopping these severs the control channel.
    [InlineData("TermService")]
    [InlineData("WlanSvc")]
    [InlineData("Dhcp")]
    // Breaks controllers in the very games being optimised for.
    [InlineData("GameInputSvc")]
    // Quiesce declines to touch security services on principle, not because Tamper Protection blocks it.
    [InlineData("WinDefend")]
    public void NeverTouchServices_covers_the_services_that_break_windows(string service)
    {
        Assert.True(Guardrails.IsServiceProtected(service));
    }

    [Fact]
    public void IsServiceProtected_is_case_insensitive()
    {
        // The registry genuinely mixes casing: RpcSs / RPCSS / rpcss all appear.
        Assert.True(Guardrails.IsServiceProtected("rpcss"));
        Assert.True(Guardrails.IsServiceProtected("RPCSS"));
        Assert.True(Guardrails.IsServiceProtected("RpcSs"));
    }

    [Fact]
    public void NeverTouchServices_does_not_block_the_services_we_actually_target()
    {
        // The eight dependency-verified, non-trigger-started candidates from the plan. If a future
        // edit accidentally widens the tier-0 list over these, the product stops doing anything.
        string[] targets =
        [
            "DiagTrack", "SysMain", "DusmSvc", "TrkWks",
            "InventorySvc", "whesvc", "WSAIFabricSvc", "MapsBroker",
        ];

        Assert.All(targets, t => Assert.False(Guardrails.IsServiceProtected(t)));
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("explorer.exe")]
    [InlineData("EXPLORER.EXE")]
    [InlineData("dwm")]
    [InlineData("audiodg.exe")]
    [InlineData("csrss")]
    public void IsProcessProtected_handles_suffix_and_casing(string image)
    {
        Assert.True(Guardrails.IsProcessProtected(image));
    }

    [Fact]
    public void msedgewebview2_is_protected_but_msedge_is_not()
    {
        // msedgewebview2 hosts other applications' UI - Widgets, new Outlook, launcher panes - so
        // closing it breaks them. It is explicitly NOT part of the browser class. msedge itself is
        // a browser and browsers close by default.
        Assert.True(Guardrails.IsProcessProtected("msedgewebview2.exe"));
        Assert.False(Guardrails.IsProcessProtected("msedge.exe"));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\PEAK\PEAK.exe")]
    [InlineData(@"C:\Program Files\Epic Games\rocketleague\Binaries\Win64\RocketLeague.exe")]
    [InlineData(@"C:\Program Files (x86)\Battle.net\Battle.net.exe")]
    [InlineData(@"C:\Program Files\EA Games\The Sims 4\Game\Bin\TS4_x64.exe")]
    public void IsUnderLauncherRoot_matches_launcher_owned_paths(string path)
    {
        Assert.True(Guardrails.IsUnderLauncherRoot(path));
    }

    [Fact]
    public void IsUnderLauncherRoot_does_not_match_ordinary_apps()
    {
        Assert.False(Guardrails.IsUnderLauncherRoot(@"C:\Users\thebr\AppData\Local\Discord\app-1.0.9\Discord.exe"));
        Assert.False(Guardrails.IsUnderLauncherRoot(@"C:\Program Files\Google\Chrome\Application\chrome.exe"));
    }

    [Fact]
    public void Overwatch_is_a_sibling_of_the_battlenet_root_not_a_child()
    {
        // Verified on the target machine: Overwatch installs to C:\Program Files (x86)\Overwatch,
        // NOT under the Battle.net root. This is precisely why launcher-root exclusion alone is
        // insufficient and the game allowlist must carry per-game paths from launcher manifests.
        // If this ever starts returning true, the allowlist requirement can be revisited.
        Assert.False(Guardrails.IsUnderLauncherRoot(@"C:\Program Files (x86)\Overwatch\_retail_\Overwatch.exe"));
    }

    [Fact]
    public void Quiesce_never_assigns_a_priority_above_AboveNormal()
    {
        // RealTime, and even High, starves dwm.exe, audiodg.exe and the input stack: input lag,
        // audio crackle, worse frame pacing - while average FPS goes up and hides the regression.
        Assert.Equal(System.Diagnostics.ProcessPriorityClass.AboveNormal, Guardrails.MaxAssignablePriority);
    }

    [Fact]
    public void Remote_session_lock_covers_every_path_back_to_the_machine()
    {
        string[] lifelines = ["TermService", "UmRdpService", "SessionEnv", "WlanSvc", "Dhcp", "Dnscache"];
        Assert.All(lifelines, s => Assert.Contains(s, Guardrails.RemoteSessionLockedServices));
    }
}
