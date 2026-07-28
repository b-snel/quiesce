using System.Windows;
using System.Windows.Media;
using Quiesce.Core.Catalog;
using Quiesce.Core.Platform;

namespace Quiesce.App.Views;

public partial class FeaturesPage
{
    private readonly ProfileStore? _profiles;

    public FeaturesPage(AppState state)
    {
        InitializeComponent();

        if (state.Catalog is null || state.Plan is null)
        {
            EntryList.ItemsSource = new[]
            {
                new FeatureRow
                {
                    EntryId = string.Empty,
                    Title = "Catalog unavailable",
                    EvidenceLabel = "—",
                    EvidenceBrush = Brushes.Gray,
                    ImpactLabel = "—",
                    TierLabel = "—",
                    Breaks = state.LoadError ?? "No catalog was found next to the executable.",
                    StatusLabel = string.Empty,
                    IsEnabled = false,
                    CanToggle = false,
                    ToggleTip = "No catalog is loaded.",
                },
            };
            return;
        }

        _profiles = new ProfileStore(state.DataRoot);
        var enabled = _profiles.ActiveEnabled();

        // While engaged, the enabled set describes what IS applied. Letting it change underneath a
        // live session would desynchronize the journal from the profile, so toggles lock until
        // Restore. Locked-with-a-reason, never hidden.
        var engaged = state.MachineState.IsDirty;

        EntryList.ItemsSource = state.Catalog.Entries.Select(entry =>
        {
            var isEnabled = enabled.Contains(entry.Id);

            // Steps only exist for enabled entries, so status for a disabled row is probed
            // separately rather than inferred from an empty step list.
            var steps = state.Plan.Steps.Where(s => s.EntryId == entry.Id).ToList();
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

            var status = !isEnabled ? "off — not applied, and Engage will skip it"
                : applied ? $"on — {elision}"
                : refused ? "on — REFUSED: Windows will not permit this on this machine"
                : someRefused ? "on — PARTIALLY refused; the rest will be applied on Engage"
                : partial ? "on — PARTIALLY applied"
                : "on — will be applied on Engage";

            return new FeatureRow
            {
                EntryId = entry.Id,
                Title = entry.Title,
                EvidenceLabel = entry.Evidence.ToString(),
                EvidenceBrush = EvidenceBrush(entry.Evidence),
                ImpactLabel = $"{entry.Impact} impact",
                TierLabel = entry.RequiresAdmin ? $"tier {entry.RiskTier} · admin" : $"tier {entry.RiskTier}",
                // The reason, not just the verdict: "refused" without the why is the same dead end
                // as a tweak that quietly did nothing.
                Breaks = refusalReason is not null
                    ? $"Refused: {refusalReason}\nBreaks: {entry.WhatItBreaks}"
                    : $"Breaks: {entry.WhatItBreaks}",
                StatusLabel = status,
                IsEnabled = isEnabled,
                CanToggle = !engaged,
                ToggleTip = engaged
                    ? "Restore first — changing what is enabled while engaged would desynchronize the journal."
                    : isEnabled ? "On. Engage will apply this." : "Off. Engage will skip this.",
            };
        }).ToList();
    }

    private void OnToggled(object sender, RoutedEventArgs e)
    {
        if (_profiles is null || sender is not Wpf.Ui.Controls.ToggleSwitch toggle)
        {
            return;
        }

        if (toggle.Tag is not string entryId || string.IsNullOrEmpty(entryId))
        {
            return;
        }

        _profiles.SetEnabled(entryId, toggle.IsChecked == true);
    }

    private static Brush EvidenceBrush(Evidence evidence)
    {
        var key = $"Evidence{evidence}Brush";
        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
}

/// <summary>Row model for the Features list. Plain properties; the page is rebuilt on load.</summary>
public sealed record FeatureRow
{
    public required string EntryId { get; init; }

    public required string Title { get; init; }

    public required string EvidenceLabel { get; init; }

    public required Brush EvidenceBrush { get; init; }

    public required string ImpactLabel { get; init; }

    public required string TierLabel { get; init; }

    public required string Breaks { get; init; }

    public required string StatusLabel { get; init; }

    /// <summary>Whether this entry is switched on in the active profile.</summary>
    public required bool IsEnabled { get; init; }

    public required bool CanToggle { get; init; }

    public required string ToggleTip { get; init; }
}
