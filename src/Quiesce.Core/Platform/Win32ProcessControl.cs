using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Quiesce.Core.Platform;

/// <summary>
/// The production <see cref="IProcessControl"/>, reading live processes.
/// </summary>
/// <remarks>
/// <para>
/// Every handle here is opened with <c>PROCESS_QUERY_LIMITED_INFORMATION</c> and nothing else. That
/// is the narrowest right that can answer "what is this program and when did it start", and it
/// deliberately cannot read or write another process's memory. The stronger
/// <c>PROCESS_QUERY_INFORMATION</c> would work too and is what <see cref="Process.MainModule"/> uses
/// under the covers — which is why MainModule is avoided: on a machine with EasyAntiCheat installed,
/// asking for more access to a protected game than the question requires is not worth the risk of
/// being classified as probing it.
/// </para>
/// <para>
/// A process that denies the query is reported with a null path rather than skipped, so the
/// classifier can make the protective decision explicitly instead of the process quietly vanishing
/// from the inventory.
/// </para>
/// </remarks>
public sealed class Win32ProcessControl : IProcessControl
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int MaxPath = 32767;
    private const uint WM_CLOSE = 0x0010;
    private const int PollIntervalMs = 100;

    private delegate bool EnumWindowsProc(nint window, nint param);

    public IReadOnlyList<ProcessSnapshot> Enumerate()
    {
        var results = new List<ProcessSnapshot>();

        // One window enumeration for the whole pass, shared across every process. Asking per process
        // would mean walking every window on the desktop once per process — several hundred passes.
        var windowed = PidsWithVisibleWindows();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var snapshot = Describe(process, windowed);
                if (snapshot is not null)
                {
                    results.Add(snapshot);
                }
            }
        }

        return results;
    }

    public ProcessSnapshot Query(ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            var snapshot = Describe(process, PidsWithVisibleWindows());

            // The PID exists, but is it still the SAME process? A recycled PID that answers to the
            // wrong creation time must read as absent, or restore would put a captured prior onto
            // an unrelated program. Reported as absent rather than as an error because from the
            // caller's point of view the process it asked about is genuinely gone.
            if (snapshot is null || snapshot.Identity.CreatedUtcTicks != identity.CreatedUtcTicks)
            {
                return Absent(identity);
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // ArgumentException: no such PID. InvalidOperationException: it exited mid-call.
            return Absent(identity);
        }
    }

    public ProcessCloseResult TryClose(ProcessIdentity identity, TimeSpan timeout, out string diagnosis)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // Re-verify identity first. Between the plan and this call the PID may have been recycled,
        // and posting WM_CLOSE at a recycled PID would close whatever now owns that number.
        var live = Query(identity);
        if (!live.Present)
        {
            diagnosis = "already exited before Quiesce asked";
            return ProcessCloseResult.AlreadyGone;
        }

        // Window messages do not cross session boundaries. Posting into another session silently
        // does nothing, which would read as "the app declined" rather than "we cannot reach it".
        var ownSession = CurrentSessionId();
        if (live.SessionId != ownSession)
        {
            diagnosis = $"runs in session {live.SessionId}, not this session ({ownSession}); " +
                        "window messages do not cross sessions, so it cannot be asked to close";
            return ProcessCloseResult.NoWindow;
        }

        var windows = TopLevelWindows(identity.Pid);
        if (windows.Count == 0)
        {
            diagnosis = "has no top-level window, so there is nothing to send a close request to";
            return ProcessCloseResult.NoWindow;
        }

        var posted = 0;
        foreach (var window in windows)
        {
            if (PostMessageW(window, WM_CLOSE, nint.Zero, nint.Zero))
            {
                posted++;
            }
        }

        if (posted == 0)
        {
            // Usually UIPI: a lower-integrity caller cannot post to a window owned by an elevated
            // process. Reported as NoWindow rather than DeclinedToClose because the application never
            // heard the request - blaming it for declining would be wrong.
            diagnosis = $"Windows refused to deliver the close request to any of its {windows.Count} " +
                        "window(s); this is normal when the target runs at a higher integrity level";
            return ProcessCloseResult.NoWindow;
        }

        if (WaitForExit(identity, timeout))
        {
            diagnosis = string.Empty;
            return ProcessCloseResult.Closed;
        }

        diagnosis =
            $"was asked to close ({posted} of {windows.Count} window(s) accepted the request) and was " +
            $"still running after {timeout.TotalSeconds:0}s. It is most likely prompting about unsaved " +
            "work; that prompt is still on screen and the program is untouched.";
        return ProcessCloseResult.DeclinedToClose;
    }

    public bool TrySetPriority(ProcessIdentity identity, ProcessPriorityClass priority, out string diagnosis)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // Identity before anything else. Writing a priority at a recycled PID would silently
        // reconfigure whatever now owns that number - and on the restore path, would write a captured
        // prior onto an unrelated program.
        if (!Query(identity).Present)
        {
            diagnosis = "no longer running";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            process.PriorityClass = priority;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                      or InvalidOperationException
                                      or NotSupportedException)
        {
            diagnosis = $"Windows refused the priority change: {ex.Message}";
            return false;
        }

        // Re-read. SetPriorityClass can report success while the kernel declines or adjusts the
        // request, and a throttle that quietly did nothing would still be journalled as applied - so
        // restore would later write a priority the process never actually had.
        var after = Query(identity);
        if (!after.Present)
        {
            diagnosis = "exited while its priority was being changed";
            return false;
        }

        if (after.PriorityClass != priority)
        {
            diagnosis = $"asked for {priority} but it reads {after.PriorityClass} afterwards";
            return false;
        }

        diagnosis = string.Empty;
        return true;
    }

    private static int CurrentSessionId()
    {
        using var self = Process.GetCurrentProcess();
        return self.SessionId;
    }

    /// <summary>Polls for exit rather than waiting on a handle, so a recycled PID cannot read as an exit.</summary>
    private bool WaitForExit(ProcessIdentity identity, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            if (!Query(identity).Present)
            {
                return true;
            }

            Thread.Sleep(PollIntervalMs);
        }

        return !Query(identity).Present;
    }

    private static List<nint> TopLevelWindows(int pid)
    {
        var found = new List<nint>();

        EnumWindows(
            (window, _) =>
            {
                if (GetWindowThreadProcessId(window, out var owner) != 0 && owner == pid && IsWindowVisible(window))
                {
                    found.Add(window);
                }

                return true;
            },
            nint.Zero);

        return found;
    }

    private static ProcessSnapshot Absent(ProcessIdentity identity) => new()
    {
        Identity = identity,
        ImageName = string.Empty,
        SessionId = -1,
        PriorityClass = ProcessPriorityClass.Normal,
        HasVisibleWindow = false,
        Present = false,
    };

    /// <summary>
    /// Every PID owning at least one visible top-level window, from a single desktop-wide pass.
    /// </summary>
    /// <remarks>
    /// This is the SAME source the close ladder uses to find windows to post to, and that matters.
    /// An earlier version derived the inventory's window flag from <see cref="Process.MainWindowHandle"/>
    /// instead, which disagreed: a live probe reported <c>window=False</c> for a process the ladder
    /// then found one window on and successfully posted to. MainWindowHandle is unreliable for console
    /// hosts and for applications whose first window is not their main one, so the flag reported to
    /// the user was understating how many processes could actually be asked to close.
    /// </remarks>
    private static HashSet<int> PidsWithVisibleWindows()
    {
        var pids = new HashSet<int>();

        EnumWindows(
            (window, _) =>
            {
                if (IsWindowVisible(window) && GetWindowThreadProcessId(window, out var owner) != 0)
                {
                    pids.Add(owner);
                }

                return true;
            },
            nint.Zero);

        return pids;
    }

    /// <summary>Reads one process, or null only when it has genuinely exited.</summary>
    private static ProcessSnapshot? Describe(Process process, HashSet<int> pidsWithWindows)
    {
        int pid;
        string imageName;
        try
        {
            pid = process.Id;
            imageName = process.ProcessName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Exited between enumeration and inspection. The only case worth omitting.
            return null;
        }

        // Creation time is the recycling-proof half of the identity. The most protected processes
        // deny it to an unelevated caller, and they are still REPORTED rather than dropped — an
        // inventory short by ten processes with nothing saying so is the kind of silent omission
        // this project refuses elsewhere, and it would differ between elevated and unelevated runs.
        long created = 0;
        var createdKnown = false;
        try
        {
            created = process.StartTime.ToUniversalTime().Ticks;
            createdKnown = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                      or System.ComponentModel.Win32Exception
                                      or NotSupportedException)
        {
            // Access denied (csrss, lsass, winlogon, services, smss) or a pseudo-process (Idle,
            // System). It must be the real Win32Exception in this filter: an earlier draft used a
            // private nested type of a similar name, which can never match what the OS throws, so
            // every access-denied escaped and took the whole enumeration down on the first
            // protected process.
            createdKnown = false;
        }

        try
        {
            return new ProcessSnapshot
            {
                Identity = new ProcessIdentity { Pid = pid, CreatedUtcTicks = created },
                CreationTimeKnown = createdKnown,
                ImageName = imageName,
                ImagePath = TryReadImagePath(pid),
                SessionId = TryReadSessionId(process),
                PriorityClass = TryReadPriority(process),
                HasVisibleWindow = pidsWithWindows.Contains(pid),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static int TryReadSessionId(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }

    private static ProcessPriorityClass TryReadPriority(Process process)
    {
        try
        {
            return process.PriorityClass;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Access denied on a protected process. Reported as Normal, which is only ever used to
            // decide whether a throttle would change anything — and a protected process is never
            // throttled, because the classifier reaches NeverTouch on the unreadable path first.
            return ProcessPriorityClass.Normal;
        }
    }

    private static string? TryReadImagePath(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(MaxPath);
            var size = buffer.Capacity;

            return QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? buffer.ToString(0, size)
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        nint process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    // PostMessage, not SendMessage: SendMessage blocks until the target's message loop processes it,
    // and a hung application would hang Quiesce with it. Posting is fire-and-forget, and the outcome
    // is judged by whether the process actually exited rather than by what the call returned.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
