using System.Runtime.InteropServices;
using Quiesce.Core.Catalog;

namespace Quiesce.Core.Platform;

/// <summary>Production <see cref="IActivationBroadcaster"/> and <see cref="IActivationCapture"/>.</summary>
public sealed partial class Win32ActivationBroadcaster : IActivationBroadcaster, IActivationCapture
{
    private const uint SpiGetMouse = 0x0003;
    private const uint SpiSetMouse = 0x0004;

    private const uint SpifUpdateIniFile = 0x0001;
    private const uint SpifSendChange = 0x0002;

    public ActivationState? Capture(ActivationKind kind)
    {
        switch (kind)
        {
            case ActivationKind.SpiSetMouse:
            {
                // SPI_GETMOUSE fills three ints: threshold1, threshold2, acceleration. Capturing
                // them is what makes the mouse-accel revert real rather than cosmetic.
                var values = new int[3];
                var handle = GCHandle.Alloc(values, GCHandleType.Pinned);
                try
                {
                    if (!SystemParametersInfoW(SpiGetMouse, 0, handle.AddrOfPinnedObject(), 0))
                    {
                        throw new InvalidOperationException(
                            $"SPI_GETMOUSE failed (Win32 error {Marshal.GetLastWin32Error()}).");
                    }
                }
                finally
                {
                    handle.Free();
                }

                return new ActivationState { Kind = kind, MouseParams = values };
            }

            // ShChangeNotify and WM_SETTINGCHANGE are pure notifications - they carry no state of
            // their own, so re-broadcasting on revert is the whole inverse.
            case ActivationKind.ShChangeNotify:
            case ActivationKind.WmSettingChange:
            case ActivationKind.None:
                return null;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown activation kind.");
        }
    }

    public void Restore(ActivationState state)
    {
        switch (state.Kind)
        {
            case ActivationKind.SpiSetMouse:
            {
                var values = state.MouseParams?.ToArray()
                    ?? throw new InvalidOperationException("SpiSetMouse activation state has no captured parameters.");

                if (values.Length != 3)
                {
                    throw new InvalidOperationException(
                        $"SpiSetMouse expects 3 captured parameters, found {values.Length}.");
                }

                ApplyMouseParams(values);
                break;
            }

            default:
                Broadcast(state.Kind);
                break;
        }
    }

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
                // Apply what the registry now holds. Mouse acceleration is off exactly when all
                // three parameters are zero, so the "lean" broadcast is unambiguous; the revert
                // path uses Restore() with the captured array instead of this.
                ApplyMouseParams([0, 0, 0]);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown activation kind.");
        }
    }

    private static void ApplyMouseParams(int[] values)
    {
        var handle = GCHandle.Alloc(values, GCHandleType.Pinned);
        try
        {
            // SPIF_UPDATEINIFILE persists to HKCU\Control Panel\Mouse; SPIF_SENDCHANGE notifies
            // running apps. Both are required for the change to be real and durable.
            if (!SystemParametersInfoW(SpiSetMouse, 0, handle.AddrOfPinnedObject(), SpifUpdateIniFile | SpifSendChange))
            {
                throw new InvalidOperationException(
                    $"SPI_SETMOUSE failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            handle.Free();
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, nint pvParam, uint fWinIni);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeoutW(
        nint hWnd, uint msg, nuint wParam, string? lParam, uint fuFlags, uint uTimeout, out nuint result);
}
