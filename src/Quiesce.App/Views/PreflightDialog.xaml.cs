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

        // Refused steps are listed alongside the ones that will run, with their reason. Hiding them
        // would make a guardrail indistinguishable from a tweak that quietly did nothing — and
        // "Quiesce refuses to touch this, and here is why" is the moment the tool earns trust.
        var refused = plan.RefusedSteps.ToList();
        StepList.ItemsSource = steps.Select(PreflightRow.From)
            .Concat(refused.Select(PreflightRow.FromRefused))
            .ToList();

        if (refused.Count > 0)
        {
            Summary.Text += $"  ·  {refused.Count} refused by guardrails";
        }

        // Said before the user approves, not discovered afterwards. Some of these entries are the ones
        // most likely to be judged "didn't do anything" — the effect simply is not there yet.
        var needsReboot = plan.RebootRequiringEntries;
        if (needsReboot.Count > 0)
        {
            Summary.Text += $"  ·  {needsReboot.Count} needs a restart";
        }

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
            Target = step.Target,
            // Dispatched on which kind of prior the step actually carries. An earlier version tested only
            // for a service and dereferenced the registry prior otherwise, which a process step - carrying
            // neither - would have turned into a null reference in the middle of the approval dialog.
            PriorText = Prior(step),
            NewText = Change(step) + (step.RequiresReboot ? "  (in effect after a restart)" : string.Empty),
            ActivationText = activations.Count > 0
                ? $"then notifies Windows: {string.Join(", ", activations)}"
                : string.Empty,
            ActivationVisibility = activations.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
    }

    /// <summary>A guardrail-refused step, rendered so the user can see what was declined and why.</summary>
    public static PreflightRow FromRefused(PlannedStep step) => new()
    {
        StepLabel = "refused",
        EntryId = step.EntryId,
        ScopeLabel = "locked",
        Target = step.Target,
        PriorText = "unchanged",
        NewText = step.RefusedReason ?? "refused by a guardrail",
        ActivationText = string.Empty,
        ActivationVisibility = Visibility.Collapsed,
    };

    private static string Prior(PlannedStep step)
    {
        if (step.ServiceBefore is { } svc)
        {
            return $"start type {svc.StartType}{(svc.DelayedAutostart ? " (delayed)" : string.Empty)}, currently {svc.RunState}";
        }

        if (step.ProcessBefore is { } process)
        {
            return $"running at {process.PriorityClass} priority";
        }

        return Describe(step.Prior!);
    }

    private static string Change(PlannedStep step)
    {
        if (step.ServiceBefore is not null)
        {
            return $"start type {step.IntendedStartType}{(step.IntendedStop ? ", stopped now" : ", left running")}";
        }

        if (step.ProcessBefore is not null)
        {
            // Stated at the point of approval, in the dialog where the user says yes. A close is the only
            // thing in Quiesce that Restore does not undo, and the moment to say so is before it happens.
            return step.ProcessAction == Core.Catalog.ProcessAction.Throttle
                ? $"priority lowered to {step.IntendedPriority}, put back on Restore"
                : "asked to close - Restore will NOT reopen it";
        }

        return $"{step.IntendedNew!.Kind} {JsonSerializer.Serialize(step.IntendedNew.Data)}";
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
