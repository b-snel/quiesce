using System.Text.Json.Serialization;
using Quiesce.Core.Catalog;

namespace Quiesce.Core.Platform;

/// <summary>
/// Live system state captured before an activation broadcast, so revert can restore the *effective*
/// setting and not merely the registry bytes behind it.
/// </summary>
/// <remarks>
/// This exists because <c>SystemParametersInfo</c> is a second writer. <c>SPI_SETMOUSE</c> with
/// <c>SPIF_UPDATEINIFILE</c> applies the array you hand it and then persists that array itself — so
/// a revert that rewrites <c>HKCU\Control Panel\Mouse</c> and stops leaves the running session on
/// the tweaked acceleration curve until sign-out, while <c>reg query</c> and any byte-level
/// round-trip check both report success. The captured parameter set makes the inverse real.
/// </remarks>
public sealed record ActivationState
{
    [JsonPropertyName("kind")]
    public required ActivationKind Kind { get; init; }

    /// <summary>
    /// For <see cref="ActivationKind.SpiSetMouse"/>: the three-element mouse parameter array
    /// (threshold1, threshold2, acceleration) as read by <c>SPI_GETMOUSE</c> before the change.
    /// </summary>
    [JsonPropertyName("mouseParams")]
    public IReadOnlyList<int>? MouseParams { get; init; }
}

/// <summary>
/// Reads the live state an activation is about to overwrite.
/// Separate from <see cref="IActivationBroadcaster"/> so the capture is mockable in tests.
/// </summary>
public interface IActivationCapture
{
    /// <summary>Returns null when the activation kind carries no restorable system state.</summary>
    ActivationState? Capture(ActivationKind kind);

    /// <summary>
    /// Re-applies a captured state. Called on revert, after the registry has been restored, so the
    /// running session actually returns to the original behaviour.
    /// </summary>
    void Restore(ActivationState state);
}
