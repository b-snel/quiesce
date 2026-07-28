namespace Quiesce.Core;

/// <summary>
/// Hard safety limits, expressed as compile-time constants that catalog data can only ever
/// <em>narrow</em>, never widen. A tweak file shipped by anyone — including a future me — cannot
/// talk Quiesce into touching anything named here.
/// </summary>
/// <remarks>
/// Every entry earned its place from a specific, verified failure mode rather than from caution.
/// The reasons are recorded next to each group because a guardrail whose rationale is lost is a
/// guardrail that eventually gets "cleaned up".
/// </remarks>
public static class Guardrails
{
    /// <summary>
    /// Tier 0: services Quiesce will never stop, disable, or reconfigure.
    /// </summary>
    /// <remarks>
    /// Rendered in the UI as visibly locked rows <em>with</em> the reason rather than hidden —
    /// "Quiesce refuses to touch DcomLaunch and tells you why" is the moment the tool earns trust.
    /// </remarks>
    public static readonly IReadOnlySet<string> NeverTouchServices =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // RPC/COM/PnP core. BrokerInfrastructure, Power, SystemEventsBroker and LSM share the
            // `svchost -k DcomLaunch -p` process on the target machine; terminating that PID is
            // CRITICAL_PROCESS_DIED (0xEF) and a possible boot loop.
            "DcomLaunch", "RpcSs", "RpcEptMapper", "BrokerInfrastructure", "Power",
            "SystemEventsBroker", "PlugPlay", "DeviceInstall", "LSM", "CoreMessagingRegistrar",

            // Identity, profiles, licensing. Breaking these can lock a user out of their own profile.
            "ProfSvc", "UserManager", "gpsvc", "SamSs", "KeyIso", "VaultSvc", "CryptSvc",
            "StateRepository", "AppXSvc", "ClipSVC", "LicenseManager", "sppsvc",

            // Management/diagnostic plumbing other components hard-depend on.
            "Winmgmt", "EventLog", "Schedule", "StorSvc",

            // Firewall and filtering. Never in a performance tool.
            "BFE", "mpssvc",

            // Networking. On the target machine the only up NIC is Wi-Fi and the operator may be
            // on RDP over it, so stopping any of these can sever the control channel with no
            // recovery short of physical access. See also SessionGuard.
            "Dhcp", "Dnscache", "nsi", "netprofm", "Wcmsvc", "WlanSvc",
            "LanmanWorkstation", "LanmanServer",

            // Remote Desktop. Same reason, more directly.
            "TermService", "UmRdpService", "SessionEnv",

            // Audio. Stopping these is the classic "booster broke my sound" bug report.
            "Audiosrv", "AudioEndpointBuilder",

            // Display/GPU. Stopping the NVIDIA container mid-session is a hang or a black screen.
            "DispBrokerDesktopSvc", "NVDisplay.ContainerLocalSystem",

            // Game input. Stopping these breaks controllers in the very games we are optimising for.
            "GameInputSvc", "GameInputRedistService",

            // Security. Tamper Protection is ON on the target machine, so these writes would fail
            // anyway — but Quiesce declines on principle, not because it is blocked.
            "WinDefend", "MDCoreSvc", "WdNisSvc",

            // Human input and camera.
            "hidserv", "camsvc",
        };

    /// <summary>
    /// Processes Quiesce will never close, kill, or throttle.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverTouchProcesses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Killing the shell loses the user's desktop, tray and any in-flight drag state, and on
            // a remote session can leave no way back in.
            "explorer",

            // System-critical: terminating any of these bugchecks the machine.
            "csrss", "wininit", "winlogon", "services", "smss", "lsass",

            // Compositor and audio graph. Throttling dwm produces exactly the stutter this tool
            // claims to remove; throttling audiodg produces crackle.
            "dwm", "audiodg",

            // Hosts other applications' UI — Widgets, new Outlook, several launcher panes. Six live
            // instances on the target machine. Explicitly NOT part of the browser class.
            "msedgewebview2",
        };

    /// <summary>
    /// Install roots whose child processes are never throttled or closed.
    /// </summary>
    /// <remarks>
    /// Launchers are all CEF/Chromium, so throttling their children breaks store, matchmaking and
    /// auth handshakes mid-queue in ways the user will attribute to the game. This list is matched
    /// by full image path, never by image name — and it is deliberately <em>not</em> sufficient on
    /// its own: Overwatch installs to <c>C:\Program Files (x86)\Overwatch</c>, a sibling of the
    /// Battle.net root rather than a child, so the game allowlist must also carry per-game paths
    /// discovered from launcher manifests.
    /// </remarks>
    public static readonly IReadOnlyList<string> LauncherRootMarkers =
    [
        @"\Steam", @"\Epic Games", @"\Battle.net", @"\Electronic Arts", @"\EA Games",
        @"\Ubisoft", @"\GOG Galaxy", @"\Riot Games", @"\Riot Vanguard",
        @"\EasyAntiCheat", @"\BattlEye", @"\XboxGames",
    ];

    /// <summary>
    /// The highest priority class Quiesce will ever assign to any process.
    /// </summary>
    /// <remarks>
    /// RealTime — and even High — starves dwm.exe, audiodg.exe and the input stack, producing input
    /// lag, audio crackle and worse frame pacing than doing nothing, all while the average-FPS
    /// counter goes <em>up</em> and hides the regression. See also <c>BannedSymbols.txt</c>, which
    /// makes <c>ProcessPriorityClass.RealTime</c> a compile error.
    /// </remarks>
    public const System.Diagnostics.ProcessPriorityClass MaxAssignablePriority =
        System.Diagnostics.ProcessPriorityClass.AboveNormal;

    /// <summary>
    /// Service groups locked out entirely while the operator is on a remote session.
    /// </summary>
    /// <remarks>
    /// Read <c>SM_REMOTESESSION</c> live, never cached — a user can connect or disconnect while the
    /// app is open.
    /// </remarks>
    public static readonly IReadOnlySet<string> RemoteSessionLockedServices =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TermService", "UmRdpService", "SessionEnv", "WlanSvc", "Dhcp", "Dnscache",
            "nsi", "netprofm", "Wcmsvc", "LanmanWorkstation", "LanmanServer", "RemoteAccess",
        };

    /// <summary>
    /// Returns true when <paramref name="serviceName"/> may never be reconfigured by Quiesce.
    /// </summary>
    public static bool IsServiceProtected(string serviceName) =>
        NeverTouchServices.Contains(serviceName);

    /// <summary>
    /// Returns true when a process may never be closed or throttled by Quiesce.
    /// </summary>
    /// <param name="imageName">Image name with or without the <c>.exe</c> suffix.</param>
    public static bool IsProcessProtected(string imageName)
    {
        ArgumentNullException.ThrowIfNull(imageName);

        var bare = imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? imageName[..^4]
            : imageName;

        return NeverTouchProcesses.Contains(bare);
    }

    /// <summary>
    /// Returns true when <paramref name="fullImagePath"/> lives under a launcher or anti-cheat
    /// install root, and so must not be throttled or closed.
    /// </summary>
    public static bool IsUnderLauncherRoot(string fullImagePath)
    {
        ArgumentNullException.ThrowIfNull(fullImagePath);

        foreach (var marker in LauncherRootMarkers)
        {
            if (fullImagePath.Contains(marker + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullImagePath.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
