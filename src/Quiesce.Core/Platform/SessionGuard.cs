using System.Runtime.InteropServices;

namespace Quiesce.Core.Platform;

/// <summary>
/// Detects whether Quiesce is running inside a remote session.
/// </summary>
/// <remarks>
/// A tool that stops network or Remote Desktop services while the operator is connected over them
/// severs its own control channel. On the target machine there is no Ethernet — the only up NIC is
/// Wi-Fi — so recovery would require physical access to the box.
/// <para>
/// Read live on every call and never cached: a session can be disconnected and reconnected locally,
/// or a local session can be shadowed remotely, at any point during a long-running engage.
/// </para>
/// </remarks>
public static partial class SessionGuard
{
    private const int SM_REMOTESESSION = 0x1000;

    /// <summary>Overrides the live probe. Tests only.</summary>
    public static bool? OverrideForTests { get; set; }

    public static bool IsRemoteSession()
    {
        if (OverrideForTests is { } forced)
        {
            return forced;
        }

        return GetSystemMetrics(SM_REMOTESESSION) != 0;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);
}
