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

    /// <summary>
    /// PIDs that will accept a close request and never exit, modelling an application sitting on a
    /// "save your work?" prompt — the case the graceful ladder exists to respect.
    /// </summary>
    public HashSet<int> RefuseToExit { get; } = [];

    /// <summary>Close requests received, in order. Asserted on to prove nothing was force-killed.</summary>
    public List<string> CloseLog { get; } = [];

    public ProcessCloseResult TryClose(ProcessIdentity identity, TimeSpan timeout, out string diagnosis)
    {
        if (!_byPid.TryGetValue(identity.Pid, out var found)
            || found.Identity.CreatedUtcTicks != identity.CreatedUtcTicks)
        {
            diagnosis = "already exited before Quiesce asked";
            return ProcessCloseResult.AlreadyGone;
        }

        if (!found.HasVisibleWindow)
        {
            diagnosis = "has no top-level window, so there is nothing to send a close request to";
            return ProcessCloseResult.NoWindow;
        }

        CloseLog.Add($"close {found.ImageName} ({identity.Pid})");

        if (RefuseToExit.Contains(identity.Pid))
        {
            diagnosis = "was asked to close and is still running; it is most likely prompting about unsaved work";
            return ProcessCloseResult.DeclinedToClose;
        }

        _byPid.Remove(identity.Pid);
        diagnosis = string.Empty;
        return ProcessCloseResult.Closed;
    }

    /// <summary>PIDs whose priority write silently does not stick, modelling a kernel-adjusted request.</summary>
    public HashSet<int> IgnorePriorityWrites { get; } = [];

    public List<string> PriorityLog { get; } = [];

    public bool TrySetPriority(ProcessIdentity identity, ProcessPriorityClass priority, out string diagnosis)
    {
        if (!_byPid.TryGetValue(identity.Pid, out var found)
            || found.Identity.CreatedUtcTicks != identity.CreatedUtcTicks)
        {
            diagnosis = "no longer running";
            return false;
        }

        PriorityLog.Add($"priority {found.ImageName} ({identity.Pid}) {found.PriorityClass} -> {priority}");

        if (IgnorePriorityWrites.Contains(identity.Pid))
        {
            // The write "succeeds" and the value does not change, so the verify re-read catches it.
            diagnosis = $"asked for {priority} but it reads {found.PriorityClass} afterwards";
            return false;
        }

        _byPid[identity.Pid] = found with { PriorityClass = priority };
        diagnosis = string.Empty;
        return true;
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
