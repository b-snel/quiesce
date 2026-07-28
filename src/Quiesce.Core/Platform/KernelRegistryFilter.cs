namespace Quiesce.Core.Platform;

/// <summary>
/// Whether Windows' User Choice Protection Driver is loaded and vetoing registry writes.
/// </summary>
/// <remarks>
/// <para>
/// UCPD.sys registers a kernel registry callback (<c>CmRegisterCallbackEx</c>) and denies
/// <c>RegNtPreSetValueKey</c> for an exact, case-insensitive list of (key path, value name) pairs.
/// It is downstream of the security check, which is why the symptom is so confusing: opening the
/// key with <c>KEY_SET_VALUE</c> succeeds, because Windows stamps granted access at open time, and
/// the denial only arrives when the write itself is attempted. No registry ACL can produce that
/// pattern — security descriptors attach to keys, not to individual value names.
/// </para>
/// <para>
/// Verified on build 26200.8875: the driver binary contains <c>TaskbarDa</c>,
/// <c>SOFTWARE\Policies\Microsoft\Dsh</c> and <c>AllowNewsAndInterests</c> as adjacent UTF-16
/// strings, and contains none of the targets that accepted writes in the same session.
/// </para>
/// <para>
/// This is a <em>probe</em>, not a constant, because the guardrail it feeds must be conditional. If
/// a future Windows build drops a pair from the table, or the driver stops running, the affected
/// tweaks should start working again on their own rather than staying refused forever on the
/// strength of something observed once in July 2026.
/// </para>
/// </remarks>
public static class KernelRegistryFilter
{
    /// <summary>Overrides the live probe. Tests only — the host's real state must not decide a test.</summary>
    internal static bool? OverrideForTests { get; set; }

    private const string DriverServiceName = "UCPD";

    /// <summary>True when the User Choice Protection Driver is present and running.</summary>
    /// <remarks>
    /// Fails <em>open</em>, unlike <see cref="SessionGuard"/>. The asymmetry is deliberate: a
    /// wrongly-active remote-session guard costs a refused tweak, whereas a wrongly-active veto
    /// guard would permanently hide a tweak that works fine. If the driver state cannot be read,
    /// assume no veto and let the write be attempted — a refused write is already handled, reported
    /// with its reason, and rolled back.
    /// </remarks>
    public static bool IsActive()
    {
        if (OverrideForTests is { } forced)
        {
            return forced;
        }

        // Queried through the same SCM P/Invoke the engine uses rather than ServiceController,
        // which M4 removed from this codebase for collapsing Automatic-Delayed into Automatic.
        // Cached because Plan calls this once per registry op and the driver cannot start or stop
        // mid-plan without a reboot.
        return _cached ??= QueryDriverRunning();
    }

    private static bool? _cached;

    /// <summary>Clears the cached probe result. Tests only.</summary>
    internal static void ResetCache() => _cached = null;

    private static bool QueryDriverRunning()
    {
        try
        {
            var snapshot = new Win32ServiceControl().Query(DriverServiceName);
            return snapshot.Present && snapshot.RunState == ServiceRunState.Running;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
