using Quiesce.Core.Platform;

namespace Quiesce.Core;

/// <summary>
/// Hard safety limits, expressed as compile-time constants.
/// </summary>
/// <remarks>
/// Catalog data may only ever <em>shrink the set of things Quiesce is willing to touch</em>. It can
/// decline to use a permitted capability; it can never grant one. Nothing in a tweak file — shipped
/// by anyone, including a future me — can talk Quiesce into touching a name listed here, and the
/// catalog loader rejects any file that tries.
/// </remarks>
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

            // NVIDIA. NvContainerLocalSystem hosts the NVIDIA App's plugins, ShadowPlay and the
            // overlay. Verified on this machine (sc.exe qfailure): its recovery actions are
            // RESTART at 6s, RESTART at 8s, then RUN PROCESS at 10s launching
            // NvContainerRecovery.bat. So an unclean stop does not simply leave it stopped - it
            // re-launches it, twice, and then runs a recovery script, and Quiesce has no way to
            // know what state that leaves. NVDisplay.ContainerLocalSystem above carries the
            // identical three actions.
            "NvContainerLocalSystem",

            // NOT an NVIDIA service, despite the name and despite having been grouped with them
            // here. nvagent is the Windows Network Virtualization Service, hosted in
            // svchost.exe -k NetSvcs, and it belongs on this list with the network group above:
            // stopping a NetSvcs co-tenant on a box whose only route in is a remote session is
            // exactly the hazard those names are here for. Recorded separately because a guardrail
            // filed under the wrong reason is one someone eventually "corrects" by deleting it -
            // and the correction would be right about NVIDIA and wrong about the machine.
            "nvagent",

            // Anti-cheat. Interfering with a kernel anti-cheat near a protected game is a
            // hardware-ban vector, and an EAC ban propagates to every EAC title on this hardware.
            "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "vgc",

            // Third-party VPN. This installs WFP filters through BFE; an unclean stop can leave a
            // kill-switch block-all filter in place with no service left to remove it - total
            // network loss, and every network guardrail passed because no network service was
            // touched. On this machine it also carries the RDP session.
            //
            // NordUpdaterService was here too and has been REMOVED, deliberately. It was lumped in
            // by name, and the rationale above is not true of it: measured on this machine it is
            // Type=16 (WIN32_OWN_PROCESS) out of C:\Program Files\NordUpdater\, not a driver and
            // not a filter; the actual network components are separate services (tapnordvpn, a
            // Type=1 kernel driver, and nordsec-threatprotection-service). It has no
            // DependOnService in either direction - checked against the whole
            // HKLM\SYSTEM\CurrentControlSet\Services graph, not just sc.exe enumdepend, which only
            // answers one of the two questions - and no failure actions at all, so a clean stop
            // stays stopped. Widening what Quiesce is willing to touch is a real decision and this
            // is the note that justifies it: a guardrail kept for a reason that does not apply to
            // it is not caution, it is an unexplained refusal.
            "nordvpn-service",
        };

    /// <summary>
    /// Registry values Windows itself refuses to let anyone write, via a kernel registry callback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are not permission problems and no amount of privilege helps. UCPD.sys — the User
    /// Choice Protection Driver — vetoes <c>RegNtPreSetValueKey</c> for an exact, case-insensitive
    /// (key path, value name) pair. The key stays fully writable and every other value name in it
    /// is accepted; only the listed name is refused. Elevation, ownership, taking ownership and
    /// running as SYSTEM all make no difference, and the veto covers deletes as well as writes.
    /// </para>
    /// <para>
    /// Measured on build 26200.8875, 2026-07-28, driver v4.7.0.653342, from an elevated process:
    /// the pair is refused for DWORD 0, DWORD 1 and REG_SZ, in both cases, to reg.exe and to .NET
    /// alike; <c>AllowNewsAndInterestsZ</c> in the same key writes fine; <c>AllowNewsAndInterests</c>
    /// in a different key writes fine. The driver binary contains each name below as a UTF-16
    /// string, and contains none of the eight targets that accepted writes in the same session.
    /// </para>
    /// <para>
    /// Gated on <see cref="KernelRegistryFilter.IsActive"/> rather than treated as permanent: if a
    /// later build drops a pair or stops loading the driver, these should quietly start working
    /// again instead of staying refused on the strength of one observation.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> OsVetoedRegistryValues =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Keyed HIVE\subkey!value, not subkey!value: SOFTWARE\Microsoft\Windows\CurrentVersion\
            // Explorer\Advanced exists under both HKLM and HKCU, and a hive-blind match would refuse
            // a machine-wide write on the strength of a per-user observation.

            // Measured denied on the target machine.
            @"HKLM\SOFTWARE\Policies\Microsoft\Dsh!AllowNewsAndInterests",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced!TaskbarDa",

            // Present in the same driver table, not individually measured here. Listed because the
            // two that WERE measured are in this table and behave identically, and because a tweak
            // that silently fails is worse than one that declines with a reason. Drop any of these
            // the moment a write to it is observed succeeding.
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Feeds!ShellFeedsTaskbarViewMode",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Feeds!IsFeedsAvailable",
            @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Feeds!EnableFeeds",
        };

    /// <summary>
    /// Decides whether a registry write must be refused before it is ever attempted.
    /// </summary>
    /// <remarks>
    /// Plan-time rather than apply-time on purpose. Attempting the write and reporting the failure
    /// "works", but it costs a rolled-back entry and a failed Engage on every run, and — because
    /// <c>SetValue</c> creates the key before writing — it leaves behind an empty key that Quiesce
    /// is then also refused permission to delete. Declining up front leaves the machine untouched.
    /// </remarks>
    /// <returns>True when the write must not be attempted; <paramref name="reason"/> explains why.</returns>
    public static bool RefuseRegistryWrite(string hive, string subkey, string valueName, out string reason)
    {
        ArgumentNullException.ThrowIfNull(hive);
        ArgumentNullException.ThrowIfNull(subkey);
        ArgumentNullException.ThrowIfNull(valueName);

        // Membership is checked before the driver probe so the cheap, pure test short-circuits the
        // SCM round trip for the overwhelming majority of ops, which are not on the list at all.
        if (OsVetoedRegistryValues.Contains($@"{hive}\{subkey}!{valueName}") && KernelRegistryFilter.IsActive())
        {
            reason =
                $"Windows refuses this write in the kernel. UCPD.sys (the User Choice Protection " +
                $"Driver) vetoes writes to {subkey}!{valueName} specifically; the key itself is " +
                "writable and every other value name in it is accepted. Elevation, ownership and " +
                "running as SYSTEM make no difference, so Quiesce will not attempt it.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Decides whether a service may be reconfigured or stopped at all.
    /// </summary>
    /// <remarks>
    /// Evaluated at plan time <em>and again</em> immediately before the mutation. The SCM is live
    /// state: between planning and applying, a service can start running, gain a dependent, or move
    /// into a different host process. The check that protects the machine is the second one.
    /// <para>
    /// Ordered cheapest-and-most-absolute first, so the reason a user sees is the most fundamental
    /// one rather than whichever check happened to run.
    /// </para>
    /// </remarks>
    /// <returns>True when the change must be refused; <paramref name="reason"/> explains why.</returns>
    public static bool RefuseServiceChange(
        ServiceSnapshot service,
        IServiceControl control,
        out string reason)
    {
        if (IsServiceProtected(service.Service))
        {
            reason = $"{service.Service} is on the never-touch list. Quiesce will not reconfigure it.";
            return true;
        }

        if (SessionGuard.IsRemoteSession() && IsRemoteFragile(service.Service))
        {
            reason =
                $"{service.Service} carries the remote session you are connected over. " +
                "Stopping it would disconnect you with no way back in.";
            return true;
        }

        // Co-tenancy. The hazard is NOT that stopping a service stops its co-tenants - it does not;
        // the SCM unloads only that service's DLL and the host survives. The hazard is that the
        // service faults inside ServiceMain or DllUnload during the stop and takes the whole host
        // process with it. Co-tenants then die WITHOUT reporting SERVICE_STOPPED, which is exactly
        // the condition that queues their failure actions - and on this machine DcomLaunch, RpcSs,
        // Power, mpssvc, CoreMessagingRegistrar, BrokerInfrastructure and SystemEventsBroker are all
        // configured with REBOOT recovery actions at 30-120 second delays. So the downside is a
        // bugcheck or a forced restart mid-game, not a tidy cascade.
        //
        // Stated precisely because the false version ("stopping A stops its co-tenants") is easy to
        // disprove, and someone who disproves it will delete the check.
        // A stopped service reports PID 0, which makes this check VACUOUS rather than passing: we
        // have learned nothing about where it would land if it started. That is only tolerable
        // because a stopped service is never stopped again - the apply path skips it - so the
        // co-tenancy question never becomes live without a fresh query first.
        if (service.HostProcessId != 0 && service.RunState == ServiceRunState.Running)
        {
            var coTenants = control.ServicesInHostProcess(service.HostProcessId);
            var protectedTenant = coTenants.FirstOrDefault(
                s => !s.Equals(service.Service, StringComparison.OrdinalIgnoreCase) && IsServiceProtected(s));

            if (protectedTenant is not null)
            {
                reason =
                    $"{service.Service} shares host process {service.HostProcessId} with {protectedTenant}, " +
                    "which must never be stopped. Quiesce will not risk the shared process.";
                return true;
            }
        }

        // A capability check, NOT a safety check. Every running service measured on this machine
        // advertises SERVICE_ACCEPT_STOP - including TermService and Dhcp, which would sever the
        // operator's only connection. Nothing is safe merely because it says it can be stopped;
        // the tier-0 list and the remote-session lock above are what actually protect the machine.
        // This only rules out services that cannot be stopped cleanly at all, where the sole
        // alternative would be killing the host process - which this tool never does.
        //
        // Evaluated only while running: dwControlsAccepted is meaningless for a stopped service,
        // and MapsBroker (Automatic but Stopped here) reports false purely for that reason.
        if (service.RunState == ServiceRunState.Running && !service.AcceptsStop)
        {
            reason = $"{service.Service} does not accept a stop request. Quiesce will not force it.";
            return true;
        }

        // Dependents must be handled explicitly, never implicitly: stopping a service the SCM will
        // cascade from can take down something the user never agreed to.
        //
        // The full refusal predicate is applied to each dependent, not merely the tier-0 test - a
        // dependent that is itself unstoppable, or that shares a host with a protected service,
        // blocks its parent for exactly the same reasons it blocks itself.
        //
        // Note this list is the TRANSITIVE closure (EnumDependentServices returns the whole chain,
        // verified against the registry dependency graph), so there is no recursion to do here and
        // adding some would double-visit services and scramble the stop order.
        foreach (var dependent in service.Dependents)
        {
            if (IsServiceProtected(dependent))
            {
                reason = $"{service.Service} is required by {dependent}, which is on the never-touch list.";
                return true;
            }

            var dependentState = control.Query(dependent);
            if (!dependentState.Present)
            {
                continue;
            }

            if (dependentState.RunState == ServiceRunState.Running && !dependentState.AcceptsStop)
            {
                reason =
                    $"{service.Service} is required by {dependent}, which is running and cannot be stopped cleanly.";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Power schemes Quiesce will never SELECT, whatever a catalog asks for.
    /// </summary>
    /// <remarks>
    /// Power saver only, and the direction is everything: Quiesce refuses to <em>engage</em> it because
    /// choosing it would make the machine slower, which is a straight inversion of what this tool
    /// claims to do. Restore may still put it back — if that is what the user had active, it is their
    /// setting and returning it is the correct undo. A guardrail applied symmetrically here would trap
    /// a Power saver user's machine on whatever Quiesce switched them to, which is exactly the failure
    /// this project exists to avoid.
    /// </remarks>
    public static readonly IReadOnlySet<Guid> NeverSelectPowerSchemes = new HashSet<Guid>
    {
        WellKnownPowerSchemes.PowerSaver,
    };

    /// <summary>
    /// Decides whether a power scheme may be selected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two rules, from two different places. The first is the compile-time refusal above. The second is
    /// a live-state check that exists because of a hazard nothing else in this file can see: every RDP
    /// guardrail in Quiesce is keyed on SERVICE NAMES, and a power scheme reaches the same outcome —
    /// operator disconnected, no way back in, physical access required — without touching a service at
    /// all. A scheme whose "sleep after" is shorter than the current one's can put the machine to sleep
    /// under a live remote session.
    /// </para>
    /// <para>
    /// Unknown counts as refused. A scheme whose sleep timeout could not be read gets declined while a
    /// session is remote, because "I cannot tell whether this would drop your connection" is not a
    /// reason to proceed. Locally there is nothing at stake and the check does not apply — worst case
    /// the screen goes to sleep and the user moves the mouse.
    /// </para>
    /// <para>
    /// Note the asymmetry with <paramref name="current"/>: never-sleeps is always allowed, and any
    /// timeout at all is allowed as long as it is not sooner than what the machine is already set to.
    /// The rule is "do not make the disconnection risk worse", not "forbid sleep".
    /// </para>
    /// </remarks>
    /// <returns>True when the switch must be refused; <paramref name="reason"/> explains why.</returns>
    public static bool RefusePowerSchemeChange(
        PowerScheme target,
        PowerScheme? current,
        bool isRemoteSession,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (NeverSelectPowerSchemes.Contains(target.Id))
        {
            reason =
                $"{target} makes the machine slower on purpose. Quiesce will never select it — though " +
                "Restore will put it back if it is what you had.";
            return true;
        }

        if (!isRemoteSession)
        {
            reason = string.Empty;
            return false;
        }

        if (target.SleepAfterAcSeconds is not { } targetSleep)
        {
            reason =
                $"Quiesce could not read how soon {target} puts the machine to sleep, and you are " +
                "connected over a remote session. It will not switch to a scheme that might sleep the " +
                "machine out from under you.";
            return true;
        }

        // Zero is "never", which is the safest possible value and must not be compared numerically -
        // as an integer it is smaller than every real timeout, so a naive "<" would refuse exactly the
        // scheme that removes the hazard. This is the shape of bug that makes a guardrail look broken
        // and get deleted.
        if (targetSleep == 0)
        {
            reason = string.Empty;
            return false;
        }

        var currentSleep = current?.SleepAfterAcSeconds;

        if (currentSleep is null || currentSleep == 0 || targetSleep < currentSleep)
        {
            reason =
                $"{target} sleeps the machine after {targetSleep}s on AC power" +
                (currentSleep is { } c && c != 0 ? $", sooner than the current {c}s" : ", and the current scheme does not sleep at all") +
                ". You are connected over a remote session, and a machine that sleeps disconnects you " +
                "with no way back in.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Services that carry a remote session. Stopping one over RDP severs the operator's only
    /// control channel — on the target machine there is no Ethernet, so recovery needs physical
    /// access to the box.
    /// </summary>
    public static bool IsRemoteFragile(string service) =>
        RemoteFragileServices.Contains(service);

    private static readonly IReadOnlySet<string> RemoteFragileServices =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TermService", "UmRdpService", "SessionEnv", "WlanSvc", "Dhcp", "Dnscache",
            "nsi", "netprofm", "Wcmsvc", "LanmanWorkstation", "LanmanServer", "RemoteAccess",
        };

    /// <summary>
    /// Anti-cheat services whose RUNNING state means a protected game is live right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A different question from <see cref="NeverTouchServices"/>, which asks "may Quiesce stop this".
    /// This asks "does the fact that this is running tell me a protected game is on screen" — and it
    /// exists because the answer was previously nowhere: <c>ProcessCloser.AnyGameRunning</c> is keyed on
    /// <c>ProcessClass.Game</c>, which is derived from the game-directory allowlist, and every production
    /// call site builds the classifier with <c>gameDirectories: null</c>. So the one refusal whose
    /// downside is unrecoverable could not fire at all.
    /// </para>
    /// <para>
    /// RIOT VANGUARD IS DELIBERATELY ABSENT, and this is the whole subtlety of the list. <c>vgc</c> and
    /// <c>vgk</c> start at BOOT by design and run permanently on any machine with Valorant installed, so
    /// "Vanguard is running" carries no information about whether a game is live. Including it would
    /// refuse every close forever on exactly the machines this tool is aimed at —
    /// <c>AnyGameRunning</c>'s own remark already warns against precisely that mistake for launcher
    /// processes, and it applies identically here. Vanguard is still never touched; it is just not
    /// evidence.
    /// </para>
    /// <para>
    /// The members here are demand-started: measured on this machine, <c>EasyAntiCheat_EOS</c> is
    /// <c>DEMAND_START</c>, which is what makes its running state meaningful — a game launcher started
    /// it. That is also why <see cref="IsAntiCheatGameSignal"/> requires the service NOT to be
    /// auto-start rather than trusting this list alone: if a future build ships one of these as
    /// automatic, "running" silently stops implying "game live", and the rule should notice by itself
    /// rather than depend on someone updating a comment.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> AntiCheatGameSignalServices =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService",
        };

    /// <summary>
    /// Whether this snapshot is evidence that a protected game is running right now.
    /// </summary>
    /// <remarks>
    /// Four conditions, all required. Present, because a service that is not installed proves nothing.
    /// Running, because all of these are installed-and-stopped for most of a machine's life. Named,
    /// because only these three are demand-started in practice. And <see cref="ServiceStartType.Manual"/>
    /// SPECIFICALLY — not merely "!= Automatic", which was the first draft and was wrong: <c>Boot</c> and
    /// <c>System</c> are also Windows starting it, and a kernel-mode anti-cheat driver is exactly a
    /// <c>System</c>-start service. Requiring Manual positively means the running state can only have
    /// been caused by something asking for it, which is the fact being inferred.
    /// <para>
    /// That also makes the Vanguard exclusion belt-and-braces rather than list-only: <c>vgk</c> is a
    /// boot-start driver, so even if someone added it above, it could not produce a false signal here.
    /// </para>
    /// <para>
    /// The trade-off is stated rather than hidden: if an anti-cheat is auto-start AND a game is live,
    /// this returns false and the close proceeds. That is a false negative chosen over the alternative,
    /// which is a tool that refuses every close forever on a machine where the anti-cheat happens to be
    /// configured differently. The allowlist-driven <c>AnyGameRunning</c> check remains the real answer
    /// once game discovery lands.
    /// </para>
    /// </remarks>
    public static bool IsAntiCheatGameSignal(ServiceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.Present
            && AntiCheatGameSignalServices.Contains(snapshot.Service)
            && snapshot.RunState == ServiceRunState.Running
            && snapshot.StartType == ServiceStartType.Manual;
    }

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
    /// Service groups locked out entirely while any session on the machine is remote.
    /// </summary>
    /// <remarks>
    /// Evaluated through <see cref="SessionGuard.IsRemoteSession"/>, which reads live and never
    /// caches — a user can connect or disconnect while the app is open.
    /// </remarks>
    public static IReadOnlySet<string> RemoteSessionLockedServices => RemoteFragileServices;

    /// <summary>
    /// Returns true when <paramref name="serviceName"/> may never be reconfigured by Quiesce.
    /// </summary>
    /// <remarks>
    /// Per-user service instances carry a <c>_&lt;luid&gt;</c> suffix — <c>CDPUserSvc_4a2f1</c> is an
    /// instance of the <c>CDPUserSvc</c> template — so a protected template name would not match its
    /// own instances without normalizing the suffix away.
    /// </remarks>
    public static bool IsServiceProtected(string serviceName)
    {
        ArgumentNullException.ThrowIfNull(serviceName);

        if (NeverTouchServices.Contains(serviceName))
        {
            return true;
        }

        var underscore = serviceName.LastIndexOf('_');
        if (underscore <= 0 || underscore == serviceName.Length - 1)
        {
            return false;
        }

        // Only strip a suffix that actually looks like a LUID (hex), so a service legitimately
        // named with a trailing underscore segment is not silently reclassified.
        var suffix = serviceName.AsSpan(underscore + 1);
        foreach (var c in suffix)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return NeverTouchServices.Contains(serviceName[..underscore]);
    }

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

    /// <summary>
    /// Browser executables, as a class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Closed by default, with a keep-alive toggle, because a browser with a few dozen tabs is
    /// usually the largest single consumer of RAM and background CPU on a gaming machine.
    /// </para>
    /// <para>
    /// <c>msedgewebview2</c> is deliberately absent, and is on <see cref="NeverTouchProcesses"/>
    /// instead. It is not a browser the user is using — it hosts other applications' UI, including
    /// Widgets, the new Outlook and several launcher panes, with six live instances on the target
    /// machine. Closing it as if it were Edge takes those applications' windows with it.
    /// </para>
    /// <para>
    /// Membership is checked against the image name, but eligibility still requires a readable image
    /// path under a plausible install root — see <see cref="ProcessClassifier"/>. The name alone is
    /// not enough: anything can be called <c>chrome.exe</c>.
    /// </para>
    /// <para>
    /// This list is deliberately WIDER than the catalog's browser group, and the two are not meant to
    /// converge. This decides what Quiesce is <em>willing</em> to treat as a browser; the catalog decides
    /// which ones it actually looks for, and pairs each with the directory its real installation lives
    /// under. Data narrowing a compile-time list is the intended direction.
    /// </para>
    /// <para>
    /// <c>tor</c> was here and has been removed: <c>tor.exe</c> is the SOCKS daemon, not the browser.
    /// Tor Browser's UI process is <c>firefox.exe</c> under <c>\Tor Browser\</c>. Calling the daemon a
    /// browser was simply wrong — and acting on that name would have cut the browser's networking out
    /// from under a window left standing, which is a worse outcome than closing the browser.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> BrowserImageNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "chromium",
            "librewolf", "waterfox",

            // Perplexity's Comet. Added after it was found running with 20 processes on the development
            // machine and sailing straight through an Engage, because a list of browsers written from
            // memory is a list of the browsers its author happened to think of.
            "comet",
        };

    /// <summary>True when <paramref name="imageName"/> names a browser, with or without <c>.exe</c>.</summary>
    public static bool IsBrowser(string imageName)
    {
        ArgumentNullException.ThrowIfNull(imageName);

        var bare = imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? imageName[..^4]
            : imageName;

        return BrowserImageNames.Contains(bare);
    }
}
