namespace Quiesce.Core.Startup;

/// <summary>
/// The 12-byte value Explorer keeps under <c>StartupApproved</c> to record whether one auto-start entry
/// is enabled — the thing Task Manager's Startup tab writes.
/// </summary>
/// <remarks>
/// MEASURED, not taken from folklore. Fourteen of these were read off a real machine, twelve from
/// <c>HKCU\...\Explorer\StartupApproved\Run</c> and two from <c>\StartupFolder</c>:
/// <code>
/// Discord         02 00 00 00  00 00 00 00 00 00 00 00   enabled
/// Comet.lnk       02 00 00 00  00 00 00 00 00 00 00 00   enabled
/// Steam           03 00 00 00  2C BC 5E B8 D2 C2 DC 01   disabled, timestamped
/// Docker Desktop  03 00 00 00  00 00 00 00 00 00 00 00   disabled, NOT timestamped
/// </code>
/// The first DWORD carries the state; the remaining eight bytes are a FILETIME recording when the entry
/// was switched off. Docker Desktop is the useful row: it proves the timestamp is optional, so Quiesce
/// never has to fabricate a plausible one to make Explorer accept a disable.
/// <para>
/// BIT 0 IS THE FLAG, not equality with 3. Only 2 and 3 were observed here, so the bit rule is
/// <em>consistent with</em> the measurement rather than proven by it — but folder entries are reported in
/// the wild carrying 6 and 7, and the bit test degrades correctly for those where <c>== 3</c> would
/// silently report a disabled entry as enabled. Erring toward "it might be disabled" is the safe
/// direction: the cost is eliding a write that was not needed, and the cost of the opposite is claiming
/// to have switched something off that is still running.
/// </para>
/// <para>
/// One thing deliberately NOT claimed: whether Explorer honours a blob Quiesce wrote rather than one
/// Task Manager wrote. Nothing in the format carries provenance and Task Manager is just another
/// user-mode writer, so there is no mechanism by which it could tell — but that is reasoning, not a
/// measurement, and it needs a logon cycle to confirm. The catalog entry says so.
/// </para>
/// </remarks>
public static class StartupApproval
{
    /// <summary>Length Explorer writes. Longer blobs are read, never produced.</summary>
    public const int BlobLength = 12;

    private const uint DisabledBit = 1;

    /// <summary>Whether a blob means "do not run this at logon". A missing value means enabled.</summary>
    /// <remarks>
    /// Absent is enabled, and that asymmetry matters for revert: an entry that never had an approval value
    /// must end up with no value again, not with an explicit "enabled" one. The registry op's tri-state
    /// prior already does exactly that — <c>ValueAbsent</c> restores by deleting.
    /// </remarks>
    public static bool IsDisabled(byte[]? blob) =>
        blob is not null && blob.Length >= sizeof(uint) && (ReadState(blob) & DisabledBit) != 0;

    /// <summary>
    /// The blob that disables an entry, derived from what is there now.
    /// </summary>
    /// <remarks>
    /// Derived rather than canonical, so that an entry the user already switched off by hand produces the
    /// bytes it already has and the engine elides the write as already-lean. Writing a canonical
    /// <c>03 00 00 00</c> + zeros instead would differ from a hand-disabled entry only in the timestamp,
    /// which is cosmetic — but it would make Quiesce report a change it did not need to make, and then
    /// "restore" a timestamp on the way back out.
    /// </remarks>
    public static byte[] Disable(byte[]? current)
    {
        if (current is null || current.Length < sizeof(uint))
        {
            // Nothing to preserve. Docker Desktop on the measured machine is exactly this shape, which is
            // why the zeroed timestamp is known to be acceptable rather than assumed.
            return [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        }

        var disabled = current.ToArray();
        WriteState(disabled, ReadState(disabled) | DisabledBit);
        return disabled;
    }

    /// <summary>The blob that re-enables an entry. Used by tests and by the revert-script notes.</summary>
    public static byte[] Enable(byte[]? current)
    {
        if (current is null || current.Length < sizeof(uint))
        {
            return [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        }

        var enabled = current.ToArray();
        WriteState(enabled, ReadState(enabled) & ~DisabledBit);
        return enabled;
    }

    /// <summary>Human-readable rendering for the UI and the CLI.</summary>
    public static string Describe(byte[]? blob) => blob switch
    {
        null => "on (no approval value recorded)",
        _ when IsDisabled(blob) => "off",
        _ => "on",
    };

    private static uint ReadState(byte[] blob) => BitConverter.ToUInt32(blob, 0);

    private static void WriteState(byte[] blob, uint state) =>
        BitConverter.GetBytes(state).CopyTo(blob, 0);
}
