namespace Quiesce.Tests;

/// <summary>
/// Serialises the test classes that take the real <c>Global\Quiesce.Mutating</c> mutex.
/// </summary>
/// <remarks>
/// The third instance of the trap <see cref="ProcessAncestryCollection"/> and
/// <see cref="SessionGuardCollection"/> document, and the sharpest of the three: the shared state is not
/// a static field in this process, it is a named kernel object visible to every process on the machine.
/// <para>
/// Two of the lock's tests hold it deliberately, to assert that a contended run refuses and that the
/// body never executes. xUnit runs classes in parallel, so any other class calling
/// <c>MutatingLock.TryRun</c> during that window would observe "busy" and fail for a reason that has
/// nothing to do with what it was testing — and would do so intermittently, depending on scheduling.
/// Anything that acquires this mutex, directly or through the engine, joins this collection.
/// </para>
/// </remarks>
public static class MutatingLockCollection
{
    public const string Name = "mutating-lock";
}
