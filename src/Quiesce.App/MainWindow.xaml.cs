using System.Windows.Controls;
using Quiesce.App.Views;

namespace Quiesce.App;

public partial class MainWindow
{
    private readonly Dictionary<string, Func<AppState, UserControl>> _factories;
    private readonly Dictionary<string, UserControl> _cache = [];

    private AppState _state;

    public MainWindow()
    {
        InitializeComponent();

        _state = AppState.Load();

        _factories = new Dictionary<string, Func<AppState, UserControl>>
        {
            ["Dashboard"] = s =>
            {
                var page = new DashboardPage(s);

                // Engage/Restore change live machine state, so every other page's rendering of
                // that state is stale the moment it completes. Drop the cache rather than try to
                // push updates into pages individually.
                page.StateChanged += (_, _) => InvalidatePages(keep: "Dashboard");
                return page;
            },
            ["Features"] = s => new FeaturesPage(s),
            ["Running apps"] = s =>
            {
                var page = new RunningAppsPage(s);

                // Adding or removing an entry changes the catalog, so every plan computed from it is
                // stale. This page is kept across the rebuild for the same reason the Dashboard is:
                // discarding the control that raised the event would tear it down mid-callback.
                page.CatalogChanged += (_, _) => InvalidatePages(keep: "Running apps");
                return page;
            },
            ["Services"] = _ => new ServicesPage(),
            ["What Quiesce won't do"] = _ => new WontDoPage(),
        };

        foreach (var name in _factories.Keys)
        {
            Nav.Items.Add(name);
        }

        Nav.SelectedIndex = 0;
        RenderRebootBanner();
    }

    /// <summary>
    /// Shows the outstanding-restart warning, naming the entries waiting on one.
    /// </summary>
    /// <remarks>
    /// Both directions matter. A change that needs a restart and does not say so is a tweak the user
    /// believes is active and is not — the placebo failure. Equally, this must not appear for entries
    /// that were already lean, or the app asks for a reboot nothing is waiting on and the warning stops
    /// meaning anything.
    /// </remarks>
    private void RenderRebootBanner()
    {
        if (!_state.RebootPending)
        {
            RebootBanner.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        RebootBanner.Visibility = System.Windows.Visibility.Visible;

        var count = _state.RebootPendingTitles.Count;
        RebootHeadline.Text = $"Restart Windows to finish {(count == 1 ? "1 change" : $"{count} changes")}";

        RebootDetail.Text =
            "These are written to the registry but Windows has not picked them up yet, so they are not " +
            "affecting your machine:\n  • " +
            string.Join("\n  • ", _state.RebootPendingTitles) +
            "\n\nRestore works normally in the meantime. This warning stays until the machine restarts — " +
            "reverting a change like this needs a restart just as much as applying it did.";
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not string name || !_factories.TryGetValue(name, out var factory))
        {
            return;
        }

        if (!_cache.TryGetValue(name, out var page))
        {
            page = factory(_state);
            _cache[name] = page;
        }

        PageHost.Content = page;
    }

    /// <summary>
    /// Rebuilds pages from freshly-read machine state.
    /// </summary>
    /// <param name="keep">
    /// The page that raised the change. Kept rather than rebuilt: it re-renders itself, and discarding it
    /// mid-callback would tear down the control still inside its own event handler.
    /// </param>
    private void InvalidatePages(string keep)
    {
        _state = AppState.Load();
        RenderRebootBanner();

        foreach (var key in _cache.Keys.Where(k => k != keep).ToList())
        {
            _cache.Remove(key);
        }

        if (Nav.SelectedItem is string current && current != keep)
        {
            PageHost.Content = _cache[current] = _factories[current](_state);
        }
    }
}
