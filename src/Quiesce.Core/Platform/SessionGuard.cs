using System.Runtime.InteropServices;

namespace Quiesce.Core.Platform;

/// <summary>
/// Detects whether <em>any</em> session on this machine is remote.
/// </summary>
/// <remarks>
/// A tool that stops network or Remote Desktop services while an operator is connected over them
/// severs its own control channel. On the target machine there is no Ethernet — the only up NIC is
/// Wi-Fi — so recovery would require physical access to the box.
/// <para>
/// <b>Deliberately not <c>GetSystemMetrics(SM_REMOTESESSION)</c>.</b> That reports only on the
/// <em>calling process's own</em> session. Quiesce needs SERVICE_CHANGE_CONFIG and SERVICE_STOP, and
/// the natural elevation architectures — an elevated helper service, or a scheduled task set to run
/// whether the user is logged on or not — put the mutating code in session 0, where
/// <c>SM_REMOTESESSION</c> returns FALSE while the operator sits in session 1 over RDP. The guard
/// would then cheerfully unlock the entire network group. Enumerating every session closes that.
/// </para>
/// <para>
/// Read live on every call and never cached: a session can be connected, disconnected or shadowed
/// at any point during a long-running engage.
/// </para>
/// </remarks>
public static partial class SessionGuard
{
    private const int WTSClientProtocolType = 16;

    /// <summary>Protocol type 0 is the physical console; anything else is a remote transport.</summary>
    private const ushort ProtocolConsole = 0;

    private enum WtsConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
    }

    /// <summary>
    /// Test seam. Internal rather than public, and asserted against in production paths: a public
    /// mutable static that switches off a safety check is not a seam, it is a back door — any
    /// in-process code, including a DI container populating public statics, could disable the
    /// remote-session guard for the lifetime of the process.
    /// </summary>
    internal static bool? OverrideForTests { get; set; }

    public static bool IsRemoteSession()
    {
        if (OverrideForTests is { } forced)
        {
            return forced;
        }

        try
        {
            return AnySessionIsRemote();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Fail closed. If the session state cannot be determined, assume remote and keep the
            // network group locked: refusing a tweak is recoverable, severing the only connection
            // to the machine is not.
            return true;
        }
    }

    private static bool AnySessionIsRemote()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count) || count == 0)
        {
            return true; // fail closed, as above
        }

        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFOW>();

            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFOW>(buffer + (i * size));

                // Session 0 is the non-interactive services session; it is never a user's console
                // and never carries a remote client.
                if (info.SessionId == 0)
                {
                    continue;
                }

                var state = (WtsConnectState)info.State;
                if (state is not (WtsConnectState.Active or WtsConnectState.Connected
                    or WtsConnectState.Shadow or WtsConnectState.Disconnected))
                {
                    continue;
                }

                if (IsRemoteProtocol(info.SessionId))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static bool IsRemoteProtocol(uint sessionId)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTSClientProtocolType, out var data, out var bytes)
            || bytes < sizeof(ushort))
        {
            return false;
        }

        try
        {
            return (ushort)Marshal.ReadInt16(data) != ProtocolConsole;
        }
        finally
        {
            WTSFreeMemory(data);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTS_SESSION_INFOW
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public int State;
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSEnumerateSessionsW(nint server, int reserved, int version, out nint sessionInfo, out int count);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(nint server, uint sessionId, int infoClass, out nint buffer, out int bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
