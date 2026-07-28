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

    public IReadOnlyList<ProcessSnapshot> Enumerate()
    {
        var results = new List<ProcessSnapshot>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var snapshot = Describe(process);
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
            var snapshot = Describe(process);

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

    private static ProcessSnapshot Absent(ProcessIdentity identity) => new()
    {
        Identity = identity,
        ImageName = string.Empty,
        SessionId = -1,
        PriorityClass = ProcessPriorityClass.Normal,
        HasVisibleWindow = false,
        Present = false,
    };

    /// <summary>Reads one process, or null only when it has genuinely exited.</summary>
    private static ProcessSnapshot? Describe(Process process)
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
                HasVisibleWindow = TryHasWindow(process),
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

    private static bool TryHasWindow(Process process)
    {
        try
        {
            return process.MainWindowHandle != nint.Zero;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
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
}
