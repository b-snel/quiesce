using System.Diagnostics;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// In-memory <see cref="IProcessControl"/>. Exists to make the awkward cases arrangeable: a process
/// that exits mid-operation, one whose image path cannot be read, and a recycled PID.
/// </summary>
public sealed class FakeProcessControl : IProcessControl
{
    private readonly Dictionary<int, ProcessSnapshot> _byPid = [];

    private long _nextTick = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc).Ticks;

    public ProcessSnapshot Add(
        string imageName,
        string? imagePath,
        int pid = 0,
        ProcessPriorityClass priority = ProcessPriorityClass.Normal,
        bool hasWindow = true,
        int sessionId = 1)
    {
        var assignedPid = pid == 0 ? _byPid.Count + 1000 : pid;
        var snapshot = new ProcessSnapshot
        {
            Identity = new ProcessIdentity { Pid = assignedPid, CreatedUtcTicks = _nextTick++ },
            ImageName = imageName,
            ImagePath = imagePath,
            SessionId = sessionId,
            PriorityClass = priority,
            HasVisibleWindow = hasWindow,
        };

        _byPid[assignedPid] = snapshot;
        return snapshot;
    }

    /// <summary>Removes a process, as if it exited.</summary>
    public void Exit(ProcessIdentity identity) => _byPid.Remove(identity.Pid);

    /// <summary>
    /// Replaces the process at a PID with a different one, as Windows does when it recycles a PID.
    /// </summary>
    public ProcessSnapshot Recycle(ProcessIdentity identity, string imageName, string? imagePath)
    {
        _byPid.Remove(identity.Pid);
        return Add(imageName, imagePath, identity.Pid);
    }

    public IReadOnlyList<ProcessSnapshot> Enumerate() => _byPid.Values.ToList();

    public ProcessSnapshot Query(ProcessIdentity identity)
    {
        if (_byPid.TryGetValue(identity.Pid, out var found)
            && found.Identity.CreatedUtcTicks == identity.CreatedUtcTicks)
        {
            return found;
        }

        return new ProcessSnapshot
        {
            Identity = identity,
            ImageName = string.Empty,
            SessionId = -1,
            PriorityClass = ProcessPriorityClass.Normal,
            HasVisibleWindow = false,
            Present = false,
        };
    }
}
