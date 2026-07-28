using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// Records what was captured, broadcast and replayed, so the tests can assert that revert restores
/// the captured parameters rather than blindly re-broadcasting.
/// </summary>
public sealed class RecordingActivation : IActivationBroadcaster, IActivationCapture
{
    /// <summary>Simulated live mouse curve. Capture reads it; Restore/Broadcast write it.</summary>
    public int[] MouseCurve { get; set; } = [6, 10, 1];

    public List<ActivationKind> Broadcasts { get; } = [];

    public List<ActivationState> Restores { get; } = [];

    public ActivationState? Capture(ActivationKind kind) => kind switch
    {
        ActivationKind.SpiSetMouse => new ActivationState { Kind = kind, MouseParams = MouseCurve.ToArray() },
        _ => null,
    };

    public void Restore(ActivationState state)
    {
        Restores.Add(state);
        if (state.Kind == ActivationKind.SpiSetMouse && state.MouseParams is { } p)
        {
            MouseCurve = p.ToArray();
        }
    }

    public void Broadcast(ActivationKind kind)
    {
        Broadcasts.Add(kind);
        if (kind == ActivationKind.SpiSetMouse)
        {
            MouseCurve = [0, 0, 0]; // the "lean" curve: acceleration off
        }
    }
}

public class ActivationTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "quiesce-activation", Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _registry = new();
    private readonly RecordingActivation _activation = new();
    private readonly TransactionEngine _engine;

    public ActivationTests()
    {
        _registry.LoadUserHive(EngineTestHarness.Sid);
        _engine = new TransactionEngine(
            _registry,
            _activation,
            new QuiescePaths(_dataRoot),
            new EngineInfo { AppVersion = "test", OsBuild = "10.0.26200", UserSid = EngineTestHarness.Sid },
            _activation);
    }

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

    private static CatalogEntry MouseEntry() =>
        EngineTestHarness.DwordEntry(id: "gaming.mouse-accel-off", activation: [ActivationKind.SpiSetMouse]);

    [Fact]
    public void Activation_state_is_captured_before_the_write_and_journaled()
    {
        _activation.MouseCurve = [6, 10, 1];
        var entry = MouseEntry();

        var engage = _engine.Engage(_engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);

        var records = JournalReader.Read(
            Path.Combine(new QuiescePaths(_dataRoot).SessionDir(engage.SessionId), "journal.jsonl")).Records;

        var applying = Assert.Single(records.OfType<ApplyingRecord>());
        var captured = Assert.Single(applying.ActivationPrior);

        Assert.Equal(ActivationKind.SpiSetMouse, captured.Kind);
        Assert.Equal([6, 10, 1], captured.MouseParams);
    }

    [Fact]
    public void Revert_replays_the_captured_curve_instead_of_re_broadcasting()
    {
        // The bug this guards: restoring the registry bytes and then broadcasting SPI_SETMOUSE
        // again would re-apply the LEAN curve, leaving acceleration off while every byte-level
        // check reports a clean round trip.
        _activation.MouseCurve = [6, 10, 1];
        var entry = MouseEntry();

        var engage = _engine.Engage(_engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);
        Assert.Equal([0, 0, 0], _activation.MouseCurve); // lean curve applied

        var revert = _engine.RevertSession(engage.SessionId, "test");

        Assert.True(revert.Clean);
        Assert.Equal([6, 10, 1], _activation.MouseCurve); // original curve genuinely restored
        Assert.Single(_activation.Restores);
        Assert.DoesNotContain(ActivationKind.SpiSetMouse, _activation.Broadcasts.Skip(1));
    }

    [Fact]
    public void Stateless_activations_are_re_broadcast_on_revert()
    {
        var entry = EngineTestHarness.DwordEntry(activation: [ActivationKind.ShChangeNotify]);

        var engage = _engine.Engage(_engine.Plan(EngineTestHarness.CatalogOf(entry), "test"), FaultInjector.None);
        _activation.Broadcasts.Clear();

        _engine.RevertSession(engage.SessionId, "test");

        Assert.Contains(ActivationKind.ShChangeNotify, _activation.Broadcasts);
        Assert.Empty(_activation.Restores); // nothing stateful to replay
    }

    [Fact]
    public void Journal_alone_carries_everything_revert_needs_including_activation()
    {
        // The panic binary owns no catalog. If activation lived only in the catalog, this revert
        // would restore bytes and leave the session on the tweaked curve.
        _activation.MouseCurve = [6, 10, 1];
        var engage = _engine.Engage(
            _engine.Plan(EngineTestHarness.CatalogOf(MouseEntry()), "test"), FaultInjector.None);

        var catalogFreeEngine = new TransactionEngine(
            _registry,
            _activation,
            new QuiescePaths(_dataRoot),
            new EngineInfo { AppVersion = "test", OsBuild = "10.0.26200", UserSid = EngineTestHarness.Sid },
            _activation);

        var revert = catalogFreeEngine.RevertSession(engage.SessionId, "panic");

        Assert.True(revert.Clean);
        Assert.Equal([6, 10, 1], _activation.MouseCurve);
    }

    [Fact]
    public void A_failed_activation_replay_is_reported_and_keeps_the_session_dirty()
    {
        _activation.MouseCurve = [6, 10, 1];
        var engage = _engine.Engage(
            _engine.Plan(EngineTestHarness.CatalogOf(MouseEntry()), "test"), FaultInjector.None);

        var failing = new ThrowingRestore();
        var engine = new TransactionEngine(
            _registry,
            failing,
            new QuiescePaths(_dataRoot),
            new EngineInfo { AppVersion = "test", OsBuild = "10.0.26200", UserSid = EngineTestHarness.Sid },
            failing);

        var revert = engine.RevertSession(engage.SessionId, "test");

        // Registry is back but behaviour is not, so this is NOT a clean revert.
        Assert.False(revert.Clean);
        Assert.Equal(1, revert.Failed);
        Assert.Contains(revert.Messages, m => m.Contains("replay failed"));
    }

    private sealed class ThrowingRestore : IActivationBroadcaster, IActivationCapture
    {
        public ActivationState? Capture(ActivationKind kind) => null;

        public void Restore(ActivationState state) =>
            throw new InvalidOperationException("SPI_SETMOUSE failed (simulated).");

        public void Broadcast(ActivationKind kind)
        {
        }
    }
}
