using System.IO;
using System.Windows;

namespace Quiesce.App;

public partial class App : Application
{
    /// <summary>
    /// Single-instance guard.
    /// </summary>
    /// <remarks>
    /// <c>Global\</c>, not <c>Local\</c>: the boot/logon recovery task runs in session 0 and a
    /// session-local mutex would be invisible to it. Two Quiesce processes mutating at once is the
    /// scenario where one instance captures the other's applied values as if they were the user's
    /// original settings — the journal lock in Core is the hard backstop, but refusing a second
    /// window is the version the user can understand.
    /// </remarks>
    private const string SingleInstanceMutexName = @"Global\Quiesce.App.SingleInstance";

    /// <summary>
    /// Set by a second instance to ask the first to show its window.
    /// </summary>
    /// <remarks>
    /// <c>Global\</c> for the same reason the mutex is: the two processes may not share a session. Squattable
    /// by any process, and that is accepted rather than fixed — the only thing setting it achieves is that a
    /// window appears.
    /// </remarks>
    private const string ShowWindowEventName = @"Global\Quiesce.App.ShowWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindow;
    private RegisteredWaitHandle? _showWindowRegistration;
    private TrayIcon? _tray;

    /// <summary>
    /// True from just before a mutation is proposed to the user until it has finished.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="Platform.MutatingLock"/>, which is the cross-PROCESS guard. This is the
    /// in-process one, and it exists for a window the cross-process lock cannot close: it is set BEFORE
    /// the preflight dialog is raised, not before the engine is called. Between those two moments the
    /// app is sitting inside <c>ShowDialog</c>, which spins a nested dispatcher loop — so timers tick,
    /// messages pump, and anything that can reach a click handler still can.
    /// </para>
    /// <para>
    /// A WPF modal disables only its OWNER window. The tray icon lives on its own message-only HWND, so
    /// with a preflight open and no flag, a tray menu item is fully clickable. What it would reach is
    /// <see cref="MainWindow.InvalidatePages"/>, which evicts cached pages and replaces
    /// <c>PageHost.Content</c> — including the <c>DashboardPage</c> whose <c>async void</c> handler is
    /// suspended at <c>ShowDialog</c>. That handler resumes into a control no longer in the visual tree,
    /// closes the user's browser, and reports into a banner nobody can see, while the page that replaced
    /// it says the machine is clean.
    /// </para>
    /// <para>
    /// A plain static bool, not a lock: it is only ever read and written on the UI thread, and anything
    /// richer would imply it guards more than it does.
    /// </para>
    /// </remarks>
    internal static bool Mutating { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);

        bool acquired;
        try
        {
            acquired = _instanceMutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died without releasing. We now hold it.
            acquired = true;
        }

        if (!acquired)
        {
            // Signal the running instance to surface its window, then leave silently.
            //
            // This replaces a MessageBox that said "Look for it in the taskbar or notification area", which
            // stops being true the moment the window can hide: on Windows 11 a new tray icon defaults into
            // the overflow, so the sentence pointed at something the user could not see. It was also reached
            // only AFTER a UAC prompt - consent given, then a dead end.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowWindowEventName);
                show.Set();
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
            {
                // The other instance is starting up or shutting down and has not published the event. Falling
                // back to the old message is right here: there IS another instance, and the user has no other
                // way to be told why this one vanished.
                MessageBox.Show(
                    "Quiesce is already running.\n\nLook for it in the notification area.",
                    "Quiesce",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        // Published only by the instance that won the mutex, so a second one can find it.
        //
        // The name is predictable and any process can set it. Accepted rather than fixed: the only
        // consequence of a stranger setting it is that a window appears. No state changes, nothing mutates,
        // and a guard here would be protecting against an outcome that is not harmful.
        _showWindow = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowWindowEventName);
        _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showWindow,
            // Marshalled onto the dispatcher: this callback arrives on a thread-pool thread and every
            // window operation it leads to has thread affinity.
            (_, _) => Dispatcher.BeginInvoke(() => SurfaceWindow()),
            state: null,
            timeout: Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);

        base.OnStartup(e);

        _tray = new TrayIcon(
            onOpen: () => SurfaceWindow(),
            onCheckSync: () => SurfaceWindow(navigateTo: "Dashboard", recheck: true),
            onSettings: () => SurfaceWindow(navigateTo: "Settings"),
            onExit: () =>
            {
                // Told first, so the window's close handler stops intercepting. Without this, Shutdown closes
                // the window, close-to-tray cancels it, and the only menu that offers to quit cannot.
                (MainWindow as MainWindow)?.PrepareToExit();
                Shutdown();
            });

        RenderTray();
    }

    /// <summary>Re-renders the tray from freshly-read state. Cheap enough to call after any mutation.</summary>
    internal void RenderTray()
    {
        try
        {
            _tray?.Render(AppState.Load());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A tray that cannot describe the machine is not a reason to take the app down. The window is
            // still the authoritative surface and reports the same failure with more room.
        }
    }

    /// <summary>Brings the main window up, optionally on a page, optionally re-checking drift.</summary>
    private void SurfaceWindow(string? navigateTo = null, bool recheck = false)
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        window.ShowFromTray(navigateTo, recheck);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The tray FIRST. An undisposed TaskbarIcon leaves a ghost icon in the notification area until the
        // shell happens to notice the owning window is gone, which can be minutes.
        _tray?.Dispose();

        _showWindowRegistration?.Unregister(waitObject: null);
        _showWindow?.Dispose();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
