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
