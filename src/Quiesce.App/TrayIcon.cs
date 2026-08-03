using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;

namespace Quiesce.App;

/// <summary>
/// The notification-area icon and its menu. Owned by <see cref="App"/>, never by a window.
/// </summary>
/// <remarks>
/// <para>
/// Created in <c>OnStartup</c> and disposed in <c>OnExit</c>, which are the only lifetime hooks the app has.
/// NOT declared in <c>MainWindow.xaml</c>: WPF would never dispose it, and the whole point is that it
/// outlives a closed window. An undisposed <c>TaskbarIcon</c> leaves a ghost in the notification area until
/// the shell notices the owning HWND is gone.
/// </para>
/// <para>
/// It hosts itself on a message-only window of the library's own, which is why Mica and
/// <c>ExtendsContentIntoTitleBar</c> on the main window are irrelevant to it — and also why a WPF modal,
/// which disables only its owner window, does not disable this. That is exactly the hole
/// <see cref="App.Mutating"/> exists to close.
/// </para>
/// <para>
/// Explorer restarting needs no code here: H.NotifyIcon 2.4.1 already handles <c>TaskbarCreated</c> and
/// re-registers itself.
/// </para>
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _openItem;
    private readonly MenuItem _syncItem;
    private readonly MenuItem _settingsItem;
    private readonly MenuItem _exitItem;
    private readonly MenuItem _header;

    private System.Drawing.Icon? _plainIcon;
    private System.Drawing.Icon? _attentionIcon;

    /// <param name="onOpen">Show the window. Never mutates.</param>
    /// <param name="onCheckSync">
    /// Show the window, re-check, and raise the resync preflight there. NOT a resync.
    /// </param>
    /// <param name="onSettings">Show the window on the Settings page.</param>
    /// <param name="onExit">Shut the app down.</param>
    public TrayIcon(Action onOpen, Action onCheckSync, Action onSettings, Action onExit)
    {
        ArgumentNullException.ThrowIfNull(onOpen);
        ArgumentNullException.ThrowIfNull(onCheckSync);
        ArgumentNullException.ThrowIfNull(onSettings);
        ArgumentNullException.ThrowIfNull(onExit);

        _header = new MenuItem { IsEnabled = false };
        _openItem = new MenuItem { Header = "Open Quiesce", FontWeight = FontWeights.SemiBold };
        _syncItem = new MenuItem { Header = "Check sync…" };
        _settingsItem = new MenuItem { Header = "Settings…" };
        _exitItem = new MenuItem { Header = "Exit Quiesce" };

        _openItem.Click += (_, _) => onOpen();
        _syncItem.Click += (_, _) => onCheckSync();
        _settingsItem.Click += (_, _) => onSettings();
        _exitItem.Click += (_, _) => onExit();

        var menu = new ContextMenu();
        menu.Items.Add(_header);
        menu.Items.Add(new Separator());
        menu.Items.Add(_openItem);
        menu.Items.Add(_syncItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_exitItem);

        _icon = new TaskbarIcon
        {
            ContextMenu = menu,
            ToolTipText = "Quiesce",
            NoLeftClickDelay = true,
        };

        // Left click opens the window, which is what every other tray application does and therefore what
        // the muscle memory expects. Deliberately the non-mutating action.
        _icon.TrayLeftMouseUp += (_, _) => onOpen();

        _icon.ForceCreate();
    }

    /// <summary>Re-renders the icon, tooltip and menu from current state.</summary>
    /// <remarks>
    /// Everything it decides comes from <see cref="TrayPresentation"/>, which is pure and tested. This method
    /// only assigns.
    /// </remarks>
    public void Render(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var attention = TrayPresentation.NeedsAttention(state);

        // Icon, NOT IconSource, and for two independent reasons.
        //
        // IconSource cannot carry a runtime-composed image at all. Its setter resolves an ImageSource
        // through a switch that handles URI-backed sources only: a RenderTargetBitmap throws
        // NotImplementedException, and a BitmapFrame decoded from a MemoryStream throws
        // UriFormatException because the arm that accepts frames goes looking for the URI it was loaded
        // from. Both are measured, and both are pinned by TrayIconConversionTests.
        //
        // And even if it could, mixing the two properties would be a trap: IconSource's setter writes
        // through to Icon, so assigning Icon for one state and IconSource for the other means switching
        // back only works when the IconSource VALUE changes - and it does not, because the plain icon is
        // cached and the same instance is assigned every time. The stale attention icon would simply
        // stay. One property for both states removes the interaction rather than documenting it.
        _icon.Icon = attention
            ? _attentionIcon ??= BuildAttentionIcon()
            : _plainIcon ??= LoadPlainIcon();

        _icon.ToolTipText = TrayPresentation.Tooltip(state);
        _header.Header = TrayPresentation.Header(state);

        var items = TrayPresentation.MenuItems(state);
        _openItem.Header = items[0];
        _syncItem.Header = items[1];
        _settingsItem.Header = items[2];
        _exitItem.Header = items[3];

        if (TrayPresentation.SyncCheckDisabledReason(state) is { } reason)
        {
            _syncItem.IsEnabled = false;
            _syncItem.ToolTip = reason;
        }
        else
        {
            _syncItem.IsEnabled = true;
            _syncItem.ToolTip = "Re-check whether this machine still matches the session, in the window.";
        }

        // Both mutating paths live behind the window, but the window can be mid-mutation with a preflight
        // open - and a modal does not disable this menu, because the icon is not on that window.
        _syncItem.IsEnabled = _syncItem.IsEnabled && !App.Mutating;
    }

    /// <summary>
    /// The tray icon asset, addressed with the assembly-qualified pack URI.
    /// </summary>
    /// <remarks>
    /// <c>/Quiesce;component/…</c> rather than the shorter <c>/Assets/…</c>. The short form resolves against
    /// the ENTRY assembly, which is this one in production and the test host under <c>dotnet test</c> — so
    /// the short form works in the app and cannot be verified by a test, which is the worst of both. The
    /// qualified form names the assembly outright and resolves identically in both, so the test exercises the
    /// same string the app uses. <c>Theme/Brushes.xaml</c> is merged the same way in the test harness for
    /// exactly this reason.
    /// </remarks>
    private const string IconPackUri = "pack://application:,,,/Quiesce;component/Assets/quiesce.ico";

    /// <summary>The shipped asset as a WPF image, for drawing the composed variant on top of.</summary>
    private static ImageSource LoadPlainImageSource() =>
        new BitmapImage(new Uri(IconPackUri, UriKind.Absolute));

    /// <summary>The shipped asset, read straight out of the assembly as the .ico bytes it is.</summary>
    /// <remarks>
    /// <c>GetResourceStream</c> plus the <c>Icon(Stream)</c> constructor, rather than a
    /// <see cref="BitmapImage"/>: the file already IS a multi-size icon, and handing those bytes to Windows
    /// unaltered lets the shell pick the size it wants instead of scaling one raster we chose.
    /// </remarks>
    internal static System.Drawing.Icon LoadPlainIcon()
    {
        var resource = Application.GetResourceStream(new Uri(IconPackUri, UriKind.Absolute))
            ?? throw new InvalidOperationException(
                $"{IconPackUri} is not in the assembly. Quiesce.App.csproj must list Assets\\quiesce.ico as a " +
                "<Resource> and not only as <ApplicationIcon> - ApplicationIcon embeds it for Explorer to draw " +
                "and does not make it reachable by a pack URI.");

        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream);
    }

    /// <summary>Both icons the tray ever assigns, so a test can check every one of them.</summary>
    /// <remarks>
    /// A list rather than two members, because the bug this exists for was in exactly one of the two and the
    /// other worked — so a test naming them individually is a test someone can extend the icon set without.
    /// </remarks>
    internal static IReadOnlyList<(string Name, System.Drawing.Icon Icon)> AllIcons() =>
    [
        ("plain", LoadPlainIcon()),
        ("attention", BuildAttentionIcon()),
    ];

    /// <summary>
    /// The plain icon with a dot in the corner, composed once and frozen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drawn rather than shipped as a second asset so the two can never drift apart visually, and cached
    /// because it is composed on the UI thread. Orange, the drift colour, and the same one the banner uses.
    /// </para>
    /// <para>
    /// IT RETURNS A <c>System.Drawing.Icon</c> AND THAT IS NOT AN ARBITRARY CHOICE. This method used to return
    /// the <see cref="RenderTargetBitmap"/> for assignment to <c>IconSource</c>, and it crashed the entire
    /// application — not the icon, the application — with <c>NotImplementedException: ImageSource type:
    /// System.Windows.Media.Imaging.RenderTargetBitmap is not supported</c>. <c>IconSource</c> resolves through
    /// a switch over <see cref="ImageSource"/> subtypes that ultimately wants a URI to read bytes from, so a
    /// composed image cannot travel that way at all: a <see cref="RenderTargetBitmap"/> throws
    /// <c>NotImplementedException</c>, and encoding it to PNG and decoding it back to a
    /// <see cref="BitmapFrame"/> — which looked like the fix, and is not — throws <c>UriFormatException</c>
    /// instead, because the arm that accepts frames goes looking for the URI it was loaded from. The library's
    /// own documentation on <c>TaskbarIcon.Icon</c> says it outright: "Use this for dynamically generated
    /// System.Drawing.Icons."
    /// </para>
    /// <para>
    /// <c>System.Drawing</c> costs no new package: <c>System.Drawing.Common</c> ships inside the Windows
    /// Desktop framework that <c>UseWPF</c> already references, and H.NotifyIcon's own <c>Icon</c> property is
    /// typed on it.
    /// </para>
    /// <para>
    /// The failure mode is why this is worth the words. The exception surfaced on the first render where
    /// <see cref="TrayPresentation.NeedsAttention"/> was true — so the app started fine on a clean machine and
    /// died on an engaged one, which is the only machine it exists for. A WinExe has no console, so there was
    /// no window, no message and no clue; and the sign-in task reported it as launch result 0xE0434352, the
    /// CLR's unhandled-exception code, which reads like the task failing rather than the app crashing.
    /// </para>
    /// </remarks>
    private static System.Drawing.Icon BuildAttentionIcon()
    {
        const int size = 32;
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            // The WPF view of the same asset. Two loaders for one file is not duplication: composing needs an
            // ImageSource the drawing context can draw, and the tray needs a System.Drawing.Icon it can
            // display, and there is no single type that is both.
            context.DrawImage(LoadPlainImageSource(), new Rect(0, 0, size, size));

            var dot = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));
            dot.Freeze();

            // Bottom-right, with a dark ring so it reads against a light or dark taskbar.
            context.DrawEllipse(
                new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00)),
                pen: null,
                new Point(size - 8, size - 8),
                8,
                8);
            context.DrawEllipse(dot, pen: null, new Point(size - 8, size - 8), 6, 6);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        return ToIcon(bitmap);
    }

    /// <summary>
    /// Turns a WPF bitmap into a GDI icon, by way of PNG because that is the only format both stacks agree on.
    /// </summary>
    /// <remarks>
    /// Separate and internal so a test can assert the thing that actually broke — that the tray is handed a
    /// real icon — without constructing a <c>TaskbarIcon</c>, which needs a message pump the test harness does
    /// not run and would leave a live icon in the test host's notification area.
    /// <para>
    /// <c>Icon.FromHandle</c> does NOT take ownership of the handle, so the HICON has to be released by hand;
    /// see <see cref="Dispose"/>. That is the price of this route and it is one handle for the life of the
    /// process, because the result is cached. <c>new Icon(Stream)</c> would own its handle and need no cleanup,
    /// but it requires actual .ico container bytes, and hand-rolling an ICONDIR to avoid one
    /// <c>DestroyIcon</c> call is a worse trade than making the ownership explicit here.
    /// </para>
    /// </remarks>
    internal static System.Drawing.Icon ToIcon(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;

        using var gdi = new System.Drawing.Bitmap(stream);
        return System.Drawing.Icon.FromHandle(gdi.GetHicon());
    }

    /// <remarks>
    /// Releases the composed icon's handle as well as the tray icon itself. <c>Icon.FromHandle</c> wraps a
    /// handle it does not own, so disposing the <c>Icon</c> alone would leak the HICON; and the plain icon
    /// came from <c>new Icon(Stream)</c>, which does own its handle, so it only needs disposing. Two icons,
    /// two different ownership rules, which is exactly the kind of thing that gets "tidied" into a leak.
    /// </remarks>
    public void Dispose()
    {
        _icon.Dispose();

        _plainIcon?.Dispose();

        if (_attentionIcon is not null)
        {
            var handle = _attentionIcon.Handle;
            _attentionIcon.Dispose();
            DestroyIcon(handle);
        }
    }

    /// <remarks>
    /// <para>
    /// A hand-written import rather than CsWin32. Despite the package reference there is no
    /// <c>NativeMethods.txt</c> anywhere in this tree and every interop declaration in <c>src</c> is written
    /// out like this, so following the house style keeps one convention instead of introducing a second.
    /// </para>
    /// <para>
    /// <c>DllImport</c> and not the newer <c>LibraryImport</c>, which is a deliberate step backwards:
    /// <c>LibraryImport</c>'s source generator emits unmanaged pointers and therefore requires
    /// <c>AllowUnsafeBlocks</c> on the whole project. Turning unsafe code on across an application that runs
    /// as Administrator, to save one marshalling stub for a single BOOL-returning call, is a bad trade.
    /// </para>
    /// </remarks>
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);
}
