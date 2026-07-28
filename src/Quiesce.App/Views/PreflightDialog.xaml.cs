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

        if (CloseSummary(steps) is { Length: > 0 } closing)
        {
            CloseHeadline.Text = closing;
            CloseWarning.Visibility = Visibility.Visible;
        }

        ReversibilityNote.Text = ReversibilityText(plan);

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

    /// <summary>
    /// Names every application this plan will ask to close, or the empty string when it closes none.
    /// </summary>
    /// <remarks>
    /// Grouped by image name, not listed per step. A close journals one step per process instance —
    /// deliberately, so each one carries its own recycling-proof identity — which means the measured
    /// Comet on this machine is nineteen steps. Nineteen rows each ending "Restore will NOT reopen it"
    /// is not nineteen times the warning; it is the warning turned into wallpaper. The per-row sentence
    /// stays, because it is the truth about that row. This is the sentence that has to be read.
    /// <para>
    /// Ordered by first appearance rather than sorted, so the order matches the list below it.
    /// </para>
    /// </remarks>
    internal static string CloseSummary(IReadOnlyList<PlannedStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var names = steps
            .Where(s => s.ProcessAction == Core.Catalog.ProcessAction.Close && s.ProcessBefore is not null)
            .Select(s => s.ProcessBefore!.ImageName + ".exe")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count switch
        {
            0 => string.Empty,
            1 => $"1 application will be asked to close: {names[0]}.",
            _ => $"{names.Count} applications will be asked to close: {string.Join(", ", names)}.",
        };
    }

    /// <summary>
    /// What this dialog is allowed to promise about putting things back.
    /// </summary>
    /// <remarks>
    /// THREE cases, and it used to have two. The elevation branch was standing in for "is this a
    /// serious change", so a plan whose effective steps were only closes fell through to
    /// "fully reversible" — because <see cref="EngagePlan.RequiresElevation"/> is
    /// <c>EffectiveSteps.Any(s =&gt; s.Op.NeedsAdmin)</c> and a process op's <c>NeedsAdmin</c> is
    /// <c>false</c>, correctly: closing a window in your own session needs no privilege. Reachable on
    /// any machine whose registry entries are already lean, which is every machine that has engaged
    /// once and restored. Closing is the one thing in this product with no undo, and the footer was
    /// calling it fully reversible at the exact moment the user was deciding whether to allow it.
    /// <para>
    /// The close clause is checked FIRST, and it wins over the elevation clause rather than being
    /// appended to it: a plan that closes something is a plan with an irreversible part regardless of
    /// what else it does, and the note has room for one sentence.
    /// </para>
    /// </remarks>
    internal static string ReversibilityText(EngagePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.EffectiveSteps.Any(s => s.ProcessAction == Core.Catalog.ProcessAction.Close))
        {
            return "Everything here is journaled before it is made, so Restore puts it back — " +
                   "everything except the closes. Nothing reopens a closed application, including Quiesce.";
        }

        return plan.RequiresElevation
            ? "Every change is written to Quiesce's journal before it is made, so Restore puts it all back — including after a crash."
            : "These are per-user changes. Every one is journaled before it is made and fully reversible.";
    }

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

        if (step.PowerPrior is { } powerPrior)
        {
            return $"power plan {powerPrior.FriendlyName ?? powerPrior.Scheme.ToString("D")}";
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

        if (step.PowerPrior is not null)
        {
            return $"power plan {step.IntendedSchemeName ?? step.IntendedScheme?.ToString("D")}, put back on Restore";
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
