using System.Runtime.InteropServices;

namespace Quiesce.Core.Platform;

/// <summary>
/// The chain of processes that launched this one, plus this one.
/// </summary>
/// <remarks>
/// <para>
/// Quiesce must never close or throttle whatever started it. Closing your own launcher is
/// self-sabotage: the app dies mid-apply with a journal it can no longer discharge, and if the
/// launcher was a shell on a remote session it can leave no way back in. Throttling it is quieter but
/// no better — the process driving the apply gets starved by the apply.
/// </para>
/// <para>
/// This also produces the right behaviour for free in two situations that would otherwise need a
/// special case. During development Quiesce is launched from a terminal or a host application, so
/// that host is an ancestor and is protected automatically. In production the user launches Quiesce
/// from the Start menu, Explorer is the ancestor, and any application that merely happens to be
/// running is not — so a group the catalog says to close still gets closed. The protection follows
/// from how Quiesce was started rather than from a hard-coded list of names to spare.
/// </para>
/// <para>
/// Parent PIDs are a snapshot and PIDs recycle, so an ancestor chain can in principle include a PID
/// that has been reused. That makes this check over-inclusive, which is the safe direction: the cost
/// is one process left alone, and the cost of the opposite error is killing the thing driving the run.
/// </para>
/// </remarks>
public static class ProcessAncestry
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    /// <summary>Overrides the live ancestry. Tests only.</summary>
    internal static IReadOnlySet<int>? OverrideForTests { get; set; }

    /// <summary>
    /// Image paths of the current process and of every ancestor — and therefore of every OTHER process
    /// running the same images.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PIDs alone are not enough, and measuring proved it. On the development machine the host
    /// application runs 14 processes; only 2 of them were in the ancestor chain, because a
    /// Chromium-style application puts its renderers and helpers <em>beside</em> the process that
    /// spawned the child, not above it. The other 12 classified as ordinary and would have been
    /// throttled — which breaks the host just as thoroughly as touching the main process would.
    /// </para>
    /// <para>
    /// Matching on the image path catches the whole family without needing to know anything about how
    /// that application structures itself. It is deliberately not name-based: a program merely
    /// <em>called</em> the same thing somewhere else on disk is not the host.
    /// </para>
    /// <para>
    /// In production this stays narrow. Quiesce is launched from Explorer, which is already
    /// never-touch, so the set contains Quiesce's own image and nothing that would spare an ordinary
    /// application the catalog asked to close.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> SelfHostImagePaths(IProcessControl processes)
    {
        ArgumentNullException.ThrowIfNull(processes);

        var chain = SelfAndAncestors();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in processes.Enumerate())
        {
            if (chain.Contains(process.Identity.Pid) && !string.IsNullOrWhiteSpace(process.ImagePath))
            {
                paths.Add(process.ImagePath);
            }
        }

        return paths;
    }

    /// <summary>
    /// The current process and every ancestor of it, as PIDs.
    /// </summary>
    public static IReadOnlySet<int> SelfAndAncestors()
    {
        if (OverrideForTests is { } forced)
        {
            return forced;
        }

        var parents = ParentMap();
        var chain = new HashSet<int>();

        var current = Environment.ProcessId;
        while (current != 0 && chain.Add(current))
        {
            if (!parents.TryGetValue(current, out var parent) || parent == current)
            {
                break;
            }

            current = parent;
        }

        return chain;
    }

    /// <summary>Every process's parent PID, from one snapshot.</summary>
    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();

        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == nint.Zero || snapshot == new nint(-1))
        {
            return map;
        }

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = Marshal.SizeOf<PROCESSENTRY32>() };

            if (!Process32FirstW(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                map[entry.th32ProcessID] = entry.th32ParentProcessID;
            }
            while (Process32NextW(snapshot, ref entry));

            return map;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public int dwSize;
        public int cntUsage;
        public int th32ProcessID;
        public nint th32DefaultHeapID;
        public int th32ModuleID;
        public int cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public int dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
