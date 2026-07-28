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

    private Mutex? _instanceMutex;

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
            MessageBox.Show(
                "Quiesce is already running.\n\nLook for it in the taskbar or notification area.",
                "Quiesce",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
