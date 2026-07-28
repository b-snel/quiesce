using System.Runtime.InteropServices;

namespace Quiesce.Core.Platform;

/// <summary>Outcome of a restore-point attempt. Deliberately not a bool.</summary>
public sealed record RestorePointResult
{
    /// <summary>True only when a genuinely new restore point exists that did not before.</summary>
    public required bool CreatedNew { get; init; }

    public long? SequenceNumber { get; init; }

    /// <summary>Plain-language explanation, shown to the user verbatim when CreatedNew is false.</summary>
    public required string Detail { get; init; }
}

/// <summary>Creates System Restore checkpoints, and reports honestly when it could not.</summary>
/// <remarks>
/// <c>SRSetRestorePointW</c> is a trap: since Windows 8 it returns <c>TRUE</c> with
/// <c>ERROR_SUCCESS</c> while silently doing nothing if a restore point already exists within the
/// last 24 hours, handing back the *existing* sequence number. A caller that trusts the return
/// value tells the user "restore point created" when none was — the exact category of lie Quiesce
/// exists to avoid. So: read the newest sequence number before, compare after, and only claim
/// success when the number actually moved.
/// <para>
/// System Restore is also disabled by default on many Windows 11 installs, in which case the call
/// fails outright. That is reported, not thrown — a checkpoint is a nice-to-have coarse net, and
/// the journal is the real undo.
/// </para>
/// </remarks>
public sealed class SystemRestore
{
    private const int BeginSystemChange = 100;
    private const int EndSystemChange = 101;
    private const int ModifySettings = 12;

    /// <summary>
    /// Attempts a checkpoint. Never throws for the ordinary failure modes (disabled, throttled,
    /// not elevated) — those come back as <c>CreatedNew: false</c> with an explanation.
    /// </summary>
    public RestorePointResult TryCreate(string description)
    {
        long? before;
        try
        {
            before = NewestSequenceNumber();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            return new RestorePointResult
            {
                CreatedNew = false,
                Detail = $"Could not read existing restore points ({ex.Message}). No checkpoint was created.",
            };
        }

        var info = new RestorePointInfo
        {
            dwEventType = BeginSystemChange,
            dwRestorePtType = ModifySettings,
            llSequenceNumber = 0,
            szDescription = description,
        };

        bool ok;
        try
        {
            ok = SRSetRestorePointW(ref info, out var status);
            if (ok && status.nStatus != 0)
            {
                ok = false;
            }
        }
        catch (DllNotFoundException)
        {
            return new RestorePointResult
            {
                CreatedNew = false,
                Detail = "System Restore is not available on this edition of Windows. No checkpoint was created.",
            };
        }
        catch (EntryPointNotFoundException)
        {
            return new RestorePointResult
            {
                CreatedNew = false,
                Detail = "System Restore API is unavailable. No checkpoint was created.",
            };
        }

        if (!ok)
        {
            return new RestorePointResult
            {
                CreatedNew = false,
                Detail =
                    "Windows refused to create a restore point. This usually means System Restore is turned off " +
                    "for the system drive, or Quiesce is not running elevated. Your changes are still fully " +
                    "reversible from Quiesce's own journal.",
            };
        }

        // The API said yes. Verify by observation, not by trust.
        long? after;
        try
        {
            after = NewestSequenceNumber();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            return new RestorePointResult
            {
                CreatedNew = false,
                Detail = $"Restore point may have been created, but it could not be confirmed ({ex.Message}).",
            };
        }

        if (after is null || after == before)
        {
            return new RestorePointResult
            {
                CreatedNew = false,
                SequenceNumber = after,
                Detail =
                    "Windows reported success but no new restore point appeared - it throttles these to one per " +
                    "24 hours by default, so an existing recent point was reused. Quiesce's journal is unaffected.",
            };
        }

        return new RestorePointResult
        {
            CreatedNew = true,
            SequenceNumber = after,
            Detail = $"Restore point #{after} created.",
        };
    }

    /// <summary>
    /// Newest restore point sequence number, or null when there are none.
    /// </summary>
    /// <remarks>
    /// Read through WMI's <c>SystemRestore</c> class rather than <c>SRGetRestorePointInfoW</c>,
    /// which reports on an in-progress change rather than enumerating existing points.
    /// </remarks>
    private static long? NewestSequenceNumber()
    {
        using var searcher = new System.Management.ManagementObjectSearcher(
            @"\\.\root\default",
            "SELECT SequenceNumber FROM SystemRestore");

        long? newest = null;
        foreach (var item in searcher.Get())
        {
            using (item)
            {
                if (item["SequenceNumber"] is { } raw)
                {
                    var value = Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture);
                    if (newest is null || value > newest)
                    {
                        newest = value;
                    }
                }
            }
        }

        return newest;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RestorePointInfo
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StateManagerStatus
    {
        public int nStatus;
        public long llSequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SRSetRestorePointW(ref RestorePointInfo pRestorePtSpec, out StateManagerStatus pSMgrStatus);
}
