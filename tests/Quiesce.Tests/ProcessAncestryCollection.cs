namespace Quiesce.Tests;

/// <summary>
/// Serialises the test classes that override <c>ProcessAncestry.OverrideForTests</c>.
/// </summary>
/// <remarks>
/// The override is a process-wide static, and xUnit runs test classes in parallel. Two classes setting it
/// to different values overlap, and the second one's value silently decides the first one's assertions —
/// which is not a hypothetical: a class that pinned the ancestry to its own PID started failing only in
/// the full run, because a class that pins it to the empty set was running beside it. Everything that
/// touches the static shares this collection so they cannot overlap.
/// </remarks>
public static class ProcessAncestryCollection
{
    public const string Name = "process-ancestry";
}
