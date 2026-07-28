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
            ["Startup"] = s =>
            {
                var page = new StartupPage(s);
                page.CatalogChanged += (_, _) => InvalidatePages(keep: "Startup");
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
        RenderDriftBanner();
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

    /// <summary>
    /// Shows the out-of-sync warning, naming what changed and what Quiesce will and will not do about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three shapes, because there are three different facts and none of them may render as another. Items
    /// Quiesce will put back get an instruction and a save-your-work warning. Items it will not get named
    /// with the reason, so "Quiesce noticed and is deliberately leaving it alone" cannot be mistaken for
    /// "Quiesce did not notice". And an UNKNOWN report says it could not tell, which is not "in sync".
    /// </para>
    /// <para>
    /// Not dismissible, the same as the reboot banner and for the reason its comment gives: dismissing it
    /// would leave the machine in a state the app had stopped mentioning. It goes away when the machine
    /// matches again, or when the session ends.
    /// </para>
    /// </remarks>
    internal static (string Headline, string Detail)? DriftBannerText(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Drift is not { } drift)
        {
            return null;
        }

        if (drift.Unknown)
        {
            return ("Quiesce cannot tell whether this machine still matches",
                drift.UnknownReason
                + "\n\nRestore still works from the journal, and from the CLI if this window cannot.");
        }

        if (!drift.Any)
        {
            return null;
        }

        var at = drift.CheckedUtc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        var resyncable = drift.Resyncable;
        var reported = drift.ReportedOnly;

        // Headline names the actionable half when there is one, because that is what the user can do
        // something about. When there is not, it says so rather than implying an action exists.
        var headline = resyncable.Count > 0
            ? "Out of sync with what Quiesce applied"
            : "Changed since Engage, and Quiesce is leaving it alone";

        var detail = new System.Text.StringBuilder();

        if (resyncable.Count > 0)
        {
            detail.Append(
                resyncable.Count == 1
                    ? "1 thing Quiesce can put back:\n  • "
                    : $"{resyncable.Count} things Quiesce can put back:\n  • ");
            detail.Append(string.Join("\n  • ", resyncable.Select(i => i.Detail)));

            // The save-your-work warning belongs here as well as in the preflight: this is where the user
            // decides whether to press Resync at all, and a close is the one thing Restore does not undo.
            detail.Append(
                "\n\nResync asks them to close again. SAVE YOUR WORK FIRST — closing is the one thing " +
                "Restore does not undo, and Quiesce does not reopen anything.");
        }

        if (reported.Count > 0)
        {
            if (detail.Length > 0)
            {
                detail.Append("\n\n");
            }

            detail.Append(
                reported.Count == 1
                    ? "1 thing changed that Quiesce will NOT put back:\n  • "
                    : $"{reported.Count} things changed that Quiesce will NOT put back:\n  • ");
            detail.Append(string.Join(
                "\n  • ",
                reported.Select(i => $"{i.Target} — {i.Detail} {i.NotResyncableReason}")));
        }

        detail.Append($"\n\nchecked {at}");

        return (headline, detail.ToString());
    }

    private void RenderDriftBanner()
    {
        if (DriftBannerText(_state) is not { } text)
        {
            DriftBanner.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        DriftBanner.Visibility = System.Windows.Visibility.Visible;
        DriftHeadline.Text = text.Headline;
        DriftDetail.Text = text.Detail;
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
    internal void InvalidatePages(string keep)
    {
        // Refused outright while a mutation is in flight. This method's whole job is to throw pages away
        // and rebuild them, and a mutation is driven by an `async void` handler that lives ON one of
        // those pages - suspended at ShowDialog, or awaiting the engine. Evicting it mid-flight leaves an
        // orphaned handler that still finishes the job, still closes what it was going to close, and
        // reports into a banner detached from the visual tree, while the page that replaced it renders
        // the state as it was before any of that happened.
        //
        // Nothing is lost by refusing: every mutating path calls Refresh() on itself when it completes,
        // and that raises the event that brings us back here.
        if (App.Mutating)
        {
            return;
        }

        _state = AppState.Load();
        RenderRebootBanner();
        RenderDriftBanner();

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
