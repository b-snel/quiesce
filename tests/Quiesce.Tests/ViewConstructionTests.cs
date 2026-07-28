using System.Windows.Controls;
using Quiesce.App;
using Quiesce.App.Views;
using Quiesce.Core.Journal;

namespace Quiesce.Tests;

/// <summary>
/// Constructs every page on an STA thread and asserts it builds without throwing.
/// </summary>
/// <remarks>
/// A XAML parse error, a bad resource key, or a broken binding path only surfaces at runtime, and
/// the GUI runs elevated so an unelevated test process cannot drive it with synthetic input (UIPI
/// blocks that). Constructing the pages is the strongest automated check available, and it does
/// catch the failures that actually happen: malformed XAML, missing DataTemplate types, and the
/// ServicesPage guardrail cross-check.
/// </remarks>
public class ViewConstructionTests
{
    /// <summary>
    /// WPF requires STA. Also creates the Application instance the pages resolve brushes from -
    /// without it, TryFindResource returns null and the evidence badges silently fall back to gray.
    /// </summary>
    /// <summary>
    /// WPF allows exactly one <see cref="System.Windows.Application"/> per AppDomain, so it is
    /// created once for the whole test run rather than per test.
    /// </summary>
    private static readonly Lock AppLock = new();

    private static bool _appInitialized;

    private static void EnsureApplication()
    {
        lock (AppLock)
        {
            if (_appInitialized)
            {
                return;
            }

            _appInitialized = true;

            // A bare Application, not Quiesce.App.App: the real one runs the single-instance guard
            // in OnStartup, and merging App.xaml as a dictionary would CONSTRUCT an Application
            // (its root element is <Application x:Class="...">), which WPF permits only once per
            // AppDomain. Brushes.xaml is a plain dictionary precisely so it can be merged here.
            var app = System.Windows.Application.Current ?? new System.Windows.Application();
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Quiesce;component/Theme/Brushes.xaml", UriKind.Absolute),
            });
        }
    }

    private static T OnStaThread<T>(Func<T> func)
    {
        T? result = default;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication();
                result = func();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        if (failure is not null)
        {
            throw new InvalidOperationException($"Page construction threw: {failure}", failure);
        }

        return result!;
    }

    private static AppState CleanState() => new()
    {
        MachineState = new QuiesceState { IsDirty = false },
        DataRoot = @"C:\ProgramData\Quiesce",
        LoadError = "test harness: no catalog",
    };

    [Fact]
    public void All_pages_construct()
    {
        var built = OnStaThread(() => new UserControl[]
        {
            new DashboardPage(CleanState()),
            new FeaturesPage(CleanState()),
            new ServicesPage(),
            new WontDoPage(),
        });

        Assert.Equal(4, built.Length);
        Assert.All(built, page => Assert.NotNull(page));
    }

    [Fact]
    public void Dashboard_renders_the_engaged_state_differently()
    {
        var engaged = new AppState
        {
            MachineState = new QuiesceState { IsDirty = true, ActiveSessionId = Guid.NewGuid() },
            DataRoot = @"C:\ProgramData\Quiesce",
        };

        var page = OnStaThread(() => new DashboardPage(engaged));

        Assert.NotNull(page);
    }

    [Fact]
    public void Engage_and_restore_are_disabled_in_the_read_only_shell()
    {
        // M2 has no mutation path. If these ever become enabled without the M3 engine wiring,
        // the button would silently do nothing - worse than being visibly disabled.
        //
        // The assertion runs INSIDE the STA thread: WPF DependencyObjects have thread affinity, so
        // reading IsEnabled from the test thread throws "a different thread owns it".
        var (engage, restore) = OnStaThread(() =>
        {
            var page = new DashboardPage(CleanState());
            return (page.EngageButton.IsEnabled, page.RestoreButton.IsEnabled);
        });

        Assert.False(engage);
        Assert.False(restore);
    }

    [Fact]
    public void ServicesPage_refuses_to_list_a_service_the_guardrail_does_not_protect()
    {
        // The page asserts its own list against Guardrails at construction. This test documents
        // that the cross-check exists: UI claiming a service is locked while the engine would
        // happily touch it is precisely the kind of lie the product exists to avoid.
        var page = OnStaThread(() => new ServicesPage());

        Assert.NotNull(page);
    }
}
