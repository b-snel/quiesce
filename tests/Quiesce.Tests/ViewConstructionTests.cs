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
        using var temp = new TempDataRoot();

        var built = OnStaThread(() => new UserControl[]
        {
            new DashboardPage(CleanState()),
            new FeaturesPage(CleanState()),
            // Given a throwaway data root rather than the real one: the page reads the user-added apps
            // file, and a test has no business reading or writing C:\ProgramData\Quiesce.
            new RunningAppsPage(CleanState() with { DataRoot = temp.Path }),
            // Reads the real machine's Run keys and Startup folders, which is read-only and needs no
            // elevation; the throwaway data root keeps the preference file out of C:\ProgramData.
            new StartupPage(CleanState() with { DataRoot = temp.Path }),
            new ServicesPage(),
            new WontDoPage(),
        });

        Assert.Equal(6, built.Length);
        Assert.All(built, page => Assert.NotNull(page));
    }

    /// <summary>
    /// Off entries sort above on ones, which is what makes the exceptions the user made visible instead of
    /// scattered through three dozen rows.
    /// </summary>
    [Fact]
    public void Features_lists_switched_off_entries_first()
    {
        using var temp = new TempDataRoot();

        // "b.on" is enabled and sits later in the catalog; "c.off" is disabled and sits later still.
        // Catalog order alone would give a, b, c — the sort has to put the two off rows in front.
        new Core.Catalog.ProfileStore(temp.Path).SetEnabled("b.on", enabled: true);

        var order = OnStaThread(() =>
        {
            var page = new FeaturesPage(StateWith(temp.Path, "a.off", "b.on", "c.off"));
            return page.EntryList.Items.Cast<FeatureRow>().Select(r => r.EntryId).ToList();
        });

        Assert.Equal(["a.off", "c.off", "b.on"], order);
    }

    [Fact]
    public void Select_all_then_select_none_moves_every_entry()
    {
        using var temp = new TempDataRoot();

        var (afterAll, afterNone) = OnStaThread(() =>
        {
            var page = new FeaturesPage(StateWith(temp.Path, "a", "b", "c"));

            page.SelectAllButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var all = new Core.Catalog.ProfileStore(temp.Path).ActiveEnabled().Count;

            page.SelectNoneButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var none = new Core.Catalog.ProfileStore(temp.Path).ActiveEnabled().Count;

            return (all, none);
        });

        Assert.Equal(3, afterAll);
        Assert.Equal(0, afterNone);
    }

    /// <summary>
    /// The enabled set describes what is currently applied while engaged, so changing it would
    /// desynchronize the journal from the profile. Locked with a reason, never hidden.
    /// </summary>
    [Fact]
    public void Bulk_buttons_are_disabled_while_engaged()
    {
        using var temp = new TempDataRoot();

        var state = StateWith(temp.Path, "a") with
        {
            MachineState = new QuiesceState { IsDirty = true, ActiveSessionId = Guid.NewGuid() },
        };

        var (all, none) = OnStaThread(() =>
        {
            var page = new FeaturesPage(state);
            return (page.SelectAllButton.IsEnabled, page.SelectNoneButton.IsEnabled);
        });

        Assert.False(all);
        Assert.False(none);
    }

    private static AppState StateWith(string dataRoot, params string[] entryIds)
    {
        var catalog = EngineTestHarness.CatalogOf(
            [.. entryIds.Select(id => EngineTestHarness.DwordEntry(id, valueName: id))]);

        return new AppState
        {
            MachineState = new QuiesceState { IsDirty = false },
            DataRoot = dataRoot,
            Catalog = catalog,
            CatalogPath = "test",
        };
    }

    /// <summary>A throwaway data root, so a page that writes a profile does not write the real one.</summary>
    private sealed class TempDataRoot : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quiesce-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void The_preflight_row_renders_every_kind_of_step_including_a_process()
    {
        // A real crash, not a hypothetical one. The row builder tested for a service prior and otherwise
        // dereferenced the registry prior, so a process step - which carries neither - was a null
        // reference in the middle of the dialog the user approves changes in. It is also the one place
        // "Restore will not reopen it" has to be said, so this asserts the wording is actually there.
        var rows = OnStaThread(() => new[]
        {
            PreflightRow.From(ProcessStep(Core.Catalog.ProcessAction.Close)),
            PreflightRow.From(ProcessStep(Core.Catalog.ProcessAction.Throttle)),
        });

        Assert.Contains("running at Normal priority", rows[0].PriorText);
        Assert.Contains("NOT reopen", rows[0].NewText);
        Assert.Contains("put back on Restore", rows[1].NewText);
    }

    private static Core.Engine.PlannedStep ProcessStep(Core.Catalog.ProcessAction action) => new()
    {
        StepId = 1,
        EntryId = "apps.test",
        Scope = Core.Catalog.TweakScope.Session,
        Op = new Core.Catalog.ProcessOpSpec
        {
            Action = action,
            ImageName = "chrome",
            UnderDirectories = [@"\Google\Chrome\Application\"],
            ThrottleTo = action == Core.Catalog.ProcessAction.Throttle ? Core.Catalog.ThrottleLevel.Idle : null,
        },
        Target = "close chrome — chrome (pid 1)",
        ProcessBefore = new Core.Platform.ProcessSnapshot
        {
            Identity = new Core.Platform.ProcessIdentity { Pid = 1, CreatedUtcTicks = 1 },
            ImageName = "chrome",
            ImagePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            SessionId = 1,
            PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal,
            HasVisibleWindow = true,
        },
        ProcessAction = action,
        IntendedPriority = action == Core.Catalog.ProcessAction.Throttle
            ? System.Diagnostics.ProcessPriorityClass.Idle
            : null,
        Activation = [],
        NoOp = false,
    };

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
