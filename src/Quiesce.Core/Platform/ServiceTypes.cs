using System.Text.Json.Serialization;
using Quiesce.Core.Catalog;

namespace Quiesce.Core.Platform;

/// <summary>Start type as the SCM stores it, without the delayed-auto flag folded in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServiceStartType>))]
public enum ServiceStartType
{
    Boot,
    System,
    Automatic,
    Manual,
    Disabled,
}

/// <summary>Whether the service was running when captured.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServiceRunState>))]
public enum ServiceRunState
{
    Stopped,
    Running,
    Other,
}

/// <summary>
/// Everything about a service that must be restored, captured as independent facts.
/// </summary>
/// <remarks>
/// Three facts, not one, and the separation is load-bearing:
/// <list type="bullet">
/// <item><see cref="StartType"/> and <see cref="DelayedAutostart"/> are distinct. .NET's
/// <c>ServiceController.StartType</c> collapses Automatic-Delayed into plain Automatic, so a
/// round-trip through it silently converts delayed-auto services to plain auto and slows every
/// subsequent boot. Four of the ten shipping candidates are delayed-auto on this machine.</item>
/// <item><see cref="RunState"/> is independent of start type: a Manual service can be running, and
/// an Automatic one can be stopped. Restore must put back both, separately.</item>
/// </list>
/// </remarks>
public sealed record ServicePrior
{
    [JsonPropertyName("service")]
    public required string Service { get; init; }

    /// <summary>False when the service does not exist on this build - a first-class outcome.</summary>
    [JsonPropertyName("present")]
    public required bool Present { get; init; }

    [JsonPropertyName("startType")]
    public ServiceStartType? StartType { get; init; }

    [JsonPropertyName("delayedAutostart")]
    public bool? DelayedAutostart { get; init; }

    [JsonPropertyName("runState")]
    public ServiceRunState? RunState { get; init; }

    /// <summary>
    /// Whether the SCM reported SERVICE_ACCEPT_STOP at capture time. Recorded so a revert can tell
    /// "was stoppable" from "we never tried".
    /// </summary>
    [JsonPropertyName("acceptsStop")]
    public bool? AcceptsStop { get; init; }

    /// <summary>
    /// Trigger-started services get clamped to Manual, never Disabled. Recorded because the clamp
    /// must be explicable after the fact.
    /// </summary>
    [JsonPropertyName("triggerStarted")]
    public bool? TriggerStarted { get; init; }

    /// <summary>PID hosting the service at capture time, for svchost co-tenancy diagnostics.</summary>
    [JsonPropertyName("hostProcessId")]
    public uint? HostProcessId { get; init; }
}

/// <summary>Live facts needed to decide whether a service may be touched at all.</summary>
public sealed record ServiceSnapshot
{
    public required string Service { get; init; }

    public required bool Present { get; init; }

    public ServiceStartType? StartType { get; init; }

    public bool DelayedAutostart { get; init; }

    public ServiceRunState RunState { get; init; }

    public bool AcceptsStop { get; init; }

    public bool TriggerStarted { get; init; }

    public uint HostProcessId { get; init; }

    /// <summary>
    /// Every service that depends on this one, <em>transitively</em>.
    /// </summary>
    /// <remarks>
    /// <c>EnumDependentServices</c> returns the full transitive closure, not the direct dependents
    /// the documentation's wording suggests — verified on this machine against the registry's
    /// <c>DependOnService</c> graph over three independent two-hop chains. So the caller must NOT
    /// recurse: doing so would double-visit services and corrupt the stop order.
    /// </remarks>
    public IReadOnlyList<string> Dependents { get; init; } = [];

    public ServicePrior ToPrior() => new()
    {
        Service = Service,
        Present = Present,
        StartType = Present ? StartType : null,
        DelayedAutostart = Present ? DelayedAutostart : null,
        RunState = Present ? RunState : null,
        AcceptsStop = Present ? AcceptsStop : null,
        TriggerStarted = Present ? TriggerStarted : null,
        HostProcessId = Present && HostProcessId != 0 ? HostProcessId : null,
    };
}

/// <summary>The mockable seam over the service control manager.</summary>
public interface IServiceControl
{
    /// <summary>
    /// Reads every fact needed to plan and to restore. Returns <c>Present = false</c> rather than
    /// throwing when the service does not exist on this build.
    /// </summary>
    ServiceSnapshot Query(string service);

    /// <summary>Services hosted by <paramref name="processId"/>, for co-tenancy checks.</summary>
    IReadOnlyList<string> ServicesInHostProcess(uint processId);

    /// <summary>
    /// Every PID currently hosting at least one service.
    /// </summary>
    /// <remarks>
    /// Feeds process classification. A service's host process must be managed through this service
    /// layer and never through the process layer: closing or throttling <c>svchost.exe</c> directly
    /// walks straight past the tier-0 list, the co-tenancy check and the remote-session lock, all of
    /// which are keyed on service names. One call, because the alternative is a full SCM enumeration
    /// per running process.
    /// </remarks>
    IReadOnlySet<uint> ServiceHostProcessIds();

    /// <summary>Sets start type and the delayed-auto flag, leaving all other config untouched.</summary>
    void SetStartType(string service, ServiceStartType startType, bool delayedAutostart);

    /// <summary>
    /// Requests a stop and waits for it. Never escalates: a service that will not stop is left
    /// running and reported, because terminating a service host is how a tool bugchecks a machine.
    /// </summary>
    /// <returns>True if it reached Stopped within the timeout.</returns>
    bool TryStop(string service, TimeSpan timeout, out string diagnosis);

    /// <summary>Starts a service and waits for it to reach Running.</summary>
    bool TryStart(string service, TimeSpan timeout, out string diagnosis);
}

/// <summary>Maps the catalog's requested mode onto the SCM's start type.</summary>
public static class ServiceStartModeExtensions
{
    public static ServiceStartType ToStartType(this ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Automatic => ServiceStartType.Automatic,
        ServiceStartMode.Manual => ServiceStartType.Manual,
        ServiceStartMode.Disabled => ServiceStartType.Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown start mode."),
    };
}
