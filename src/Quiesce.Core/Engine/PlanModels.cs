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

    // --- process ops --------------------------------------------------------

    /// <summary>
    /// The one live process this step acts on, resolved at plan time.
    /// </summary>
    /// <remarks>
    /// One step per process, not one per group: the preflight list has to name every application by PID
    /// before any of them is asked to close, and each process needs its own journal record because each
    /// has its own prior. It also fixes the target set at plan time, so a process that starts between
    /// the preflight dialog and the apply is not touched — what the user approved is what runs.
    /// </remarks>
    public ProcessSnapshot? ProcessBefore { get; init; }

    public ProcessAction? ProcessAction { get; init; }

    public System.Diagnostics.ProcessPriorityClass? IntendedPriority { get; init; }

    /// <summary>
    /// Set when a guardrail refused this step. Refused steps are shown to the user with the reason
    /// and never applied — visible refusal, not silent omission.
    /// </summary>
    public string? RefusedReason { get; init; }

    /// <summary>
    /// Why this step is a no-op, when "already lean" would be the wrong words.
    /// </summary>
    /// <remarks>
    /// Registry and service steps elide because the machine already holds the target value, and
    /// "already lean" says that well. A process group elides because nothing it names is running, which
    /// is a different fact and reads as nonsense under the same label. Null keeps the default wording.
    /// </remarks>
    public string? NoOpDetail { get; init; }

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

    public ProcessIdentity? Process { get; init; }

    public string? ProcessImageName { get; init; }

    /// <summary>
    /// The class to write back. Null for a close, which has no undo at all.
    /// </summary>
    public System.Diagnostics.ProcessPriorityClass? ProcessPriorPriority { get; init; }

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

    /// <summary>
    /// A throttle that was applied, and so can be rolled back.
    /// </summary>
    /// <remarks>
    /// There is deliberately no equivalent for a close. Nothing unwinds "the application exited", so a
    /// close never enters the rollback set — which is also why the catalog loader refuses an entry that
    /// mixes the two.
    /// </remarks>
    public static AppliedStep ForThrottle(
        PlannedStep step,
        ProcessSnapshot process,
        System.Diagnostics.ProcessPriorityClass prior) => new()
    {
        StepId = step.StepId,
        Process = process.Identity,
        ProcessImageName = process.ImageName,
        ProcessPriorPriority = prior,
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

/// <summary>Result of applying one process step.</summary>
/// <remarks>
/// Has a <see cref="Note"/> where the service outcome does not, because a process step has a third
/// ending that is neither success nor failure: the application was asked to close and declined, or a
/// guardrail refused it after the plan was built. Nothing happened and nothing needs undoing, so it is
/// not a failure — but it is also not "already lean", and the user has to be told which of their
/// applications is still running and why.
/// </remarks>
public sealed record ProcessApplyOutcome
{
    public bool Skipped { get; init; }

    /// <summary>Non-null when the step failed and its entry must roll back.</summary>
    public string? Failure { get; init; }

    public AppliedStep? Applied { get; init; }

    /// <summary>
    /// The step did real work that has no undo, so it counts as applied but must not be rolled back.
    /// </summary>
    /// <remarks>
    /// Only a successful close. Putting one in the rollback set would be meaningless — nothing unwinds
    /// "the application exited" — but counting it as skipped would be a lie: an application the user was
    /// using is gone, and the summary has to say so.
    /// </remarks>
    public bool AppliedWithoutUndo { get; init; }

    /// <summary>Surfaced verbatim to the user. Never a silent omission.</summary>
    public string? Note { get; init; }
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
    /// Things the user must be told that are neither an applied step nor a failure.
    /// </summary>
    /// <remarks>
    /// Two kinds so far, both from process ops: an application that was asked to close and did not
    /// (almost always a "save your work?" prompt, which the graceful ladder exists to respect), and an
    /// application that <em>was</em> closed and will not be reopened by Restore. The second is the more
    /// important one — it is the only thing Quiesce does that its undo does not cover, so it has to be
    /// said at the moment it happens rather than discovered later.
    /// </remarks>
    public IReadOnlyList<string> Notes { get; init; } = [];

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
