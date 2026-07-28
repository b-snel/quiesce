using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Quiesce.Core.Platform;

/// <summary>
/// The production <see cref="IServiceControl"/>, over the Windows Service Control Manager.
/// </summary>
/// <remarks>
/// Deliberately not built on <c>System.ServiceProcess.ServiceController</c>:
/// <list type="bullet">
/// <item>Its <c>StartType</c> collapses Automatic-Delayed into plain <c>Automatic</c>, so a
/// capture/restore round-trip through it silently converts delayed-auto services to plain auto and
/// slows every subsequent boot. Four of the ten shipping candidates are delayed-auto here.</item>
/// <item>It offers no way to <em>write</em> the start type at all.</item>
/// <item>It cannot report the hosting process id, which is what the svchost co-tenancy guardrail
/// needs to avoid a <c>CRITICAL_PROCESS_DIED</c> bugcheck.</item>
/// </list>
/// Writes go through <c>ChangeServiceConfig</c> rather than the raw registry because the SCM
/// validates the transition and notifies itself; writing <c>Start</c> under
/// <c>HKLM\SYSTEM\CurrentControlSet\Services</c> directly leaves the running SCM with a stale view.
/// </remarks>
public sealed class Win32ServiceControl : IServiceControl
{
    // Service Control Manager access rights.
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;

    // Per-service access rights. Requested narrowly per operation: a tool that runs elevated should
    // not open every handle with SERVICE_ALL_ACCESS just because it can.
    private const uint SERVICE_QUERY_CONFIG = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_ENUMERATE_DEPENDENTS = 0x0008;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;

    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

    // Start types as the SCM stores them.
    private const uint SERVICE_BOOT_START = 0x0;
    private const uint SERVICE_SYSTEM_START = 0x1;
    private const uint SERVICE_AUTO_START = 0x2;
    private const uint SERVICE_DEMAND_START = 0x3;
    private const uint SERVICE_DISABLED = 0x4;

    private const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    private const uint SERVICE_CONFIG_TRIGGER_INFO = 8;

    private const uint SERVICE_CONTROL_STOP = 0x1;
    private const uint SC_STATUS_PROCESS_INFO = 0;

    private const uint SERVICE_ACCEPT_STOP = 0x1;

    private const uint SERVICE_STOPPED = 0x1;
    private const uint SERVICE_START_PENDING = 0x2;
    private const uint SERVICE_STOP_PENDING = 0x3;
    private const uint SERVICE_RUNNING = 0x4;

    private const uint SERVICE_WIN32 = 0x30;
    private const uint SERVICE_STATE_ALL = 0x3;
    private const uint SC_ENUM_PROCESS_INFO = 0;

    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_MORE_DATA = 234;
    private const int ERROR_SERVICE_NOT_ACTIVE = 1062;

    /// <summary>
    /// Hard ceiling on <c>cbBufSize</c> for the Query* family, measured on this machine.
    /// </summary>
    /// <remarks>
    /// 8192 succeeds; 8193 fails with <c>ERROR_RPC_X_BAD_STUB_DATA</c> (1783) even when the
    /// allocation is genuinely that large — the API is rejecting the declared SIZE, not running out
    /// of room. That makes the obvious implementation (one generously-sized scratch buffer reused
    /// for every query) fail 100% of the time, with an RPC error that reads like a marshalling bug
    /// in the caller. Always size from the probe, and never hand these APIs more than this.
    /// </remarks>
    private const int MaxQueryBufferBytes = 8192;

    public ServiceSnapshot Query(string service)
    {
        using var scm = OpenManager(SC_MANAGER_CONNECT);
        using var handle = OpenServiceW(
            scm,
            service,
            SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS | SERVICE_ENUMERATE_DEPENDENTS);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();

            // Absence is a first-class outcome, not an exception: service names come and go between
            // Windows releases (Fax is gone on build 26200), and a tool that throws here is one
            // feature update away from being unusable.
            if (error == ERROR_SERVICE_DOES_NOT_EXIST)
            {
                return new ServiceSnapshot { Service = service, Present = false };
            }

            throw new Win32Exception(error, $"OpenService('{service}') failed.");
        }

        var (startType, _) = ReadConfig(handle, service);
        var status = ReadStatus(handle, service);

        return new ServiceSnapshot
        {
            Service = service,
            Present = true,
            StartType = startType,
            DelayedAutostart = ReadDelayedAutostart(handle),
            RunState = status.dwCurrentState switch
            {
                SERVICE_RUNNING => ServiceRunState.Running,
                SERVICE_STOPPED => ServiceRunState.Stopped,
                _ => ServiceRunState.Other,
            },
            AcceptsStop = (status.dwControlsAccepted & SERVICE_ACCEPT_STOP) != 0,
            TriggerStarted = ReadHasTriggers(handle),
            HostProcessId = status.dwProcessId,
            Dependents = ReadDependents(handle),
        };
    }

    public IReadOnlyList<string> ServicesInHostProcess(uint processId)
    {
        if (processId == 0)
        {
            return [];
        }

        return EnumerateServiceProcesses()
            .Where(e => e.Pid == processId)
            .Select(e => e.Name)
            .ToList();
    }

    public IReadOnlySet<uint> ServiceHostProcessIds() =>
        EnumerateServiceProcesses()
            .Where(e => e.Pid != 0)
            .Select(e => e.Pid)
            .ToHashSet();

    /// <summary>
    /// One SCM enumeration returning every service and the PID currently hosting it.
    /// </summary>
    /// <remarks>
    /// Shared because both callers used to do their own full enumeration, and the process classifier
    /// needs the host-PID set once for every running process — doing it per process would mean one
    /// full SCM enumeration per process, several hundred on a normal machine.
    /// </remarks>
    private static List<(string Name, uint Pid)> EnumerateServiceProcesses()
    {
        using var scm = OpenManager(SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);

        var bytesNeeded = 0;
        var count = 0;
        var resume = 0;

        EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
            IntPtr.Zero, 0, out bytesNeeded, out count, ref resume, null);

        if (bytesNeeded <= 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(bytesNeeded);
        try
        {
            resume = 0;
            if (!EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                    buffer, bytesNeeded, out bytesNeeded, out count, ref resume, null))
            {
                return [];
            }

            var results = new List<(string, uint)>();
            var size = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();

            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buffer + (i * size));
                if (entry.lpServiceName is not null)
                {
                    results.Add((entry.lpServiceName, entry.ServiceStatusProcess.dwProcessId));
                }
            }

            return results;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void SetStartType(string service, ServiceStartType startType, bool delayedAutostart)
    {
        using var scm = OpenManager(SC_MANAGER_CONNECT);
        using var handle = OpenServiceW(scm, service, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenService('{service}') for config failed.");
        }

        // SERVICE_NO_CHANGE for every numeric field we are not setting, and null for every string
        // field, so this touches the start type and nothing else. Passing real values for the rest
        // would silently rewrite the binary path or account of a service we only meant to reclassify.
        if (!ChangeServiceConfigW(
                handle,
                SERVICE_NO_CHANGE,
                ToNative(startType),
                SERVICE_NO_CHANGE,
                null, null, IntPtr.Zero, null, null, null, null))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"ChangeServiceConfig('{service}') failed.");
        }

        // Delayed-auto lives in a separate config class and the SCM keeps it independently of the
        // start type, so restoring "Automatic + delayed" genuinely needs both calls, in this order.
        //
        // Only write it when it actually differs from what is already there. Six of the ten
        // shipping candidates have NO DelayedAutostart value at all, and issuing this call
        // materializes one — a silent registry mutation that survives revert and quietly breaks the
        // exact-restore promise even though the behaviour is unchanged.
        if (ReadDelayedAutostart(handle) != delayedAutostart)
        {
            var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayedAutostart };
            var block = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_DELAYED_AUTO_START_INFO>());
            try
            {
                Marshal.StructureToPtr(info, block, fDeleteOld: false);
                if (!ChangeServiceConfig2W(handle, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, block))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(), $"ChangeServiceConfig2('{service}', delayed-auto) failed.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(block);
            }
        }
    }

    public bool TryStop(string service, TimeSpan timeout, out string diagnosis)
    {
        using var scm = OpenManager(SC_MANAGER_CONNECT);
        using var handle = OpenServiceW(scm, service, SERVICE_STOP | SERVICE_QUERY_STATUS);

        if (handle.IsInvalid)
        {
            diagnosis = $"OpenService failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return false;
        }

        var status = default(SERVICE_STATUS_PROCESS);
        if (!ControlService(handle, SERVICE_CONTROL_STOP, ref status))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_SERVICE_NOT_ACTIVE)
            {
                diagnosis = "already stopped";
                return true;
            }

            diagnosis = $"stop request rejected: {new Win32Exception(error).Message}";
            return false;
        }

        return WaitForState(handle, SERVICE_STOPPED, timeout, out diagnosis);
    }

    public bool TryStart(string service, TimeSpan timeout, out string diagnosis)
    {
        using var scm = OpenManager(SC_MANAGER_CONNECT);
        using var handle = OpenServiceW(scm, service, SERVICE_START | SERVICE_QUERY_STATUS);

        if (handle.IsInvalid)
        {
            diagnosis = $"OpenService failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return false;
        }

        if (!StartServiceW(handle, 0, null))
        {
            var error = Marshal.GetLastWin32Error();
            diagnosis = $"start request rejected: {new Win32Exception(error).Message}";
            return false;
        }

        return WaitForState(handle, SERVICE_RUNNING, timeout, out diagnosis);
    }

    /// <summary>
    /// Polls until the service reaches <paramref name="desired"/>, following Microsoft's documented
    /// pattern: sleep a fraction of <c>dwWaitHint</c>, and treat a <c>dwCheckPoint</c> that stops
    /// advancing as failure rather than waiting forever.
    /// </summary>
    /// <remarks>
    /// Returning false here is the end of the road by design. There is no escalation path: the only
    /// way to force a service that will not stop is to terminate its host process, and doing that
    /// to a shared svchost is <c>CRITICAL_PROCESS_DIED</c>. A stuck service is reported and left
    /// running.
    /// </remarks>
    private static bool WaitForState(SafeServiceHandle handle, uint desired, TimeSpan timeout, out string diagnosis)
    {
        var deadline = DateTime.UtcNow + timeout;
        var lastCheckPoint = uint.MaxValue;
        var lastProgress = DateTime.UtcNow;

        while (true)
        {
            var status = QueryStatus(handle);

            if (status.dwCurrentState == desired)
            {
                diagnosis = string.Empty;
                return true;
            }

            if (status.dwCheckPoint != lastCheckPoint)
            {
                lastCheckPoint = status.dwCheckPoint;
                lastProgress = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - lastProgress > TimeSpan.FromSeconds(10))
            {
                diagnosis =
                    $"the service stopped reporting progress (state {status.dwCurrentState}, " +
                    "checkpoint stalled). Quiesce will not force it.";
                return false;
            }

            if (DateTime.UtcNow >= deadline)
            {
                diagnosis = $"timed out after {timeout.TotalSeconds:0}s in state {status.dwCurrentState}.";
                return false;
            }

            // dwWaitHint is the service's own estimate; clamp it so a pathological hint neither
            // spins the CPU nor sleeps past the deadline.
            var wait = Math.Clamp((int)(status.dwWaitHint / 10), 200, 2000);
            Thread.Sleep(wait);
        }
    }

    private static SafeServiceHandle OpenManager(uint access)
    {
        var scm = OpenSCManagerW(null, null, access);
        return scm.IsInvalid
            ? throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.")
            : scm;
    }

    private static (ServiceStartType StartType, string BinaryPath) ReadConfig(SafeServiceHandle handle, string service)
    {
        // Two-call buffer sizing: the first call is expected to fail with ERROR_INSUFFICIENT_BUFFER
        // and report the size it wants. Never round the result up — see MaxQueryBufferBytes.
        QueryServiceConfigW(handle, IntPtr.Zero, 0, out var needed);
        if (needed == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"QueryServiceConfig('{service}') sizing failed.");
        }

        if (needed > MaxQueryBufferBytes)
        {
            throw new InvalidOperationException(
                $"QueryServiceConfig('{service}') wants {needed} bytes, above the {MaxQueryBufferBytes}-byte RPC " +
                "ceiling. Passing that size would fail with ERROR_RPC_X_BAD_STUB_DATA rather than a buffer error.");
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!QueryServiceConfigW(handle, buffer, needed, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"QueryServiceConfig('{service}') failed.");
            }

            var config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIGW>(buffer);
            return (FromNative(config.dwStartType), config.lpBinaryPathName ?? string.Empty);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool ReadDelayedAutostart(SafeServiceHandle handle)
    {
        QueryServiceConfig2W(handle, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, IntPtr.Zero, 0, out var needed);
        if (needed == 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!QueryServiceConfig2W(handle, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, buffer, needed, out _))
            {
                return false;
            }

            return Marshal.PtrToStructure<SERVICE_DELAYED_AUTO_START_INFO>(buffer).fDelayedAutostart;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Whether the service has any start triggers. A trigger-started service must only ever be
    /// downgraded to Manual: setting it Disabled leaves the trigger firing into a failed activation,
    /// and the dependent feature breaks silently weeks later with nothing to connect it to.
    /// </summary>
    private static bool ReadHasTriggers(SafeServiceHandle handle)
    {
        QueryServiceConfig2W(handle, SERVICE_CONFIG_TRIGGER_INFO, IntPtr.Zero, 0, out var needed);
        if (needed == 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!QueryServiceConfig2W(handle, SERVICE_CONFIG_TRIGGER_INFO, buffer, needed, out _))
            {
                return false;
            }

            return Marshal.PtrToStructure<SERVICE_TRIGGER_INFO>(buffer).cTriggers > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Direct dependents, in every state. Querying only active dependents would miss a stopped
    /// service that starts later and then fails; transitive expansion is the caller's job.
    /// </summary>
    private static IReadOnlyList<string> ReadDependents(SafeServiceHandle handle)
    {
        if (EnumDependentServicesW(handle, SERVICE_STATE_ALL, IntPtr.Zero, 0, out var needed, out _))
        {
            return []; // succeeded with a zero buffer: no dependents
        }

        if (Marshal.GetLastWin32Error() is not (ERROR_MORE_DATA or ERROR_INSUFFICIENT_BUFFER) || needed == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumDependentServicesW(handle, SERVICE_STATE_ALL, buffer, needed, out _, out var count))
            {
                return [];
            }

            var results = new List<string>();
            var size = Marshal.SizeOf<ENUM_SERVICE_STATUS>();

            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS>(buffer + (i * size));
                if (entry.lpServiceName is not null)
                {
                    results.Add(entry.lpServiceName);
                }
            }

            return results;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SERVICE_STATUS_PROCESS ReadStatus(SafeServiceHandle handle, string service)
    {
        try
        {
            return QueryStatus(handle);
        }
        catch (Win32Exception ex)
        {
            throw new Win32Exception(ex.NativeErrorCode, $"QueryServiceStatusEx('{service}') failed.");
        }
    }

    private static SERVICE_STATUS_PROCESS QueryStatus(SafeServiceHandle handle)
    {
        var size = Marshal.SizeOf<SERVICE_STATUS_PROCESS>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(handle, SC_STATUS_PROCESS_INFO, buffer, size, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryServiceStatusEx failed.");
            }

            return Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint ToNative(ServiceStartType startType) => startType switch
    {
        ServiceStartType.Boot => SERVICE_BOOT_START,
        ServiceStartType.System => SERVICE_SYSTEM_START,
        ServiceStartType.Automatic => SERVICE_AUTO_START,
        ServiceStartType.Manual => SERVICE_DEMAND_START,
        ServiceStartType.Disabled => SERVICE_DISABLED,
        _ => throw new ArgumentOutOfRangeException(nameof(startType), startType, "Unknown start type."),
    };

    private static ServiceStartType FromNative(uint value) => value switch
    {
        SERVICE_BOOT_START => ServiceStartType.Boot,
        SERVICE_SYSTEM_START => ServiceStartType.System,
        SERVICE_AUTO_START => ServiceStartType.Automatic,
        SERVICE_DEMAND_START => ServiceStartType.Manual,
        SERVICE_DISABLED => ServiceStartType.Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown native start type."),
    };

    // ------------------------------------------------------------- interop

    private sealed class SafeServiceHandle() : SafeHandle(IntPtr.Zero, ownsHandle: true)
    {
        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QUERY_SERVICE_CONFIGW
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpBinaryPathName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpLoadOrderGroup;
        public uint dwTagId;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDependencies;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpServiceStartName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DELAYED_AUTO_START_INFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fDelayedAutostart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_TRIGGER_INFO
    {
        public uint cTriggers;
        public IntPtr pTriggers;
        public IntPtr pReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUM_SERVICE_STATUS
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string lpServiceName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDisplayName;
        public SERVICE_STATUS ServiceStatus;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUM_SERVICE_STATUS_PROCESS
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string lpServiceName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenServiceW(SafeServiceHandle scm, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(SafeServiceHandle service, IntPtr config, int bufSize, out int bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2W(SafeServiceHandle service, uint infoLevel, IntPtr buffer, int bufSize, out int bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(
        SafeServiceHandle service, uint serviceType, uint startType, uint errorControl,
        string? binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(SafeServiceHandle service, uint infoLevel, IntPtr info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(SafeServiceHandle service, uint infoLevel, IntPtr buffer, int bufSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(SafeServiceHandle service, uint control, ref SERVICE_STATUS_PROCESS status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(SafeServiceHandle service, int numArgs, string[]? args);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDependentServicesW(
        SafeServiceHandle service, uint serviceState, IntPtr services, int bufSize,
        out int bytesNeeded, out int servicesReturned);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusExW(
        SafeServiceHandle scm, uint infoLevel, uint serviceType, uint serviceState,
        IntPtr services, int bufSize, out int bytesNeeded, out int servicesReturned,
        ref int resumeHandle, string? groupName);
}
