using Quiesce.Core.Catalog;
using Quiesce.Core.Journal;

namespace Quiesce.Tests;

/// <summary>
/// "I cannot tell" must never be reported as "clean".
/// </summary>
/// <remarks>
/// <para>
/// These pin a bug found on real hardware, not in review. The data root is deliberately hardened to
/// Administrators — an elevated Quiesce later executes the revert plan it finds there — and
/// <see cref="File.Exists(string)"/> returns <c>false</c> when the answer is really "you are not permitted
/// to look", because it swallows every exception by design. So an unelevated read fell through to a default
/// state and reported <c>isDirty: false</c>.
/// </para>
/// <para>
/// The observed consequence: with GameDVR and mouse acceleration genuinely turned off in the registry at
/// that moment, <c>quiesce inventory</c> printed <c>machine: clean</c>. The same fall-through made
/// <c>restore</c> print "No active session. Nothing to restore." and <c>recover</c> print "Machine is
/// clean". Three separate reassurances, all false, all from one swallowed access denial on the single
/// question this tool exists to answer.
/// </para>
/// <para>
/// Unreadability is arranged by putting a DIRECTORY where the file belongs. Reading it throws
/// <see cref="UnauthorizedAccessException"/> — the same exception an ACL denial produces — without the test
/// needing to manipulate ACLs or run elevated, which a test suite must never require.
/// </para>
/// </remarks>
public class StateReadabilityTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), "quiesce-state", Guid.NewGuid().ToString("N"));

    public StateReadabilityTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void MakeUnreadable(string fileName) =>
        Directory.CreateDirectory(Path.Combine(_dataRoot, fileName));

    [Fact]
    public void An_unreadable_state_file_is_an_error_and_never_reported_as_clean()
    {
        MakeUnreadable("state.json");

        var ex = Assert.Throws<StateUnreadableException>(() => new StateStore(_dataRoot).Load());

        // The message has to carry the instruction, because the person reading it is mid-recovery.
        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("elevated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT read this as 'clean'", ex.Message);
    }

    [Fact]
    public void A_genuinely_absent_state_file_still_reads_as_clean()
    {
        // The one case a default state is the truth: first run, nothing has ever been applied. If this
        // regressed into an error, a fresh install could not report its own state.
        var state = new StateStore(_dataRoot).Load();

        Assert.False(state.IsDirty);
        Assert.Null(state.ActiveSessionId);
    }

    [Fact]
    public void A_state_file_that_exists_round_trips()
    {
        var store = new StateStore(_dataRoot);
        var sessionId = Guid.NewGuid();

        store.Save(new QuiesceState { IsDirty = true, ActiveSessionId = sessionId });

        var loaded = store.Load();
        Assert.True(loaded.IsDirty);
        Assert.Equal(sessionId, loaded.ActiveSessionId);
    }

    [Fact]
    public void An_unreadable_profile_file_does_not_silently_become_the_shipped_defaults()
    {
        // Milder than the state lie but the same shape: an unelevated print-plan would compute the plan
        // from BuiltInDefault while telling the user it was showing them their own profile.
        MakeUnreadable("profiles.json");

        Assert.Throws<StateUnreadableException>(() => new ProfileStore(_dataRoot).Load());
    }

    [Fact]
    public void An_absent_profile_file_still_falls_back_to_the_built_in_default()
    {
        var file = new ProfileStore(_dataRoot).Load();

        Assert.Equal(ProfileStore.DefaultProfileName, file.Active);
        Assert.Equal(ProfileStore.BuiltInDefault, file.Profiles[ProfileStore.DefaultProfileName].Enabled);
    }
}
