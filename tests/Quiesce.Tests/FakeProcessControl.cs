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
        var assignedPid = pid == 0 ? NextFreePid() : pid;
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

    /// <summary>
    /// The next unused PID at or above 1000, for the callers that do not care which they get.
    /// </summary>
    /// <remarks>
    /// It used to be <c>_byPid.Count + 1000</c>, which is not the same thing and produced a genuine
    /// PID-dependent flake. A test that adds one process at an EXPLICIT pid and then two auto-assigned
    /// ones gets 1001 and 1002 — so when the explicit pid happened to be 1001 itself, the second Add
    /// silently OVERWROTE the first, the count did not grow, and the third overwrote the second. Three
    /// added processes, one surviving entry.
    /// <para>
    /// Observed exactly once in a full run and not reproducible afterwards, because whether it happens
    /// depends on the test host's own <c>Environment.ProcessId</c>:
    /// <c>ProcessesInAnotherSessionAreNotOffered</c> adds "self" at <c>Environment.ProcessId</c>, so on a
    /// run where the runner happened to be PID 1001 the map collapsed to a single session-2 process, the
    /// discovery list came back empty, and the assertion failed. Every other PID passes. That is the
    /// worst possible failure mode for a test suite: correct almost always, wrong for a reason that looks
    /// like nothing to do with the test.
    /// </para>
    /// <para>
    /// Monotonic and skipping occupied slots, so it cannot collide with an explicit pid whatever the
    /// runner's own is. Explicit pids may still overwrite each other, deliberately — that is what
    /// <see cref="Recycle"/> is.
    /// </para>
    /// </remarks>
    private int NextFreePid()
    {
        while (_byPid.ContainsKey(_nextAutoPid))
        {
            _nextAutoPid++;
        }

        return _nextAutoPid++;
    }

    private int _nextAutoPid = 1000;

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

        BeforeClose?.Invoke();
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

    /// <summary>
    /// Runs immediately before a priority write lands, so a test can inspect the world as it was at that
    /// instant. Exists to prove the write-ahead ordering: the journal must already describe the change.
    /// </summary>
    public Action? BeforePriorityWrite { get; set; }

    /// <summary>Runs immediately before a close request is delivered, for the same reason.</summary>
    public Action? BeforeClose { get; set; }

    public bool TrySetPriority(ProcessIdentity identity, ProcessPriorityClass priority, out string diagnosis)
    {
        if (!_byPid.TryGetValue(identity.Pid, out var found)
            || found.Identity.CreatedUtcTicks != identity.CreatedUtcTicks)
        {
            diagnosis = "no longer running";
            return false;
        }

        BeforePriorityWrite?.Invoke();
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
