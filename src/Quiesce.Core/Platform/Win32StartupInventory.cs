using Microsoft.Win32;
using Quiesce.Core.Startup;

namespace Quiesce.Core.Platform;

/// <summary>Reads the machine's auto-start surface. Read-only; nothing here mutates anything.</summary>
/// <remarks>
/// Every registry read goes through <see cref="RegistryKey.OpenBaseKey"/> with
/// <see cref="RegistryView.Registry64"/>, never the <c>Registry.*</c> statics, for the reason
/// <c>BannedSymbols.txt</c> gives: the statics use <c>RegistryView.Default</c>, which silently redirects
/// HKLM\SOFTWARE into WOW6432Node. That would matter here more than usual — this class deliberately reads
/// the 32-bit Run key as well, and it has to be able to tell the two apart.
/// </remarks>
public sealed class Win32StartupInventory : IStartupInventory
{
    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public IReadOnlyList<StartupItem> Read(StartupLocation location) => location switch
    {
        StartupLocation.UserRun => RunValues(RegistryHive.CurrentUser, RunPath, location),
        StartupLocation.MachineRun => RunValues(RegistryHive.LocalMachine, RunPath, location),
        StartupLocation.MachineRun32 => RunValues(RegistryHive.LocalMachine, Run32Path, location),
        StartupLocation.UserStartupFolder => FolderEntries(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), location),
        StartupLocation.MachineStartupFolder => FolderEntries(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), location),
        StartupLocation.LogonTask => [],
        _ => [],
    };

    /// <summary>The approval subkey each location is governed by, and the hive it lives in.</summary>
    /// <remarks>
    /// A WOW6432Node Run value is governed by <c>StartupApproved\Run32</c> — not by a WOW6432Node copy of
    /// the approval key. The approval key itself is always in the 64-bit view, which is why this is a
    /// mapping and not a path rewrite.
    /// </remarks>
    internal static (RegistryHive Hive, string Subkey)? ApprovalKey(StartupLocation location) => location switch
    {
        StartupLocation.UserRun => (RegistryHive.CurrentUser, ApprovedPath + @"\Run"),
        StartupLocation.UserStartupFolder => (RegistryHive.CurrentUser, ApprovedPath + @"\StartupFolder"),
        StartupLocation.MachineRun => (RegistryHive.LocalMachine, ApprovedPath + @"\Run"),
        StartupLocation.MachineRun32 => (RegistryHive.LocalMachine, ApprovedPath + @"\Run32"),
        StartupLocation.MachineStartupFolder => (RegistryHive.LocalMachine, ApprovedPath + @"\StartupFolder"),
        _ => null,
    };

    /// <summary>
    /// The approval key as <c>HIVE|subkey</c>, matching how a catalog op spells it. Null when unmanageable.
    /// </summary>
    /// <remarks>
    /// Exists so the UI can match a stored preference to a live item by the target its op actually writes,
    /// rather than by re-deriving the mapping and drifting from it.
    /// </remarks>
    public static string? ApprovalKeyDescription(StartupLocation location) =>
        ApprovalKey(location) is { } approval
            ? $"{(approval.Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}|{approval.Subkey}"
            : null;

    private static List<StartupItem> RunValues(RegistryHive hive, string subkey, StartupLocation location)
    {
        var items = new List<StartupItem>();

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subkey, writable: false);
            if (key is null)
            {
                return items;
            }

            foreach (var name in key.GetValueNames())
            {
                items.Add(new StartupItem
                {
                    Name = name,
                    Command = key.GetValue(name)?.ToString() ?? string.Empty,
                    Location = location,
                    ApprovalBlob = ReadApproval(location, name),
                });
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // An unreadable Run key is reported as no entries at that location and nothing more. It is NOT
            // treated as "nothing starts up here" anywhere the result is rendered — the caller reports the
            // locations it could not read, for the reason StateStore.Load documents at length.
        }

        return items;
    }

    private static List<StartupItem> FolderEntries(string folder, StartupLocation location)
    {
        var items = new List<StartupItem>();

        if (string.IsNullOrEmpty(folder))
        {
            return items;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(folder, "*.lnk");
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            return items;
        }

        foreach (var file in files)
        {
            items.Add(new StartupItem
            {
                Name = Path.GetFileName(file),
                // The shortcut's target is deliberately NOT resolved. Doing it needs COM (IShellLink or the
                // WScript.Shell late-bound object), and the target adds nothing Quiesce acts on: the
                // approval value is keyed on the FILE NAME, so the file name is the identity. Showing the
                // path it points at would be nice; pulling in COM to get it would not.
                Command = file,
                Location = location,
                ApprovalBlob = ReadApproval(location, Path.GetFileName(file)),
            });
        }

        return items;
    }

    private static byte[]? ReadApproval(StartupLocation location, string name)
    {
        if (ApprovalKey(location) is not { } approval)
        {
            return null;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(approval.Hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(approval.Subkey, writable: false);

            // Null and "value absent" are the same thing to the caller, and both mean enabled. Only a
            // Binary value is accepted: a value of another kind is not something this format describes,
            // and guessing at it would be worse than reporting no approval record.
            return key?.GetValue(name) as byte[];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
