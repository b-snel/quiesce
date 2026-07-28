using System.Text.Json;
using System.Windows;
using Quiesce.Core.Engine;
using Quiesce.Core.Platform;

namespace Quiesce.App.Views;

/// <summary>
/// Shows exactly what Engage will do, and asks. Returns <c>true</c> only on explicit approval.
/// </summary>
/// <remarks>
/// Rendered from the same <see cref="PlannedStep"/> objects the engine journals as
/// <c>planned</c> records and then executes, so what the user approves and what runs cannot
/// diverge. Building a separate "summary" model for the dialog would reintroduce exactly that gap.
/// </remarks>
public partial class PreflightDialog
{
    public PreflightDialog(EngagePlan plan, RestorePointResult? restorePoint)
    {
        InitializeComponent();

        var steps = plan.EffectiveSteps.ToList();
        var elided = plan.Steps.Count(s => s.NoOp);

        Summary.Text = steps.Count == 1
            ? "1 change to apply"
            : $"{steps.Count} changes to apply";

        if (elided > 0)
        {
            Summary.Text += $"  ({elided} already lean, skipped)";
        }

        StepList.ItemsSource = steps.Select(PreflightRow.From).ToList();

        ReversibilityNote.Text = plan.RequiresElevation
            ? "Every change is written to Quiesce's journal before it is made, so Restore puts it all back — including after a crash."
            : "These are per-user changes. Every one is journaled before it is made and fully reversible.";

        if (restorePoint is not null)
        {
            RestorePointBanner.Visibility = Visibility.Visible;
            RestorePointText.Text = restorePoint.CreatedNew
                ? $"System Restore: {restorePoint.Detail}"
                : $"System Restore: {restorePoint.Detail}";
        }
    }

    /// <summary>True when the user approved. Read after <c>ShowDialog</c>.</summary>
    public bool Approved { get; private set; }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        Approved = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Approved = false;
        DialogResult = false;
    }
}

/// <summary>One row of the preflight list, projected from a <see cref="PlannedStep"/>.</summary>
public sealed record PreflightRow
{
    public required string StepLabel { get; init; }

    public required string EntryId { get; init; }

    public required string ScopeLabel { get; init; }

    public required string Target { get; init; }

    public required string PriorText { get; init; }

    public required string NewText { get; init; }

    public required string ActivationText { get; init; }

    public required Visibility ActivationVisibility { get; init; }

    public static PreflightRow From(PlannedStep step)
    {
        var activations = step.Activation
            .Where(a => a != Core.Catalog.ActivationKind.None)
            .ToList();

        return new PreflightRow
        {
            StepLabel = $"step {step.StepId}",
            EntryId = step.EntryId,
            ScopeLabel = step.Scope == Core.Catalog.TweakScope.Session ? "session" : "persistent",
            Target = step.Target.ToString(),
            PriorText = Describe(step.Prior),
            NewText = $"{step.IntendedNew.Kind} {JsonSerializer.Serialize(step.IntendedNew.Data)}",
            ActivationText = activations.Count > 0
                ? $"then notifies Windows: {string.Join(", ", activations)}"
                : string.Empty,
            ActivationVisibility = activations.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
    }

    private static string Describe(RegistryProbe probe) => probe.Presence switch
    {
        RegPresence.ValuePresent => $"{probe.Value!.Kind} {JsonSerializer.Serialize(probe.Value.Data)}",
        // Spelled out rather than shown as "0": the difference between absent and zero is the
        // single most important fact about how this will be undone.
        RegPresence.ValueAbsent => "(value does not exist - Restore will delete it again)",
        RegPresence.KeyAbsent => "(key does not exist - Restore will remove what we create)",
        _ => "(unknown)",
    };
}
