using Quiesce.Core;

namespace Quiesce.App.Views;

public partial class ServicesPage
{
    /// <summary>
    /// User-facing reasons for the tier-0 lock, grouped the same way <see cref="Guardrails"/>
    /// groups them. Rendering the reason is the point: "Quiesce refuses to touch DcomLaunch and
    /// tells you why" is the trust moment.
    /// </summary>
    private static readonly (string Reason, string[] Services)[] Groups =
    [
        ("Stopping this host process bugchecks Windows (CRITICAL_PROCESS_DIED). These share one svchost.",
            ["DcomLaunch", "RpcSs", "RpcEptMapper", "BrokerInfrastructure", "Power", "SystemEventsBroker", "LSM", "CoreMessagingRegistrar"]),
        ("Device and driver plumbing; games and controllers stop enumerating without it.",
            ["PlugPlay", "DeviceInstall", "hidserv", "camsvc", "GameInputSvc", "GameInputRedistService"]),
        ("Sign-in, profiles and licensing. Breaking these can lock you out of your own account.",
            ["ProfSvc", "UserManager", "gpsvc", "SamSs", "KeyIso", "VaultSvc", "CryptSvc", "StateRepository", "AppXSvc", "ClipSVC", "LicenseManager", "sppsvc"]),
        ("System plumbing other components hard-depend on.",
            ["Winmgmt", "EventLog", "Schedule", "StorSvc"]),
        ("Firewall and network filtering. Never in a performance tool.",
            ["BFE", "mpssvc"]),
        ("Networking. If you are on Wi-Fi or Remote Desktop, stopping these severs your own connection.",
            ["Dhcp", "Dnscache", "nsi", "netprofm", "Wcmsvc", "WlanSvc", "LanmanWorkstation", "LanmanServer"]),
        ("Remote Desktop. Stopping these over an RDP session locks you out until you reach the machine physically.",
            ["TermService", "UmRdpService", "SessionEnv"]),
        ("Audio. Stopping these is the classic 'booster broke my sound' bug report.",
            ["Audiosrv", "AudioEndpointBuilder"]),
        ("Display and GPU. Stopping the display container mid-session hangs or blanks the screen.",
            ["DispBrokerDesktopSvc", "NVDisplay.ContainerLocalSystem"]),
        ("Security. Quiesce does not touch antivirus - by principle, not because it is blocked.",
            ["WinDefend", "MDCoreSvc", "WdNisSvc"]),
    ];

    public ServicesPage()
    {
        InitializeComponent();

        var rows = Groups
            .SelectMany(g => g.Services.Select(s => new LockedServiceRow { Name = s, Reason = g.Reason }))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The page must agree with the compiled guardrail, or the UI is lying about the lock.
        foreach (var row in rows.Where(row => !Guardrails.IsServiceProtected(row.Name)))
        {
            throw new InvalidOperationException(
                $"ServicesPage lists '{row.Name}' as locked but Guardrails does not protect it.");
        }

        LockedList.ItemsSource = rows;
    }
}

public sealed record LockedServiceRow
{
    public required string Name { get; init; }

    public required string Reason { get; init; }
}
