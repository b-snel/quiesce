using System.Windows.Media;
using Quiesce.Core.Catalog;

namespace Quiesce.App.Views;

public partial class FeaturesPage
{
    public FeaturesPage(AppState state)
    {
        InitializeComponent();

        if (state.Catalog is null || state.Plan is null)
        {
            EntryList.ItemsSource = new[]
            {
                new FeatureRow
                {
                    Title = "Catalog unavailable",
                    EvidenceLabel = "—",
                    EvidenceBrush = Brushes.Gray,
                    ImpactLabel = "—",
                    TierLabel = "—",
                    Breaks = state.LoadError ?? "No catalog was found next to the executable.",
                    StatusLabel = "",
                    IsApplied = false,
                },
            };
            return;
        }

        EntryList.ItemsSource = state.Catalog.Entries.Select(entry =>
        {
            var steps = state.Plan.Steps.Where(s => s.EntryId == entry.Id).ToList();
            var applied = steps.Count > 0 && steps.All(s => s.NoOp);
            var partial = !applied && steps.Any(s => s.NoOp);

            return new FeatureRow
            {
                Title = entry.Title,
                EvidenceLabel = entry.Evidence.ToString(),
                EvidenceBrush = EvidenceBrush(entry.Evidence),
                ImpactLabel = $"{entry.Impact} impact",
                TierLabel = $"tier {entry.RiskTier}",
                Breaks = $"Breaks: {entry.WhatItBreaks}",
                // Tri-state on purpose: "partially applied" is a real machine state and hiding it
                // behind a binary toggle is how tools lie. The M3 toggle gets a dedicated visual.
                StatusLabel = applied ? "state: applied (already lean)"
                    : partial ? "state: PARTIALLY applied — click for detail (M3)"
                    : "state: not applied",
                IsApplied = applied,
            };
        }).ToList();
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
    public required string Title { get; init; }

    public required string EvidenceLabel { get; init; }

    public required Brush EvidenceBrush { get; init; }

    public required string ImpactLabel { get; init; }

    public required string TierLabel { get; init; }

    public required string Breaks { get; init; }

    public required string StatusLabel { get; init; }

    public required bool IsApplied { get; init; }
}
