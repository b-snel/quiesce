using System.Runtime.InteropServices;
using Quiesce.Core.Catalog;

namespace Quiesce.Core.Platform;

/// <summary>Production <see cref="IActivationBroadcaster"/>.</summary>
public sealed partial class Win32ActivationBroadcaster : IActivationBroadcaster
{
    public void Broadcast(ActivationKind kind)
    {
        switch (kind)
        {
            case ActivationKind.None:
                break;

            case ActivationKind.ShChangeNotify:
                // SHCNE_ASSOCCHANGED, SHCNF_IDLIST: the shell's "re-read your world" notification.
                SHChangeNotify(0x08000000, 0x0000, 0, 0);
                break;

            case ActivationKind.WmSettingChange:
                // Broadcast with a timeout: SendMessage without one blocks forever on any hung
                // top-level window, which for a system-mutating tool means "restore hangs".
                _ = SendMessageTimeoutW(
                    hWnd: 0xFFFF, // HWND_BROADCAST
                    msg: 0x001A,  // WM_SETTINGCHANGE
                    wParam: 0,
                    lParam: null,
                    fuFlags: 0x0002, // SMTO_ABORTIFHUNG
                    uTimeout: 2000,
                    out _);
                break;

            case ActivationKind.SpiSetMouse:
                // Arrives with the mouse-acceleration entry in M3; it needs the SPI parameter set
                // captured alongside, which the M3 journal records will carry.
                throw new NotSupportedException("SpiSetMouse activation lands in M3.");

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown activation kind.");
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeoutW(
        nint hWnd, uint msg, nuint wParam, string? lParam, uint fuFlags, uint uTimeout, out nuint result);
}
