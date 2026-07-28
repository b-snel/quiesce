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

    private ImageSource? _plainIcon;
    private ImageSource? _attentionIcon;

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

        _icon.IconSource = attention
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

    private static ImageSource LoadPlainIcon() => new BitmapImage(new Uri(IconPackUri, UriKind.Absolute));

    /// <summary>
    /// The plain icon with a dot in the corner, composed once and frozen.
    /// </summary>
    /// <remarks>
    /// Drawn rather than shipped as a second asset so the two can never drift apart visually, and cached
    /// because it is composed on the UI thread. Orange, the drift colour, and the same one the banner uses.
    /// </remarks>
    private static ImageSource BuildAttentionIcon()
    {
        const int size = 32;
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawImage(LoadPlainIcon(), new Rect(0, 0, size, size));

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

        return bitmap;
    }

    public void Dispose() => _icon.Dispose();
}
