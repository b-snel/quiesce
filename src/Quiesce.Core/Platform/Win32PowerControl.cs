using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Quiesce.Core.Platform;

/// <summary>
/// The production <see cref="IPowerControl"/>, over the <c>powrprof.dll</c> scheme APIs.
/// </summary>
/// <remarks>
/// <para>
/// NOT implemented as a registry op, although the active scheme really does live in the registry at
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes!ActivePowerScheme</c> and Quiesce
/// already has a perfectly good registry engine. Writing that value directly changes what the machine
/// will use at next boot and leaves the running Power service on the old scheme, so the tweak would
/// verify green against a re-read while nothing about the machine had actually changed until a
/// restart. Same reasoning as using <c>ChangeServiceConfig</c> rather than writing the SCM's
/// <c>Start</c> value: go through the component that owns the setting, so that applying it is what
/// makes it take effect.
/// </para>
/// <para>
/// It also means no activation broadcast is needed. The Power service applies the scheme itself and
/// notifies its own listeners, which is why this is the one op kind with nothing in
/// <c>activation</c>.
/// </para>
/// <para>
/// EVERY FUNCTION HERE RETURNS A WIN32 ERROR CODE, NOT A BOOL. <c>ERROR_SUCCESS</c> is zero, so the
/// natural-looking <c>if (PowerSetActiveScheme(...))</c> is exactly inverted — it would treat every
/// success as a failure and every failure as a success. They are declared returning <c>uint</c> here
/// so that mistake cannot be written silently.
/// </para>
/// <para>
/// Measured on this machine, 2026-07-28, as a standard (non-elevated) interactive user:
/// <c>powercfg /setactive</c> on the already-active scheme returned exit code 0. So selecting a
/// scheme needs no elevation even though the registry key it lands in grants <c>BUILTIN\Users</c>
/// read-only — the call goes through the Power service, which does its own access check. That is why
/// <c>PowerOpSpec.NeedsAdmin</c> is false: declaring admin here would gate the row permanently for a
/// user who could have run it perfectly well.
/// </para>
/// </remarks>
public sealed partial class Win32PowerControl : IPowerControl
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;

    /// <summary>POWER_DATA_ACCESSOR.ACCESS_SCHEME — enumerate scheme GUIDs.</summary>
    private const uint AccessScheme = 16;

    /// <summary>
    /// Refuses to enumerate forever if <c>PowerEnumerate</c> never reports the end of the list.
    /// </summary>
    /// <remarks>
    /// A machine with a hundred power schemes does not exist; a loop with no bound in a tool that runs
    /// elevated does. The cap is far above any real machine (four schemes here) so it can only ever fire
    /// on a broken API contract, and it fires by stopping rather than by throwing — a truncated scheme
    /// list degrades into "target not installed", which is already a handled no-op.
    /// </remarks>
    private const uint MaxSchemes = 128;

    public PowerSchemeSnapshot Query()
    {
        var installed = Enumerate();
        var active = TryGetActiveScheme();

        return new PowerSchemeSnapshot
        {
            Active = active,
            ActiveFriendlyName = active is { } id
                ? installed.FirstOrDefault(s => s.Id == id)?.FriendlyName ?? ReadFriendlyName(id)
                : null,
            Installed = installed,
        };
    }

    public void SetActiveScheme(Guid scheme)
    {
        var error = PowerSetActiveScheme(nint.Zero, in scheme);

        if (error != ErrorSuccess)
        {
            throw new Win32Exception(
                (int)error,
                $"Could not select power scheme {scheme:D}: {new Win32Exception((int)error).Message}");
        }
    }

    /// <summary>
    /// Reads the active scheme, returning null rather than throwing when it cannot be read.
    /// </summary>
    /// <remarks>
    /// "Cannot read the active scheme" has to be a value, not an exception, because it is the one
    /// condition under which Quiesce must decline to change the scheme at all: with no prior captured
    /// there would be nothing to restore, and an unrevertable change is the one thing this project
    /// does not ship.
    /// </remarks>
    private static Guid? TryGetActiveScheme()
    {
        var buffer = nint.Zero;

        try
        {
            if (PowerGetActiveScheme(nint.Zero, out buffer) != ErrorSuccess || buffer == nint.Zero)
            {
                return null;
            }

            return Marshal.PtrToStructure<Guid>(buffer);
        }
        finally
        {
            // LocalFree, not CoTaskMemFree and not Marshal.FreeHGlobal: PowerGetActiveScheme documents
            // that the caller frees the returned GUID with LocalFree. Getting the allocator wrong here
            // is a heap corruption bug that would show up somewhere else entirely.
            if (buffer != nint.Zero)
            {
                LocalFree(buffer);
            }
        }
    }

    private static List<PowerScheme> Enumerate()
    {
        var schemes = new List<PowerScheme>();

        for (var index = 0u; index < MaxSchemes; index++)
        {
            var buffer = new byte[16];
            var size = (uint)buffer.Length;

            var error = PowerEnumerate(
                nint.Zero, nint.Zero, nint.Zero, AccessScheme, index, buffer, ref size);

            if (error == ErrorNoMoreItems)
            {
                break;
            }

            if (error != ErrorSuccess || size < 16)
            {
                // One unreadable slot is not grounds to abandon the list: the enumeration is used to
                // answer "is the target installed", and a short list can only ever make Quiesce more
                // conservative (it declines as not-installed) rather than less.
                continue;
            }

            var id = new Guid(buffer.AsSpan(0, 16));
            schemes.Add(new PowerScheme
            {
                Id = id,
                FriendlyName = ReadFriendlyName(id),
                SleepAfterAcSeconds = ReadSleepAfterAcSeconds(id),
            });
        }

        return schemes;
    }

    private static string? ReadFriendlyName(Guid scheme)
    {
        uint size = 0;

        // Probe for the size first. This returns ERROR_MORE_DATA with the required byte count; sizing
        // from the probe rather than guessing is the same discipline the SCM wrapper needs.
        var error = PowerReadFriendlyName(nint.Zero, in scheme, nint.Zero, nint.Zero, null, ref size);

        if ((error != ErrorSuccess && error != ErrorMoreData) || size == 0)
        {
            return null;
        }

        var buffer = new byte[size];
        if (PowerReadFriendlyName(nint.Zero, in scheme, nint.Zero, nint.Zero, buffer, ref size) != ErrorSuccess)
        {
            return null;
        }

        // A null-terminated UTF-16 string. TrimEnd('\0') rather than assuming exactly one terminator:
        // the reported size includes it, and some builds pad.
        return Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, buffer.Length))
            .TrimEnd('\0');
    }

    /// <summary>
    /// This scheme's AC "sleep after" timeout in seconds, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Fed straight into the remote-session guardrail. Null propagates as "unknown", which the
    /// guardrail treats as a refusal rather than as permission — the honest reading of "I cannot tell
    /// whether this would put the machine to sleep while you are connected over RDP".
    /// </remarks>
    private static uint? ReadSleepAfterAcSeconds(Guid scheme)
    {
        var subGroup = SubGroupSleep;
        var setting = SettingStandbyIdle;

        return PowerReadACValueIndex(nint.Zero, in scheme, in subGroup, in setting, out var seconds) == ErrorSuccess
            ? seconds
            : null;
    }

    /// <summary>GUID_SLEEP_SUBGROUP.</summary>
    private static readonly Guid SubGroupSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");

    /// <summary>GUID_STANDBY_TIMEOUT — "Sleep after", in seconds. Zero means never.</summary>
    private static readonly Guid SettingStandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadACValueIndex(
        nint rootPowerKey,
        in Guid schemeGuid,
        in Guid subGroupOfPowerSettingsGuid,
        in Guid powerSettingGuid,
        out uint acValueIndex);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(nint userRootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerEnumerate(
        nint rootPowerKey,
        nint schemeGuid,
        nint subGroupOfPowerSettingsGuid,
        uint accessFlags,
        uint index,
        [Out] byte[]? buffer,
        ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadFriendlyName(
        nint rootPowerKey,
        in Guid schemeGuid,
        nint subGroupOfPowerSettingsGuid,
        nint powerSettingGuid,
        [Out] byte[]? buffer,
        ref uint bufferSize);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint mem);
}
