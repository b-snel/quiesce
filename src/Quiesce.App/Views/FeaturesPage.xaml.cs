using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Platform;

namespace Quiesce.App.Views;

public partial class FeaturesPage
{
    private readonly ProfileStore? _profiles;
    private readonly ObservableCollection<FeatureRow> _rows = [];

    public FeaturesPage(AppState state)
    {
        InitializeComponent();

        if (state.Catalog is null)
        {
            EntryList.ItemsSource = new[]
            {
                new FeatureRow
                {
                    EntryId = string.Empty,
                    Ordinal = 0,
                    Title = "Catalog unavailable",
                    EvidenceLabel = "—",
                    EvidenceBrush = Brushes.Gray,
                    ImpactLabel = "—",
                    TierLabel = "—",
                    RebootLabel = string.Empty,
                    NeedsReboot = false,
                    Breaks = state.LoadError ?? "No catalog was found next to the executable.",
                    StatusWhenOn = string.Empty,
                    StatusWhenOff = string.Empty,
                    CanToggle = false,
                    ToggleTipWhenOn = "No catalog is loaded.",
                    ToggleTipWhenOff = "No catalog is loaded.",
                },
            };

            SelectAllButton.IsEnabled = false;
            SelectNoneButton.IsEnabled = false;
            ResetButton.IsEnabled = false;
            return;
        }

        _profiles = new ProfileStore(state.DataRoot);
        var enabled = _profiles.ActiveEnabled();

        // While engaged, the enabled set describes what IS applied. Letting it change underneath a
        // live session would desynchronize the journal from the profile, so toggles lock until
        // Restore. Locked-with-a-reason, never hidden.
        var engaged = state.MachineState.IsDirty;

        // Status comes from the all-entries plan, not the profile-filtered one: a row that is switched off
        // has no steps in the plan Engage would run, and describing it needs the same live probe as an
        // enabled row. Falls back to the filtered plan so a page built without one still renders.
        var statusPlan = state.StatusPlan ?? state.Plan;

        var pendingReboot = state.MachineState.RebootPending
            ? state.MachineState.RebootPendingEntryIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        var ordinal = 0;
        foreach (var entry in state.Catalog.Entries)
        {
            _rows.Add(BuildRow(entry, ordinal++, enabled.Contains(entry.Id), engaged, statusPlan, pendingReboot));
        }

        // Off first, catalog order within each group. Live on IsEnabled, so a row the user switches off
        // travels to the top immediately rather than at the next page load — which is the whole point:
        // the exceptions you made are the ones you want to see.
        var view = new ListCollectionView(_rows);
        view.SortDescriptions.Add(new SortDescription(nameof(FeatureRow.IsEnabled), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(FeatureRow.Ordinal), ListSortDirection.Ascending));
        view.IsLiveSorting = true;
        view.LiveSortingProperties.Add(nameof(FeatureRow.IsEnabled));
        EntryList.ItemsSource = view;

        SelectAllButton.IsEnabled = !engaged;
        SelectNoneButton.IsEnabled = !engaged;
        ResetButton.IsEnabled = !engaged;

        UpdateCount();

        if (engaged)
        {
            ShowBulkNote(
                "This machine is engaged, so what is switched on is a record of what is currently applied. " +
                "Restore first to change it.");
        }
    }

    private static FeatureRow BuildRow(
        CatalogEntry entry,
        int ordinal,
        bool isEnabled,
        bool engaged,
        EngagePlan? statusPlan,
        IReadOnlySet<string> pendingReboot)
    {
        var steps = statusPlan?.Steps.Where(s => s.EntryId == entry.Id).ToList() ?? [];
        var applied = steps.Count > 0 && steps.All(s => s.NoOp);
        var partial = steps.Count > 0 && !applied && steps.Any(s => s.NoOp);

        // Refusal has to be tested BEFORE the fall-through, or an entry Windows blocks in the
        // kernel would read "will be applied on Engage" — a promise the tool cannot keep. It is
        // tested AFTER `applied`, because already-lean steps are never refused and saying
        // "blocked" about a write that will not happen is equally false.
        var refused = steps.Count > 0 && steps.All(s => s.RefusedReason is not null);
        var someRefused = steps.Any(s => s.RefusedReason is not null);
        var refusalReason = steps.FirstOrDefault(s => s.RefusedReason is not null)?.RefusedReason;

        // "Already at its lean value" is right for a registry or service row and wrong for a process
        // group, which elides because nothing it names is running. The step says which it is.
        var elision = steps.FirstOrDefault(s => s.NoOp && s.NoOpDetail is not null)?.NoOpDetail
            ?? "already at its lean value";

        var whenOn = applied ? $"on — {elision}"
            : refused ? "on — REFUSED: Windows will not permit this on this machine"
            : someRefused ? "on — PARTIALLY refused; the rest will be applied on Engage"
            : partial ? "on — PARTIALLY applied"
            : "on — will be applied on Engage";

        // The off wording carries the live probe too. "Off" alone leaves the user unable to tell a tweak
        // they are declining from one the machine is already in the state of.
        var whenOff = applied ? $"off — Engage will skip it; the machine is {elision} anyway"
            : refused ? "off — and Windows would refuse it on this machine in any case"
            : "off — not applied, and Engage will skip it";

        var awaitingReboot = pendingReboot.Contains(entry.Id);

        return new FeatureRow
        {
            EntryId = entry.Id,
            Ordinal = ordinal,
            Title = entry.Title,
            EvidenceLabel = entry.Evidence.ToString(),
            EvidenceBrush = EvidenceBrush(entry.Evidence),
            ImpactLabel = $"{entry.Impact} impact",
            TierLabel = entry.RequiresAdmin ? $"tier {entry.RiskTier} · admin" : $"tier {entry.RiskTier}",
            NeedsReboot = entry.RequiresReboot,
            // Two different facts sharing one chip: "will need a restart" and "is waiting on one right
            // now". The second is the one that explains why a tweak looks like it did nothing.
            RebootLabel = awaitingReboot ? "waiting on restart" : "needs restart",
            ClosesApplications = entry.Ops.OfType<ProcessOpSpec>().Any(o => o.Action == ProcessAction.Close),
            IsMachineWide = entry.RequiresAdmin,
            FullyRefused = refused,
            AlreadyLean = applied,
            // The reason, not just the verdict: "refused" without the why is the same dead end
            // as a tweak that quietly did nothing.
            Breaks = refusalReason is not null
                ? $"Refused: {refusalReason}\nBreaks: {entry.WhatItBreaks}"
                : $"Breaks: {entry.WhatItBreaks}",
            StatusWhenOn = whenOn,
            StatusWhenOff = whenOff,
            IsEnabled = isEnabled,
            CanToggle = !engaged,
            ToggleTipWhenOn = engaged ? EngagedTip : "On. Engage will apply this.",
            ToggleTipWhenOff = engaged ? EngagedTip : "Off. Engage will skip this.",
        };
    }

    private const string EngagedTip =
        "Restore first — changing what is enabled while engaged would desynchronize the journal.";

    private void OnToggled(object sender, RoutedEventArgs e)
    {
        if (_profiles is null || sender is not Wpf.Ui.Controls.ToggleSwitch toggle)
        {
            return;
        }

        if (toggle.DataContext is not FeatureRow row || string.IsNullOrEmpty(row.EntryId))
        {
            return;
        }

        var enabled = toggle.IsChecked == true;
        if (row.IsEnabled == enabled)
        {
            // Raised by the re-sort regenerating containers, not by a click. Persisting here would be
            // harmless but writing the profile on every scroll is not something to do by accident.
            return;
        }

        _profiles.SetEnabled(row.EntryId, enabled);

        // Deferred one dispatcher turn. Setting IsEnabled re-sorts the view, which moves this very row —
        // and the container holding the switch that is still in the middle of raising this event.
        Dispatcher.BeginInvoke(() =>
        {
            row.IsEnabled = enabled;
            UpdateCount();
        });
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => Bulk(enable: true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => Bulk(enable: false);

    /// <summary>
    /// Switches every entry on or off, then says what that did.
    /// </summary>
    /// <remarks>
    /// Nothing here touches the machine — a profile is a list of ids, and Engage's preflight is still the
    /// gate. What the note exists for is the gap between "I clicked one button" and "36 rows changed":
    /// select-all quietly enables the irreversible closes, the machine-wide service rows and the ones
    /// Windows will refuse, and a user who has to discover that by reading the preflight has been
    /// surprised by their own app.
    /// </remarks>
    private void Bulk(bool enable)
    {
        if (_profiles is null)
        {
            return;
        }

        var changing = _rows.Where(r => r.IsEnabled != enable && !string.IsNullOrEmpty(r.EntryId)).ToList();
        if (changing.Count == 0)
        {
            ShowBulkNote(enable ? "Everything is already on." : "Everything is already off.");
            return;
        }

        // The whole set, not the difference. "Select all" is a claim about what the profile IS, and stating
        // it that way is also what drops any id the catalog no longer ships.
        _profiles.SetEnabledExactly(enable
            ? _rows.Select(r => r.EntryId).Where(id => !string.IsNullOrEmpty(id))
            : []);

        foreach (var row in changing)
        {
            row.IsEnabled = enable;
        }

        UpdateCount();
        ShowBulkNote(enable ? DescribeEnabled(changing) : $"Switched off {changing.Count} entries. Engage will do nothing until you switch something on.");
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        if (_profiles is null)
        {
            return;
        }

        var defaults = ProfileStore.BuiltInDefault.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Intersected with what this catalog actually ships, so a default naming an entry that has since
        // been renamed cannot leave a dead id sitting in the profile.
        _profiles.SetEnabledExactly(_rows
            .Select(r => r.EntryId)
            .Where(id => !string.IsNullOrEmpty(id) && defaults.Contains(id)));

        foreach (var row in _rows)
        {
            row.IsEnabled = defaults.Contains(row.EntryId);
        }

        UpdateCount();
        ShowBulkNote(
            $"Back to the shipped default: {_rows.Count(r => r.IsEnabled)} on. That set is the small, " +
            "defensible one — GameDVR capture, mouse acceleration, Widgets, the consumer-features and " +
            "content-delivery rows, and closing browsers.");
    }

    /// <summary>
    /// Summarises a bulk enable in terms of the things a user would want to have been told.
    /// </summary>
    /// <remarks>
    /// Every count here is derived from the live plan and the catalog, not from a hand-maintained list of
    /// scary rows — a hard-coded hazard list is the thing that silently goes stale when the catalog grows.
    /// </remarks>
    private string DescribeEnabled(IReadOnlyList<FeatureRow> newlyOn)
    {
        var note = $"Switched on {newlyOn.Count} entries — {_rows.Count(r => r.IsEnabled)} of {_rows.Count} now on. ";

        var caveats = new List<string>();

        var irreversible = newlyOn.Count(r => r.ClosesApplications);
        var admin = newlyOn.Count(r => r.IsMachineWide);
        var reboot = newlyOn.Count(r => r.NeedsReboot);
        var refused = newlyOn.Count(r => r.FullyRefused);
        var alreadyLean = newlyOn.Count(r => r.AlreadyLean);

        if (admin > 0)
        {
            caveats.Add($"{admin} change the machine for every user, not just you");
        }

        if (irreversible > 0)
        {
            caveats.Add($"{irreversible} close applications, which Restore does not reopen");
        }

        if (reboot > 0)
        {
            caveats.Add($"{reboot} need a restart before they do anything");
        }

        if (refused > 0)
        {
            caveats.Add($"{refused} will be refused by Windows on this machine and are listed as such");
        }

        if (alreadyLean > 0)
        {
            caveats.Add($"{alreadyLean} are already at their lean value, so Engage will skip them");
        }

        note += caveats.Count == 0
            ? "Nothing here carries a caveat."
            : "Worth knowing: " + string.Join("; ", caveats) +
              ". Nothing is applied until you Engage, and the preflight names every change first.";

        return note;
    }

    private void UpdateCount()
    {
        var on = _rows.Count(r => r.IsEnabled);
        var off = _rows.Count - on;
        CountLabel.Text = $"{on} on, {off} off — off ones sort to the top";
    }

    private void ShowBulkNote(string text)
    {
        BulkNoteText.Text = text;
        BulkNote.Visibility = Visibility.Visible;
    }

    private static Brush EvidenceBrush(Evidence evidence)
    {
        var key = $"Evidence{evidence}Brush";
        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
}

/// <summary>
/// Row model for the Features list.
/// </summary>
/// <remarks>
/// Observable, and a class rather than a record, because the list re-sorts live on
/// <see cref="IsEnabled"/> — the collection view has to hear the change to move the row. The two
/// status strings are precomputed for both states so that toggling a row does not require re-probing
/// the machine to describe it correctly.
/// </remarks>
public sealed class FeatureRow : INotifyPropertyChanged
{
    private bool _isEnabled;

    public required string EntryId { get; init; }

    /// <summary>Catalog position, the secondary sort key. Keeps category grouping intact within on/off.</summary>
    public required int Ordinal { get; init; }

    public required string Title { get; init; }

    public required string EvidenceLabel { get; init; }

    public required Brush EvidenceBrush { get; init; }

    public required string ImpactLabel { get; init; }

    public required string TierLabel { get; init; }

    public required bool NeedsReboot { get; init; }

    public required string RebootLabel { get; init; }

    // Typed facts rather than something read back out of the status sentence. A bulk-enable summary that
    // works by string-matching its own labels breaks silently the first time a label is reworded.

    /// <summary>Contains a close op, so Restore will not undo what this does.</summary>
    public bool ClosesApplications { get; init; }

    /// <summary>Changes the machine for every user rather than just this one.</summary>
    public bool IsMachineWide { get; init; }

    /// <summary>Every step is refused on this machine, so enabling it changes nothing.</summary>
    public bool FullyRefused { get; init; }

    /// <summary>The machine already holds the target state, so Engage will skip it.</summary>
    public bool AlreadyLean { get; init; }

    public required string Breaks { get; init; }

    public required string StatusWhenOn { get; init; }

    public required string StatusWhenOff { get; init; }

    public required bool CanToggle { get; init; }

    public required string ToggleTipWhenOn { get; init; }

    public required string ToggleTipWhenOff { get; init; }

    /// <summary>Whether this entry is switched on in the active profile.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(ToggleTip));
            OnPropertyChanged(nameof(OffMarkerVisibility));
        }
    }

    public string StatusLabel => IsEnabled ? StatusWhenOn : StatusWhenOff;

    public string ToggleTip => IsEnabled ? ToggleTipWhenOn : ToggleTipWhenOff;

    public Visibility OffMarkerVisibility => IsEnabled ? Visibility.Hidden : Visibility.Visible;

    public Visibility RebootVisibility => NeedsReboot ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
