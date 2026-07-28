using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// In-memory <see cref="IServiceControl"/> with real SCM semantics — including the ones the
/// guardrails depend on: disabling does NOT stop a running service, host-process co-tenancy, and
/// a stop that can be made to refuse.
/// </summary>
public sealed class FakeServiceControl : IServiceControl
{
    public sealed class Entry
    {
        public required string Name { get; init; }

        public bool Present { get; set; } = true;

        public ServiceStartType StartType { get; set; } = ServiceStartType.Automatic;

        public bool DelayedAutostart { get; set; }

        public ServiceRunState RunState { get; set; } = ServiceRunState.Running;

        public bool AcceptsStop { get; set; } = true;

        public bool TriggerStarted { get; set; }

        public uint HostProcessId { get; set; }

        public List<string> Dependents { get; } = [];

        /// <summary>Makes TryStop refuse, simulating a service that will not shut down.</summary>
        public bool RefuseStop { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Log { get; } = [];

    public Entry Add(string name, Action<Entry>? configure = null)
    {
        var entry = new Entry { Name = name, HostProcessId = (uint)(1000 + _entries.Count) };
        configure?.Invoke(entry);
        _entries[name] = entry;
        return entry;
    }

    public Entry this[string name] => _entries[name];

    public ServiceSnapshot Query(string service)
    {
        if (!_entries.TryGetValue(service, out var entry) || !entry.Present)
        {
            return new ServiceSnapshot { Service = service, Present = false };
        }

        return new ServiceSnapshot
        {
            Service = service,
            Present = true,
            StartType = entry.StartType,
            DelayedAutostart = entry.DelayedAutostart,
            RunState = entry.RunState,
            AcceptsStop = entry.AcceptsStop,
            TriggerStarted = entry.TriggerStarted,
            HostProcessId = entry.HostProcessId,
            Dependents = entry.Dependents,
        };
    }

    public IReadOnlyList<string> ServicesInHostProcess(uint processId) =>
        _entries.Values
            .Where(e => e.Present && e.HostProcessId == processId && e.RunState == ServiceRunState.Running)
            .Select(e => e.Name)
            .ToList();

    public void SetStartType(string service, ServiceStartType startType, bool delayedAutostart)
    {
        Log.Add($"config {service} -> {startType}{(delayedAutostart ? " delayed" : string.Empty)}");

        var entry = _entries[service];
        entry.StartType = startType;
        entry.DelayedAutostart = delayedAutostart;

        // Deliberately does NOT stop the service. The real SCM behaves this way, and that is
        // exactly what makes "set Disabled, then fail to stop" leave a service Disabled+Running.
    }

    public bool TryStop(string service, TimeSpan timeout, out string diagnosis)
    {
        Log.Add($"stop {service}");
        var entry = _entries[service];

        if (entry.RefuseStop)
        {
            diagnosis = "the service stopped reporting progress. Quiesce will not force it.";
            return false;
        }

        entry.RunState = ServiceRunState.Stopped;
        diagnosis = string.Empty;
        return true;
    }

    public bool TryStart(string service, TimeSpan timeout, out string diagnosis)
    {
        Log.Add($"start {service}");
        _entries[service].RunState = ServiceRunState.Running;
        diagnosis = string.Empty;
        return true;
    }
}
