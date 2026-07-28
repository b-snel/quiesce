using System.Security.Principal;

namespace Quiesce.Core.Platform;

/// <summary>
/// Whether this process holds an elevated token.
/// </summary>
/// <remarks>
/// Lives in Core rather than the CLI because the engine needs it to explain a refused write: the
/// difference between "you need admin" and "you have admin and Windows refused anyway" is the whole
/// diagnosis, and the engine is where the refusal is caught.
/// </remarks>
public static class Elevation
{
    /// <summary>
    /// True when the current token has the Administrators group enabled — not merely present.
    /// A filtered (split) token lists Administrators as deny-only and returns false here, which is
    /// the correct answer: it cannot write to admin-owned keys.
    /// </summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
