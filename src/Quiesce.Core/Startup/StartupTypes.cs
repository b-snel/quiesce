namespace Quiesce.Core.Startup;

/// <summary>
/// Where an auto-start entry lives, which decides both how it is read and how it is switched off.
/// </summary>
/// <remarks>
/// The <c>StartupApproved</c> subkey name does not always match the source key name — a value under
/// <c>WOW6432Node\...\Run</c> is governed by <c>StartupApproved\Run32</c> — so the mapping is data here
/// rather than a string transformation, and both hives are represented separately because writing the
/// machine-wide approval needs elevation and the per-user one does not.
/// </remarks>
public enum StartupLocation
{
    /// <summary>HKCU <c>...\CurrentVersion\Run</c>, governed by <c>StartupApproved\Run</c>.</summary>
    UserRun,

    /// <summary>The per-user Startup folder, governed by <c>StartupApproved\StartupFolder</c>.</summary>
    UserStartupFolder,

    /// <summary>HKLM <c>...\CurrentVersion\Run</c>, governed by <c>StartupApproved\Run</c> in HKLM.</summary>
    MachineRun,

    /// <summary>HKLM <c>WOW6432Node\...\Run</c>, governed by <c>StartupApproved\Run32</c> in HKLM.</summary>
    MachineRun32,

    /// <summary>The all-users Startup folder, governed by HKLM <c>StartupApproved\StartupFolder</c>.</summary>
    MachineStartupFolder,

    /// <summary>
    /// A scheduled task with a logon trigger. Listed but NOT manageable.
    /// </summary>
    /// <remarks>
    /// Included so the list cannot imply it is complete. A logon task is not a registry value and there is
    /// no approval key for it, so nothing Quiesce does with a registry op can touch it — and that matters
    /// concretely: on the measured machine Comet's updater has BOTH a Run value and a logon task, so
    /// switching off the Run value alone leaves the task firing and would look like a failed tweak.
    /// </remarks>
    LogonTask,
}

/// <summary>One thing that runs when the user signs in.</summary>
public sealed record StartupItem
{
    /// <summary>The value name, or the shortcut file name including <c>.lnk</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The command line, or the shortcut's target. Empty when it could not be read.</summary>
    public required string Command { get; init; }

    public required StartupLocation Location { get; init; }

    /// <summary>The raw approval blob, or null when Explorer has never recorded one.</summary>
    public required byte[]? ApprovalBlob { get; init; }

    /// <summary>True when this entry is already switched off, so there is nothing to do.</summary>
    public bool AlreadyDisabled => StartupApproval.IsDisabled(ApprovalBlob);

    /// <summary>False for a logon task, which no registry op can reach.</summary>
    public bool CanDisable => Location != StartupLocation.LogonTask;

    /// <summary>True when switching this off writes to HKLM and therefore needs elevation.</summary>
    public bool NeedsAdmin => Location
        is StartupLocation.MachineRun
        or StartupLocation.MachineRun32
        or StartupLocation.MachineStartupFolder;
}

/// <summary>
/// Read access to the machine's auto-start surface.
/// </summary>
/// <remarks>
/// A seam for the same reason <see cref="Platform.IProcessControl"/> is one: the interesting cases — an
/// entry with no approval value, a blob of an unexpected length, a shortcut whose target cannot be
/// resolved — are tedious to arrange against a real machine and trivial against a fake. Read-only by
/// construction: everything that mutates goes through the registry op and therefore through the journal.
/// </remarks>
public interface IStartupInventory
{
    /// <summary>Entries at one location, with their approval blobs already resolved.</summary>
    IReadOnlyList<StartupItem> Read(StartupLocation location);
}
