namespace Quiesce.Tests;

/// <summary>
/// Serialises the test classes that override <c>SessionGuard.OverrideForTests</c>.
/// </summary>
/// <remarks>
/// The second instance of the same trap <see cref="ProcessAncestryCollection"/> documents, and it had
/// been latent for a while: four classes touch this process-wide static and two of them set it to
/// <c>true</c> mid-test to assert a remote-session refusal, while the other two pin it to <c>false</c>
/// in their constructors. xUnit runs classes in parallel, so whichever wrote last decided the other's
/// assertions.
/// <para>
/// It surfaced when the power scheme tests were added — a test asserting that becoming remote between
/// plan and apply still refuses the switch passed on its own and failed in the full run. Nothing about
/// that test was wrong; it was the fourth writer to a shared variable. Everything that touches the
/// static shares this collection so they cannot overlap.
/// </para>
/// </remarks>
public static class SessionGuardCollection
{
    public const string Name = "session-guard";
}
