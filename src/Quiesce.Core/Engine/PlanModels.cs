using Quiesce.Core.Catalog;
using Quiesce.Core.Platform;

namespace Quiesce.Core.Engine;

/// <summary>One concrete step an Engage would take, with the live prior probed at plan time.</summary>
public sealed record PlannedStep
{
    public required int StepId { get; init; }

    public required string EntryId { get; init; }

    public required TweakScope Scope { get; init; }

    /// <summary>The catalog op this step came from. Dispatch on its concrete type.</summary>
    public required OpSpec Op { get; init; }

    /// <summary>Human identity, used by the preflight list and the journal.</summary>
    public required string Target { get; init; }

    // --- registry ops -------------------------------------------------------

    public RegistryTarget? RegistryTarget { get; init; }

    /// <summary>Live state at plan time. Re-probed at apply time; this copy drives the preflight UI.</summary>
    public RegistryProbe? Prior { get; init; }

    public RegistryData? IntendedNew { get; init; }

    // --- service ops --------------------------------------------------------

    public ServiceSnapshot? ServiceBefore { get; init; }

    /// <summary>
    /// The start type actually planned, which may differ from what the catalog asked for: a
    /// trigger-started service is clamped to Manual rather than Disabled.
    /// </summary>
    public ServiceStartType? IntendedStartType { get; init; }

    public bool IntendedStop { get; init; }

    /// <summary>
    /// Set when a guardrail refused this step. Refused steps are shown to the user with the reason
    /// and never applied — visible refusal, not silent omission.
    /// </summary>
    public string? RefusedReason { get; init; }

    public required IReadOnlyList<ActivationKind> Activation { get; init; }

    /// <summary>
    /// The live state already equals the target. No-op steps are never applied and never
    /// journalled as applied, so Restore cannot "restore" a value the user had set themselves —
    /// and the UI can honestly say "already lean".
    /// </summary>
    public required bool NoOp { get; init; }

    /// <summary>Neither a no-op nor refused: this step will actually run.</summary>
    public bool WillRun => !NoOp && RefusedReason is null;
}

/// <summary>
/// A step that was actually performed, paired with the prior state captured immediately before it.
/// This is what entry-level rollback unwinds.
/// </summary>
public sealed record AppliedStep
{
    public required int StepId { get; init; }

    public RegistryTarget? RegistryTarget { get; init; }

    public RegistryProbe? RegistryPrior { get; init; }

    public string? Service { get; init; }

    public ServiceSnapshot? ServicePrior { get; init; }

    public static AppliedStep ForRegistry(PlannedStep step, RegistryTarget target, RegistryProbe prior) => new()
    {
        StepId = step.StepId,
        RegistryTarget = target,
        RegistryPrior = prior,
    };

    public static AppliedStep ForService(PlannedStep step, string service, ServiceSnapshot prior) => new()
    {
        StepId = step.StepId,
        Service = service,
        ServicePrior = prior,
    };
}

/// <summary>Result of applying one service step.</summary>
public sealed record ServiceApplyOutcome
{
    public bool Skipped { get; init; }

    /// <summary>Non-null when the step failed and its entry must roll back.</summary>
    public string? Failure { get; init; }

    public AppliedStep? Applied { get; init; }
}

public sealed record EngagePlan
{
    public required string Profile { get; init; }

    public required string CatalogVersion { get; init; }

    public required IReadOnlyList<PlannedStep> Steps { get; init; }

    public IEnumerable<PlannedStep> EffectiveSteps => Steps.Where(s => s.WillRun);

    /// <summary>Steps a guardrail refused. Surfaced with reasons rather than dropped silently.</summary>
    public IEnumerable<PlannedStep> RefusedSteps => Steps.Where(s => s.RefusedReason is not null);

    public bool RequiresElevation => EffectiveSteps.Any(s => s.Op.NeedsAdmin);
}

public sealed record EngageResult
{
    public required Guid SessionId { get; init; }

    public required int Applied { get; init; }

    public required int SkippedNoop { get; init; }

    public required IReadOnlyList<string> RolledBackEntries { get; init; }

    /// <summary>
    /// Why each rolled-back entry failed, keyed by entry id. Surfaced verbatim so that a refusal by
    /// Windows reads as "Windows blocked this, and here is why" rather than "the app is broken".
    /// </summary>
    public IReadOnlyDictionary<string, string> Diagnoses { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool Success => RolledBackEntries.Count == 0;
}

public sealed record RevertResult
{
    public required int Reverted { get; init; }

    public required int Deferred { get; init; }

    public required int Failed { get; init; }

    public required IReadOnlyList<string> Messages { get; init; }

    /// <summary>Only a fully clean revert clears the dirty flag.</summary>
    public bool Clean => Deferred == 0 && Failed == 0;
}

/// <summary>Thrown by the fault-injection hook to simulate a crash mid-apply.</summary>
/// <remarks>
/// The CLI deliberately does not catch it: the process dies with the journal and state file in
/// exactly the shape a real crash leaves, and the recovery path has to cope with that — which is
/// the point of the exercise.
/// </remarks>
public sealed class FaultInjectedException(int afterStep)
    : Exception($"Fault injected after step {afterStep} (simulated crash).")
{
    public int AfterStep { get; } = afterStep;
}

/// <summary>Deterministic crash injection for the M1 acceptance tests.</summary>
public sealed class FaultInjector(int? failAfterStep)
{
    public static readonly FaultInjector None = new(null);

    /// <summary>Parses <c>afterStepN</c>; null input means no fault.</summary>
    public static FaultInjector Parse(string? spec)
    {
        if (spec is null)
        {
            return None;
        }

        const string prefix = "afterStep";
        if (spec.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(spec.AsSpan(prefix.Length), out var n) && n >= 1)
        {
            return new FaultInjector(n);
        }

        throw new ArgumentException($"Unrecognized fault spec '{spec}'. Expected afterStep<N>.");
    }

    public void AfterStepApplied(int stepId)
    {
        if (failAfterStep == stepId)
        {
            throw new FaultInjectedException(stepId);
        }
    }
}
